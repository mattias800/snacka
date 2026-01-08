using System.ComponentModel.DataAnnotations;
using Miscord.Shared.Models;

namespace Miscord.Server.DTOs;

public record CommunityResponse(
    Guid Id,
    string Name,
    string? Description,
    string? Icon,
    Guid OwnerId,
    string OwnerUsername,
    DateTime CreatedAt,
    int MemberCount
);

public record CreateCommunityRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [StringLength(1000)] string? Description
);

public record UpdateCommunityRequest(
    [StringLength(100, MinimumLength = 1)] string? Name,
    [StringLength(1000)] string? Description,
    string? Icon
);

public record ChannelResponse(
    Guid Id,
    string Name,
    string? Topic,
    Guid CommunityId,
    ChannelType Type,
    int Position,
    DateTime CreatedAt,
    int UnreadCount = 0
);

public record CreateChannelRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    [StringLength(1000)] string? Topic,
    ChannelType Type = ChannelType.Text
);

public record UpdateChannelRequest(
    [StringLength(100, MinimumLength = 1)] string? Name,
    [StringLength(1000)] string? Topic,
    int? Position
);

public record MessageResponse(
    Guid Id,
    string Content,
    Guid AuthorId,
    string AuthorUsername,
    string? AuthorAvatar,
    Guid ChannelId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsEdited,
    Guid? ReplyToId = null,
    ReplyPreview? ReplyTo = null,
    List<ReactionSummary>? Reactions = null,
    bool IsPinned = false,
    DateTime? PinnedAt = null,
    string? PinnedByUsername = null,
    List<AttachmentResponse>? Attachments = null
);

/// <summary>
/// Response containing file attachment metadata
/// </summary>
public record AttachmentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long FileSize,
    bool IsImage,
    string Url
);

/// <summary>
/// Preview of the message being replied to
/// </summary>
public record ReplyPreview(
    Guid Id,
    string Content,
    Guid AuthorId,
    string AuthorUsername
);

public record SendMessageRequest(
    [Required, StringLength(2000, MinimumLength = 1)] string Content,
    Guid? ReplyToId = null
);

public record UpdateMessageRequest([Required, StringLength(2000, MinimumLength = 1)] string Content);

public record CommunityMemberResponse(
    Guid UserId,
    string Username,
    string? Avatar,
    bool IsOnline,
    UserRole Role,
    DateTime JoinedAt
);

public record UpdateMemberRoleRequest([Required] UserRole Role);

public record TransferOwnershipRequest([Required] Guid NewOwnerId);

// SignalR Event DTOs - used for type-safe event broadcasting
public record ChannelDeletedEvent(Guid ChannelId);

public record MessageDeletedEvent(Guid ChannelId, Guid MessageId);

public record UserOfflineEvent(Guid UserId);

// Typing indicator events
public record TypingEvent(Guid ChannelId, Guid UserId, string Username);

public record DMTypingEvent(Guid UserId, string Username);

// Reaction DTOs
/// <summary>
/// Summary of reactions for a specific emoji on a message
/// </summary>
public record ReactionSummary(
    string Emoji,
    int Count,
    bool HasReacted,
    List<ReactionUser> Users
);

/// <summary>
/// User who reacted with a specific emoji
/// </summary>
public record ReactionUser(Guid UserId, string Username);

/// <summary>
/// Request to add a reaction to a message
/// </summary>
public record AddReactionRequest([Required, StringLength(10, MinimumLength = 1)] string Emoji);

/// <summary>
/// SignalR event when a reaction is added or removed
/// </summary>
public record ReactionUpdatedEvent(
    Guid MessageId,
    Guid ChannelId,
    string Emoji,
    int Count,
    Guid UserId,
    string Username,
    bool Added
);

/// <summary>
/// SignalR event when a message is pinned or unpinned
/// </summary>
public record MessagePinnedEvent(
    Guid MessageId,
    Guid ChannelId,
    bool IsPinned,
    DateTime? PinnedAt,
    Guid? PinnedByUserId,
    string? PinnedByUsername
);
