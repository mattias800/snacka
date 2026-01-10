namespace Miscord.Server.DTOs;

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

// WebRTC signaling DTOs
public record WebRtcOffer(Guid TargetUserId, string Sdp);
public record WebRtcAnswer(Guid TargetUserId, string Sdp);
public record WebRtcIceCandidate(Guid TargetUserId, string Candidate, string? SdpMid, int? SdpMLineIndex);

// Events sent to clients
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
