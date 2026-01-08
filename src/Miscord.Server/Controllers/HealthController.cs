using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Miscord.Server.Services;

namespace Miscord.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IServerInviteService _inviteService;
    private readonly TenorSettings _tenorSettings;

    public HealthController(IConfiguration configuration, IServerInviteService inviteService, IOptions<TenorSettings> tenorSettings)
    {
        _configuration = configuration;
        _inviteService = inviteService;
        _tenorSettings = tenorSettings.Value;
    }

    [HttpGet]
    public async Task<ActionResult<ServerInfoResponse>> GetHealth(CancellationToken cancellationToken)
    {
        var hasUsers = await _inviteService.HasAnyUsersAsync(cancellationToken);
        string? bootstrapInviteCode = null;

        // Only return bootstrap invite code when there are no users (first-time setup)
        if (!hasUsers)
        {
            bootstrapInviteCode = await _inviteService.GetOrCreateBootstrapInviteAsync(cancellationToken);
        }

        return Ok(new ServerInfoResponse(
            Name: _configuration["ServerInfo:Name"] ?? "Miscord Server",
            Description: _configuration["ServerInfo:Description"],
            Version: "1.0.0",
            AllowRegistration: _configuration.GetValue("ServerInfo:AllowRegistration", true),
            HasUsers: hasUsers,
            BootstrapInviteCode: bootstrapInviteCode,
            GifsEnabled: !string.IsNullOrWhiteSpace(_tenorSettings.ApiKey)
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
    bool GifsEnabled  // True if Tenor API key is configured
);
