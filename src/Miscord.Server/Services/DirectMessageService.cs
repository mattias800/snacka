using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public sealed class DirectMessageService : IDirectMessageService
{
    private readonly MiscordDbContext _db;

    public DirectMessageService(MiscordDbContext db) => _db = db;

    public async Task<IEnumerable<DirectMessageResponse>> GetConversationAsync(
        Guid currentUserId, Guid otherUserId, int skip = 0, int take = 50,
        CancellationToken cancellationToken = default)
    {
        var messages = await _db.DirectMessages
            .Include(dm => dm.Sender)
            .Include(dm => dm.Recipient)
            .Where(dm =>
                (dm.SenderId == currentUserId && dm.RecipientId == otherUserId) ||
                (dm.SenderId == otherUserId && dm.RecipientId == currentUserId))
            .OrderByDescending(dm => dm.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return messages.Select(ToResponse).Reverse();
    }

    public async Task<DirectMessageResponse> SendMessageAsync(
        Guid senderId, Guid recipientId, string content,
        CancellationToken cancellationToken = default)
    {
        var sender = await _db.Users.FindAsync([senderId], cancellationToken)
            ?? throw new InvalidOperationException("Sender not found.");

        var recipient = await _db.Users.FindAsync([recipientId], cancellationToken)
            ?? throw new InvalidOperationException("Recipient not found.");

        var message = new DirectMessage
        {
            Content = content,
            SenderId = senderId,
            RecipientId = recipientId,
            IsRead = false
        };

        _db.DirectMessages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        message.Sender = sender;
        message.Recipient = recipient;

        return ToResponse(message);
    }

    public async Task<DirectMessageResponse> UpdateMessageAsync(
        Guid messageId, Guid userId, string content,
        CancellationToken cancellationToken = default)
    {
        var message = await _db.DirectMessages
            .Include(dm => dm.Sender)
            .Include(dm => dm.Recipient)
            .FirstOrDefaultAsync(dm => dm.Id == messageId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.SenderId != userId)
            throw new UnauthorizedAccessException("You can only edit your own messages.");

        message.Content = content;
        await _db.SaveChangesAsync(cancellationToken);

        return ToResponse(message);
    }

    public async Task DeleteMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default)
    {
        var message = await _db.DirectMessages.FindAsync([messageId], cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.SenderId != userId)
            throw new UnauthorizedAccessException("You can only delete your own messages.");

        _db.DirectMessages.Remove(message);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<ConversationSummary>> GetConversationsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var messages = await _db.DirectMessages
            .Include(dm => dm.Sender)
            .Include(dm => dm.Recipient)
            .Where(dm => dm.SenderId == userId || dm.RecipientId == userId)
            .ToListAsync(cancellationToken);

        var conversations = messages
            .GroupBy(dm => dm.SenderId == userId ? dm.RecipientId : dm.SenderId)
            .Select(g =>
            {
                var otherUserId = g.Key;
                var lastMessage = g.OrderByDescending(m => m.CreatedAt).First();
                var otherUser = lastMessage.SenderId == userId ? lastMessage.Recipient : lastMessage.Sender;
                var unreadCount = g.Count(m => m.RecipientId == userId && !m.IsRead);

                var otherUsername = otherUser?.Username ?? "Unknown";
                var otherEffectiveDisplayName = otherUser?.EffectiveDisplayName ?? otherUsername;

                return new ConversationSummary(
                    otherUserId,
                    otherUsername,
                    otherEffectiveDisplayName,
                    otherUser?.AvatarFileName,
                    otherUser?.IsOnline ?? false,
                    ToResponse(lastMessage),
                    unreadCount
                );
            })
            .OrderByDescending(c => c.LastMessage?.CreatedAt)
            .ToList();

        return conversations;
    }

    public async Task MarkAsReadAsync(Guid currentUserId, Guid otherUserId, CancellationToken cancellationToken = default)
    {
        var unreadMessages = await _db.DirectMessages
            .Where(dm => dm.SenderId == otherUserId && dm.RecipientId == currentUserId && !dm.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var message in unreadMessages)
        {
            message.IsRead = true;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static DirectMessageResponse ToResponse(DirectMessage dm)
    {
        var senderUsername = dm.Sender?.Username ?? "Unknown";
        var senderEffectiveDisplayName = dm.Sender?.EffectiveDisplayName ?? senderUsername;
        var recipientUsername = dm.Recipient?.Username ?? "Unknown";
        var recipientEffectiveDisplayName = dm.Recipient?.EffectiveDisplayName ?? recipientUsername;

        return new DirectMessageResponse(
            dm.Id,
            dm.Content,
            dm.SenderId,
            senderUsername,
            senderEffectiveDisplayName,
            dm.RecipientId,
            recipientUsername,
            recipientEffectiveDisplayName,
            dm.CreatedAt,
            dm.IsRead
        );
    }
}
