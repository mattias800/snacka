using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;

namespace Snacka.Server.Services;

/// <summary>
/// Tells the whole community that a channel has a new message, so clients can show unread
/// counts for channels they don't have open (messages themselves only go to the channel's group).
/// Carries ids only, no content.
/// </summary>
public static class ChannelActivity
{
    public static async Task NotifyAsync(IHubContext<SnackaHub> hub, SnackaDbContext db, Guid channelId, Guid messageId, Guid authorId, CancellationToken cancellationToken)
    {
        var communityId = await db.Channels.Where(c => c.Id == channelId).Select(c => c.CommunityId).FirstAsync(cancellationToken);
        await hub.Clients.Group($"community:{communityId}")
            .SendAsync("ChannelActivity", new ChannelActivityEvent(communityId, channelId, messageId, authorId), cancellationToken);
    }
}
