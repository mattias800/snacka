using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly SnackaDbContext _db;
    private readonly IServerInviteService _inviteService;

    public AdminController(SnackaDbContext db, IServerInviteService inviteService)
    {
        _db = db;
        _inviteService = inviteService;
    }

    // GET /api/admin/invites - List all invites
    [HttpGet("invites")]
    public async Task<ActionResult<IEnumerable<ServerInviteResponse>>> GetInvites(CancellationToken cancellationToken)
    {
        if (!await IsServerAdminAsync(cancellationToken))
            return Forbid();

        var invites = await _inviteService.GetAllInvitesAsync(cancellationToken);
        var response = invites.Select(i => new ServerInviteResponse(
            i.Id,
            i.Code,
            i.MaxUses,
            i.CurrentUses,
            i.ExpiresAt,
            i.IsRevoked,
            i.CreatedBy?.Username,
            i.CreatedAt
        ));

        return Ok(response);
    }

    // POST /api/admin/invites - Create new invite
    [HttpPost("invites")]
    public async Task<ActionResult<ServerInviteResponse>> CreateInvite(
        [FromBody] CreateInviteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        if (!await IsServerAdminAsync(cancellationToken))
            return Forbid();

        var invite = await _inviteService.CreateInviteAsync(
            userId.Value,
            request.MaxUses,
            request.ExpiresAt,
            cancellationToken
        );

        // Reload to get CreatedBy navigation property
        var user = await _db.Users.FindAsync([userId.Value], cancellationToken);

        return Ok(new ServerInviteResponse(
            invite.Id,
            invite.Code,
            invite.MaxUses,
            invite.CurrentUses,
            invite.ExpiresAt,
            invite.IsRevoked,
            user?.Username,
            invite.CreatedAt
        ));
    }

    // DELETE /api/admin/invites/{id} - Revoke invite
    [HttpDelete("invites/{id:guid}")]
    public async Task<ActionResult> RevokeInvite(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            await _inviteService.RevokeInviteAsync(id, userId.Value, cancellationToken);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // GET /api/admin/users - List all users
    [HttpGet("users")]
    public async Task<ActionResult<IEnumerable<AdminUserResponse>>> GetUsers(CancellationToken cancellationToken)
    {
        if (!await IsServerAdminAsync(cancellationToken))
            return Forbid();

        var users = await _db.Users
            .Include(u => u.InvitedBy)
            .OrderBy(u => u.CreatedAt)
            .ToListAsync(cancellationToken);

        var response = users.Select(u => new AdminUserResponse(
            u.Id,
            u.Username,
            u.Email,
            u.IsServerAdmin,
            u.IsOnline,
            u.CreatedAt,
            u.InvitedBy?.Username
        ));

        return Ok(response);
    }

    // PUT /api/admin/users/{id}/admin - Set user admin status
    [HttpPut("users/{id:guid}/admin")]
    public async Task<ActionResult<AdminUserResponse>> SetUserAdminStatus(
        Guid id,
        [FromBody] SetAdminStatusRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null)
            return Unauthorized();

        if (!await IsServerAdminAsync(cancellationToken))
            return Forbid();

        // Prevent removing your own admin status
        if (id == currentUserId && !request.IsAdmin)
            return BadRequest(new { error = "You cannot remove your own admin status." });

        var user = await _db.Users
            .Include(u => u.InvitedBy)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
            return NotFound(new { error = "User not found." });

        user.IsServerAdmin = request.IsAdmin;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new AdminUserResponse(
            user.Id,
            user.Username,
            user.Email,
            user.IsServerAdmin,
            user.IsOnline,
            user.CreatedAt,
            user.InvitedBy?.Username
        ));
    }

    // DELETE /api/admin/users/{id} - Delete user account
    [HttpDelete("users/{id:guid}")]
    public async Task<ActionResult> DeleteUser(Guid id, CancellationToken cancellationToken)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null)
            return Unauthorized();

        if (!await IsServerAdminAsync(cancellationToken))
            return Forbid();

        // Prevent deleting yourself
        if (id == currentUserId)
            return BadRequest(new { error = "You cannot delete your own account from the admin panel. Use account settings instead." });

        var user = await _db.Users.FindAsync([id], cancellationToken);
        if (user is null)
            return NotFound(new { error = "User not found." });

        // Communities cascade with their owner: hand each one with other members to its
        // highest-ranking, longest-standing member instead of deleting it for everyone.
        var owned = await _db.Communities.Where(c => c.OwnerId == id).ToListAsync(cancellationToken);
        foreach (var community in owned)
        {
            var successor = await _db.UserCommunities
                .Where(uc => uc.CommunityId == community.Id && uc.UserId != id)
                .OrderBy(uc => uc.Role)
                .ThenBy(uc => uc.JoinedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (successor is null) continue; // nobody else in it: it goes with its owner
            community.OwnerId = successor.UserId;
            successor.Role = UserRole.Owner;
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private async Task<bool> IsServerAdminAsync(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return false;

        var user = await _db.Users.FindAsync([userId.Value], cancellationToken);
        return user?.IsServerAdmin ?? false;
    }
}
