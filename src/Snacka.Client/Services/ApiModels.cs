using Snacka.Shared.Models;

namespace Snacka.Client.Services;

// Auth Models
public record RegisterRequest(string Username, string Email, string Password, string InviteCode);
public record LoginRequest(string Email, string Password);
public record RefreshTokenRequest(string RefreshToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record AuthResponse(
    Guid UserId,
    string Username,
    string Email,
    bool IsServerAdmin,
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt
);

public record UserProfileResponse(
    Guid Id,
    string Username,
    string? DisplayName,
    string EffectiveDisplayName,
    string Email,
    string? Avatar,
    string? Status,
    bool IsOnline,
    bool IsServerAdmin,
    DateTime CreatedAt
);

public record UpdateProfileRequest(string? Username, string? DisplayName, string? Status);

public record AvatarUploadResponse(string? Avatar, bool Success);

// Admin Models
public record CreateInviteRequest(int MaxUses = 0, DateTime? ExpiresAt = null);

public record ServerInviteResponse(
    Guid Id,
    string Code,
    int MaxUses,
    int CurrentUses,
    DateTime? ExpiresAt,
    bool IsRevoked,
    string? CreatedByUsername,
    DateTime CreatedAt
);

public record AdminUserResponse(
    Guid Id,
    string Username,
    string Email,
    bool IsServerAdmin,
    bool IsOnline,
    DateTime CreatedAt,
    string? InvitedByUsername
);

public record SetAdminStatusRequest(bool IsAdmin);

// Community Models
public record CommunityResponse(
    Guid Id,
    string Name,
    string? Description,
    string? Icon,
    Guid OwnerId,
    string OwnerUsername,
    string OwnerEffectiveDisplayName,
    DateTime CreatedAt,
    int MemberCount
);

public record CreateCommunityRequest(string Name, string? Description);

// Channel Models
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

public record CreateChannelRequest(string Name, string? Topic, ChannelType Type = ChannelType.Text);
public record UpdateChannelRequest(string? Name, string? Topic);

// Message Models
public record MessageResponse(
    Guid Id,
    string Content,
    Guid AuthorId,
    string AuthorUsername,
    string AuthorEffectiveDisplayName,
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
    string? PinnedByEffectiveDisplayName = null,
    List<AttachmentResponse>? Attachments = null,
    // Thread fields
    Guid? ThreadParentMessageId = null,  // If set, this message is part of a thread
    int ReplyCount = 0,                   // Number of thread replies (for thread parent messages)
    DateTime? LastReplyAt = null          // Timestamp of most recent reply (for thread parent messages)
);

/// <summary>
/// Response containing a thread with its parent message and replies
/// </summary>
public record ThreadResponse(
    MessageResponse ParentMessage,
    List<MessageResponse> Replies,
    int TotalReplyCount,
    int Page,
    int PageSize
);

/// <summary>
/// A search result item containing a message and its channel context
/// </summary>
public record MessageSearchResult(
    MessageResponse Message,
    string ChannelName
);

/// <summary>
/// Response from message search containing results and metadata
/// </summary>
public record MessageSearchResponse(
    List<MessageSearchResult> Results,
    int TotalCount,
    string Query
);

/// <summary>
/// File attachment metadata
/// </summary>
public record AttachmentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long FileSize,
    bool IsImage,
    bool IsAudio,
    string Url
);

/// <summary>
/// File to be uploaded with a message
/// </summary>
public class FileAttachment : IDisposable
{
    public required string FileName { get; init; }
    public required Stream Stream { get; init; }
    public required string ContentType { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Preview of the message being replied to
/// </summary>
public record ReplyPreview(
    Guid Id,
    string Content,
    Guid AuthorId,
    string AuthorUsername,
    string AuthorEffectiveDisplayName
);

public record SendMessageRequest(string Content, Guid? ReplyToId = null);
public record UpdateMessageRequest(string Content);

// Reaction Models
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
public record ReactionUser(Guid UserId, string Username, string EffectiveDisplayName);

/// <summary>
/// Request to add a reaction to a message
/// </summary>
public record AddReactionRequest(string Emoji);

/// <summary>
/// Event when a reaction is added or removed
/// </summary>
public record ReactionUpdatedEvent(
    Guid MessageId,
    Guid ChannelId,
    string Emoji,
    int Count,
    Guid UserId,
    string Username,
    string EffectiveDisplayName,
    bool Added
);

/// <summary>
/// Event when a message is pinned or unpinned
/// </summary>
public record MessagePinnedEvent(
    Guid MessageId,
    Guid ChannelId,
    bool IsPinned,
    DateTime? PinnedAt,
    Guid? PinnedByUserId,
    string? PinnedByUsername,
    string? PinnedByEffectiveDisplayName
);

// Member Models
public record CommunityMemberResponse(
    Guid UserId,
    string Username,
    string? DisplayName,
    string? DisplayNameOverride,
    string EffectiveDisplayName,
    string? Avatar,
    bool IsOnline,
    UserRole Role,
    DateTime JoinedAt
);

public record UpdateMemberRoleRequest(UserRole Role);
public record UpdateNicknameRequest(string? Nickname);
public record TransferOwnershipRequest(Guid NewOwnerId);

// Direct Message Models
public record DirectMessageResponse(
    Guid Id,
    string Content,
    Guid SenderId,
    string SenderUsername,
    string SenderEffectiveDisplayName,
    Guid RecipientId,
    string RecipientUsername,
    string RecipientEffectiveDisplayName,
    DateTime CreatedAt,
    bool IsRead
);

public record SendDirectMessageRequest(string Content);

public record ConversationSummary(
    Guid UserId,
    string Username,
    string EffectiveDisplayName,
    string? Avatar,
    bool IsOnline,
    DirectMessageResponse? LastMessage,
    int UnreadCount
);

// Error
public record ApiError(string Error);

// SignalR Event DTOs - must match server-side records
public record ChannelDeletedEvent(Guid ChannelId);
public record ChannelsReorderedEvent(Guid CommunityId, List<ChannelResponse> Channels);
public record MessageDeletedEvent(Guid ChannelId, Guid MessageId);
public record UserPresenceEvent(Guid UserId, string Username, bool IsOnline);
public record DirectMessageDeletedEvent(Guid MessageId);
public record CommunityMemberAddedEvent(Guid CommunityId, Guid UserId);
public record CommunityMemberRemovedEvent(Guid CommunityId, Guid UserId);

// Voice Channel Models
public record VoiceParticipantResponse(
    Guid Id,
    Guid UserId,
    string Username,
    Guid ChannelId,
    bool IsMuted,
    bool IsDeafened,
    bool IsServerMuted,
    bool IsServerDeafened,
    bool IsScreenSharing,
    bool ScreenShareHasAudio,
    bool IsCameraOn,
    DateTime JoinedAt
);

public record VoiceStateUpdate(
    bool? IsMuted = null,
    bool? IsDeafened = null,
    bool? IsScreenSharing = null,
    bool? ScreenShareHasAudio = null,
    bool? IsCameraOn = null
);

// Voice Channel Events
public record VoiceParticipantJoinedEvent(Guid ChannelId, VoiceParticipantResponse Participant);
public record VoiceParticipantLeftEvent(Guid ChannelId, Guid UserId);
public record VoiceStateChangedEvent(Guid ChannelId, Guid UserId, VoiceStateUpdate State);
public record SpeakingStateChangedEvent(Guid ChannelId, Guid UserId, bool IsSpeaking);

// Admin voice action events
public record ServerVoiceStateChangedEvent(
    Guid ChannelId,
    Guid TargetUserId,
    bool? IsServerMuted,
    bool? IsServerDeafened,
    Guid AdminUserId,
    string AdminUsername
);

public record UserMovedEvent(
    Guid UserId,
    string Username,
    Guid FromChannelId,
    Guid ToChannelId,
    Guid AdminUserId,
    string AdminUsername
);

// WebRTC Signaling Events (P2P - legacy)
public record WebRtcOfferEvent(Guid FromUserId, string Sdp);
public record WebRtcAnswerEvent(Guid FromUserId, string Sdp);
public record IceCandidateEvent(Guid FromUserId, string Candidate, string? SdpMid, int? SdpMLineIndex);

// SFU Signaling Events
public record SfuOfferEvent(string Sdp, Guid ChannelId);
public record SfuIceCandidateEvent(string Candidate, string? SdpMid, int? SdpMLineIndex);

// Video Stream Signaling Events
public record VideoStreamStartedEvent(Guid ChannelId, Guid UserId, string Username, VideoStreamType StreamType);
public record VideoStreamStoppedEvent(Guid ChannelId, Guid UserId, VideoStreamType StreamType);

// Thread Events
public record ThreadReplyEvent(Guid ChannelId, Guid ParentMessageId, MessageResponse Reply);
public record ThreadMetadataUpdatedEvent(Guid ChannelId, Guid MessageId, int ReplyCount, DateTime? LastReplyAt);

// GIF Search Models
public record GifSearchResponse(
    List<GifResult> Results,
    string? NextPos
);

public record GifResult(
    string Id,
    string Title,
    string PreviewUrl,
    string Url,
    int Width,
    int Height
);

// Community Invite Models
public enum CommunityInviteStatus
{
    Pending,
    Accepted,
    Declined
}

public record CommunityInviteResponse(
    Guid Id,
    Guid CommunityId,
    string CommunityName,
    string? CommunityIcon,
    Guid InvitedUserId,
    string InvitedUserUsername,
    string InvitedUserEffectiveDisplayName,
    Guid InvitedById,
    string InvitedByUsername,
    string InvitedByEffectiveDisplayName,
    CommunityInviteStatus Status,
    DateTime CreatedAt,
    DateTime? RespondedAt
);

public record CommunityInviteReceivedEvent(
    Guid InviteId,
    Guid CommunityId,
    string CommunityName,
    string? CommunityIcon,
    Guid InvitedById,
    string InvitedByUsername,
    string InvitedByEffectiveDisplayName,
    DateTime CreatedAt
);

public record CommunityInviteRespondedEvent(
    Guid InviteId,
    Guid CommunityId,
    Guid InvitedUserId,
    string InvitedUserUsername,
    CommunityInviteStatus Status
);

public record UserSearchResult(
    Guid Id,
    string Username,
    string EffectiveDisplayName,
    string? Avatar,
    bool IsOnline
);

public record CreateCommunityInviteRequest(Guid UserId);
