using Miscord.Shared.Models;

namespace Miscord.Client.Services;

// Auth Models
public record RegisterRequest(string Username, string Email, string Password);
public record LoginRequest(string Email, string Password);
public record RefreshTokenRequest(string RefreshToken);

public record AuthResponse(
    Guid UserId,
    string Username,
    string Email,
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt
);

public record UserProfileResponse(
    Guid Id,
    string Username,
    string Email,
    string? Avatar,
    string? Status,
    bool IsOnline,
    DateTime CreatedAt
);

// Community Models
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

public record CreateCommunityRequest(string Name, string? Description);

// Channel Models
public record ChannelResponse(
    Guid Id,
    string Name,
    string? Topic,
    Guid CommunityId,
    ChannelType Type,
    int Position,
    DateTime CreatedAt
);

public record CreateChannelRequest(string Name, string? Topic, ChannelType Type = ChannelType.Text);
public record UpdateChannelRequest(string? Name, string? Topic);

// Message Models
public record MessageResponse(
    Guid Id,
    string Content,
    Guid AuthorId,
    string AuthorUsername,
    string? AuthorAvatar,
    Guid ChannelId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsEdited
);

public record SendMessageRequest(string Content);
public record UpdateMessageRequest(string Content);

// Member Models
public record CommunityMemberResponse(
    Guid UserId,
    string Username,
    string? Avatar,
    bool IsOnline,
    UserRole Role,
    DateTime JoinedAt
);

public record UpdateMemberRoleRequest(UserRole Role);
public record TransferOwnershipRequest(Guid NewOwnerId);

// Direct Message Models
public record DirectMessageResponse(
    Guid Id,
    string Content,
    Guid SenderId,
    string SenderUsername,
    Guid RecipientId,
    string RecipientUsername,
    DateTime CreatedAt,
    bool IsRead
);

public record SendDirectMessageRequest(string Content);

public record ConversationSummary(
    Guid UserId,
    string Username,
    string? Avatar,
    bool IsOnline,
    DirectMessageResponse? LastMessage,
    int UnreadCount
);

// Error
public record ApiError(string Error);

// SignalR Event DTOs - must match server-side records
public record ChannelDeletedEvent(Guid ChannelId);
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
    bool IsScreenSharing,
    bool IsCameraOn,
    DateTime JoinedAt
);

public record VoiceStateUpdate(
    bool? IsMuted = null,
    bool? IsDeafened = null,
    bool? IsScreenSharing = null,
    bool? IsCameraOn = null
);

// Voice Channel Events
public record VoiceParticipantJoinedEvent(Guid ChannelId, VoiceParticipantResponse Participant);
public record VoiceParticipantLeftEvent(Guid ChannelId, Guid UserId);
public record VoiceStateChangedEvent(Guid ChannelId, Guid UserId, VoiceStateUpdate State);
public record SpeakingStateChangedEvent(Guid ChannelId, Guid UserId, bool IsSpeaking);

// WebRTC Signaling Events (P2P - legacy)
public record WebRtcOfferEvent(Guid FromUserId, string Sdp);
public record WebRtcAnswerEvent(Guid FromUserId, string Sdp);
public record IceCandidateEvent(Guid FromUserId, string Candidate, string? SdpMid, int? SdpMLineIndex);

// SFU Signaling Events
public record SfuOfferEvent(string Sdp, Guid ChannelId);
public record SfuIceCandidateEvent(string Candidate, string? SdpMid, int? SdpMLineIndex);
