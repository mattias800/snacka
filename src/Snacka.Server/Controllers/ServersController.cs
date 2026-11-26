using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/communities")]
[Authorize]
public class CommunitiesController : ControllerBase
{
    private readonly ICommunityService _communityService;
    private readonly ICommunityMemberService _memberService;
    private readonly ICommunityInviteService _inviteService;
    private readonly IHubContext<SnackaHub> _hubContext;

    public CommunitiesController(
        ICommunityService communityService,
        ICommunityMemberService memberService,
        ICommunityInviteService inviteService,
        IHubContext<SnackaHub> hubContext)
    {
        _communityService = communityService;
        _memberService = memberService;
        _inviteService = inviteService;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CommunityResponse>>> GetCommunities(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var communities = await _communityService.GetUserCommunitiesAsync(userId.Value, cancellationToken);
        return Ok(communities);
    }

    /// <summary>Unread message counts per community of the caller (for badges on community icons).</summary>
    [HttpGet("unread")]
    public async Task<ActionResult<IReadOnlyList<CommunityUnreadResponse>>> GetUnread(
        [FromServices] IChannelService channels,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();
        return Ok(await channels.GetUnreadByCommunityAsync(userId.Value, cancellationToken));
    }

    [HttpGet("discover")]
    public async Task<ActionResult<IEnumerable<CommunityResponse>>> DiscoverCommunities(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var communities = await _communityService.GetDiscoverableCommunitiesAsync(userId.Value, cancellationToken);
        return Ok(communities);
    }

    [HttpGet("{communityId:guid}")]
    public async Task<ActionResult<CommunityResponse>> GetCommunity(Guid communityId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        try
        {
            var community = await _communityService.GetCommunityAsync(communityId, cancellationToken);
            return Ok(community);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<CommunityResponse>> CreateCommunity(
        [FromBody] CreateCommunityRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var community = await _communityService.CreateCommunityAsync(userId.Value, request, cancellationToken);
            return CreatedAtAction(nameof(GetCommunity), new { communityId = community.Id }, community);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{communityId:guid}")]
    public async Task<ActionResult<CommunityResponse>> UpdateCommunity(
        Guid communityId,
        [FromBody] UpdateCommunityRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var community = await _communityService.UpdateCommunityAsync(communityId, userId.Value, request, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("CommunityUpdated", community, cancellationToken);
            return Ok(community);
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

    [HttpDelete("{communityId:guid}")]
    public async Task<IActionResult> DeleteCommunity(Guid communityId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _communityService.DeleteCommunityAsync(communityId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("CommunityDeleted", communityId, cancellationToken);
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

    [HttpGet("{communityId:guid}/members")]
    public async Task<ActionResult<IEnumerable<CommunityMemberResponse>>> GetMembers(
        Guid communityId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        var members = await _memberService.GetMembersAsync(communityId, cancellationToken);
        return Ok(members);
    }

    [HttpGet("{communityId:guid}/members/{memberId:guid}")]
    public async Task<ActionResult<CommunityMemberResponse>> GetMember(
        Guid communityId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        try
        {
            var member = await _memberService.GetMemberAsync(communityId, memberId, cancellationToken);
            return Ok(member);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("{communityId:guid}/members/{memberId:guid}/role")]
    public async Task<ActionResult<CommunityMemberResponse>> UpdateMemberRole(
        Guid communityId,
        Guid memberId,
        [FromBody] UpdateMemberRoleRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var member = await _memberService.UpdateMemberRoleAsync(communityId, memberId, userId.Value, request.Role, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("MemberRoleUpdated", member, cancellationToken);
            return Ok(member);
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

    [HttpPost("{communityId:guid}/join")]
    public async Task<IActionResult> JoinCommunity(Guid communityId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _memberService.JoinCommunityAsync(communityId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("CommunityMemberAdded", communityId, userId.Value, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{communityId:guid}/leave")]
    public async Task<IActionResult> LeaveCommunity(Guid communityId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _memberService.LeaveCommunityAsync(communityId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("CommunityMemberRemoved", communityId, userId.Value, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{communityId:guid}/transfer-ownership")]
    public async Task<IActionResult> TransferOwnership(
        Guid communityId,
        [FromBody] TransferOwnershipRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _memberService.TransferOwnershipAsync(communityId, request.NewOwnerId, userId.Value, cancellationToken);

            // Notify all community members about the ownership change
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("OwnershipTransferred", communityId, request.NewOwnerId, cancellationToken);

            return NoContent();
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

    [HttpPut("{communityId:guid}/members/me/nickname")]
    public async Task<ActionResult<CommunityMemberResponse>> UpdateMyNickname(
        Guid communityId,
        [FromBody] UpdateNicknameRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var member = await _memberService.UpdateNicknameAsync(communityId, userId.Value, request.Nickname, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("MemberNicknameUpdated", member, cancellationToken);
            return Ok(member);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{communityId:guid}/members/{memberId:guid}/nickname")]
    public async Task<ActionResult<CommunityMemberResponse>> UpdateMemberNickname(
        Guid communityId,
        Guid memberId,
        [FromBody] UpdateNicknameRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        // Check if user is admin/owner to change other member's nickname
        try
        {
            var requestingMember = await _memberService.GetMemberAsync(communityId, userId.Value, cancellationToken);
            if (requestingMember.Role != Snacka.Shared.Models.UserRole.Owner &&
                requestingMember.Role != Snacka.Shared.Models.UserRole.Admin)
            {
                return StatusCode(403, new { error = "Only admins and owners can change other members' nicknames." });
            }

            var member = await _memberService.UpdateNicknameAsync(communityId, memberId, request.Nickname, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("MemberNicknameUpdated", member, cancellationToken);
            return Ok(member);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ==================== Community Invites ====================

    /// <summary>
    /// Search for users to invite to the community.
    /// </summary>
    [HttpGet("{communityId:guid}/users/search")]
    public async Task<ActionResult<IEnumerable<UserSearchResult>>> SearchUsersToInvite(
        Guid communityId,
        [FromQuery] string q,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        var users = await _inviteService.SearchUsersToInviteAsync(communityId, q, cancellationToken);
        return Ok(users);
    }

    /// <summary>
    /// Invite a user to join the community.
    /// </summary>
    [HttpPost("{communityId:guid}/invites")]
    public async Task<ActionResult<CommunityInviteResponse>> CreateInvite(
        Guid communityId,
        [FromBody] CreateCommunityInviteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var invite = await _inviteService.CreateInviteAsync(communityId, request.UserId, userId.Value, cancellationToken);

            // Notify the invited user in real-time
            await _hubContext.Clients.User(request.UserId.ToString())
                .SendAsync("CommunityInviteReceived", new CommunityInviteReceivedEvent(
                    invite.Id,
                    invite.CommunityId,
                    invite.CommunityName,
                    invite.CommunityIcon,
                    invite.InvitedById,
                    invite.InvitedByUsername,
                    invite.InvitedByEffectiveDisplayName,
                    invite.CreatedAt
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
    /// Get all invites for a community (admin/owner only).
    /// </summary>
    [HttpGet("{communityId:guid}/invites")]
    public async Task<ActionResult<IEnumerable<CommunityInviteResponse>>> GetCommunityInvites(
        Guid communityId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var member = await _memberService.GetMemberAsync(communityId, userId.Value, cancellationToken);
            if (member.Role != Snacka.Shared.Models.UserRole.Owner &&
                member.Role != Snacka.Shared.Models.UserRole.Admin)
            {
                return StatusCode(403, new { error = "Only admins and owners can view community invites." });
            }

            var invites = await _inviteService.GetInvitesForCommunityAsync(communityId, cancellationToken);
            return Ok(invites);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel a pending invite.
    /// </summary>
    [HttpDelete("{communityId:guid}/invites/{inviteId:guid}")]
    public async Task<IActionResult> CancelInvite(
        Guid communityId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _inviteService.CancelInviteAsync(inviteId, userId.Value, cancellationToken);
            return NoContent();
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
