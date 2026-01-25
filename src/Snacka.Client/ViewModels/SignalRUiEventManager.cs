using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Coordinators;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

/// <summary>
/// Callbacks for UI-specific actions triggered by SignalR events.
/// These are actions that require access to ViewModel state.
/// </summary>
public record SignalRUiCallbacks(
    Func<ChannelResponse?> GetSelectedChannel,
    Action<ChannelResponse?> SetSelectedChannel,
    Func<CommunityResponse?> GetSelectedCommunity,
    Func<ChannelResponse?> GetCurrentVoiceChannel,
    Action ClearVoiceUiState,
    Func<Guid, ChannelResponse?> GetChannelById,
    Func<ChannelResponse?> GetFirstTextChannel,
    Func<Task> LeaveVoiceChannelAsync,
    Func<ChannelResponse, Task> JoinVoiceChannelAsync,
    Action RaiseTotalDmUnreadCountChanged
);

/// <summary>
/// Manages UI-specific SignalR event subscriptions for MainAppViewModel.
/// Handles events that require UI state updates not covered by stores/coordinators.
/// </summary>
public class SignalRUiEventManager : IDisposable
{
    private readonly ISignalRService _signalR;
    private readonly IWebRtcService _webRtc;
    private readonly StoreContainer _stores;
    private readonly ICommunityCoordinator _communityCoordinator;
    private readonly IVoiceCoordinator _voiceCoordinator;
    private readonly IConversationStateService _conversationStateService;
    private readonly Guid _currentUserId;
    private readonly SignalRUiCallbacks _callbacks;

    // Child ViewModels (nullable - may not be set)
    private readonly ScreenShareViewModel? _screenShare;
    private readonly VoiceControlViewModel? _voiceControl;
    private readonly VideoFullscreenViewModel? _videoFullscreen;
    private readonly PinnedMessagesPopupViewModel? _pinnedMessagesPopup;

    private readonly CompositeDisposable _subscriptions = new();
    private bool _disposed;

    public SignalRUiEventManager(
        ISignalRService signalR,
        IWebRtcService webRtc,
        StoreContainer stores,
        ICommunityCoordinator communityCoordinator,
        IVoiceCoordinator voiceCoordinator,
        IConversationStateService conversationStateService,
        Guid currentUserId,
        SignalRUiCallbacks callbacks,
        ScreenShareViewModel? screenShare = null,
        VoiceControlViewModel? voiceControl = null,
        VideoFullscreenViewModel? videoFullscreen = null,
        PinnedMessagesPopupViewModel? pinnedMessagesPopup = null)
    {
        _signalR = signalR;
        _webRtc = webRtc;
        _stores = stores;
        _communityCoordinator = communityCoordinator;
        _voiceCoordinator = voiceCoordinator;
        _conversationStateService = conversationStateService;
        _currentUserId = currentUserId;
        _callbacks = callbacks;
        _screenShare = screenShare;
        _voiceControl = voiceControl;
        _videoFullscreen = videoFullscreen;
        _pinnedMessagesPopup = pinnedMessagesPopup;
    }

    /// <summary>
    /// Sets up all SignalR event handlers for UI updates.
    /// </summary>
    public void Setup()
    {
        // ChannelCreated: VoiceChannelViewModels handled by VoiceChannelViewModelManager
        // ChannelStore updated by SignalREventDispatcher

        _signalR.ChannelUpdated += OnChannelUpdated;
        _signalR.ChannelDeleted += OnChannelDeleted;

        // ChannelsReordered: VoiceChannelViewModels handled by VoiceChannelViewModelManager
        // ChannelStore updated by SignalREventDispatcher

        // MessageReceived handled entirely by SignalREventDispatcher -> MessageStore, TypingStore, ChannelStore
        // MessageEdited, MessageDeleted, ThreadReplyReceived, ReactionUpdated handled by ThreadPanelViewModel
        // ThreadMetadataUpdated handled by SignalREventDispatcher -> MessageStore

        _signalR.MessagePinned += OnMessagePinned;

        // UserOnline/UserOffline handled by SignalREventDispatcher -> CommunityStore/PresenceStore

        _signalR.CommunityMemberAdded += OnCommunityMemberAdded;

        // CommunityMemberRemoved event is now handled by SignalREventDispatcher -> CommunityStore
        // StoreMembers auto-updates via DynamicData binding

        // Voice channel events (VoiceParticipantJoined, VoiceParticipantLeft, VoiceStateChanged, SpeakingStateChanged)
        // now handled by VoiceChannelContentViewModel which subscribes to SignalR events directly
        // VoiceChannelViewModels auto-update via VoiceStore subscription

        // VoiceSessionActiveOnOtherDevice is handled by SignalREventDispatcher -> VoiceStore

        _signalR.DisconnectedFromVoice += OnDisconnectedFromVoice;

        // Local speaking detection - broadcast to others and update own state
        _webRtc.SpeakingChanged += OnSpeakingChanged;

        // Admin voice state changed (server mute/deafen)
        _signalR.ServerVoiceStateChanged += OnServerVoiceStateChanged;

        // User moved by admin - only handle current user (other users handled by VoiceParticipantLeft/Joined events)
        _signalR.UserMoved += OnUserMoved;

        // Video stream stopped - close fullscreen if viewing that stream
        _signalR.VideoStreamStopped += OnVideoStreamStopped;

        // Typing indicator events now handled by SignalREventDispatcher -> TypingStore
        // UI subscribes to store via _typingSubscription (set up in SubscribeToTypingStore)

        // Conversation events handled by ConversationStateService directly (subscribes to SignalR)
        // Subscribe to unread count changes to update UI
        _conversationStateService.TotalUnreadCount
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => _callbacks.RaiseTotalDmUnreadCountChanged())
            .DisposeWith(_subscriptions);

        // GamingStationStatusChanged now handled by GamingStationViewModel directly
        // Gaming Station command events now handled by IGamingStationCommandHandler

        // Channel typing cleanup now handled by TypingStore
        // DM typing cleanup is handled internally by DMContentViewModel
    }

    private void OnChannelUpdated(ChannelResponse channel)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update SelectedChannel if it's the one that changed
            if (_callbacks.GetSelectedChannel()?.Id == channel.Id)
                _callbacks.SetSelectedChannel(channel);
        });
    }

    private void OnChannelDeleted(ChannelDeletedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // VoiceChannelViewModels handled by VoiceChannelViewModelManager
            // ChannelStore updated by SignalREventDispatcher

            // Select a different channel if the deleted one was selected
            var firstTextChannel = _callbacks.GetFirstTextChannel();
            if (_callbacks.GetSelectedChannel()?.Id == e.ChannelId && firstTextChannel != null)
                _callbacks.SetSelectedChannel(firstTextChannel);
        });
    }

    private void OnMessagePinned(MessagePinnedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Update pinned messages popup if open (view-specific; store update via SignalREventDispatcher)
            _pinnedMessagesPopup?.OnMessagePinStatusChanged(e.MessageId, e.IsPinned);
        });
    }

    private void OnCommunityMemberAdded(CommunityMemberAddedEvent e)
    {
        // If this is for the currently selected community, reload members via coordinator
        var selectedCommunity = _callbacks.GetSelectedCommunity();
        if (selectedCommunity is not null && e.CommunityId == selectedCommunity.Id)
        {
            _ = _communityCoordinator.ReloadMembersAsync(selectedCommunity.Id);
        }
    }

    private void OnDisconnectedFromVoice(DisconnectedFromVoiceEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            // Let coordinator handle WebRTC/screen share cleanup
            _screenShare?.ForceStop();
            var channelName = await _voiceCoordinator.HandleForcedDisconnectAsync(e.ChannelId);

            if (channelName != null)
            {
                // Clear all voice-related UI state
                _callbacks.ClearVoiceUiState();

                // Track that we're in voice on another device
                _stores.VoiceStore.SetVoiceOnOtherDevice(e.ChannelId, channelName);
            }
        });
    }

    private void OnSpeakingChanged(bool isSpeaking)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            // Update local speaking state via VoiceControlViewModel
            _voiceControl?.UpdateSpeakingState(isSpeaking);

            // Capture channel reference to avoid race condition during leave
            var currentChannel = _callbacks.GetCurrentVoiceChannel();
            if (currentChannel is not null)
            {
                // Broadcast to others - VoiceStore will be updated via SignalR event response
                await _signalR.UpdateSpeakingStateAsync(currentChannel.Id, isSpeaking);

                // VoiceChannelViewModel and VoiceChannelContentViewModel auto-update via VoiceStore subscription
            }
        });
    }

    private void OnServerVoiceStateChanged(ServerVoiceStateChangedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // VoiceChannelViewModel auto-updates via VoiceStore subscription

            // If this is the current user being server-muted, update local state
            if (e.TargetUserId == _currentUserId)
            {
                _voiceControl?.HandleServerVoiceStateUpdate(e.IsServerMuted, e.IsServerDeafened);

                // Apply to WebRTC
                if (e.IsServerMuted == true)
                    _webRtc.SetMuted(true);
                if (e.IsServerDeafened == true)
                    _webRtc.SetMuted(true);
            }
        });
    }

    private void OnUserMoved(UserMovedEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (e.UserId == _currentUserId)
            {
                // Leave current channel and join new one (updates view state)
                await _callbacks.LeaveVoiceChannelAsync();
                var channel = _callbacks.GetChannelById(e.ToChannelId);
                if (channel != null)
                {
                    await _callbacks.JoinVoiceChannelAsync(channel);
                }
            }
        });
    }

    private void OnVideoStreamStopped(VideoStreamStoppedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // If we're viewing this stream in fullscreen, close it
            if (_videoFullscreen?.IsOpen == true && _videoFullscreen?.Stream?.UserId == e.UserId)
            {
                _videoFullscreen?.Close();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from events
        _signalR.ChannelUpdated -= OnChannelUpdated;
        _signalR.ChannelDeleted -= OnChannelDeleted;
        _signalR.MessagePinned -= OnMessagePinned;
        _signalR.CommunityMemberAdded -= OnCommunityMemberAdded;
        _signalR.DisconnectedFromVoice -= OnDisconnectedFromVoice;
        _webRtc.SpeakingChanged -= OnSpeakingChanged;
        _signalR.ServerVoiceStateChanged -= OnServerVoiceStateChanged;
        _signalR.UserMoved -= OnUserMoved;
        _signalR.VideoStreamStopped -= OnVideoStreamStopped;

        _subscriptions.Dispose();
    }
}
