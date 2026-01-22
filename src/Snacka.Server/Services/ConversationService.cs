using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

public sealed class ConversationService : IConversationService
{
    private readonly SnackaDbContext _db;

    public ConversationService(SnackaDbContext db) => _db = db;

    public async Task<ConversationResponse> CreateConversationAsync(
        Guid creatorId,
        List<Guid> participantIds,
        string? name,
        CancellationToken cancellationToken = default)
    {
        // Ensure creator is in participants list
        if (!participantIds.Contains(creatorId))
        {
            participantIds = [creatorId, .. participantIds];
        }

        // Validate all participants exist
        var users = await _db.Users
            .Where(u => participantIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        if (users.Count != participantIds.Count)
        {
            throw new InvalidOperationException("One or more participants not found.");
        }

        // For 1:1 conversations, check if one already exists
        if (participantIds.Count == 2)
        {
            var existingConversation = await FindDirectConversationAsync(
                participantIds[0], participantIds[1], cancellationToken);

            if (existingConversation != null)
            {
                return await ToResponseAsync(existingConversation, creatorId, cancellationToken);
            }
        }

        var isGroup = participantIds.Count > 2;
        var conversation = new Conversation
        {
            Name = isGroup ? name : null,
            IsGroup = isGroup,
            CreatedById = creatorId
        };

        _db.Conversations.Add(conversation);

        // Add participants
        foreach (var userId in participantIds)
        {
            var participant = new ConversationParticipant
            {
                ConversationId = conversation.Id,
                UserId = userId,
                AddedById = userId == creatorId ? null : creatorId
            };
            _db.ConversationParticipants.Add(participant);

            // Create read state for each participant
            var readState = new ConversationReadState
            {
                ConversationId = conversation.Id,
                UserId = userId
            };
            _db.ConversationReadStates.Add(readState);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Reload with participants
        return await GetConversationAsync(conversation.Id, creatorId, cancellationToken)
            ?? throw new InvalidOperationException("Failed to create conversation.");
    }

    public async Task<ConversationResponse?> GetConversationAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Participants)
                .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        if (conversation == null)
            return null;

        // Check if user is a participant
        if (!conversation.Participants.Any(p => p.UserId == userId))
            return null;

        return await ToResponseAsync(conversation, userId, cancellationToken);
    }

    public async Task<ConversationResponse> GetOrCreateDirectConversationAsync(
        Guid userId1,
        Guid userId2,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindDirectConversationAsync(userId1, userId2, cancellationToken);
        if (existing != null)
        {
            return await ToResponseAsync(existing, userId1, cancellationToken);
        }

        return await CreateConversationAsync(userId1, [userId2], null, cancellationToken);
    }

    public async Task<List<ConversationResponse>> GetUserConversationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var conversationIds = await _db.ConversationParticipants
            .Where(cp => cp.UserId == userId)
            .Select(cp => cp.ConversationId)
            .ToListAsync(cancellationToken);

        var conversations = await _db.Conversations
            .Include(c => c.Participants)
                .ThenInclude(p => p.User)
            .Where(c => conversationIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        var responses = new List<ConversationResponse>();
        foreach (var conversation in conversations)
        {
            responses.Add(await ToResponseAsync(conversation, userId, cancellationToken));
        }

        // Order by last message date
        return responses
            .OrderByDescending(c => c.LastMessage?.CreatedAt ?? c.CreatedAt)
            .ToList();
    }

    public async Task<List<Guid>> GetUserConversationIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ConversationParticipants
            .Where(cp => cp.UserId == userId)
            .Select(cp => cp.ConversationId)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ConversationMessageResponse>> GetMessagesAsync(
        Guid conversationId,
        Guid userId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        // Verify user is a participant
        if (!await IsParticipantAsync(conversationId, userId, cancellationToken))
        {
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
        }

        var messages = await _db.DirectMessages
            .Include(dm => dm.Sender)
            .Where(dm => dm.ConversationId == conversationId)
            .OrderByDescending(dm => dm.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return messages.Select(ToMessageResponse).Reverse().ToList();
    }

    public async Task<ConversationMessageResponse> SendMessageAsync(
        Guid conversationId,
        Guid authorId,
        string content,
        CancellationToken cancellationToken = default)
    {
        // Verify user is a participant
        if (!await IsParticipantAsync(conversationId, authorId, cancellationToken))
        {
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
        }

        var author = await _db.Users.FindAsync([authorId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var message = new DirectMessage
        {
            ConversationId = conversationId,
            Content = content,
            SenderId = authorId
        };

        _db.DirectMessages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        message.Sender = author;

        return ToMessageResponse(message);
    }

    public async Task<ConversationMessageResponse> UpdateMessageAsync(
        Guid conversationId,
        Guid messageId,
        Guid userId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var message = await _db.DirectMessages
            .Include(dm => dm.Sender)
            .FirstOrDefaultAsync(dm => dm.Id == messageId && dm.ConversationId == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.SenderId != userId)
        {
            throw new UnauthorizedAccessException("You can only edit your own messages.");
        }

        message.Content = content;
        message.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return ToMessageResponse(message);
    }

    public async Task DeleteMessageAsync(
        Guid conversationId,
        Guid messageId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var message = await _db.DirectMessages
            .FirstOrDefaultAsync(dm => dm.Id == messageId && dm.ConversationId == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.SenderId != userId)
        {
            throw new UnauthorizedAccessException("You can only delete your own messages.");
        }

        _db.DirectMessages.Remove(message);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ParticipantInfo> AddParticipantAsync(
        Guid conversationId,
        Guid userId,
        Guid addedById,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Conversation not found.");

        if (!conversation.IsGroup)
        {
            throw new InvalidOperationException("Cannot add participants to a 1:1 conversation.");
        }

        // Verify adder is a participant
        if (!await IsParticipantAsync(conversationId, addedById, cancellationToken))
        {
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
        }

        // Check if already a participant
        var existingParticipant = await _db.ConversationParticipants
            .FirstOrDefaultAsync(cp => cp.ConversationId == conversationId && cp.UserId == userId, cancellationToken);

        if (existingParticipant != null)
        {
            throw new InvalidOperationException("User is already a participant.");
        }

        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var participant = new ConversationParticipant
        {
            ConversationId = conversationId,
            UserId = userId,
            AddedById = addedById
        };

        _db.ConversationParticipants.Add(participant);

        // Create read state
        var readState = new ConversationReadState
        {
            ConversationId = conversationId,
            UserId = userId
        };
        _db.ConversationReadStates.Add(readState);

        await _db.SaveChangesAsync(cancellationToken);

        return ToParticipantInfo(user, participant.JoinedAt);
    }

    public async Task RemoveParticipantAsync(
        Guid conversationId,
        Guid userId,
        Guid removedById,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Conversation not found.");

        if (!conversation.IsGroup)
        {
            throw new InvalidOperationException("Cannot remove participants from a 1:1 conversation.");
        }

        // Verify remover is a participant
        if (!conversation.Participants.Any(p => p.UserId == removedById))
        {
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
        }

        var participant = conversation.Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant == null)
        {
            throw new InvalidOperationException("User is not a participant.");
        }

        _db.ConversationParticipants.Remove(participant);

        // Remove read state
        var readState = await _db.ConversationReadStates
            .FirstOrDefaultAsync(crs => crs.ConversationId == conversationId && crs.UserId == userId, cancellationToken);

        if (readState != null)
        {
            _db.ConversationReadStates.Remove(readState);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // If no participants left, delete the conversation
        if (conversation.Participants.Count <= 1)
        {
            _db.Conversations.Remove(conversation);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<ConversationResponse> UpdateConversationAsync(
        Guid conversationId,
        Guid userId,
        string? name,
        string? iconFileName,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Participants)
                .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Conversation not found.");

        if (!conversation.IsGroup)
        {
            throw new InvalidOperationException("Cannot update 1:1 conversations.");
        }

        if (!conversation.Participants.Any(p => p.UserId == userId))
        {
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
        }

        conversation.Name = name;
        if (iconFileName != null)
        {
            conversation.IconFileName = iconFileName;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return await ToResponseAsync(conversation, userId, cancellationToken);
    }

    public async Task MarkAsReadAsync(
        Guid conversationId,
        Guid userId,
        Guid? messageId = null,
        CancellationToken cancellationToken = default)
    {
        var readState = await _db.ConversationReadStates
            .FirstOrDefaultAsync(crs => crs.ConversationId == conversationId && crs.UserId == userId, cancellationToken);

        if (readState == null)
        {
            // Create read state if it doesn't exist (for migrated conversations)
            readState = new ConversationReadState
            {
                ConversationId = conversationId,
                UserId = userId
            };
            _db.ConversationReadStates.Add(readState);
        }

        if (messageId.HasValue)
        {
            readState.LastReadMessageId = messageId;
        }
        else
        {
            // Mark as read up to the latest message
            var latestMessage = await _db.DirectMessages
                .Where(dm => dm.ConversationId == conversationId)
                .OrderByDescending(dm => dm.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            readState.LastReadMessageId = latestMessage?.Id;
        }

        readState.LastReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var readState = await _db.ConversationReadStates
            .FirstOrDefaultAsync(crs => crs.ConversationId == conversationId && crs.UserId == userId, cancellationToken);

        if (readState?.LastReadMessageId == null)
        {
            // If no read state, all messages (except user's own) are unread
            return await _db.DirectMessages
                .CountAsync(dm => dm.ConversationId == conversationId && dm.SenderId != userId, cancellationToken);
        }

        // Get the timestamp of the last read message
        var lastReadMessage = await _db.DirectMessages
            .FirstOrDefaultAsync(dm => dm.Id == readState.LastReadMessageId, cancellationToken);

        if (lastReadMessage == null)
        {
            return 0;
        }

        // Count messages after the last read message (excluding user's own)
        return await _db.DirectMessages
            .CountAsync(dm =>
                dm.ConversationId == conversationId &&
                dm.SenderId != userId &&
                dm.CreatedAt > lastReadMessage.CreatedAt,
                cancellationToken);
    }

    public async Task<bool> IsParticipantAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ConversationParticipants
            .AnyAsync(cp => cp.ConversationId == conversationId && cp.UserId == userId, cancellationToken);
    }

    private async Task<Conversation?> FindDirectConversationAsync(
        Guid userId1,
        Guid userId2,
        CancellationToken cancellationToken)
    {
        // Find a non-group conversation where both users are participants
        var conversationId = await _db.ConversationParticipants
            .Where(cp => cp.UserId == userId1)
            .Select(cp => cp.ConversationId)
            .Intersect(
                _db.ConversationParticipants
                    .Where(cp => cp.UserId == userId2)
                    .Select(cp => cp.ConversationId)
            )
            .Join(
                _db.Conversations.Where(c => !c.IsGroup),
                id => id,
                c => c.Id,
                (id, c) => c.Id
            )
            .FirstOrDefaultAsync(cancellationToken);

        if (conversationId == Guid.Empty)
            return null;

        return await _db.Conversations
            .Include(c => c.Participants)
                .ThenInclude(p => p.User)
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
    }

    private async Task<ConversationResponse> ToResponseAsync(
        Conversation conversation,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var participants = conversation.Participants
            .Select(p => ToParticipantInfo(p.User!, p.JoinedAt))
            .ToList();

        var lastMessage = await _db.DirectMessages
            .Include(dm => dm.Sender)
            .Where(dm => dm.ConversationId == conversation.Id)
            .OrderByDescending(dm => dm.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var unreadCount = await GetUnreadCountAsync(conversation.Id, userId, cancellationToken);

        return new ConversationResponse(
            conversation.Id,
            conversation.Name,
            conversation.IconFileName,
            conversation.IsGroup,
            conversation.CreatedAt,
            participants,
            lastMessage != null ? ToMessageResponse(lastMessage) : null,
            unreadCount
        );
    }

    private static ParticipantInfo ToParticipantInfo(User user, DateTime joinedAt)
    {
        return new ParticipantInfo(
            user.Id,
            user.Username,
            user.EffectiveDisplayName,
            user.AvatarFileName,
            user.IsOnline,
            joinedAt
        );
    }

    private static ConversationMessageResponse ToMessageResponse(DirectMessage dm)
    {
        var senderUsername = dm.Sender?.Username ?? "Unknown";
        var senderDisplayName = dm.Sender?.EffectiveDisplayName ?? senderUsername;

        return new ConversationMessageResponse(
            dm.Id,
            dm.ConversationId,
            dm.Content,
            dm.SenderId,
            senderUsername,
            senderDisplayName,
            dm.Sender?.AvatarFileName,
            dm.CreatedAt,
            dm.UpdatedAt
        );
    }
}
