using Microsoft.AspNetCore.SignalR.Client;
using Snacka.Shared.Models;

namespace Snacka.Client.Services;

/// <summary>
/// Represents the current connection state of the SignalR service.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public interface ISignalRService : IAsyncDisposable
{
    bool IsConnected { get; }
    ConnectionState State { get; }
    int ReconnectSecondsRemaining { get; }

    // Connection state events
    event Action<ConnectionState>? ConnectionStateChanged;
    event Action<int>? ReconnectCountdownChanged;
    Task ConnectAsync(string baseUrl, string accessToken);
    Task DisconnectAsync();
    Task JoinServerAsync(Guid serverId);
    Task LeaveServerAsync(Guid serverId);
    Task JoinChannelAsync(Guid channelId);
    Task LeaveChannelAsync(Guid channelId);

    // Voice channel methods
    Task<VoiceParticipantResponse?> JoinVoiceChannelAsync(Guid channelId);
    Task LeaveVoiceChannelAsync(Guid channelId);
    Task<IEnumerable<VoiceParticipantResponse>> GetVoiceParticipantsAsync(Guid channelId);
    Task UpdateVoiceStateAsync(Guid channelId, VoiceStateUpdate update);
    Task UpdateSpeakingStateAsync(Guid channelId, bool isSpeaking);

    // Admin voice control methods
    Task ServerMuteUserAsync(Guid channelId, Guid targetUserId, bool isServerMuted);
    Task ServerDeafenUserAsync(Guid channelId, Guid targetUserId, bool isServerDeafened);
    Task MoveUserAsync(Guid targetUserId, Guid targetChannelId);

    // WebRTC signaling methods (P2P - legacy)
    Task SendWebRtcOfferAsync(Guid targetUserId, string sdp);
    Task SendWebRtcAnswerAsync(Guid targetUserId, string sdp);
    Task SendIceCandidateAsync(Guid targetUserId, string candidate, string? sdpMid, int? sdpMLineIndex);

    // SFU signaling methods
    Task SendSfuAnswerAsync(Guid channelId, string sdp);
    Task SendSfuIceCandidateAsync(Guid channelId, string candidate, string? sdpMid, int? sdpMLineIndex);

    // Screen share viewing methods
    Task WatchScreenShareAsync(Guid channelId, Guid streamerUserId);
    Task StopWatchingScreenShareAsync(Guid channelId, Guid streamerUserId);

    // Drawing annotation methods
    Task SendAnnotationAsync(AnnotationMessage message);
    Task ClearAnnotationsAsync(Guid channelId, Guid sharerUserId);

    // Typing indicator methods
    Task SendTypingAsync(Guid channelId);
    Task SendDMTypingAsync(Guid recipientUserId);

    // Channel events
    event Action<ChannelResponse>? ChannelCreated;
    event Action<ChannelResponse>? ChannelUpdated;
    event Action<ChannelDeletedEvent>? ChannelDeleted;
    event Action<ChannelsReorderedEvent>? ChannelsReordered;
    event Action<MessageResponse>? MessageReceived;
    event Action<MessageResponse>? MessageEdited;
    event Action<MessageDeletedEvent>? MessageDeleted;
    event Action<ReactionUpdatedEvent>? ReactionUpdated;
    event Action<MessagePinnedEvent>? MessagePinned;

    // Direct message events
    event Action<DirectMessageResponse>? DirectMessageReceived;
    event Action<DirectMessageResponse>? DirectMessageEdited;
    event Action<DirectMessageDeletedEvent>? DirectMessageDeleted;

    // User presence events
    event Action<UserPresenceEvent>? UserOnline;
    event Action<UserPresenceEvent>? UserOffline;

    // Community member events
    event Action<CommunityMemberAddedEvent>? CommunityMemberAdded;
    event Action<CommunityMemberRemovedEvent>? CommunityMemberRemoved;

    // Voice channel events
    event Action<VoiceParticipantJoinedEvent>? VoiceParticipantJoined;
    event Action<VoiceParticipantLeftEvent>? VoiceParticipantLeft;
    event Action<VoiceStateChangedEvent>? VoiceStateChanged;
    event Action<SpeakingStateChangedEvent>? SpeakingStateChanged;

    // Admin voice action events
    event Action<ServerVoiceStateChangedEvent>? ServerVoiceStateChanged;
    event Action<UserMovedEvent>? UserMoved;

    // WebRTC signaling events (P2P - legacy)
    event Action<WebRtcOfferEvent>? WebRtcOfferReceived;
    event Action<WebRtcAnswerEvent>? WebRtcAnswerReceived;
    event Action<IceCandidateEvent>? IceCandidateReceived;

    // SFU signaling events
    event Action<SfuOfferEvent>? SfuOfferReceived;
    event Action<SfuIceCandidateEvent>? SfuIceCandidateReceived;

    // Video stream signaling events
    event Action<VideoStreamStartedEvent>? VideoStreamStarted;
    event Action<VideoStreamStoppedEvent>? VideoStreamStopped;

    // Drawing annotation events
    event Action<AnnotationMessage>? AnnotationReceived;

    // Typing indicator events
    event Action<TypingEvent>? UserTyping;
    event Action<DMTypingEvent>? DMUserTyping;

    // SSRC mapping events (for per-user volume control and video routing)
    event Action<SsrcMappingEvent>? UserAudioSsrcMapped;
    event Action<ScreenAudioSsrcMappingEvent>? UserScreenAudioSsrcMapped;
    event Action<CameraVideoSsrcMappingEvent>? UserCameraVideoSsrcMapped;
    event Action<SsrcMappingBatchEvent>? SsrcMappingsBatchReceived;

    // Thread events
    event Action<ThreadReplyEvent>? ThreadReplyReceived;
    event Action<ThreadMetadataUpdatedEvent>? ThreadMetadataUpdated;

    // Community invite events
    event Action<CommunityInviteReceivedEvent>? CommunityInviteReceived;
    event Action<CommunityInviteRespondedEvent>? CommunityInviteResponded;

    // Admin user management events
    event Action<AdminUserResponse>? UserRegistered;
}

// Typing indicator event DTOs
public record TypingEvent(Guid ChannelId, Guid UserId, string Username);
public record DMTypingEvent(Guid UserId, string Username);

/// <summary>
/// Custom retry policy that notifies when a retry is scheduled.
/// </summary>
public class CountdownRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan[] _retryDelays = [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    public event Action<TimeSpan>? RetryScheduled;

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.PreviousRetryCount >= _retryDelays.Length)
        {
            // Keep retrying every 30 seconds
            var delay = TimeSpan.FromSeconds(30);
            RetryScheduled?.Invoke(delay);
            return delay;
        }

        var nextDelay = _retryDelays[retryContext.PreviousRetryCount];
        RetryScheduled?.Invoke(nextDelay);
        return nextDelay;
    }
}

public class SignalRService : ISignalRService
{
    private HubConnection? _hubConnection;
    private ConnectionState _state = ConnectionState.Disconnected;
    private int _reconnectSecondsRemaining;
    private Timer? _countdownTimer;
    private CountdownRetryPolicy? _retryPolicy;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    public int ReconnectSecondsRemaining => _reconnectSecondsRemaining;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                ConnectionStateChanged?.Invoke(value);
            }
        }
    }

    // Connection state events
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

    // Direct message events
    public event Action<DirectMessageResponse>? DirectMessageReceived;
    public event Action<DirectMessageResponse>? DirectMessageEdited;
    public event Action<DirectMessageDeletedEvent>? DirectMessageDeleted;

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

    // Admin voice action events
    public event Action<ServerVoiceStateChangedEvent>? ServerVoiceStateChanged;
    public event Action<UserMovedEvent>? UserMoved;

    // WebRTC signaling events (P2P - legacy)
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

    // SSRC mapping events (for per-user volume control and video routing)
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

    // Admin user management events
    public event Action<AdminUserResponse>? UserRegistered;

    private void OnRetryScheduled(TimeSpan delay)
    {
        _reconnectSecondsRemaining = (int)Math.Ceiling(delay.TotalSeconds);
        ReconnectCountdownChanged?.Invoke(_reconnectSecondsRemaining);

        // Start countdown timer
        _countdownTimer?.Dispose();
        _countdownTimer = new Timer(CountdownTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void CountdownTick(object? state)
    {
        if (_reconnectSecondsRemaining > 0)
        {
            _reconnectSecondsRemaining--;
            ReconnectCountdownChanged?.Invoke(_reconnectSecondsRemaining);
        }
        else
        {
            StopCountdown();
        }
    }

    private void StopCountdown()
    {
        _countdownTimer?.Dispose();
        _countdownTimer = null;
        _reconnectSecondsRemaining = 0;
        ReconnectCountdownChanged?.Invoke(0);
    }

    public async Task ConnectAsync(string baseUrl, string accessToken)
    {
        if (_hubConnection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = $"{baseUrl.TrimEnd('/')}/hubs/snacka";
        Console.WriteLine($"SignalR: Connecting to {hubUrl}");

        State = ConnectionState.Connecting;

        // Create retry policy with countdown notifications
        _retryPolicy = new CountdownRetryPolicy();
        _retryPolicy.RetryScheduled += OnRetryScheduled;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect(_retryPolicy)
            .Build();

        RegisterHandlers();

        try
        {
            await _hubConnection.StartAsync();
            State = ConnectionState.Connected;
            Console.WriteLine("SignalR: Connected successfully");

            // Get current online users and emit events for each
            await RefreshOnlineUsersAsync();
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            Console.WriteLine($"SignalR: Connection failed - {ex.Message}");
            throw;
        }
    }

    private async Task RefreshOnlineUsersAsync()
    {
        if (_hubConnection is null || !IsConnected) return;

        try
        {
            var onlineUsers = await _hubConnection.InvokeAsync<IEnumerable<UserPresenceRecord>>("GetOnlineUsers");
            foreach (var user in onlineUsers)
            {
                Console.WriteLine($"SignalR: Initial online user - {user.Username}");
                UserOnline?.Invoke(new UserPresenceEvent(user.UserId, user.Username, true));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SignalR: Failed to get online users - {ex.Message}");
        }
    }

    // Record to match server's UserPresence
    private record UserPresenceRecord(Guid UserId, string Username, bool IsOnline);

    public async Task DisconnectAsync()
    {
        StopCountdown();

        if (_retryPolicy != null)
        {
            _retryPolicy.RetryScheduled -= OnRetryScheduled;
            _retryPolicy = null;
        }

        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
            State = ConnectionState.Disconnected;
            Console.WriteLine("SignalR: Disconnected");
        }
    }

    public async Task JoinServerAsync(Guid serverId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("JoinServer", serverId);
        Console.WriteLine($"SignalR: Joined server {serverId}");
    }

    public async Task LeaveServerAsync(Guid serverId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("LeaveServer", serverId);
        Console.WriteLine($"SignalR: Left server {serverId}");
    }

    public async Task JoinChannelAsync(Guid channelId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("JoinChannel", channelId);
        Console.WriteLine($"SignalR: Joined channel {channelId}");
    }

    public async Task LeaveChannelAsync(Guid channelId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("LeaveChannel", channelId);
        Console.WriteLine($"SignalR: Left channel {channelId}");
    }

    // Voice channel methods
    public async Task<VoiceParticipantResponse?> JoinVoiceChannelAsync(Guid channelId)
    {
        if (_hubConnection is null || !IsConnected) return null;
        try
        {
            var participant = await _hubConnection.InvokeAsync<VoiceParticipantResponse?>("JoinVoiceChannel", channelId);
            Console.WriteLine($"SignalR: Joined voice channel {channelId}");
            return participant;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SignalR: Failed to join voice channel - {ex.Message}");
            return null;
        }
    }

    public async Task LeaveVoiceChannelAsync(Guid channelId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("LeaveVoiceChannel", channelId);
        Console.WriteLine($"SignalR: Left voice channel {channelId}");
    }

    public async Task<IEnumerable<VoiceParticipantResponse>> GetVoiceParticipantsAsync(Guid channelId)
    {
        if (_hubConnection is null || !IsConnected) return [];
        return await _hubConnection.InvokeAsync<IEnumerable<VoiceParticipantResponse>>("GetVoiceParticipants", channelId);
    }

    public async Task UpdateVoiceStateAsync(Guid channelId, VoiceStateUpdate update)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("UpdateVoiceState", channelId, update);
    }

    public async Task UpdateSpeakingStateAsync(Guid channelId, bool isSpeaking)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("UpdateSpeakingState", channelId, isSpeaking);
    }

    // Admin voice control methods
    public async Task ServerMuteUserAsync(Guid channelId, Guid targetUserId, bool isServerMuted)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("ServerMuteUser", channelId, targetUserId, isServerMuted);
        Console.WriteLine($"SignalR: ServerMuteUser - {targetUserId} in channel {channelId}, muted={isServerMuted}");
    }

    public async Task ServerDeafenUserAsync(Guid channelId, Guid targetUserId, bool isServerDeafened)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("ServerDeafenUser", channelId, targetUserId, isServerDeafened);
        Console.WriteLine($"SignalR: ServerDeafenUser - {targetUserId} in channel {channelId}, deafened={isServerDeafened}");
    }

    public async Task MoveUserAsync(Guid targetUserId, Guid targetChannelId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("MoveUser", targetUserId, targetChannelId);
        Console.WriteLine($"SignalR: MoveUser - {targetUserId} to channel {targetChannelId}");
    }

    // WebRTC signaling methods
    public async Task SendWebRtcOfferAsync(Guid targetUserId, string sdp)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendWebRtcOffer", targetUserId, sdp);
        Console.WriteLine($"SignalR: Sent WebRTC offer to {targetUserId}");
    }

    public async Task SendWebRtcAnswerAsync(Guid targetUserId, string sdp)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendWebRtcAnswer", targetUserId, sdp);
        Console.WriteLine($"SignalR: Sent WebRTC answer to {targetUserId}");
    }

    public async Task SendIceCandidateAsync(Guid targetUserId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendIceCandidate", targetUserId, candidate, sdpMid, sdpMLineIndex);
    }

    // SFU signaling methods
    public async Task SendSfuAnswerAsync(Guid channelId, string sdp)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendSfuAnswer", channelId, sdp);
        Console.WriteLine($"SignalR: Sent SFU answer for channel {channelId}");
    }

    public async Task SendSfuIceCandidateAsync(Guid channelId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendSfuIceCandidate", channelId, candidate, sdpMid, sdpMLineIndex);
    }

    // Screen share viewing methods
    public async Task WatchScreenShareAsync(Guid channelId, Guid streamerUserId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("WatchScreenShare", channelId, streamerUserId);
        Console.WriteLine($"SignalR: Started watching screen share from {streamerUserId} in channel {channelId}");
    }

    public async Task StopWatchingScreenShareAsync(Guid channelId, Guid streamerUserId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("StopWatchingScreenShare", channelId, streamerUserId);
        Console.WriteLine($"SignalR: Stopped watching screen share from {streamerUserId} in channel {channelId}");
    }

    // Drawing annotation methods
    public async Task SendAnnotationAsync(AnnotationMessage message)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendAnnotation", message);
        Console.WriteLine($"SignalR: Sent annotation ({message.Action}) for screen share by {message.SharerUserId}");
    }

    public async Task ClearAnnotationsAsync(Guid channelId, Guid sharerUserId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("ClearAnnotations", channelId, sharerUserId);
        Console.WriteLine($"SignalR: Cleared annotations for screen share by {sharerUserId} in channel {channelId}");
    }

    // Typing indicator methods
    public async Task SendTypingAsync(Guid channelId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendTyping", channelId);
    }

    public async Task SendDMTypingAsync(Guid recipientUserId)
    {
        if (_hubConnection is null || !IsConnected) return;
        await _hubConnection.InvokeAsync("SendDMTyping", recipientUserId);
    }

    private void RegisterHandlers()
    {
        if (_hubConnection is null) return;

        _hubConnection.On<ChannelResponse>("ChannelCreated", channel =>
        {
            Console.WriteLine($"SignalR: ChannelCreated - {channel.Name}");
            ChannelCreated?.Invoke(channel);
        });

        _hubConnection.On<ChannelResponse>("ChannelUpdated", channel =>
        {
            Console.WriteLine($"SignalR: ChannelUpdated - {channel.Name}");
            ChannelUpdated?.Invoke(channel);
        });

        _hubConnection.On<ChannelDeletedEvent>("ChannelDeleted", e =>
        {
            Console.WriteLine($"SignalR: ChannelDeleted - {e.ChannelId}");
            ChannelDeleted?.Invoke(e);
        });

        _hubConnection.On<ChannelsReorderedEvent>("ChannelsReordered", e =>
        {
            Console.WriteLine($"SignalR: ChannelsReordered - {e.Channels.Count} channels");
            ChannelsReordered?.Invoke(e);
        });

        _hubConnection.On<MessageResponse>("ReceiveChannelMessage", message =>
        {
            Console.WriteLine($"SignalR: ReceiveChannelMessage from {message.AuthorUsername}");
            MessageReceived?.Invoke(message);
        });

        _hubConnection.On<MessageResponse>("ChannelMessageEdited", message =>
        {
            Console.WriteLine($"SignalR: ChannelMessageEdited - {message.Id}");
            MessageEdited?.Invoke(message);
        });

        _hubConnection.On<MessageDeletedEvent>("ChannelMessageDeleted", e =>
        {
            Console.WriteLine($"SignalR: ChannelMessageDeleted - {e.MessageId}");
            MessageDeleted?.Invoke(e);
        });

        _hubConnection.On<ReactionUpdatedEvent>("ReactionUpdated", e =>
        {
            Console.WriteLine($"SignalR: ReactionUpdated - {e.MessageId} {e.Emoji} {(e.Added ? "added" : "removed")}");
            ReactionUpdated?.Invoke(e);
        });

        _hubConnection.On<MessagePinnedEvent>("MessagePinned", e =>
        {
            Console.WriteLine($"SignalR: MessagePinned - {e.MessageId} {(e.IsPinned ? "pinned" : "unpinned")}");
            MessagePinned?.Invoke(e);
        });

        // Direct message events
        _hubConnection.On<DirectMessageResponse>("ReceiveDirectMessage", message =>
        {
            Console.WriteLine($"SignalR: ReceiveDirectMessage from {message.SenderUsername}");
            DirectMessageReceived?.Invoke(message);
        });

        _hubConnection.On<DirectMessageResponse>("DirectMessageEdited", message =>
        {
            Console.WriteLine($"SignalR: DirectMessageEdited - {message.Id}");
            DirectMessageEdited?.Invoke(message);
        });

        _hubConnection.On<Guid>("DirectMessageDeleted", messageId =>
        {
            Console.WriteLine($"SignalR: DirectMessageDeleted - {messageId}");
            DirectMessageDeleted?.Invoke(new DirectMessageDeletedEvent(messageId));
        });

        // User presence events
        _hubConnection.On<UserPresenceEvent>("UserOnline", e =>
        {
            Console.WriteLine($"SignalR: UserOnline - {e.Username}");
            UserOnline?.Invoke(e);
        });

        _hubConnection.On<UserPresenceEvent>("UserOffline", e =>
        {
            Console.WriteLine($"SignalR: UserOffline - {e.Username}");
            UserOffline?.Invoke(e);
        });

        // Community member events
        _hubConnection.On<Guid, Guid>("CommunityMemberAdded", (communityId, userId) =>
        {
            Console.WriteLine($"SignalR: CommunityMemberAdded - user {userId} joined community {communityId}");
            CommunityMemberAdded?.Invoke(new CommunityMemberAddedEvent(communityId, userId));
        });

        _hubConnection.On<Guid, Guid>("CommunityMemberRemoved", (communityId, userId) =>
        {
            Console.WriteLine($"SignalR: CommunityMemberRemoved - user {userId} left community {communityId}");
            CommunityMemberRemoved?.Invoke(new CommunityMemberRemovedEvent(communityId, userId));
        });

        // Voice channel events
        _hubConnection.On<VoiceParticipantJoinedEvent>("VoiceParticipantJoined", e =>
        {
            Console.WriteLine($"SignalR: VoiceParticipantJoined - {e.Participant.Username} joined channel {e.ChannelId}");
            VoiceParticipantJoined?.Invoke(e);
        });

        _hubConnection.On<VoiceParticipantLeftEvent>("VoiceParticipantLeft", e =>
        {
            Console.WriteLine($"SignalR: VoiceParticipantLeft - user {e.UserId} left channel {e.ChannelId}");
            VoiceParticipantLeft?.Invoke(e);
        });

        _hubConnection.On<VoiceStateChangedEvent>("VoiceStateChanged", e =>
        {
            Console.WriteLine($"SignalR: VoiceStateChanged - user {e.UserId} in channel {e.ChannelId}");
            VoiceStateChanged?.Invoke(e);
        });

        _hubConnection.On<SpeakingStateChangedEvent>("SpeakingStateChanged", e =>
        {
            SpeakingStateChanged?.Invoke(e);
        });

        // Admin voice action events
        _hubConnection.On<ServerVoiceStateChangedEvent>("ServerVoiceStateChanged", e =>
        {
            Console.WriteLine($"SignalR: ServerVoiceStateChanged - user {e.TargetUserId} in channel {e.ChannelId}, serverMuted={e.IsServerMuted}, serverDeafened={e.IsServerDeafened}");
            ServerVoiceStateChanged?.Invoke(e);
        });

        _hubConnection.On<UserMovedEvent>("UserMoved", e =>
        {
            Console.WriteLine($"SignalR: UserMoved - {e.Username} moved from {e.FromChannelId} to {e.ToChannelId} by {e.AdminUsername}");
            UserMoved?.Invoke(e);
        });

        // WebRTC signaling events
        _hubConnection.On<WebRtcOfferEvent>("WebRtcOffer", e =>
        {
            Console.WriteLine($"SignalR: WebRtcOffer received from {e.FromUserId}");
            WebRtcOfferReceived?.Invoke(e);
        });

        _hubConnection.On<WebRtcAnswerEvent>("WebRtcAnswer", e =>
        {
            Console.WriteLine($"SignalR: WebRtcAnswer received from {e.FromUserId}");
            WebRtcAnswerReceived?.Invoke(e);
        });

        _hubConnection.On<IceCandidateEvent>("IceCandidate", e =>
        {
            Console.WriteLine($"SignalR: IceCandidate received from {e.FromUserId}");
            IceCandidateReceived?.Invoke(e);
        });

        // SFU signaling events
        _hubConnection.On<SfuOfferEvent>("SfuOffer", e =>
        {
            Console.WriteLine($"SignalR: SfuOffer received for channel {e.ChannelId}");
            SfuOfferReceived?.Invoke(e);
        });

        _hubConnection.On<SfuIceCandidateEvent>("SfuIceCandidate", e =>
        {
            Console.WriteLine($"SignalR: SfuIceCandidate received");
            SfuIceCandidateReceived?.Invoke(e);
        });

        // Video stream signaling events
        _hubConnection.On<VideoStreamStartedEvent>("VideoStreamStarted", e =>
        {
            Console.WriteLine($"SignalR: VideoStreamStarted - {e.Username} started {e.StreamType} in channel {e.ChannelId}");
            VideoStreamStarted?.Invoke(e);
        });

        _hubConnection.On<VideoStreamStoppedEvent>("VideoStreamStopped", e =>
        {
            Console.WriteLine($"SignalR: VideoStreamStopped - {e.UserId} stopped {e.StreamType} in channel {e.ChannelId}");
            VideoStreamStopped?.Invoke(e);
        });

        // Drawing annotation events
        _hubConnection.On<AnnotationMessage>("ReceiveAnnotation", message =>
        {
            Console.WriteLine($"SignalR: ReceiveAnnotation ({message.Action}) for screen share by {message.SharerUserId}");
            AnnotationReceived?.Invoke(message);
        });

        // Typing indicator events
        _hubConnection.On<TypingEvent>("UserTyping", e =>
        {
            UserTyping?.Invoke(e);
        });

        _hubConnection.On<DMTypingEvent>("DMUserTyping", e =>
        {
            DMUserTyping?.Invoke(e);
        });

        // SSRC mapping events (for per-user volume control)
        _hubConnection.On<SsrcMappingEvent>("UserAudioSsrcMapped", e =>
        {
            Console.WriteLine($"SignalR: UserAudioSsrcMapped - user {e.UserId} has mic SSRC {e.AudioSsrc} in channel {e.ChannelId}");
            UserAudioSsrcMapped?.Invoke(e);
        });

        _hubConnection.On<ScreenAudioSsrcMappingEvent>("UserScreenAudioSsrcMapped", e =>
        {
            Console.WriteLine($"SignalR: UserScreenAudioSsrcMapped - user {e.UserId} has screen audio SSRC {e.ScreenAudioSsrc} in channel {e.ChannelId}");
            UserScreenAudioSsrcMapped?.Invoke(e);
        });

        _hubConnection.On<CameraVideoSsrcMappingEvent>("UserCameraVideoSsrcMapped", e =>
        {
            Console.WriteLine($"SignalR: UserCameraVideoSsrcMapped - user {e.UserId} has camera video SSRC {e.CameraVideoSsrc} in channel {e.ChannelId}");
            UserCameraVideoSsrcMapped?.Invoke(e);
        });

        _hubConnection.On<SsrcMappingBatchEvent>("SsrcMappingsBatch", e =>
        {
            Console.WriteLine($"SignalR: SsrcMappingsBatch - {e.Mappings.Count} mappings for channel {e.ChannelId}");
            SsrcMappingsBatchReceived?.Invoke(e);
        });

        // Thread events
        _hubConnection.On<ThreadReplyEvent>("ReceiveThreadReply", e =>
        {
            Console.WriteLine($"SignalR: ReceiveThreadReply - reply to message {e.ParentMessageId} in channel {e.ChannelId}");
            ThreadReplyReceived?.Invoke(e);
        });

        _hubConnection.On<ThreadMetadataUpdatedEvent>("ThreadMetadataUpdated", e =>
        {
            Console.WriteLine($"SignalR: ThreadMetadataUpdated - message {e.MessageId} now has {e.ReplyCount} replies");
            ThreadMetadataUpdated?.Invoke(e);
        });

        // Community invite events
        _hubConnection.On<CommunityInviteReceivedEvent>("CommunityInviteReceived", e =>
        {
            Console.WriteLine($"SignalR: CommunityInviteReceived - invite to {e.CommunityName} from {e.InvitedByUsername}");
            CommunityInviteReceived?.Invoke(e);
        });

        _hubConnection.On<CommunityInviteRespondedEvent>("CommunityInviteResponded", e =>
        {
            Console.WriteLine($"SignalR: CommunityInviteResponded - {e.InvitedUserUsername} {e.Status} invite to community {e.CommunityId}");
            CommunityInviteResponded?.Invoke(e);
        });

        // Admin user management events
        _hubConnection.On<AdminUserResponse>("UserRegistered", e =>
        {
            Console.WriteLine($"SignalR: UserRegistered - {e.Username}");
            UserRegistered?.Invoke(e);
        });

        _hubConnection.Reconnecting += error =>
        {
            State = ConnectionState.Reconnecting;
            Console.WriteLine($"SignalR: Reconnecting... {error?.Message}");
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            StopCountdown();
            State = ConnectionState.Connected;
            Console.WriteLine($"SignalR: Reconnected with ID {connectionId}");
            return Task.CompletedTask;
        };

        _hubConnection.Closed += error =>
        {
            StopCountdown();
            State = ConnectionState.Disconnected;
            Console.WriteLine($"SignalR: Connection closed. {error?.Message}");
            return Task.CompletedTask;
        };
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
