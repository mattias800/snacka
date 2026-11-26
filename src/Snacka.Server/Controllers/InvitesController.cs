using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

/// <summary>
/// Controller for managing the current user's community invites.
/// </summary>
[ApiController]
[Route("api/invites")]
[Authorize]
public class InvitesController : ControllerBase
{
    private readonly ICommunityInviteService _inviteService;
    private readonly IHubContext<SnackaHub> _hubContext;
    private readonly ILogger<InvitesController> _logger;

    public InvitesController(
        ICommunityInviteService inviteService,
        IHubContext<SnackaHub> hubContext,
        ILogger<InvitesController> logger)
    {
        _inviteService = inviteService;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Get all pending invites for the current user.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<CommunityInviteResponse>>> GetMyInvites(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var invites = await _inviteService.GetPendingInvitesForUserAsync(userId.Value, cancellationToken);
        return Ok(invites);
    }

    /// <summary>
    /// Accept an invite and join the community.
    /// </summary>
    [HttpPost("{inviteId:guid}/accept")]
    public async Task<ActionResult<CommunityInviteResponse>> AcceptInvite(
        Guid inviteId,
        [FromServices] IRealtimeMembership realtime,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var invite = await _inviteService.AcceptInviteAsync(inviteId, userId.Value, cancellationToken);
            await realtime.JoinedCommunityAsync(userId.Value, invite.CommunityId, cancellationToken);

            // Notify the community about the new member
            _logger.LogInformation("Broadcasting CommunityMemberAdded to group community:{CommunityId} for user {UserId}",
                invite.CommunityId, userId.Value);
            await _hubContext.Clients.Group($"community:{invite.CommunityId}")
                .SendAsync("CommunityMemberAdded", invite.CommunityId, userId.Value, cancellationToken);

            // Notify the inviter that the invite was accepted
            await _hubContext.Clients.User(invite.InvitedById.ToString())
                .SendAsync("CommunityInviteResponded", new CommunityInviteRespondedEvent(
                    invite.Id,
                    invite.CommunityId,
                    invite.InvitedUserId,
                    invite.InvitedUserUsername,
                    CommunityInviteStatus.Accepted
                ), cancellationToken);

            return Ok(invite);
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
    /// Decline an invite.
    /// </summary>
    [HttpPost("{inviteId:guid}/decline")]
    public async Task<ActionResult<CommunityInviteResponse>> DeclineInvite(
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var invite = await _inviteService.DeclineInviteAsync(inviteId, userId.Value, cancellationToken);

            // Notify the inviter that the invite was declined
            await _hubContext.Clients.User(invite.InvitedById.ToString())
                .SendAsync("CommunityInviteResponded", new CommunityInviteRespondedEvent(
                    invite.Id,
                    invite.CommunityId,
                    invite.InvitedUserId,
                    invite.InvitedUserUsername,
                    CommunityInviteStatus.Declined
                ), cancellationToken);

            return Ok(invite);
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
