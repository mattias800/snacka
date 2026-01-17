namespace Snacka.Server.DTOs;

/// <summary>
/// Response DTO for a notification.
/// </summary>
public record NotificationResponse(
    Guid Id,
    string Type,
    string Title,
    string Description,
    string PayloadJson,
    bool IsRead,
    bool IsDismissed,
    Guid? ActorId,
    string? ActorUsername,
    string? ActorEffectiveDisplayName,
    Guid? CommunityId,
    string? CommunityName,
    Guid? ChannelId,
    string? ChannelName,
    DateTime CreatedAt,
    DateTime? ReadAt
);

/// <summary>
/// Response DTO for unread notification count.
/// </summary>
public record NotificationCountResponse(int UnreadCount);
