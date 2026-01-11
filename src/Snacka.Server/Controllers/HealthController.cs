using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IServerInviteService _inviteService;
    private readonly IWebHostEnvironment _environment;
    private readonly TenorSettings _tenorSettings;
    private readonly KlipySettings _klipySettings;

    public HealthController(IConfiguration configuration, IServerInviteService inviteService, IWebHostEnvironment environment, IOptions<TenorSettings> tenorSettings, IOptions<KlipySettings> klipySettings)
    {
        _configuration = configuration;
        _inviteService = inviteService;
        _environment = environment;
        _tenorSettings = tenorSettings.Value;
        _klipySettings = klipySettings.Value;
    }

    [HttpGet]
    public async Task<ActionResult<ServerInfoResponse>> GetHealth(CancellationToken cancellationToken)
    {
        var hasUsers = await _inviteService.HasAnyUsersAsync(cancellationToken);
        string? bootstrapInviteCode = null;

        // Return bootstrap invite code when there are no users (first-time setup)
        // OR in development mode (for dev-start.sh multi-client testing)
        if (!hasUsers || _environment.IsDevelopment())
        {
            bootstrapInviteCode = await _inviteService.GetOrCreateBootstrapInviteAsync(cancellationToken);
        }

        // GIFs are enabled if either Tenor or Klipy API key is configured
        var gifsEnabled = !string.IsNullOrWhiteSpace(_tenorSettings.ApiKey) ||
                          !string.IsNullOrWhiteSpace(_klipySettings.ApiKey);

        return Ok(new ServerInfoResponse(
            Name: _configuration["ServerInfo:Name"] ?? "Snacka Server",
            Description: _configuration["ServerInfo:Description"],
            Version: "1.0.0",
            AllowRegistration: _configuration.GetValue("ServerInfo:AllowRegistration", true),
            HasUsers: hasUsers,
            BootstrapInviteCode: bootstrapInviteCode,
            GifsEnabled: gifsEnabled
        ));
    }
}

public record ServerInfoResponse(
    string Name,
    string? Description,
    string Version,
    bool AllowRegistration,
    bool HasUsers,
    string? BootstrapInviteCode,  // Only returned if no users exist
    bool GifsEnabled  // True if Tenor or Klipy API key is configured
);
