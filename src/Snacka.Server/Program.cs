using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.Data;
using Snacka.Server.Hubs;
using Snacka.Server.Middleware;
using Snacka.Server.Services;
using Snacka.Server.Services.Sfu;

var builder = WebApplication.CreateBuilder(args);

// Configure forwarded headers for reverse proxy support (NGINX, etc.)
// This must be configured before other middleware to get correct client IPs and hostnames
// SECURITY (#19): only trust X-Forwarded-* from known proxies. Trusting everyone let any client
// pick its own IP and bypass every IP-based rate limit (including login). Default: loopback only
// (a proxy on the same host). Add networks for other setups, e.g. Docker behind a host proxy:
//   ReverseProxy__TrustedNetworks__0=172.16.0.0/12
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    foreach (var cidr in builder.Configuration.GetSection("ReverseProxy:TrustedNetworks").Get<string[]>() ?? [])
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            throw new InvalidOperationException($"Invalid ReverseProxy:TrustedNetworks entry '{cidr}' (expected CIDR like 172.16.0.0/12).");
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefix));
    }
});

// Where a self-hosted server keeps its database, uploads and generated secrets (#53).
var dataDirectory = Path.GetFullPath(
    Environment.GetEnvironmentVariable("SNACKA_DATA")
    ?? builder.Configuration["DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "snacka-data"));
Directory.CreateDirectory(dataDirectory);

// SECURITY: Get JWT secret from environment variable or config; a self-hosted server without one
// generates its own on first run.
var jwtSecretKey = new[] { Environment.GetEnvironmentVariable("JWT_SECRET_KEY"), builder.Configuration.GetSection(JwtSettings.SectionName)["SecretKey"] }
    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
    ?? GeneratedSecrets.JwtSecret(dataDirectory);

if (SecretValidator.ValidateJwtSecret(jwtSecretKey) is { } secretError)
{
    throw new InvalidOperationException(secretError);
}
ArgumentNullException.ThrowIfNull(jwtSecretKey);

// Configure JWT settings with resolved secret key
builder.Services.Configure<JwtSettings>(options =>
{
    builder.Configuration.GetSection(JwtSettings.SectionName).Bind(options);
    options.SecretKey = jwtSecretKey;
});

// SECURITY: Configure rate limiting to prevent brute force attacks
builder.Services.AddRateLimiter(options =>
{
    // Reject requests that exceed rate limits with 429 Too Many Requests
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Login: 10 attempts per minute per IP (allows typos, prevents brute force)
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:LoginPerMinute", 10),
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0 // Don't queue, reject immediately
            }));

    // Register: 5 attempts per hour per IP (prevents mass account creation)
    options.AddPolicy("register", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:RegisterPerHour", 5),
                Window = TimeSpan.FromHours(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Refresh token: 30 attempts per minute per IP (more generous for automated refresh)
    options.AddPolicy("refresh", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // General API rate limit: 100 requests per minute per IP (for other endpoints)
    options.AddPolicy("api", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Log when rate limits are exceeded
    options.OnRejected = async (context, cancellationToken) =>
    {
        var logger = context.HttpContext.RequestServices.GetService<ILogger<Program>>();
        logger?.LogWarning("Rate limit exceeded for {IP} on {Path}",
            context.HttpContext.Connection.RemoteIpAddress,
            context.HttpContext.Request.Path);

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsync(
            "Too many requests. Please try again later.", cancellationToken);
    };
});

builder.Services.AddSnackaDatabase(builder.Configuration.GetConnectionString("DefaultConnection"), dataDirectory);

// Add authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration.GetSection(JwtSettings.SectionName)["Issuer"],
        ValidAudience = builder.Configuration.GetSection(JwtSettings.SectionName)["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecretKey)),
        ClockSkew = TimeSpan.Zero
    };

    // Allow JWT tokens to be passed in query string for SignalR, attachments, and avatars
    // This enables <img> tags to load authenticated images via ?access_token=xxx
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) &&
                (path.StartsWithSegments("/hubs") ||
                 path.StartsWithSegments("/ws/tunnel") ||
                 path.StartsWithSegments("/api/attachments") ||
                 path.Value?.Contains("/avatar") == true))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        },
        // SECURITY (#15): refresh tokens are long-lived and must never work as access tokens.
        OnTokenValidated = context =>
        {
            if (context.Principal?.FindFirst(AuthService.TokenTypeClaim)?.Value == AuthService.RefreshTokenType)
                context.Fail("Refresh tokens can't be used as access tokens.");
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// Add services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<IChannelActivityService, ChannelActivityService>();
builder.Services.AddScoped<IRealtimeMembership, RealtimeMembership>();
builder.Services.Configure<LiveKitSettings>(builder.Configuration.GetSection(LiveKitSettings.SectionName));
builder.Services.AddSingleton<ILiveKitTokenService, LiveKitTokenService>();
builder.Services.AddScoped<IServerInviteService, ServerInviteService>();
builder.Services.AddScoped<IDirectMessageService, DirectMessageService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<ICommunityService, CommunityService>();
builder.Services.AddScoped<IChannelService, ChannelService>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<ICommunityMemberService, CommunityMemberService>();
builder.Services.AddScoped<ICommunityInviteService, CommunityInviteService>();
builder.Services.AddScoped<IVoiceService, VoiceService>();
builder.Services.AddScoped<IReactionService, ReactionService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IGamingStationService, GamingStationService>();

// Add link preview service with HttpClient
builder.Services.AddHttpClient<ILinkPreviewService, LinkPreviewService>();

// Add GIF service (Tenor or Klipy based on configuration)
builder.Services.Configure<GifSettings>(builder.Configuration.GetSection("Gif"));
builder.Services.Configure<TenorSettings>(builder.Configuration.GetSection("Tenor"));
builder.Services.Configure<KlipySettings>(builder.Configuration.GetSection("Klipy"));

var gifProvider = builder.Configuration.GetValue<string>("Gif:Provider") ?? "Tenor";
if (gifProvider.Equals("Klipy", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IGifService, KlipyService>();
}
else
{
    builder.Services.AddHttpClient<IGifService, TenorService>();
}

// Add SFU service (Singleton to maintain WebRTC connections across requests)
builder.Services.AddSingleton<ISfuService, SfuService>();

// Add Tunnel service for port forwarding (Singleton to maintain tunnel state)
builder.Services.AddSingleton<ITunnelService, TunnelService>();

// Add TURN service for WebRTC NAT traversal
builder.Services.Configure<TurnSettings>(builder.Configuration.GetSection(TurnSettings.SectionName));
builder.Services.AddSingleton<ITurnService, TurnService>();

// Add file storage service
builder.Services.Configure<FileStorageSettings>(options =>
{
    builder.Configuration.GetSection(FileStorageSettings.SectionName).Bind(options);
    if (string.IsNullOrWhiteSpace(options.BasePath))
        options.BasePath = Path.Combine(dataDirectory, "uploads");
});
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IImageProcessingService, ImageProcessingService>();

// Add controllers
builder.Services.AddControllers(options => options.Filters.Add<Snacka.Server.Middleware.AccessDeniedExceptionFilter>());

// Add OpenAPI/Swagger
builder.Services.AddOpenApi();

// Add SignalR
builder.Services.AddSignalR();

// SECURITY: Configure HSTS for production
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = true;
});

// Configure CORS - restrict to configured origins in production
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>();

builder.Services.AddCors(options =>
{
    if (builder.Environment.IsDevelopment() || allowedOrigins == null || allowedOrigins.Length == 0)
    {
        // Development mode: allow all origins but log a warning
        options.AddPolicy("AllowConfigured", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
        if (!builder.Environment.IsDevelopment())
        {
            Console.WriteLine("WARNING: CORS is configured to allow all origins. Set AllowedOrigins in appsettings.json for production.");
        }
    }
    else
    {
        // Production mode: restrict to configured origins
        options.AddPolicy("AllowConfigured", policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        });
    }
});

var app = builder.Build();

// Apply pending migrations and ensure database is created
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.MigrateSnackaDatabase();
    var db = scope.ServiceProvider.GetRequiredService<SnackaDbContext>();

    // In development, ensure there are unlimited bootstrap invites for easy testing
    if (app.Environment.IsDevelopment())
    {
        var devInviteCount = db.ServerInvites
            .Where(i => i.CreatedById == null && !i.IsRevoked && (i.ExpiresAt == null || i.ExpiresAt > DateTime.UtcNow))
            .Count();

        // Create multiple unlimited invites for concurrent dev testing
        while (devInviteCount < 10)
        {
            var inviteService = scope.ServiceProvider.GetRequiredService<IServerInviteService>();
            var invite = inviteService.CreateInviteAsync(null, maxUses: 0).Result;  // maxUses: 0 = unlimited
            devInviteCount++;
        }
        if (devInviteCount > 0)
        {
            Console.WriteLine("Development: Created unlimited bootstrap invites for testing");
        }
    }

    // Reset all users to offline on server startup
    // This handles cases where the server was restarted without proper disconnect cleanup
    var onlineUsers = db.Users.Where(u => u.IsOnline).ToList();
    foreach (var user in onlineUsers)
    {
        user.IsOnline = false;
    }
    if (onlineUsers.Count > 0)
    {
        db.SaveChanges();
        Console.WriteLine($"Reset {onlineUsers.Count} users to offline status on startup");
    }

    // Clear any stale voice participants from previous server session
    var staleParticipants = db.VoiceParticipants.ToList();
    if (staleParticipants.Count > 0)
    {
        db.VoiceParticipants.RemoveRange(staleParticipants);
        db.SaveChanges();
        Console.WriteLine($"Cleared {staleParticipants.Count} stale voice participants on startup");
    }

    // Check if server needs initial setup (no users)
    var hasUsers = db.Users.Any();
    if (!hasUsers)
    {
        // Get or create the bootstrap invite code
        var inviteService = scope.ServiceProvider.GetRequiredService<IServerInviteService>();
        var inviteCode = inviteService.GetOrCreateBootstrapInviteAsync(CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine("");
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    FIRST TIME SETUP                          ║");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
        Console.WriteLine("║  No users found. Please complete the setup wizard to         ║");
        Console.WriteLine("║  create the first administrator account.                     ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║  Open your browser and navigate to:                          ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║    http://<your-server-address>:5117/setup                   ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║  If using a reverse proxy with SSL:                          ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║    https://<your-domain>/setup                               ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine($"║  Invite Code: {inviteCode,-46} ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine("");
    }
}

// Configure the HTTP request pipeline.

// IMPORTANT: UseForwardedHeaders must be first to get correct client IPs for rate limiting
// This enables the server to work correctly behind reverse proxies (NGINX, etc.)
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    // SECURITY: Enable HSTS in production
    // Note: When behind a reverse proxy that handles SSL, HSTS headers should be set by the proxy
    app.UseHsts();
}

// SECURITY: Add security headers to all responses
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(self), geolocation=()");
    await next();
});

// HTTPS redirection - only in development where we might access the API directly
// In production, the reverse proxy (NGINX, etc.) handles SSL termination and redirects
// Skipping this in production avoids the "Failed to determine the https port" warning
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors("AllowConfigured");
app.UseRateLimiter();

// Serve static files (setup wizard, etc.)
app.UseStaticFiles();

// SPA fallback for setup wizard - only for routes without file extensions
app.Use(async (context, next) =>
{
    await next();

    // If we got a 404 and the path starts with /setup and has no file extension, serve index.html
    if (context.Response.StatusCode == 404 &&
        !context.Response.HasStarted &&
        context.Request.Path.StartsWithSegments("/setup") &&
        !Path.HasExtension(context.Request.Path.Value))
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(
            Path.Combine(builder.Environment.WebRootPath, "setup", "index.html"));
    }
});

// Setup wizard middleware - redirect to /setup if server has no users
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Skip middleware for API endpoints, setup wizard, and static files
    if (path.StartsWith("/api") ||
        path.StartsWith("/setup") ||
        path.StartsWith("/hubs") ||
        path.Contains('.'))
    {
        await next();
        return;
    }

    // Check if server needs setup (no users exist)
    using var scope = context.RequestServices.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SnackaDbContext>();
    var hasUsers = await db.Users.AnyAsync();

    if (!hasUsers && path == "/")
    {
        // Redirect to setup wizard
        context.Response.Redirect("/setup/");
        return;
    }

    await next();
});

// Enable WebSocket support for tunnel data plane
app.UseWebSockets();

// Tunnel proxy must be before auth so it can handle its own cookie-based auth
app.UseTunnelProxy();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<SnackaHub>("/hubs/snacka");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

// Make Program accessible for integration tests
public partial class Program { }
