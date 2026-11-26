using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
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
    /// <summary>
    /// The test's own SQLite file, as a self-hosted server uses (requests and the hub hit it
    /// concurrently, so it can't be one shared in-memory connection).
    /// </summary>
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "snacka-server-tests", $"{Guid.NewGuid():N}.db");

    internal const string TestSecretKey = "q7Vx2LmP9rTz4Kb8Nw1Hc6Jd3Fy5Gs0Ue-Integration";
    internal const string TestIssuer = "TestIssuer";
    internal const string TestAudience = "TestAudience";

    /// <param name="extraConfig">Additional configuration values (override the defaults below).</param>
    public IntegrationTestBase(IDictionary<string, string?>? extraConfig = null)
    {
        // Program.cs reads these early, before ConfigureAppConfiguration runs, so they're
        // environment variables (the same for every test, which may run in parallel).
        Environment.SetEnvironmentVariable("JWT_SECRET_KEY", TestSecretKey);
        Environment.SetEnvironmentVariable("SNACKA_DATA", Path.Combine(Path.GetTempPath(), "snacka-server-tests"));

        var testConfig = new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = TestSecretKey,
            ["Jwt:Issuer"] = TestIssuer,
            ["Jwt:Audience"] = TestAudience,
            ["Jwt:AccessTokenExpirationMinutes"] = "60",
            ["Jwt:RefreshTokenExpirationDays"] = "7"
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, string?>())
            testConfig[key] = value;

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
                    // Swap the server's default database for this test's own file: real SQL, and
                    // the real (SQLite) migrations.
                    var descriptorsToRemove = services
                        .Where(d => d.ServiceType == typeof(SnackaDbContext) || d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true)
                        .ToList();
                    foreach (var descriptor in descriptorsToRemove)
                        services.Remove(descriptor);
                    services.AddDbContext<SnackaDbContext, SqliteSnackaDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

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

    /// <summary>
    /// A real SignalR client connected to this test server as the owner of <paramref name="accessToken"/>,
    /// for multi-client scenarios (one per simulated user).
    /// </summary>
    public async Task<HubConnection> ConnectHubAsync(string accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "hubs/snacka"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    public void ClearAuthToken()
    {
        Client.DefaultRequestHeaders.Authorization = null;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            File.Delete(file);
    }
}
