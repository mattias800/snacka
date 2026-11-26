using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;

namespace Snacka.Server.Services;

/// <summary>Who gets to hear about a user's presence and profile changes (#20).</summary>
public static class Audience
{
    /// <summary>
    /// The user ids (as SignalR user identifiers) of everyone who shares a community or a
    /// conversation with <paramref name="userId"/>, not including the user.
    /// </summary>
    public static async Task<List<string>> AcquaintancesOfAsync(this SnackaDbContext db, Guid userId, CancellationToken cancellationToken = default)
    {
        var communityIds = db.UserCommunities.Where(uc => uc.UserId == userId).Select(uc => uc.CommunityId);
        var conversationIds = db.ConversationParticipants.Where(p => p.UserId == userId).Select(p => p.ConversationId);
        var ids = await db.UserCommunities.Where(uc => communityIds.Contains(uc.CommunityId)).Select(uc => uc.UserId)
            .Union(db.ConversationParticipants.Where(p => conversationIds.Contains(p.ConversationId)).Select(p => p.UserId))
            .Where(id => id != userId)
            .ToListAsync(cancellationToken);
        return ids.Select(id => id.ToString()).ToList();
    }
}
