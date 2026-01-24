using Snacka.Client.Models;
using Snacka.Client.Stores;

namespace Snacka.Client.Services;

/// <summary>
/// Routes SignalR events to appropriate stores.
/// This centralizes event handling that was previously scattered across ViewModels.
/// </summary>
public interface ISignalREventDispatcher : IDisposable
{
    /// <summary>
    /// Registers all stores and starts dispatching events.
    /// Call this after all stores are initialized.
    /// </summary>
    void Initialize(
        IChannelStore channelStore,
        ICommunityStore communityStore,
        IMessageStore messageStore,
        IVoiceStore voiceStore,
        IPresenceStore presenceStore,
        IGamingStationStore gamingStationStore,
        Guid currentUserId
    );
}

public sealed class SignalREventDispatcher : ISignalREventDispatcher
{
    private readonly ISignalRService _signalR;
    private readonly List<IDisposable> _subscriptions = new();
    private Guid _currentUserId;

    // Store references
    private IChannelStore? _channelStore;
    private ICommunityStore? _communityStore;
    private IMessageStore? _messageStore;
    private IVoiceStore? _voiceStore;
    private IPresenceStore? _presenceStore;
    private IGamingStationStore? _gamingStationStore;

    public SignalREventDispatcher(ISignalRService signalR)
    {
        _signalR = signalR;
    }

    public void Initialize(
        IChannelStore channelStore,
        ICommunityStore communityStore,
        IMessageStore messageStore,
        IVoiceStore voiceStore,
        IPresenceStore presenceStore,
        IGamingStationStore gamingStationStore,
        Guid currentUserId)
    {
        _channelStore = channelStore;
        _communityStore = communityStore;
        _messageStore = messageStore;
        _voiceStore = voiceStore;
        _presenceStore = presenceStore;
        _gamingStationStore = gamingStationStore;
        _currentUserId = currentUserId;

        // Subscribe to connection state changes
        _signalR.ConnectionStateChanged += OnConnectionStateChanged;
        _signalR.ReconnectCountdownChanged += OnReconnectCountdownChanged;

        // Subscribe to channel events
        _signalR.ChannelCreated += OnChannelCreated;
        _signalR.ChannelUpdated += OnChannelUpdated;
        _signalR.ChannelDeleted += OnChannelDeleted;
        _signalR.ChannelsReordered += OnChannelsReordered;

        // Subscribe to message events
        _signalR.MessageReceived += OnMessageReceived;
        _signalR.MessageEdited += OnMessageEdited;
        _signalR.MessageDeleted += OnMessageDeleted;
        _signalR.ReactionUpdated += OnReactionUpdated;
        _signalR.MessagePinned += OnMessagePinned;
        _signalR.ThreadMetadataUpdated += OnThreadMetadataUpdated;

        // Subscribe to presence events
        _signalR.UserOnline += OnUserOnline;
        _signalR.UserOffline += OnUserOffline;

        // Subscribe to community member events
        _signalR.CommunityMemberAdded += OnCommunityMemberAdded;
        _signalR.CommunityMemberRemoved += OnCommunityMemberRemoved;

        // Subscribe to voice events
        _signalR.VoiceParticipantJoined += OnVoiceParticipantJoined;
        _signalR.VoiceParticipantLeft += OnVoiceParticipantLeft;
        _signalR.VoiceStateChanged += OnVoiceStateChanged;
        _signalR.SpeakingStateChanged += OnSpeakingStateChanged;
        _signalR.ServerVoiceStateChanged += OnServerVoiceStateChanged;
        _signalR.VoiceSessionActiveOnOtherDevice += OnVoiceSessionActiveOnOtherDevice;
        _signalR.DisconnectedFromVoice += OnDisconnectedFromVoice;

        // Subscribe to gaming station events
        _signalR.GamingStationStatusChanged += OnGamingStationStatusChanged;
    }

    #region Connection Events

    private void OnConnectionStateChanged(ConnectionState state)
    {
        _presenceStore?.SetConnectionStatus(state);
    }

    private void OnReconnectCountdownChanged(int seconds)
    {
        _presenceStore?.SetReconnectCountdown(seconds);
    }

    #endregion

    #region Channel Events

    private void OnChannelCreated(ChannelResponse channel)
    {
        _channelStore?.AddChannel(channel);
    }

    private void OnChannelUpdated(ChannelResponse channel)
    {
        _channelStore?.UpdateChannel(channel);
    }

    private void OnChannelDeleted(ChannelDeletedEvent e)
    {
        _channelStore?.RemoveChannel(e.ChannelId);
    }

    private void OnChannelsReordered(ChannelsReorderedEvent e)
    {
        _channelStore?.ReorderChannels(e.Channels);
    }

    #endregion

    #region Message Events

    private void OnMessageReceived(MessageResponse message)
    {
        _messageStore?.AddMessage(message);

        // Increment unread count if:
        // 1. Not viewing this channel (different from selected)
        // 2. Not our own message
        var selectedChannelId = _channelStore?.GetSelectedChannelId();
        if (selectedChannelId != message.ChannelId && message.AuthorId != _currentUserId)
        {
            _channelStore?.IncrementUnreadCount(message.ChannelId);
        }
    }

    private void OnMessageEdited(MessageResponse message)
    {
        _messageStore?.UpdateMessage(message);
    }

    private void OnMessageDeleted(MessageDeletedEvent e)
    {
        _messageStore?.DeleteMessage(e.MessageId);
    }

    private void OnReactionUpdated(ReactionUpdatedEvent e)
    {
        if (e.Added)
        {
            _messageStore?.AddReaction(e.MessageId, e.Emoji, e.UserId, e.Username, e.EffectiveDisplayName);
        }
        else
        {
            _messageStore?.RemoveReaction(e.MessageId, e.Emoji, e.UserId);
        }
    }

    private void OnMessagePinned(MessagePinnedEvent e)
    {
        _messageStore?.UpdatePinState(
            e.MessageId,
            e.IsPinned,
            e.PinnedAt,
            e.PinnedByUsername,
            e.PinnedByEffectiveDisplayName);
    }

    private void OnThreadMetadataUpdated(ThreadMetadataUpdatedEvent e)
    {
        _messageStore?.UpdateThreadMetadata(e.MessageId, e.ReplyCount, e.LastReplyAt);
    }

    #endregion

    #region Presence Events

    private void OnUserOnline(UserPresenceEvent e)
    {
        _presenceStore?.SetUserOnline(e.UserId);
        _communityStore?.UpdateMemberOnlineStatus(e.UserId, true);
    }

    private void OnUserOffline(UserPresenceEvent e)
    {
        _presenceStore?.SetUserOffline(e.UserId);
        _communityStore?.UpdateMemberOnlineStatus(e.UserId, false);
    }

    #endregion

    #region Community Member Events

    private void OnCommunityMemberAdded(CommunityMemberAddedEvent e)
    {
        // Member details will be loaded by coordinator when needed
        // This event just signals that a member was added
    }

    private void OnCommunityMemberRemoved(CommunityMemberRemovedEvent e)
    {
        _communityStore?.RemoveMember(e.CommunityId, e.UserId);
    }

    #endregion

    #region Voice Events

    private void OnVoiceParticipantJoined(VoiceParticipantJoinedEvent e)
    {
        _voiceStore?.AddParticipant(e.Participant);
    }

    private void OnVoiceParticipantLeft(VoiceParticipantLeftEvent e)
    {
        _voiceStore?.RemoveParticipant(e.ChannelId, e.UserId);
    }

    private void OnVoiceStateChanged(VoiceStateChangedEvent e)
    {
        _voiceStore?.UpdateVoiceState(e.ChannelId, e.UserId, e.State);
    }

    private void OnSpeakingStateChanged(SpeakingStateChangedEvent e)
    {
        _voiceStore?.UpdateSpeakingState(e.ChannelId, e.UserId, e.IsSpeaking);

        // Update local speaking state if this is the current user
        if (e.UserId == _currentUserId)
        {
            _voiceStore?.SetLocalSpeaking(e.IsSpeaking);
        }
    }

    private void OnServerVoiceStateChanged(ServerVoiceStateChangedEvent e)
    {
        _voiceStore?.UpdateServerVoiceState(e.ChannelId, e.TargetUserId, e.IsServerMuted, e.IsServerDeafened);
    }

    private void OnVoiceSessionActiveOnOtherDevice(VoiceSessionActiveOnOtherDeviceEvent e)
    {
        _voiceStore?.SetVoiceOnOtherDevice(e.ChannelId, e.ChannelName);
    }

    private void OnDisconnectedFromVoice(DisconnectedFromVoiceEvent e)
    {
        // Clear voice state when disconnected by server
        _voiceStore?.SetCurrentChannel(null);
        _voiceStore?.SetConnectionStatus(VoiceConnectionStatus.Disconnected);
    }

    #endregion

    #region Gaming Station Events

    private void OnGamingStationStatusChanged(GamingStationStatusChangedEvent e)
    {
        // Only track stations owned by the current user
        if (e.UserId != _currentUserId) return;

        var existing = _gamingStationStore?.GetStation(e.MachineId);
        if (existing is not null || e.IsAvailable)
        {
            _gamingStationStore?.AddOrUpdateStation(new MyGamingStationInfo(
                e.MachineId,
                e.DisplayName,
                e.IsAvailable,
                e.IsInVoiceChannel,
                e.CurrentChannelId,
                e.IsScreenSharing,
                IsCurrentMachine: e.MachineId == _gamingStationStore?.CurrentMachineId
            ));
        }
    }

    #endregion

    public void Dispose()
    {
        // Unsubscribe from all events
        _signalR.ConnectionStateChanged -= OnConnectionStateChanged;
        _signalR.ReconnectCountdownChanged -= OnReconnectCountdownChanged;

        _signalR.ChannelCreated -= OnChannelCreated;
        _signalR.ChannelUpdated -= OnChannelUpdated;
        _signalR.ChannelDeleted -= OnChannelDeleted;
        _signalR.ChannelsReordered -= OnChannelsReordered;

        _signalR.MessageReceived -= OnMessageReceived;
        _signalR.MessageEdited -= OnMessageEdited;
        _signalR.MessageDeleted -= OnMessageDeleted;
        _signalR.ReactionUpdated -= OnReactionUpdated;
        _signalR.MessagePinned -= OnMessagePinned;
        _signalR.ThreadMetadataUpdated -= OnThreadMetadataUpdated;

        _signalR.UserOnline -= OnUserOnline;
        _signalR.UserOffline -= OnUserOffline;

        _signalR.CommunityMemberAdded -= OnCommunityMemberAdded;
        _signalR.CommunityMemberRemoved -= OnCommunityMemberRemoved;

        _signalR.VoiceParticipantJoined -= OnVoiceParticipantJoined;
        _signalR.VoiceParticipantLeft -= OnVoiceParticipantLeft;
        _signalR.VoiceStateChanged -= OnVoiceStateChanged;
        _signalR.SpeakingStateChanged -= OnSpeakingStateChanged;
        _signalR.ServerVoiceStateChanged -= OnServerVoiceStateChanged;
        _signalR.VoiceSessionActiveOnOtherDevice -= OnVoiceSessionActiveOnOtherDevice;
        _signalR.DisconnectedFromVoice -= OnDisconnectedFromVoice;

        _signalR.GamingStationStatusChanged -= OnGamingStationStatusChanged;

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }
}
