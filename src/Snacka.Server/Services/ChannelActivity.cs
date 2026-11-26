using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;

namespace Snacka.Server.Services;

/// <summary>
/// Tells the whole community about a new message ("ChannelActivity"), whichever channel they have
/// open: messages themselves only go to the channel's group. Clients use it for unread counts and
/// to notify people who were mentioned or replied to. Every member can read every channel, so the
/// message itself is included.
/// </summary>
public interface IChannelActivityService
{
    Task MessagePostedAsync(MessageResponse message, CancellationToken cancellationToken = default);
}

public sealed partial class ChannelActivityService : IChannelActivityService
{
    private readonly SnackaDbContext _db;
    private readonly IHubContext<SnackaHub> _hub;

    public ChannelActivityService(SnackaDbContext db, IHubContext<SnackaHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task MessagePostedAsync(MessageResponse message, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels
            .Where(c => c.Id == message.ChannelId)
            .Select(c => new { c.CommunityId, c.Name })
            .FirstAsync(cancellationToken);
        var mentioned = await MentionedMembersAsync(channel.CommunityId, message, cancellationToken);
        var threadParentAuthorId = message.ThreadParentMessageId is { } parentId
            ? await _db.Messages.Where(m => m.Id == parentId).Select(m => (Guid?)m.AuthorId).FirstOrDefaultAsync(cancellationToken)
            : null;

        await _hub.Clients.Group($"community:{channel.CommunityId}").SendAsync("ChannelActivity",
            new ChannelActivityEvent(channel.CommunityId, message.ChannelId, channel.Name, message, mentioned, threadParentAuthorId),
            cancellationToken);
    }

    /// <summary>Members named as <c>@username</c> (any case), or everyone for <c>@everyone</c>; never the author.</summary>
    private async Task<List<Guid>> MentionedMembersAsync(Guid communityId, MessageResponse message, CancellationToken cancellationToken)
    {
        var names = MentionedNames(message.Content);
        if (names.Count == 0) return [];
        var members = _db.UserCommunities.Where(uc => uc.CommunityId == communityId && uc.UserId != message.AuthorId);
        if (!names.Contains("everyone"))
            members = members.Where(uc => names.Contains(uc.User!.Username.ToLower()));
        return await members.Select(uc => uc.UserId).ToListAsync(cancellationToken);
    }

    /// <summary>The lower-cased names after each <c>@</c> that starts a word.</summary>
    public static HashSet<string> MentionedNames(string content) =>
        MentionPattern().Matches(content).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();

    [GeneratedRegex(@"(?<![\w@])@([\p{L}\p{N}_.\-]*[\p{L}\p{N}_])")]
    private static partial Regex MentionPattern();
}
