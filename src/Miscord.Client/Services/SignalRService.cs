using Microsoft.AspNetCore.SignalR.Client;

namespace Miscord.Client.Services;

public interface ISignalRService : IAsyncDisposable
{
    bool IsConnected { get; }
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

    // WebRTC signaling methods (P2P - legacy)
    Task SendWebRtcOfferAsync(Guid targetUserId, string sdp);
    Task SendWebRtcAnswerAsync(Guid targetUserId, string sdp);
    Task SendIceCandidateAsync(Guid targetUserId, string candidate, string? sdpMid, int? sdpMLineIndex);

    // SFU signaling methods
    Task SendSfuAnswerAsync(Guid channelId, string sdp);
    Task SendSfuIceCandidateAsync(Guid channelId, string candidate, string? sdpMid, int? sdpMLineIndex);

    // Channel events
    event Action<ChannelResponse>? ChannelCreated;
    event Action<ChannelResponse>? ChannelUpdated;
    event Action<ChannelDeletedEvent>? ChannelDeleted;
    event Action<MessageResponse>? MessageReceived;
    event Action<MessageResponse>? MessageEdited;
    event Action<MessageDeletedEvent>? MessageDeleted;

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

    // WebRTC signaling events (P2P - legacy)
    event Action<WebRtcOfferEvent>? WebRtcOfferReceived;
    event Action<WebRtcAnswerEvent>? WebRtcAnswerReceived;
    event Action<IceCandidateEvent>? IceCandidateReceived;

    // SFU signaling events
    event Action<SfuOfferEvent>? SfuOfferReceived;
    event Action<SfuIceCandidateEvent>? SfuIceCandidateReceived;
}

public class SignalRService : ISignalRService
{
    private HubConnection? _hubConnection;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    // Channel events
    public event Action<ChannelResponse>? ChannelCreated;
    public event Action<ChannelResponse>? ChannelUpdated;
    public event Action<ChannelDeletedEvent>? ChannelDeleted;
    public event Action<MessageResponse>? MessageReceived;
    public event Action<MessageResponse>? MessageEdited;
    public event Action<MessageDeletedEvent>? MessageDeleted;

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

    // WebRTC signaling events (P2P - legacy)
    public event Action<WebRtcOfferEvent>? WebRtcOfferReceived;
    public event Action<WebRtcAnswerEvent>? WebRtcAnswerReceived;
    public event Action<IceCandidateEvent>? IceCandidateReceived;

    // SFU signaling events
    public event Action<SfuOfferEvent>? SfuOfferReceived;
    public event Action<SfuIceCandidateEvent>? SfuIceCandidateReceived;

    public async Task ConnectAsync(string baseUrl, string accessToken)
    {
        if (_hubConnection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = $"{baseUrl.TrimEnd('/')}/hubs/miscord";
        Console.WriteLine($"SignalR: Connecting to {hubUrl}");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        RegisterHandlers();

        try
        {
            await _hubConnection.StartAsync();
            Console.WriteLine("SignalR: Connected successfully");

            // Get current online users and emit events for each
            await RefreshOnlineUsersAsync();
        }
        catch (Exception ex)
        {
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
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
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

        _hubConnection.Reconnecting += error =>
        {
            Console.WriteLine($"SignalR: Reconnecting... {error?.Message}");
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            Console.WriteLine($"SignalR: Reconnected with ID {connectionId}");
            return Task.CompletedTask;
        };

        _hubConnection.Closed += error =>
        {
            Console.WriteLine($"SignalR: Connection closed. {error?.Message}");
            return Task.CompletedTask;
        };
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
