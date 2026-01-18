using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Services;

namespace Snacka.Server.Tests;

public class IntegrationTestBase : IDisposable
{
    public readonly WebApplicationFactory<Program> Factory;
    public readonly HttpClient Client;
    private readonly string _dbName;

    private const string TestSecretKey = "ThisIsATestSecretKeyThatIsLongEnoughForHmacSha256Testing!";
    private const string TestIssuer = "TestIssuer";
    private const string TestAudience = "TestAudience";

    public IntegrationTestBase()
    {
        _dbName = Guid.NewGuid().ToString();

        // Set environment variable for JWT secret BEFORE factory creation
        // Program.cs validates this early, before ConfigureAppConfiguration runs
        Environment.SetEnvironmentVariable("JWT_SECRET_KEY", TestSecretKey);

        var testConfig = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = TestSecretKey,
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:AccessTokenExpirationMinutes"] = "60",
            ["Jwt:RefreshTokenExpirationDays"] = "7"
        };

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    // Clear existing configuration sources and add test config
                    config.Sources.Clear();
                    config.AddInMemoryCollection(testConfig);
                });

                builder.ConfigureServices(services =>
                {
                    // Remove all database-related registrations (may be multiple)
                    var descriptorsToRemove = services
                        .Where(d => d.ServiceType == typeof(DbContextOptions<SnackaDbContext>) ||
                                    d.ServiceType == typeof(DbContextOptions<DataProtectionDbContext>) ||
                                    d.ServiceType == typeof(DbContextOptions) ||
                                    d.ServiceType == typeof(SnackaDbContext) ||
                                    d.ServiceType == typeof(DataProtectionDbContext) ||
                                    d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true)
                        .ToList();
                    foreach (var descriptor in descriptorsToRemove)
                        services.Remove(descriptor);

                    // Add in-memory databases
                    services.AddDbContext<SnackaDbContext>(options =>
                    {
                        options.UseInMemoryDatabase(_dbName);
                    });

                    services.AddDbContext<DataProtectionDbContext>(options =>
                    {
                        options.UseInMemoryDatabase(_dbName + "_dataprotection");
                    });

                    // Reconfigure JWT Bearer authentication with test settings
                    services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidateAudience = true,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            ValidIssuer = TestIssuer,
                            ValidAudience = TestAudience,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSecretKey)),
                            ClockSkew = TimeSpan.Zero
                        };
                    });
                });
            });

        Client = Factory.CreateClient();
    }

    public async Task<AuthResponse> RegisterUserAsync(string username, string email, string password)
    {
        var inviteCode = await CreateInviteCodeAsync();
        var response = await Client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, email, password, inviteCode));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return auth!;
    }

    public async Task<string> CreateInviteCodeAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var inviteService = scope.ServiceProvider.GetRequiredService<IServerInviteService>();
        var invite = await inviteService.CreateInviteAsync(null, maxUses: 0);
        return invite.Code;
    }

    public async Task<AuthResponse> LoginUserAsync(string email, string password)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        return auth!;
    }

    public void SetAuthToken(string token)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void ClearAuthToken()
    {
        Client.DefaultRequestHeaders.Authorization = null;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }
}
