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
            .Where(m => m.ChannelId == channelId)
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

        _db.Messages.Remove(message);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static MessageResponse ToMessageResponse(Message m, Guid? currentUserId = null) => new(
        m.Id,
        m.Content,
        m.AuthorId,
        m.Author?.Username ?? "Unknown",
        m.Author?.Avatar,
        m.ChannelId,
        m.CreatedAt,
        m.UpdatedAt,
        m.IsEdited,
        m.ReplyToId,
        m.ReplyTo is not null ? new ReplyPreview(
            m.ReplyTo.Id,
            m.ReplyTo.Content.Length > 100 ? m.ReplyTo.Content[..100] + "..." : m.ReplyTo.Content,
            m.ReplyTo.AuthorId,
            m.ReplyTo.Author?.Username ?? "Unknown"
        ) : null,
        m.Reactions.Count > 0 ? m.Reactions
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionSummary(
                g.Key,
                g.Count(),
                currentUserId.HasValue && g.Any(r => r.UserId == currentUserId.Value),
                g.Select(r => new ReactionUser(r.UserId, r.User?.Username ?? "Unknown")).ToList()
            ))
            .ToList() : null
    );
}
