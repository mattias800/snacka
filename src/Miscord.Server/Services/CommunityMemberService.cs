using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public sealed class CommunityMemberService : ICommunityMemberService
{
    private readonly MiscordDbContext _db;

    public CommunityMemberService(MiscordDbContext db) => _db = db;

    public async Task<IEnumerable<CommunityMemberResponse>> GetMembersAsync(Guid communityId, CancellationToken cancellationToken = default)
    {
        var members = await _db.UserCommunities
            .Include(uc => uc.User)
            .Where(uc => uc.CommunityId == communityId)
            .ToListAsync(cancellationToken);

        return members.Select(uc => new CommunityMemberResponse(
            uc.UserId,
            uc.User?.Username ?? "Unknown",
            uc.User?.Avatar,
            uc.User?.IsOnline ?? false,
            uc.Role,
            uc.JoinedAt
        ));
    }

    public async Task<CommunityMemberResponse> GetMemberAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        var userCommunity = await _db.UserCommunities
            .Include(uc => uc.User)
            .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == communityId, cancellationToken)
            ?? throw new InvalidOperationException("User is not a member of this community.");

        return new CommunityMemberResponse(
            userCommunity.UserId,
            userCommunity.User?.Username ?? "Unknown",
            userCommunity.User?.Avatar,
            userCommunity.User?.IsOnline ?? false,
            userCommunity.Role,
            userCommunity.JoinedAt
        );
    }

    public async Task JoinCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        var exists = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId && uc.CommunityId == communityId, cancellationToken);

        if (exists)
            throw new InvalidOperationException("User is already a member of this community.");

        var userCommunity = new UserCommunity
        {
            UserId = userId,
            CommunityId = communityId,
            Role = UserRole.Member
        };

        _db.UserCommunities.Add(userCommunity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task LeaveCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        var community = await _db.Communities.FindAsync([communityId], cancellationToken)
            ?? throw new InvalidOperationException("Community not found.");

        if (community.OwnerId == userId)
            throw new InvalidOperationException("The owner cannot leave the community. Transfer ownership or delete the community.");

        var userCommunity = await _db.UserCommunities
            .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == communityId, cancellationToken)
            ?? throw new InvalidOperationException("User is not a member of this community.");

        _db.UserCommunities.Remove(userCommunity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsMemberAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId && uc.CommunityId == communityId, cancellationToken);
    }

    public async Task<CommunityMemberResponse> UpdateMemberRoleAsync(Guid communityId, Guid targetUserId, Guid requestingUserId, UserRole newRole, CancellationToken cancellationToken = default)
    {
        // Get the requesting user's membership
        var requestingMembership = await _db.UserCommunities
            .FirstOrDefaultAsync(uc => uc.UserId == requestingUserId && uc.CommunityId == communityId, cancellationToken)
            ?? throw new UnauthorizedAccessException("You are not a member of this community.");

        // Only owners can change roles
        if (requestingMembership.Role != UserRole.Owner)
            throw new UnauthorizedAccessException("Only the community owner can change member roles.");

        // Get the target user's membership
        var targetMembership = await _db.UserCommunities
            .Include(uc => uc.User)
            .FirstOrDefaultAsync(uc => uc.UserId == targetUserId && uc.CommunityId == communityId, cancellationToken)
            ?? throw new InvalidOperationException("Target user is not a member of this community.");

        // Cannot change owner's role
        if (targetMembership.Role == UserRole.Owner)
            throw new InvalidOperationException("Cannot change the owner's role.");

        // Cannot promote someone to owner
        if (newRole == UserRole.Owner)
            throw new InvalidOperationException("Cannot promote a member to owner. Use transfer ownership instead.");

        targetMembership.Role = newRole;
        await _db.SaveChangesAsync(cancellationToken);

        return new CommunityMemberResponse(
            targetMembership.UserId,
            targetMembership.User?.Username ?? "Unknown",
            targetMembership.User?.Avatar,
            targetMembership.User?.IsOnline ?? false,
            targetMembership.Role,
            targetMembership.JoinedAt
        );
    }

    public async Task TransferOwnershipAsync(Guid communityId, Guid newOwnerId, Guid currentOwnerId, CancellationToken cancellationToken = default)
    {
        // Get the community
        var community = await _db.Communities.FindAsync([communityId], cancellationToken)
            ?? throw new InvalidOperationException("Community not found.");

        // Verify the current user is the owner
        if (community.OwnerId != currentOwnerId)
            throw new UnauthorizedAccessException("Only the current owner can transfer ownership.");

        // Can't transfer to yourself
        if (newOwnerId == currentOwnerId)
            throw new InvalidOperationException("Cannot transfer ownership to yourself.");

        // Get the new owner's membership
        var newOwnerMembership = await _db.UserCommunities
            .FirstOrDefaultAsync(uc => uc.UserId == newOwnerId && uc.CommunityId == communityId, cancellationToken)
            ?? throw new InvalidOperationException("New owner must be a member of the community.");

        // Get the current owner's membership
        var currentOwnerMembership = await _db.UserCommunities
            .FirstOrDefaultAsync(uc => uc.UserId == currentOwnerId && uc.CommunityId == communityId, cancellationToken)
            ?? throw new InvalidOperationException("Current owner membership not found.");

        // Update community owner
        community.OwnerId = newOwnerId;
        community.UpdatedAt = DateTime.UtcNow;

        // Update roles
        newOwnerMembership.Role = UserRole.Owner;
        currentOwnerMembership.Role = UserRole.Admin; // Former owner becomes admin

        await _db.SaveChangesAsync(cancellationToken);
    }
}
