using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public sealed class MessageService : IMessageService
{
    private readonly MiscordDbContext _db;

    public MessageService(MiscordDbContext db) => _db = db;

    public async Task<IEnumerable<MessageResponse>> GetMessagesAsync(Guid channelId, Guid currentUserId, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        var messages = await _db.Messages
            .Include(m => m.Author)
            .Include(m => m.ReplyTo)
            .ThenInclude(r => r!.Author)
            .Include(m => m.Reactions)
            .ThenInclude(r => r.User)
            .Include(m => m.PinnedBy)
            .Include(m => m.Attachments)
            .Where(m => m.ChannelId == channelId && m.ThreadParentMessageId == null) // Exclude thread messages
            .OrderByDescending(m => m.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return messages.Select(m => ToMessageResponse(m, currentUserId)).Reverse();
    }

    public async Task<MessageResponse> SendMessageAsync(Guid channelId, Guid authorId, string content, Guid? replyToId = null, CancellationToken cancellationToken = default)
    {
        var author = await _db.Users.FindAsync([authorId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var channel = await _db.Channels.FindAsync([channelId], cancellationToken)
            ?? throw new InvalidOperationException("Channel not found.");

        Message? replyTo = null;
        if (replyToId.HasValue)
        {
            replyTo = await _db.Messages
                .Include(m => m.Author)
                .FirstOrDefaultAsync(m => m.Id == replyToId.Value && m.ChannelId == channelId, cancellationToken);
            // Silently ignore invalid reply references
        }

        var message = new Message
        {
            Content = content,
            AuthorId = authorId,
            ChannelId = channelId,
            ReplyToId = replyTo?.Id
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        message.Author = author;
        message.ReplyTo = replyTo;
        return ToMessageResponse(message);
    }

    public async Task<MessageResponse> UpdateMessageAsync(Guid messageId, Guid userId, string content, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages
            .Include(m => m.Author)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.AuthorId != userId)
            throw new UnauthorizedAccessException("You can only edit your own messages.");

        message.Content = content;
        message.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return ToMessageResponse(message);
    }

    public async Task DeleteMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        // Allow deletion by author or community admin/owner
        if (message.AuthorId != userId)
        {
            var userCommunity = await _db.UserCommunities
                .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == message.Channel!.CommunityId, cancellationToken);

            if (userCommunity is null || userCommunity.Role == UserRole.Member)
                throw new UnauthorizedAccessException("You cannot delete this message.");
        }

        // If this is a thread reply, decrement the parent's reply count
        if (message.ThreadParentMessageId.HasValue)
        {
            var parentMessage = await _db.Messages
                .FirstOrDefaultAsync(m => m.Id == message.ThreadParentMessageId.Value, cancellationToken);

            if (parentMessage is not null)
            {
                parentMessage.ReplyCount = Math.Max(0, parentMessage.ReplyCount - 1);
                // Update LastReplyAt to the most recent remaining reply, or null if no replies left
                if (parentMessage.ReplyCount == 0)
                {
                    parentMessage.LastReplyAt = null;
                }
                else
                {
                    var lastReply = await _db.Messages
                        .Where(m => m.ThreadParentMessageId == parentMessage.Id && m.Id != messageId)
                        .OrderByDescending(m => m.CreatedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                    parentMessage.LastReplyAt = lastReply?.CreatedAt;
                }
            }
        }

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MessagePinnedEvent> PinMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        // Check if user can pin (author or admin/owner)
        var canPin = message.AuthorId == userId;
        if (!canPin)
        {
            var userCommunity = await _db.UserCommunities
                .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == message.Channel!.CommunityId, cancellationToken);

            canPin = userCommunity is not null && userCommunity.Role != UserRole.Member;
        }

        if (!canPin)
            throw new UnauthorizedAccessException("You cannot pin this message.");

        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        message.IsPinned = true;
        message.PinnedAt = DateTime.UtcNow;
        message.PinnedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken);

        return new MessagePinnedEvent(
            message.Id,
            message.ChannelId,
            true,
            message.PinnedAt,
            userId,
            user.Username,
            user.EffectiveDisplayName
        );
    }

    public async Task<MessagePinnedEvent> UnpinMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages
            .Include(m => m.Channel)
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        if (!message.IsPinned)
            throw new InvalidOperationException("Message is not pinned.");

        // Check if user can unpin (author, pinner, or admin/owner)
        var canUnpin = message.AuthorId == userId || message.PinnedByUserId == userId;
        if (!canUnpin)
        {
            var userCommunity = await _db.UserCommunities
                .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == message.Channel!.CommunityId, cancellationToken);

            canUnpin = userCommunity is not null && userCommunity.Role != UserRole.Member;
        }

        if (!canUnpin)
            throw new UnauthorizedAccessException("You cannot unpin this message.");

        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        message.IsPinned = false;
        message.PinnedAt = null;
        message.PinnedByUserId = null;
        await _db.SaveChangesAsync(cancellationToken);

        return new MessagePinnedEvent(
            message.Id,
            message.ChannelId,
            false,
            null,
            userId,
            user.Username,
            user.EffectiveDisplayName
        );
    }

    public async Task<IEnumerable<MessageResponse>> GetPinnedMessagesAsync(Guid channelId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var messages = await _db.Messages
            .Include(m => m.Author)
            .Include(m => m.ReplyTo)
            .ThenInclude(r => r!.Author)
            .Include(m => m.Reactions)
            .ThenInclude(r => r.User)
            .Include(m => m.PinnedBy)
            .Include(m => m.Attachments)
            .Where(m => m.ChannelId == channelId && m.IsPinned && m.ThreadParentMessageId == null) // Exclude thread messages
            .OrderByDescending(m => m.PinnedAt)
            .ToListAsync(cancellationToken);

        return messages.Select(m => ToMessageResponse(m, currentUserId));
    }

    public async Task<ThreadResponse> GetThreadAsync(Guid parentMessageId, Guid currentUserId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var parentMessage = await _db.Messages
            .Include(m => m.Author)
            .Include(m => m.ReplyTo)
            .ThenInclude(r => r!.Author)
            .Include(m => m.Reactions)
            .ThenInclude(r => r.User)
            .Include(m => m.PinnedBy)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == parentMessageId, cancellationToken)
            ?? throw new InvalidOperationException("Parent message not found.");

        // Get total count of replies
        var totalReplyCount = await _db.Messages
            .CountAsync(m => m.ThreadParentMessageId == parentMessageId, cancellationToken);

        // Get paginated replies
        var replies = await _db.Messages
            .Include(m => m.Author)
            .Include(m => m.ReplyTo)
            .ThenInclude(r => r!.Author)
            .Include(m => m.Reactions)
            .ThenInclude(r => r.User)
            .Include(m => m.Attachments)
            .Where(m => m.ThreadParentMessageId == parentMessageId)
            .OrderBy(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new ThreadResponse(
            ToMessageResponse(parentMessage, currentUserId),
            replies.Select(m => ToMessageResponse(m, currentUserId)).ToList(),
            totalReplyCount,
            page,
            pageSize
        );
    }

    public async Task<MessageResponse> CreateThreadReplyAsync(Guid parentMessageId, Guid authorId, string content, Guid? replyToId = null, CancellationToken cancellationToken = default)
    {
        var author = await _db.Users.FindAsync([authorId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var parentMessage = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == parentMessageId, cancellationToken)
            ?? throw new InvalidOperationException("Parent message not found.");

        // Thread messages can't be nested - if parent is already in a thread, use its parent
        if (parentMessage.ThreadParentMessageId.HasValue)
            throw new InvalidOperationException("Cannot create a thread from a thread reply. Reply to the original thread parent instead.");

        Message? replyTo = null;
        if (replyToId.HasValue)
        {
            // ReplyTo can be the parent message or any message in the same thread
            replyTo = await _db.Messages
                .Include(m => m.Author)
                .FirstOrDefaultAsync(m => m.Id == replyToId.Value &&
                    (m.Id == parentMessageId || m.ThreadParentMessageId == parentMessageId), cancellationToken);
            // Silently ignore invalid reply references
        }

        var message = new Message
        {
            Content = content,
            AuthorId = authorId,
            ChannelId = parentMessage.ChannelId,
            ThreadParentMessageId = parentMessageId,
            ReplyToId = replyTo?.Id
        };

        // Update parent message's thread metadata
        parentMessage.ReplyCount++;
        parentMessage.LastReplyAt = DateTime.UtcNow;

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        message.Author = author;
        message.ReplyTo = replyTo;
        return ToMessageResponse(message);
    }

    private static MessageResponse ToMessageResponse(Message m, Guid? currentUserId = null)
    {
        var authorUsername = m.Author?.Username ?? "Unknown";
        var authorEffectiveDisplayName = m.Author?.EffectiveDisplayName ?? authorUsername;
        var replyAuthorUsername = m.ReplyTo?.Author?.Username ?? "Unknown";
        var replyAuthorEffectiveDisplayName = m.ReplyTo?.Author?.EffectiveDisplayName ?? replyAuthorUsername;
        var pinnedByUsername = m.PinnedBy?.Username;
        var pinnedByEffectiveDisplayName = m.PinnedBy?.EffectiveDisplayName ?? pinnedByUsername;

        return new MessageResponse(
            m.Id,
            m.Content,
            m.AuthorId,
            authorUsername,
            authorEffectiveDisplayName,
            m.Author?.AvatarFileName,
            m.ChannelId,
            m.CreatedAt,
            m.UpdatedAt,
            m.IsEdited,
            m.ReplyToId,
            m.ReplyTo is not null ? new ReplyPreview(
                m.ReplyTo.Id,
                m.ReplyTo.Content.Length > 100 ? m.ReplyTo.Content[..100] + "..." : m.ReplyTo.Content,
                m.ReplyTo.AuthorId,
                replyAuthorUsername,
                replyAuthorEffectiveDisplayName
            ) : null,
            m.Reactions.Count > 0 ? m.Reactions
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionSummary(
                    g.Key,
                    g.Count(),
                    currentUserId.HasValue && g.Any(r => r.UserId == currentUserId.Value),
                    g.Select(r => new ReactionUser(
                        r.UserId,
                        r.User?.Username ?? "Unknown",
                        r.User?.EffectiveDisplayName ?? r.User?.Username ?? "Unknown"
                    )).ToList()
                ))
                .ToList() : null,
            m.IsPinned,
            m.PinnedAt,
            pinnedByUsername,
            pinnedByEffectiveDisplayName,
            m.Attachments.Count > 0 ? m.Attachments
                .Select(a => new AttachmentResponse(
                    a.Id,
                    a.FileName,
                    a.ContentType,
                    a.FileSize,
                    a.IsImage,
                    a.IsAudio,
                    $"/api/attachments/{a.StoredFileName}"
                ))
                .ToList() : null,
            // Thread fields
            m.ThreadParentMessageId,
            m.ReplyCount,
            m.LastReplyAt
        );
    }
}
