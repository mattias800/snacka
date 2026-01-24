using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Snacka.Client.Services;

namespace Snacka.Client.Stores;

/// <summary>
/// Immutable state representing a reaction on a message.
/// </summary>
public record ReactionState(
    string Emoji,
    int Count,
    bool HasReacted,
    ImmutableList<ReactionUserState> Users
);

/// <summary>
/// Immutable state representing a user who reacted.
/// </summary>
public record ReactionUserState(
    Guid UserId,
    string Username,
    string EffectiveDisplayName
);

/// <summary>
/// Immutable state representing an attachment.
/// </summary>
public record AttachmentState(
    Guid Id,
    string FileName,
    string ContentType,
    long FileSize,
    bool IsImage,
    bool IsAudio,
    string Url
);

/// <summary>
/// Immutable state representing a reply preview.
/// </summary>
public record ReplyPreviewState(
    Guid Id,
    string Content,
    Guid AuthorId,
    string AuthorUsername,
    string AuthorEffectiveDisplayName
);

/// <summary>
/// Immutable state representing a message.
/// </summary>
public record MessageState(
    Guid Id,
    Guid ChannelId,
    string Content,
    Guid AuthorId,
    string AuthorUsername,
    string AuthorEffectiveDisplayName,
    string? AuthorAvatar,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsEdited,
    bool IsPinned,
    DateTime? PinnedAt,
    string? PinnedByUsername,
    string? PinnedByEffectiveDisplayName,
    Guid? ReplyToId,
    ReplyPreviewState? ReplyTo,
    ImmutableList<ReactionState> Reactions,
    ImmutableList<AttachmentState> Attachments,
    Guid? ThreadParentMessageId,
    int ReplyCount,
    DateTime? LastReplyAt
);

/// <summary>
/// Store managing message state for channels.
/// </summary>
public interface IMessageStore : IStore<MessageState, Guid>
{
    /// <summary>
    /// Currently active channel ID for message display.
    /// </summary>
    IObservable<Guid?> CurrentChannelId { get; }

    /// <summary>
    /// Messages for the current channel, sorted by creation time.
    /// </summary>
    IObservable<IReadOnlyCollection<MessageState>> CurrentChannelMessages { get; }

    /// <summary>
    /// Pinned messages for the current channel.
    /// </summary>
    IObservable<IReadOnlyCollection<MessageState>> PinnedMessages { get; }

    /// <summary>
    /// Gets a message by ID synchronously.
    /// </summary>
    MessageState? GetMessage(Guid messageId);

    /// <summary>
    /// Gets messages for a specific channel synchronously.
    /// </summary>
    IReadOnlyList<MessageState> GetMessagesForChannel(Guid channelId);

    /// <summary>
    /// Gets the current channel ID synchronously.
    /// </summary>
    Guid? GetCurrentChannelId();

    // Actions
    void SetCurrentChannel(Guid? channelId);
    void SetMessages(Guid channelId, IEnumerable<MessageResponse> messages);
    void AddMessage(MessageResponse message);
    void UpdateMessage(MessageResponse message);
    void DeleteMessage(Guid messageId);
    void UpdatePinState(Guid messageId, bool isPinned, DateTime? pinnedAt, string? pinnedByUsername, string? pinnedByEffectiveDisplayName);
    void AddReaction(Guid messageId, string emoji, Guid userId, string username, string effectiveDisplayName);
    void RemoveReaction(Guid messageId, string emoji, Guid userId);
    void UpdateThreadMetadata(Guid messageId, int replyCount, DateTime? lastReplyAt);
    void ClearChannel(Guid channelId);
    void Clear();
}

public sealed class MessageStore : IMessageStore, IDisposable
{
    private readonly SourceCache<MessageState, Guid> _messageCache;
    private readonly BehaviorSubject<Guid?> _currentChannelId;
    private readonly IDisposable _cleanUp;

    public MessageStore()
    {
        _messageCache = new SourceCache<MessageState, Guid>(m => m.Id);
        _currentChannelId = new BehaviorSubject<Guid?>(null);

        _cleanUp = _messageCache.Connect().Subscribe();
    }

    public IObservable<IChangeSet<MessageState, Guid>> Connect() => _messageCache.Connect();

    public IObservable<IReadOnlyCollection<MessageState>> Items =>
        _messageCache.Connect()
            .QueryWhenChanged(cache => cache.Items.ToList().AsReadOnly() as IReadOnlyCollection<MessageState>);

    public IObservable<Guid?> CurrentChannelId => _currentChannelId.AsObservable();

    public IObservable<IReadOnlyCollection<MessageState>> CurrentChannelMessages =>
        _currentChannelId
            .CombineLatest(
                _messageCache.Connect().QueryWhenChanged(),
                (channelId, cache) =>
                {
                    if (channelId is null)
                        return Array.Empty<MessageState>() as IReadOnlyCollection<MessageState>;

                    return cache.Items
                        .Where(m => m.ChannelId == channelId.Value && m.ThreadParentMessageId is null)
                        .OrderBy(m => m.CreatedAt)
                        .ToList()
                        .AsReadOnly() as IReadOnlyCollection<MessageState>;
                });

    public IObservable<IReadOnlyCollection<MessageState>> PinnedMessages =>
        _currentChannelId
            .CombineLatest(
                _messageCache.Connect().QueryWhenChanged(),
                (channelId, cache) =>
                {
                    if (channelId is null)
                        return Array.Empty<MessageState>() as IReadOnlyCollection<MessageState>;

                    return cache.Items
                        .Where(m => m.ChannelId == channelId.Value && m.IsPinned)
                        .OrderByDescending(m => m.PinnedAt)
                        .ToList()
                        .AsReadOnly() as IReadOnlyCollection<MessageState>;
                });

    public MessageState? GetMessage(Guid messageId)
    {
        var lookup = _messageCache.Lookup(messageId);
        return lookup.HasValue ? lookup.Value : null;
    }

    public IReadOnlyList<MessageState> GetMessagesForChannel(Guid channelId)
    {
        return _messageCache.Items
            .Where(m => m.ChannelId == channelId)
            .OrderBy(m => m.CreatedAt)
            .ToList()
            .AsReadOnly();
    }

    public Guid? GetCurrentChannelId() => _currentChannelId.Value;

    public void SetCurrentChannel(Guid? channelId)
    {
        _currentChannelId.OnNext(channelId);
    }

    public void SetMessages(Guid channelId, IEnumerable<MessageResponse> messages)
    {
        _messageCache.Edit(cache =>
        {
            // Remove existing messages for this channel
            var toRemove = cache.Items.Where(m => m.ChannelId == channelId).Select(m => m.Id).ToList();
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }

            // Add new messages
            foreach (var message in messages)
            {
                cache.AddOrUpdate(MapToState(message));
            }
        });
    }

    public void AddMessage(MessageResponse message)
    {
        _messageCache.AddOrUpdate(MapToState(message));
    }

    public void UpdateMessage(MessageResponse message)
    {
        _messageCache.AddOrUpdate(MapToState(message));
    }

    public void DeleteMessage(Guid messageId)
    {
        _messageCache.Remove(messageId);
    }

    public void UpdatePinState(Guid messageId, bool isPinned, DateTime? pinnedAt, string? pinnedByUsername, string? pinnedByEffectiveDisplayName)
    {
        var existing = _messageCache.Lookup(messageId);
        if (existing.HasValue)
        {
            _messageCache.AddOrUpdate(existing.Value with
            {
                IsPinned = isPinned,
                PinnedAt = pinnedAt,
                PinnedByUsername = pinnedByUsername,
                PinnedByEffectiveDisplayName = pinnedByEffectiveDisplayName
            });
        }
    }

    public void AddReaction(Guid messageId, string emoji, Guid userId, string username, string effectiveDisplayName)
    {
        var existing = _messageCache.Lookup(messageId);
        if (!existing.HasValue) return;

        var message = existing.Value;
        var reactions = message.Reactions.ToBuilder();

        var reactionIndex = reactions.FindIndex(r => r.Emoji == emoji);
        if (reactionIndex >= 0)
        {
            var reaction = reactions[reactionIndex];
            // Check if user already reacted
            if (reaction.Users.Any(u => u.UserId == userId)) return;

            var newUsers = reaction.Users.Add(new ReactionUserState(userId, username, effectiveDisplayName));
            reactions[reactionIndex] = reaction with
            {
                Count = reaction.Count + 1,
                HasReacted = true, // User who added reaction always has reacted
                Users = newUsers
            };
        }
        else
        {
            reactions.Add(new ReactionState(
                emoji,
                1,
                true,
                ImmutableList.Create(new ReactionUserState(userId, username, effectiveDisplayName))
            ));
        }

        _messageCache.AddOrUpdate(message with { Reactions = reactions.ToImmutable() });
    }

    public void RemoveReaction(Guid messageId, string emoji, Guid userId)
    {
        var existing = _messageCache.Lookup(messageId);
        if (!existing.HasValue) return;

        var message = existing.Value;
        var reactions = message.Reactions.ToBuilder();

        var reactionIndex = reactions.FindIndex(r => r.Emoji == emoji);
        if (reactionIndex >= 0)
        {
            var reaction = reactions[reactionIndex];
            var newUsers = reaction.Users.RemoveAll(u => u.UserId == userId);

            if (newUsers.Count == 0)
            {
                reactions.RemoveAt(reactionIndex);
            }
            else
            {
                reactions[reactionIndex] = reaction with
                {
                    Count = newUsers.Count,
                    HasReacted = newUsers.Any(u => u.UserId == userId),
                    Users = newUsers
                };
            }

            _messageCache.AddOrUpdate(message with { Reactions = reactions.ToImmutable() });
        }
    }

    public void UpdateThreadMetadata(Guid messageId, int replyCount, DateTime? lastReplyAt)
    {
        var existing = _messageCache.Lookup(messageId);
        if (existing.HasValue)
        {
            _messageCache.AddOrUpdate(existing.Value with
            {
                ReplyCount = replyCount,
                LastReplyAt = lastReplyAt
            });
        }
    }

    public void ClearChannel(Guid channelId)
    {
        var toRemove = _messageCache.Items.Where(m => m.ChannelId == channelId).Select(m => m.Id).ToList();
        _messageCache.Edit(cache =>
        {
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }
        });
    }

    public void Clear()
    {
        _messageCache.Clear();
        _currentChannelId.OnNext(null);
    }

    private static MessageState MapToState(MessageResponse response) =>
        new MessageState(
            Id: response.Id,
            ChannelId: response.ChannelId,
            Content: response.Content,
            AuthorId: response.AuthorId,
            AuthorUsername: response.AuthorUsername,
            AuthorEffectiveDisplayName: response.AuthorEffectiveDisplayName,
            AuthorAvatar: response.AuthorAvatar,
            CreatedAt: response.CreatedAt,
            UpdatedAt: response.UpdatedAt,
            IsEdited: response.IsEdited,
            IsPinned: response.IsPinned,
            PinnedAt: response.PinnedAt,
            PinnedByUsername: response.PinnedByUsername,
            PinnedByEffectiveDisplayName: response.PinnedByEffectiveDisplayName,
            ReplyToId: response.ReplyToId,
            ReplyTo: response.ReplyTo is not null
                ? new ReplyPreviewState(
                    response.ReplyTo.Id,
                    response.ReplyTo.Content,
                    response.ReplyTo.AuthorId,
                    response.ReplyTo.AuthorUsername,
                    response.ReplyTo.AuthorEffectiveDisplayName)
                : null,
            Reactions: response.Reactions?.Select(r => new ReactionState(
                r.Emoji,
                r.Count,
                r.HasReacted,
                r.Users.Select(u => new ReactionUserState(u.UserId, u.Username, u.EffectiveDisplayName)).ToImmutableList()
            )).ToImmutableList() ?? ImmutableList<ReactionState>.Empty,
            Attachments: response.Attachments?.Select(a => new AttachmentState(
                a.Id, a.FileName, a.ContentType, a.FileSize, a.IsImage, a.IsAudio, a.Url
            )).ToImmutableList() ?? ImmutableList<AttachmentState>.Empty,
            ThreadParentMessageId: response.ThreadParentMessageId,
            ReplyCount: response.ReplyCount,
            LastReplyAt: response.LastReplyAt
        );

    public void Dispose()
    {
        _cleanUp.Dispose();
        _messageCache.Dispose();
        _currentChannelId.Dispose();
    }
}
