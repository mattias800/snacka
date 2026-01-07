using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Miscord.Server.DTOs;
using Miscord.Server.Hubs;
using Miscord.Server.Services;

namespace Miscord.Server.Controllers;

[ApiController]
[Route("api/communities")]
[Authorize]
public class CommunitiesController : ControllerBase
{
    private readonly ICommunityService _communityService;
    private readonly ICommunityMemberService _memberService;
    private readonly IHubContext<MiscordHub> _hubContext;

    public CommunitiesController(
        ICommunityService communityService,
        ICommunityMemberService memberService,
        IHubContext<MiscordHub> hubContext)
    {
        _communityService = communityService;
        _memberService = memberService;
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
        }
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
