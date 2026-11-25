using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/gaming-stations")]
[Authorize]
public class GamingStationsController : ControllerBase
{
    private readonly IGamingStationService _stationService;
    private readonly IHubContext<SnackaHub> _hubContext;

    public GamingStationsController(
        IGamingStationService stationService,
        IHubContext<SnackaHub> hubContext)
    {
        _stationService = stationService;
        _hubContext = hubContext;
    }

    // ========================================================================
    // Station Management
    // ========================================================================

    /// <summary>
    /// Get all stations the user owns or has access to.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<GamingStationResponse>>> GetStations(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var stations = await _stationService.GetStationsAsync(userId.Value, cancellationToken);
        return Ok(stations);
    }

    /// <summary>
    /// Get a specific station by ID.
    /// </summary>
    [HttpGet("{stationId:guid}")]
    public async Task<ActionResult<GamingStationResponse>> GetStation(
        Guid stationId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var station = await _stationService.GetStationAsync(stationId, userId.Value, cancellationToken);
        if (station is null) return NotFound();

        return Ok(station);
    }

    /// <summary>
    /// Register a new gaming station.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<GamingStationResponse>> RegisterStation(
        [FromBody] RegisterStationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var station = await _stationService.RegisterStationAsync(userId.Value, request, cancellationToken);
            return CreatedAtAction(nameof(GetStation), new { stationId = station.Id }, station);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update a station's settings.
    /// </summary>
    [HttpPut("{stationId:guid}")]
    public async Task<ActionResult<GamingStationResponse>> UpdateStation(
        Guid stationId,
        [FromBody] UpdateStationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var station = await _stationService.UpdateStationAsync(stationId, userId.Value, request, cancellationToken);
            return Ok(station);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Unregister (delete) a station.
    /// </summary>
    [HttpDelete("{stationId:guid}")]
    public async Task<IActionResult> DeleteStation(
        Guid stationId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _stationService.DeleteStationAsync(stationId, userId.Value, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    // ========================================================================
    // Access Management
    // ========================================================================

    /// <summary>
    /// Get all access grants for a station.
    /// </summary>
    [HttpGet("{stationId:guid}/access")]
    public async Task<ActionResult<IEnumerable<StationAccessGrantResponse>>> GetAccessGrants(
        Guid stationId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var grants = await _stationService.GetAccessGrantsAsync(stationId, userId.Value, cancellationToken);
            return Ok(grants);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Grant access to a user.
    /// </summary>
    [HttpPost("{stationId:guid}/access")]
    public async Task<ActionResult<StationAccessGrantResponse>> GrantAccess(
        Guid stationId,
        [FromBody] GrantStationAccessRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var grant = await _stationService.GrantAccessAsync(stationId, userId.Value, request, cancellationToken);

            // Notify the granted user
            await _hubContext.Clients.User(request.UserId.ToString())
                .SendAsync("StationAccessGranted", new StationAccessGrantedEvent(
                    stationId,
                    grant.Username, // TODO: Get station name
                    request.UserId,
                    grant.Permission,
                    userId.Value,
                    User.Identity?.Name ?? "Unknown"
                ), cancellationToken);

            return Ok(grant);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing access grant.
    /// </summary>
    [HttpPut("{stationId:guid}/access/{grantId:guid}")]
    public async Task<ActionResult<StationAccessGrantResponse>> UpdateAccess(
        Guid stationId,
        Guid grantId,
        [FromBody] UpdateStationAccessRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var grant = await _stationService.UpdateAccessAsync(stationId, grantId, userId.Value, request, cancellationToken);
            return Ok(grant);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Revoke access from a user.
    /// </summary>
    [HttpDelete("{stationId:guid}/access/{targetUserId:guid}")]
    public async Task<IActionResult> RevokeAccess(
        Guid stationId,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _stationService.RevokeAccessAsync(stationId, targetUserId, userId.Value, cancellationToken);

            // Notify the revoked user
            await _hubContext.Clients.User(targetUserId.ToString())
                .SendAsync("StationAccessRevoked", new StationAccessRevokedEvent(
                    stationId,
                    targetUserId,
                    userId.Value
                ), cancellationToken);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    // ========================================================================
    // Session Management
    // ========================================================================

    /// <summary>
    /// Get the current active session for a station.
    /// </summary>
    [HttpGet("{stationId:guid}/session")]
    public async Task<ActionResult<StationSessionResponse>> GetActiveSession(
        Guid stationId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        // Check user has access
        var permission = await _stationService.GetUserPermissionAsync(stationId, userId.Value, cancellationToken);
        if (permission is null) return Forbid();

        var session = await _stationService.GetActiveSessionAsync(stationId, cancellationToken);
        if (session is null) return NotFound();

        return Ok(session);
    }

    /// <summary>
    /// Assign a player slot to a connected user.
    /// </summary>
    [HttpPost("{stationId:guid}/player-slot")]
    public async Task<ActionResult<StationSessionUserResponse>> AssignPlayerSlot(
        Guid stationId,
        [FromBody] AssignPlayerSlotRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var sessionUser = await _stationService.AssignPlayerSlotAsync(
                stationId, request.UserId, request.PlayerSlot, userId.Value, cancellationToken);

            if (sessionUser is null) return NotFound();

            // Notify connected users about slot assignment
            await _hubContext.Clients.Group($"station:{stationId}")
                .SendAsync("PlayerSlotAssigned", new PlayerSlotAssignedEvent(
                    stationId,
                    request.UserId,
                    request.PlayerSlot
                ), cancellationToken);

            return Ok(sessionUser);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
