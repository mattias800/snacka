using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;

namespace Snacka.Server.Services;

/// <summary>
/// Membership checks for channel-scoped data (#14). Every read or write of channel messages,
/// threads, pins and reactions must go through one of these, so no endpoint can forget it.
/// Throws <see cref="UnauthorizedAccessException"/>, which the API maps to 403.
/// </summary>
public static class ChannelAccess
{
    public static async Task EnsureChannelMemberAsync(this SnackaDbContext db, Guid channelId, Guid userId, CancellationToken cancellationToken)
    {
        var communityId = await db.Channels
            .Where(c => c.Id == channelId)
            .Select(c => (Guid?)c.CommunityId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        var isMember = await db.UserCommunities.AnyAsync(uc => uc.CommunityId == communityId && uc.UserId == userId, cancellationToken);
        if (!isMember)
            throw new UnauthorizedAccessException("You are not a member of this channel's community.");
    }

    public static async Task EnsureCommunityMemberAsync(this SnackaDbContext db, Guid communityId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.UserCommunities.AnyAsync(uc => uc.CommunityId == communityId && uc.UserId == userId, cancellationToken))
            throw new UnauthorizedAccessException("You are not a member of this community.");
    }

    public static async Task EnsureMessageMemberAsync(this SnackaDbContext db, Guid messageId, Guid userId, CancellationToken cancellationToken)
    {
        var channelId = await db.Messages
            .Where(m => m.Id == messageId)
            .Select(m => (Guid?)m.ChannelId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        await db.EnsureChannelMemberAsync(channelId, userId, cancellationToken);
    }
}
