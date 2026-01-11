using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

public sealed class CommunityService : ICommunityService
{
    private readonly SnackaDbContext _db;

    public CommunityService(SnackaDbContext db) => _db = db;

    public async Task<IEnumerable<CommunityResponse>> GetUserCommunitiesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var communities = await _db.UserCommunities
            .Include(uc => uc.Community)
            .ThenInclude(c => c!.Owner)
            .Include(uc => uc.Community)
            .ThenInclude(c => c!.UserCommunities)
            .Where(uc => uc.UserId == userId)
            .Select(uc => uc.Community!)
            .ToListAsync(cancellationToken);

        return communities.Select(ToCommunityResponse);
    }

    public async Task<IEnumerable<CommunityResponse>> GetDiscoverableCommunitiesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Get all communities that the user is NOT a member of (for discovery/joining)
        var userCommunityIds = await _db.UserCommunities
            .Where(uc => uc.UserId == userId)
            .Select(uc => uc.CommunityId)
            .ToListAsync(cancellationToken);

        var communities = await _db.Communities
            .Include(c => c.Owner)
            .Include(c => c.UserCommunities)
            .Where(c => !userCommunityIds.Contains(c.Id))
            .OrderByDescending(c => c.UserCommunities.Count) // Show popular communities first
            .Take(50) // Limit for performance
            .ToListAsync(cancellationToken);

        return communities.Select(ToCommunityResponse);
    }

    public async Task<CommunityResponse> GetCommunityAsync(Guid communityId, CancellationToken cancellationToken = default)
    {
        var community = await _db.Communities
            .Include(c => c.Owner)
            .Include(c => c.UserCommunities)
            .FirstOrDefaultAsync(c => c.Id == communityId, cancellationToken)
            ?? throw new InvalidOperationException("Community not found.");

        return ToCommunityResponse(community);
    }

    public async Task<CommunityResponse> CreateCommunityAsync(Guid ownerId, CreateCommunityRequest request, CancellationToken cancellationToken = default)
    {
        var owner = await _db.Users.FindAsync([ownerId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var community = new Community
        {
            Name = request.Name,
            Description = request.Description,
            OwnerId = ownerId
        };

        _db.Communities.Add(community);

        // Add owner as first member
        var userCommunity = new UserCommunity
        {
            UserId = ownerId,
            CommunityId = community.Id,
            Role = UserRole.Owner
        };
        _db.UserCommunities.Add(userCommunity);

        // Create default text channel
        var generalTextChannel = new Channel
        {
            Name = "general",
            CommunityId = community.Id,
            Type = ChannelType.Text,
            Position = 0
        };
        _db.Channels.Add(generalTextChannel);

        // Create default voice channel
        var generalVoiceChannel = new Channel
        {
            Name = "general",
            CommunityId = community.Id,
            Type = ChannelType.Voice,
            Position = 1
        };
        _db.Channels.Add(generalVoiceChannel);

        await _db.SaveChangesAsync(cancellationToken);

        // Reload community with relationships for accurate count
        var createdCommunity = await _db.Communities
            .Include(c => c.Owner)
            .Include(c => c.UserCommunities)
            .FirstAsync(c => c.Id == community.Id, cancellationToken);

        return ToCommunityResponse(createdCommunity);
    }

    public async Task<CommunityResponse> UpdateCommunityAsync(Guid communityId, Guid userId, UpdateCommunityRequest request, CancellationToken cancellationToken = default)
    {
        var community = await _db.Communities
            .Include(c => c.Owner)
            .Include(c => c.UserCommunities)
            .FirstOrDefaultAsync(c => c.Id == communityId, cancellationToken)
            ?? throw new InvalidOperationException("Community not found.");

        await EnsureCanManageCommunityAsync(communityId, userId, cancellationToken);

        if (request.Name is not null)
            community.Name = request.Name;
        if (request.Description is not null)
            community.Description = request.Description;
        if (request.Icon is not null)
            community.Icon = request.Icon;

        community.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return ToCommunityResponse(community);
    }

    public async Task DeleteCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default)
    {
        var community = await _db.Communities.FindAsync([communityId], cancellationToken)
            ?? throw new InvalidOperationException("Community not found.");

        if (community.OwnerId != userId)
            throw new UnauthorizedAccessException("Only the owner can delete the community.");

        _db.Communities.Remove(community);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureCanManageCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken)
    {
        var userCommunity = await _db.UserCommunities
            .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == communityId, cancellationToken);

        if (userCommunity is null)
            throw new UnauthorizedAccessException("User is not a member of this community.");

        if (userCommunity.Role != UserRole.Owner && userCommunity.Role != UserRole.Admin)
            throw new UnauthorizedAccessException("You don't have permission to manage this community.");
    }

    private static CommunityResponse ToCommunityResponse(Community c)
    {
        var ownerUsername = c.Owner?.Username ?? "Unknown";
        var ownerEffectiveDisplayName = c.Owner?.EffectiveDisplayName ?? ownerUsername;

        return new CommunityResponse(
            c.Id,
            c.Name,
            c.Description,
            c.Icon,
            c.OwnerId,
            ownerUsername,
            ownerEffectiveDisplayName,
            c.CreatedAt,
            c.UserCommunities.Count
        );
    }
}
