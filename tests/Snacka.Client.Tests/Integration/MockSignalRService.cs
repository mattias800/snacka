using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.Tests.Integration;

/// <summary>
/// A mock ISignalRService that allows tests to raise events to simulate server-side actions.
/// Used for integration testing the SignalR → Store → ViewModel flow.
/// </summary>
public class MockSignalRService : ISignalRService
{
    private ConnectionState _state = ConnectionState.Connected;

    public bool IsConnected => _state == ConnectionState.Connected;
    public ConnectionState State => _state;
    public int ReconnectSecondsRemaining => 0;

    #region Events

    // Connection events
    public event Action<ConnectionState>? ConnectionStateChanged;
    public event Action<int>? ReconnectCountdownChanged;

    // Channel events
    public event Action<ChannelResponse>? ChannelCreated;
    public event Action<ChannelResponse>? ChannelUpdated;
    public event Action<ChannelDeletedEvent>? ChannelDeleted;
    public event Action<ChannelsReorderedEvent>? ChannelsReordered;
    public event Action<MessageResponse>? MessageReceived;
    public event Action<MessageResponse>? MessageEdited;
    public event Action<MessageDeletedEvent>? MessageDeleted;
    public event Action<ReactionUpdatedEvent>? ReactionUpdated;
    public event Action<MessagePinnedEvent>? MessagePinned;

    // Conversation events
    public event Action<ConversationResponse>? ConversationCreated;
    public event Action<ConversationMessageResponse>? ConversationMessageReceived;
    public event Action<ConversationMessageResponse>? ConversationMessageUpdated;
    public event Action<ConversationMessageDeletedEvent>? ConversationMessageDeleted;
    public event Action<ConversationParticipantAddedEvent>? ConversationParticipantAdded;
    public event Action<ConversationParticipantRemovedEvent>? ConversationParticipantRemoved;
    public event Action<ConversationResponse>? ConversationUpdated;
    public event Action<ConversationTypingEvent>? ConversationUserTyping;
    public event Action<Guid>? AddedToConversation;
    public event Action<Guid>? RemovedFromConversation;

    // User presence events
    public event Action<UserPresenceEvent>? UserOnline;
    public event Action<UserPresenceEvent>? UserOffline;

    // Community member events
    public event Action<CommunityMemberAddedEvent>? CommunityMemberAdded;
    public event Action<CommunityMemberRemovedEvent>? CommunityMemberRemoved;

    // Voice channel events
    public event Action<VoiceParticipantJoinedEvent>? VoiceParticipantJoined;
    public event Action<VoiceParticipantLeftEvent>? VoiceParticipantLeft;
    public event Action<VoiceStateChangedEvent>? VoiceStateChanged;
    public event Action<SpeakingStateChangedEvent>? SpeakingStateChanged;

    // Multi-device voice events
    public event Action<VoiceSessionActiveOnOtherDeviceEvent>? VoiceSessionActiveOnOtherDevice;
    public event Action<DisconnectedFromVoiceEvent>? DisconnectedFromVoice;

    // Admin voice action events
    public event Action<ServerVoiceStateChangedEvent>? ServerVoiceStateChanged;
    public event Action<UserMovedEvent>? UserMoved;

    // WebRTC signaling events
    public event Action<WebRtcOfferEvent>? WebRtcOfferReceived;
    public event Action<WebRtcAnswerEvent>? WebRtcAnswerReceived;
    public event Action<IceCandidateEvent>? IceCandidateReceived;

    // SFU signaling events
    public event Action<SfuOfferEvent>? SfuOfferReceived;
    public event Action<SfuIceCandidateEvent>? SfuIceCandidateReceived;

    // Video stream signaling events
    public event Action<VideoStreamStartedEvent>? VideoStreamStarted;
    public event Action<VideoStreamStoppedEvent>? VideoStreamStopped;

    // Drawing annotation events
    public event Action<AnnotationMessage>? AnnotationReceived;

    // Typing indicator events
    public event Action<TypingEvent>? UserTyping;
    public event Action<DMTypingEvent>? DMUserTyping;

    // SSRC mapping events
    public event Action<SsrcMappingEvent>? UserAudioSsrcMapped;
    public event Action<ScreenAudioSsrcMappingEvent>? UserScreenAudioSsrcMapped;
    public event Action<CameraVideoSsrcMappingEvent>? UserCameraVideoSsrcMapped;
    public event Action<SsrcMappingBatchEvent>? SsrcMappingsBatchReceived;

    // Thread events
    public event Action<ThreadReplyEvent>? ThreadReplyReceived;
    public event Action<ThreadMetadataUpdatedEvent>? ThreadMetadataUpdated;

    // Community invite events
    public event Action<CommunityInviteReceivedEvent>? CommunityInviteReceived;
    public event Action<CommunityInviteRespondedEvent>? CommunityInviteResponded;

    // Notification events
    public event Action<NotificationResponse>? NotificationReceived;
    public event Action<IEnumerable<NotificationResponse>>? PendingNotificationsReceived;
    public event Action<int>? UnreadNotificationCountChanged;

    // Admin events
    public event Action<AdminUserResponse>? UserRegistered;

    // Controller streaming events
    public event Action<ControllerAccessRequestedEvent>? ControllerAccessRequested;
    public event Action<ControllerAccessAcceptedEvent>? ControllerAccessAccepted;
    public event Action<ControllerAccessDeclinedEvent>? ControllerAccessDeclined;
    public event Action<ControllerAccessStoppedEvent>? ControllerAccessStopped;
    public event Action<ControllerStateReceivedEvent>? ControllerStateReceived;
    public event Action<ControllerRumbleReceivedEvent>? ControllerRumbleReceived;

    // Port forwarding events
    public event Action<PortSharedEvent>? PortShared;
    public event Action<PortShareStoppedEvent>? PortShareStopped;

    // Gaming station events
    public event Action<GamingStationStatusChangedEvent>? GamingStationStatusChanged;
    public event Action<StationCommandJoinChannelEvent>? StationCommandJoinChannel;
    public event Action<StationCommandLeaveChannelEvent>? StationCommandLeaveChannel;
    public event Action<StationCommandStartScreenShareEvent>? StationCommandStartScreenShare;
    public event Action<StationCommandStopScreenShareEvent>? StationCommandStopScreenShare;
    public event Action<StationCommandDisableEvent>? StationCommandDisable;
    public event Action<StationKeyboardInputEvent>? StationKeyboardInputReceived;
    public event Action<StationMouseInputEvent>? StationMouseInputReceived;

    #endregion

    #region Event Raisers - Used by tests to simulate SignalR events

    public void RaiseConnectionStateChanged(ConnectionState state)
    {
        _state = state;
        ConnectionStateChanged?.Invoke(state);
    }

    public void RaiseChannelCreated(ChannelResponse channel) => ChannelCreated?.Invoke(channel);
    public void RaiseChannelUpdated(ChannelResponse channel) => ChannelUpdated?.Invoke(channel);
    public void RaiseChannelDeleted(ChannelDeletedEvent e) => ChannelDeleted?.Invoke(e);
    public void RaiseChannelsReordered(ChannelsReorderedEvent e) => ChannelsReordered?.Invoke(e);

    public void RaiseMessageReceived(MessageResponse message) => MessageReceived?.Invoke(message);
    public void RaiseMessageEdited(MessageResponse message) => MessageEdited?.Invoke(message);
    public void RaiseMessageDeleted(MessageDeletedEvent e) => MessageDeleted?.Invoke(e);
    public void RaiseReactionUpdated(ReactionUpdatedEvent e) => ReactionUpdated?.Invoke(e);
    public void RaiseMessagePinned(MessagePinnedEvent e) => MessagePinned?.Invoke(e);
    public void RaiseThreadMetadataUpdated(ThreadMetadataUpdatedEvent e) => ThreadMetadataUpdated?.Invoke(e);

    public void RaiseUserOnline(UserPresenceEvent e) => UserOnline?.Invoke(e);
    public void RaiseUserOffline(UserPresenceEvent e) => UserOffline?.Invoke(e);

    public void RaiseCommunityMemberAdded(CommunityMemberAddedEvent e) => CommunityMemberAdded?.Invoke(e);
    public void RaiseCommunityMemberRemoved(CommunityMemberRemovedEvent e) => CommunityMemberRemoved?.Invoke(e);

    public void RaiseVoiceParticipantJoined(VoiceParticipantJoinedEvent e) => VoiceParticipantJoined?.Invoke(e);
    public void RaiseVoiceParticipantLeft(VoiceParticipantLeftEvent e) => VoiceParticipantLeft?.Invoke(e);
    public void RaiseVoiceStateChanged(VoiceStateChangedEvent e) => VoiceStateChanged?.Invoke(e);
    public void RaiseSpeakingStateChanged(SpeakingStateChangedEvent e) => SpeakingStateChanged?.Invoke(e);
    public void RaiseServerVoiceStateChanged(ServerVoiceStateChangedEvent e) => ServerVoiceStateChanged?.Invoke(e);

    public void RaiseVoiceSessionActiveOnOtherDevice(VoiceSessionActiveOnOtherDeviceEvent e) =>
        VoiceSessionActiveOnOtherDevice?.Invoke(e);
    public void RaiseDisconnectedFromVoice(DisconnectedFromVoiceEvent e) => DisconnectedFromVoice?.Invoke(e);

    public void RaiseUserTyping(TypingEvent e) => UserTyping?.Invoke(e);
    public void RaiseGamingStationStatusChanged(GamingStationStatusChangedEvent e) =>
        GamingStationStatusChanged?.Invoke(e);

    #endregion

    #region Stub Method Implementations

    public Task ConnectAsync(string baseUrl, string accessToken)
    {
        _state = ConnectionState.Connected;
        ConnectionStateChanged?.Invoke(_state);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _state = ConnectionState.Disconnected;
        ConnectionStateChanged?.Invoke(_state);
        return Task.CompletedTask;
    }

    public Task JoinServerAsync(Guid serverId) => Task.CompletedTask;
    public Task LeaveServerAsync(Guid serverId) => Task.CompletedTask;
    public Task JoinChannelAsync(Guid channelId) => Task.CompletedTask;
    public Task LeaveChannelAsync(Guid channelId) => Task.CompletedTask;

    public Task<VoiceParticipantResponse?> JoinVoiceChannelAsync(Guid channelId) =>
        Task.FromResult<VoiceParticipantResponse?>(null);
    public Task LeaveVoiceChannelAsync(Guid channelId) => Task.CompletedTask;
    public Task<IEnumerable<VoiceParticipantResponse>> GetVoiceParticipantsAsync(Guid channelId) =>
        Task.FromResult<IEnumerable<VoiceParticipantResponse>>(Array.Empty<VoiceParticipantResponse>());
    public Task UpdateVoiceStateAsync(Guid channelId, VoiceStateUpdate update) => Task.CompletedTask;
    public Task UpdateSpeakingStateAsync(Guid channelId, bool isSpeaking) => Task.CompletedTask;

    public Task ServerMuteUserAsync(Guid channelId, Guid targetUserId, bool isServerMuted) => Task.CompletedTask;
    public Task ServerDeafenUserAsync(Guid channelId, Guid targetUserId, bool isServerDeafened) => Task.CompletedTask;
    public Task MoveUserAsync(Guid targetUserId, Guid targetChannelId) => Task.CompletedTask;

    public Task SendWebRtcOfferAsync(Guid targetUserId, string sdp) => Task.CompletedTask;
    public Task SendWebRtcAnswerAsync(Guid targetUserId, string sdp) => Task.CompletedTask;
    public Task SendIceCandidateAsync(Guid targetUserId, string candidate, string? sdpMid, int? sdpMLineIndex) =>
        Task.CompletedTask;

    public Task SendSfuAnswerAsync(Guid channelId, string sdp) => Task.CompletedTask;
    public Task SendSfuIceCandidateAsync(Guid channelId, string candidate, string? sdpMid, int? sdpMLineIndex) =>
        Task.CompletedTask;

    public Task WatchScreenShareAsync(Guid channelId, Guid streamerUserId) => Task.CompletedTask;
    public Task StopWatchingScreenShareAsync(Guid channelId, Guid streamerUserId) => Task.CompletedTask;

    public Task SendAnnotationAsync(AnnotationMessage message) => Task.CompletedTask;
    public Task ClearAnnotationsAsync(Guid channelId, Guid sharerUserId) => Task.CompletedTask;

    public Task SendTypingAsync(Guid channelId) => Task.CompletedTask;
    public Task SendDMTypingAsync(Guid recipientUserId) => Task.CompletedTask;
    public Task SendConversationTypingAsync(Guid conversationId) => Task.CompletedTask;

    public Task JoinConversationGroupAsync(Guid conversationId) => Task.CompletedTask;
    public Task LeaveConversationGroupAsync(Guid conversationId) => Task.CompletedTask;

    public Task RequestControllerAccessAsync(Guid channelId, Guid hostUserId) => Task.CompletedTask;
    public Task AcceptControllerAccessAsync(Guid channelId, Guid guestUserId, byte controllerSlot) => Task.CompletedTask;
    public Task DeclineControllerAccessAsync(Guid channelId, Guid guestUserId) => Task.CompletedTask;
    public Task StopControllerAccessAsync(Guid channelId, Guid otherUserId) => Task.CompletedTask;
    public Task SendControllerStateAsync(ControllerStateMessage state) => Task.CompletedTask;
    public Task SendControllerRumbleAsync(ControllerRumbleMessage rumble) => Task.CompletedTask;

    public Task SendGamingStationCommandAsync(Guid targetUserId, string command, string? parameter = null) =>
        Task.CompletedTask;

    // Gaming Station methods
    public Task SetGamingStationAvailableAsync(bool available, string? displayName, string machineId) =>
        Task.CompletedTask;
    public Task CommandStationJoinChannelAsync(string targetMachineId, Guid channelId) => Task.CompletedTask;
    public Task CommandStationLeaveChannelAsync(string targetMachineId) => Task.CompletedTask;
    public Task CommandStationStartScreenShareAsync(string targetMachineId) => Task.CompletedTask;
    public Task CommandStationStopScreenShareAsync(string targetMachineId) => Task.CompletedTask;
    public Task CommandStationDisableAsync(string targetMachineId) => Task.CompletedTask;
    public Task SendStationKeyboardInputAsync(Guid channelId, StationKeyboardInput input) => Task.CompletedTask;
    public Task SendStationMouseInputAsync(Guid channelId, StationMouseInput input) => Task.CompletedTask;

    // Port forwarding methods
    public Task<SharedPortInfo?> SharePortAsync(int port, string? label) =>
        Task.FromResult<SharedPortInfo?>(new SharedPortInfo("test-tunnel", Guid.Empty, "test", port, label, DateTime.UtcNow));
    public Task StopSharingPortAsync(string tunnelId) => Task.CompletedTask;
    public Task<IEnumerable<SharedPortInfo>> GetSharedPortsAsync(Guid channelId) =>
        Task.FromResult<IEnumerable<SharedPortInfo>>(Array.Empty<SharedPortInfo>());
    public Task<TunnelAccessResponse?> RequestTunnelAccessAsync(string tunnelId) =>
        Task.FromResult<TunnelAccessResponse?>(new TunnelAccessResponse("http://localhost/tunnel/test"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    #endregion
}
