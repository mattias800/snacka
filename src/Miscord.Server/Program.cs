using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Miscord.Server.Data;
using Miscord.Server.Hubs;
using Miscord.Server.Services;
using Miscord.Server.Services.Sfu;

var builder = WebApplication.CreateBuilder(args);

// Configure JWT settings
builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection(JwtSettings.SectionName));

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("JWT settings are not configured.");

// Add database context - Use SQLite for local development, PostgreSQL for production
var useSqlite = builder.Configuration.GetValue<bool>("UseSqlite", true);
if (useSqlite)
{
    var sqliteConnection = builder.Configuration.GetConnectionString("SqliteConnection")
        ?? "Data Source=miscord.db";
    builder.Services.AddDbContext<MiscordDbContext>(options =>
        options.UseSqlite(sqliteConnection));
}
else
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("DefaultConnection is not configured.");
    builder.Services.AddDbContext<MiscordDbContext>(options =>
        options.UseNpgsql(connectionString));
}

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
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SecretKey)),
        ClockSkew = TimeSpan.Zero
    };

    // Allow JWT tokens to be passed in query string for SignalR
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// Add services
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IServerInviteService, ServerInviteService>();
builder.Services.AddScoped<IDirectMessageService, DirectMessageService>();
builder.Services.AddScoped<ICommunityService, CommunityService>();
builder.Services.AddScoped<IChannelService, ChannelService>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<ICommunityMemberService, CommunityMemberService>();
builder.Services.AddScoped<IVoiceService, VoiceService>();
builder.Services.AddScoped<IReactionService, ReactionService>();

// Add link preview service with HttpClient
builder.Services.AddHttpClient<ILinkPreviewService, LinkPreviewService>();

// Add SFU service (Singleton to maintain WebRTC connections across requests)
builder.Services.AddSingleton<ISfuService, SfuService>();

// Add file storage service
builder.Services.Configure<FileStorageSettings>(
    builder.Configuration.GetSection(FileStorageSettings.SectionName));
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IImageProcessingService, ImageProcessingService>();

// Add controllers
builder.Services.AddControllers();

// Add OpenAPI/Swagger
builder.Services.AddOpenApi();

// Add SignalR
builder.Services.AddSignalR();

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Ensure database is created and reset online status
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MiscordDbContext>();
    db.Database.EnsureCreated();

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
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MiscordHub>("/hubs/miscord");

app.Run();

// Make Program accessible for integration tests
public partial class Program { }
