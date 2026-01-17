namespace Snacka.Shared.Models;

/// <summary>
/// Represents a persistent notification for a user.
/// Uses eager creation - one row per recipient for simple querying.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The user who receives this notification.
    /// </summary>
    public Guid RecipientId { get; set; }
    public User? Recipient { get; set; }

    /// <summary>
    /// Type of notification (matches NotificationType constants).
    /// Stored as string for extensibility without migrations.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// JSON payload containing type-specific data.
    /// Enables extensibility without schema changes.
    /// </summary>
    public required string PayloadJson { get; set; }

    /// <summary>
    /// Human-readable title for display.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Human-readable description/body text.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// Whether the notification has been read by the recipient.
    /// </summary>
    public bool IsRead { get; set; } = false;

    /// <summary>
    /// Whether the notification has been dismissed (logical delete).
    /// Dismissed notifications are hidden from UI but kept in DB.
    /// </summary>
    public bool IsDismissed { get; set; } = false;

    /// <summary>
    /// Actor who triggered the notification (e.g., who sent the DM).
    /// </summary>
    public Guid? ActorId { get; set; }
    public User? Actor { get; set; }

    /// <summary>
    /// Related community for community-scoped notifications.
    /// </summary>
    public Guid? CommunityId { get; set; }
    public Community? Community { get; set; }

    /// <summary>
    /// Related channel for channel-scoped notifications.
    /// </summary>
    public Guid? ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When IsRead was set to true.
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// When IsDismissed was set to true.
    /// </summary>
    public DateTime? DismissedAt { get; set; }
}
