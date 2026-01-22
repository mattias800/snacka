using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;

namespace Snacka.Server.Services;

/// <summary>
/// Service for direct message operations. Wraps ConversationService for DM-specific operations.
/// </summary>
public sealed class DirectMessageService : IDirectMessageService
{
    private readonly SnackaDbContext _db;
    private readonly IConversationService _conversationService;

    public DirectMessageService(SnackaDbContext db, IConversationService conversationService)
    {
        _db = db;
        _conversationService = conversationService;
    }

    public async Task<IEnumerable<ConversationSummaryResponse>> GetConversationsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var conversations = await _db.Conversations
            .Include(c => c.Participants)
                .ThenInclude(p => p.User)
            .Include(c => c.Messages.OrderByDescending(m => m.CreatedAt).Take(1))
                .ThenInclude(m => m.Sender)
            .Include(c => c.ReadStates)
            .Where(c => c.Participants.Any(p => p.UserId == userId))
            .OrderByDescending(c => c.Messages.Max(m => (DateTime?)m.CreatedAt) ?? c.CreatedAt)
            .ToListAsync(cancellationToken);

        return conversations.Select(conv =>
        {
            var lastMessage = conv.Messages.FirstOrDefault();
            ConversationMessageResponse? lastMessageResponse = null;

            if (lastMessage != null)
            {
                lastMessageResponse = new ConversationMessageResponse(
                    lastMessage.Id,
                    lastMessage.ConversationId,
                    lastMessage.Content,
                    lastMessage.SenderId,
                    lastMessage.Sender?.Username ?? "Unknown",
                    lastMessage.Sender?.EffectiveDisplayName ?? "Unknown",
                    lastMessage.Sender?.AvatarFileName,
                    lastMessage.CreatedAt,
                    lastMessage.UpdatedAt
                );
            }

            // Calculate unread count
            var readState = conv.ReadStates.FirstOrDefault(rs => rs.UserId == userId);
            var unreadCount = 0;
            if (lastMessage != null && lastMessage.SenderId != userId)
            {
                if (readState?.LastReadMessageId == null || readState.LastReadMessageId != lastMessage.Id)
                {
                    unreadCount = conv.Messages.Count(m =>
                        m.SenderId != userId &&
                        (readState?.LastReadAt == null || m.CreatedAt > readState.LastReadAt));
                }
            }

            // Determine display name and online status
            string displayName;
            bool isOnline;

            if (conv.IsGroup)
            {
                displayName = conv.Name ?? string.Join(", ",
                    conv.Participants
                        .Where(p => p.UserId != userId)
                        .Take(3)
                        .Select(p => p.User?.EffectiveDisplayName ?? "Unknown"));
                isOnline = true; // Groups are always "online"
            }
            else
            {
                var otherParticipant = conv.Participants.FirstOrDefault(p => p.UserId != userId);
                displayName = otherParticipant?.User?.EffectiveDisplayName ?? "Unknown";
                isOnline = otherParticipant?.User?.IsOnline ?? false;
            }

            return new ConversationSummaryResponse(
                conv.Id,
                displayName,
                conv.IconFileName,
                conv.IsGroup,
                isOnline,
                lastMessageResponse,
                unreadCount
            );
        }).ToList();
    }

    public async Task<ConversationResponse> GetOrCreateConversationAsync(
        Guid userId, Guid otherUserId, CancellationToken cancellationToken = default)
    {
        return await _conversationService.GetOrCreateDirectConversationAsync(
            userId, otherUserId, cancellationToken);
    }
}
