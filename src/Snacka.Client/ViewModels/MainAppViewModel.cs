using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;
using Avalonia.Controls;
using Avalonia.Threading;
using Snacka.Client.Controls;
using Snacka.Client.Coordinators;
using Snacka.Client.Models;
using Snacka.Client.Services;
using Snacka.Client.Services.Autocomplete;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Client.Stores;
using Snacka.Shared.Models;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

public class MainAppViewModel : ViewModelBase, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly IWebRtcService _webRtc;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly ISettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly IControllerStreamingService _controllerStreamingService;
    private readonly IControllerHostService _controllerHostService;
    private readonly StoreContainer _stores;
    private readonly ISignalREventDispatcher _signalREventDispatcher;
    private readonly IChannelCoordinator _channelCoordinator;
    private readonly ICommunityCoordinator _communityCoordinator;
    private readonly IVoiceCoordinator _voiceCoordinator;
    private readonly IMessageCoordinator _messageCoordinator;
    private readonly IGamingStationCommandHandler _gamingStationCommandHandler;
    private readonly AuthResponse _auth;
    private readonly Action _onLogout;
    private readonly Action? _onSwitchServer;
    private readonly Action? _onOpenSettings;
    private readonly string _baseUrl;

    private CommunityResponse? _selectedCommunity;
    private ChannelResponse? _selectedChannel;
    private string _messageInput = string.Empty;
    private bool _isLoading;
    private bool _isMessagesLoading;
    private string? _errorMessage;
    private ChannelState? _editingChannel;
    private string _editingChannelName = string.Empty;
    private ChannelState? _channelPendingDelete;
    private MessageResponse? _editingMessage;
    private string _editingMessageContent = string.Empty;
    private MessageResponse? _replyingToMessage;

    // Voice channel state
    private ChannelResponse? _currentVoiceChannel;
    private bool _isMuted;
    private bool _isDeafened;
    private bool _isCameraOn;
    private bool _isScreenSharing;
    private bool _isSpeaking;
    private VoiceConnectionStatus _voiceConnectionStatus = VoiceConnectionStatus.Disconnected;

    // Multi-device voice state is now tracked by VoiceStore

    // Voice channels with participant tracking (managed by VoiceChannelViewModelManager)
    private VoiceChannelViewModelManager? _voiceChannelManager;

    // Voice channel content view (for displaying video grid)
    private ChannelResponse? _selectedVoiceChannelForViewing;
    private VoiceChannelContentViewModel? _voiceChannelContent;

    // Permission state
    private UserRole? _currentUserRole;

    // Inline DM ViewModel (encapsulates all DM state and logic)
    private DMContentViewModel? _dmContent;

    // Screen share picker state
    private bool _isScreenSharePickerOpen;
    private ScreenSharePickerViewModel? _screenSharePicker;

    // Voice video overlay state (for viewing video grid while navigating elsewhere)
    private bool _isVoiceVideoOverlayOpen;

    // Video fullscreen ViewModel (encapsulates fullscreen state, annotations, and input capture)
    private VideoFullscreenViewModel? _videoFullscreen;

    // Gaming station ViewModel (encapsulates all gaming station state and logic)
    private GamingStationViewModel? _gamingStation;
    private readonly ObservableCollection<MyGamingStationInfo> _myGamingStations = new();
    private string _currentMachineId = "";  // Unique ID for this machine

    // Voice control ViewModel (encapsulates mute, deafen, speaking state and commands)
    private VoiceControlViewModel? _voiceControl;

    // Screen share ViewModel (encapsulates screen sharing state and commands)
    private ScreenShareViewModel? _screenShare;

    // Message input ViewModel (encapsulates message composition, editing, reply, autocomplete)
    private MessageInputViewModel? _messageInputVm;

    // Channel management ViewModel (encapsulates channel editing, deletion, reordering)
    private ChannelManagementViewModel? _channelManagementVm;

    // Welcome modal ViewModel (encapsulates first-time user welcome flow)
    private WelcomeModalViewModel? _welcomeModal;

    // Controller host ViewModel (encapsulates controller access requests and sessions)
    private ControllerHostViewModel? _controllerHost;

    // Thread panel ViewModel (encapsulates thread display and updates)
    private ThreadPanelViewModel? _threadPanel;

    /// <summary>
    /// Fired when an NV12 frame should be rendered to GPU fullscreen view.
    /// Args: (width, height, nv12Data)
    /// </summary>
    public event Action<int, int, byte[]>? GpuFullscreenFrameReceived;

    // Drawing annotation service (shared with VideoFullscreenViewModel and sharer overlay)
    private readonly AnnotationService _annotationService;

    // Sharer annotation overlay manager (handles overlay windows for screen sharing)
    private SharerAnnotationOverlayManager? _sharerAnnotationOverlay;

    // Typing indicator state - backed by TypingStore, subscriptions update when channel changes
    private bool _isAnyoneTyping;
    private string _typingIndicatorText = string.Empty;
    private IDisposable? _typingSubscription;

    // Self-contained GIF picker (appears in message list)
    private GifPickerViewModel? _gifPicker;

    // File attachment state (legacy fallback, normally handled by MessageInputViewModel)
    private ObservableCollection<PendingAttachment> _pendingAttachments = new();
    private AttachmentResponse? _lightboxImage;

    // Pinned messages popup (extracted ViewModel)
    private PinnedMessagesPopupViewModel? _pinnedMessagesPopup;

    // Invite user popup (extracted ViewModel)
    private InviteUserPopupViewModel? _inviteUserPopup;

    // Centralized conversation state service (shared across ViewModels)
    private readonly IConversationStateService _conversationStateService;
    private bool _isRecentDmsExpanded = true;

    // Activity feed state (right panel)
    private ActivityFeedViewModel? _activityFeed;

    // Members list ViewModel (encapsulates member operations and nickname editing)
    private MembersListViewModel? _membersListViewModel;

    // Server feature flags
    private readonly bool _isGifsEnabled;

    // Connection state
    private ConnectionState _connectionState = ConnectionState.Connected;
    private int _reconnectSecondsRemaining;

    // Store subscriptions (for Redux-style state management migration)
    private readonly CompositeDisposable _storeSubscriptions = new();

    // Store-backed bindable collections (for view binding migration)
    private readonly ReadOnlyObservableCollection<ChannelResponse> _storeAllChannels;
    private readonly ReadOnlyObservableCollection<ChannelResponse> _storeTextChannels;
    private readonly ReadOnlyObservableCollection<ChannelResponse> _storeVoiceChannels;
    private readonly ReadOnlyObservableCollection<MessageResponse> _storeMessages;
    private readonly ReadOnlyObservableCollection<CommunityMemberResponse> _storeMembers;
    private readonly ReadOnlyObservableCollection<CommunityResponse> _storeCommunities;

    // Community discovery (extracted ViewModel)
    private CommunityDiscoveryViewModel? _communityDiscovery;
    private bool _isWelcomeModalOpen;

    // Audio device quick selector (extracted ViewModel)
    private AudioDeviceQuickSelectViewModel? _audioDeviceQuickSelect;

    public MainAppViewModel(IApiClient apiClient, ISignalRService signalR, IWebRtcService webRtc, IScreenCaptureService screenCaptureService, ISettingsStore settingsStore, IAudioDeviceService audioDeviceService, IControllerStreamingService controllerStreamingService, IControllerHostService controllerHostService, string baseUrl, AuthResponse auth, IConversationStateService conversationStateService, StoreContainer stores, ISignalREventDispatcher signalREventDispatcher, IChannelCoordinator channelCoordinator, ICommunityCoordinator communityCoordinator, IVoiceCoordinator voiceCoordinator, IMessageCoordinator messageCoordinator, IGamingStationCommandHandler gamingStationCommandHandler, Action onLogout, Action? onSwitchServer = null, Action? onOpenSettings = null, bool gifsEnabled = false)
    {
        _apiClient = apiClient;
        _conversationStateService = conversationStateService;
        _isGifsEnabled = gifsEnabled;
        _screenCaptureService = screenCaptureService;
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _controllerStreamingService = controllerStreamingService;
        _controllerHostService = controllerHostService;
        _signalR = signalR;
        _webRtc = webRtc;
        _baseUrl = baseUrl;
        _auth = auth;
        _onLogout = onLogout;
        _onSwitchServer = onSwitchServer;
        _onOpenSettings = onOpenSettings;
        _stores = stores;
        _signalREventDispatcher = signalREventDispatcher;
        _channelCoordinator = channelCoordinator;
        _communityCoordinator = communityCoordinator;
        _voiceCoordinator = voiceCoordinator;
        _messageCoordinator = messageCoordinator;
        _gamingStationCommandHandler = gamingStationCommandHandler;

        // Load persisted mute/deafen state
        _isMuted = _settingsStore.Settings.IsMuted;
        _isDeafened = _settingsStore.Settings.IsDeafened;

        // Generate unique machine ID for gaming station feature
        _currentMachineId = GetOrCreateMachineId();

        // Set local user ID for WebRTC
        if (_webRtc is WebRtcService webRtcService)
        {
            webRtcService.SetLocalUserId(auth.UserId);
        }

        // Create voice channel content view model for video grid
        // Uses VoiceStore and ChannelStore for reactive state (Redux-style)
        _voiceChannelContent = new VoiceChannelContentViewModel(
            _webRtc,
            _stores.VoiceStore,
            _stores.ChannelStore,
            _signalR,
            auth.UserId);
        _voiceChannelContent.ParticipantLeft += (channelId, userId) =>
        {
            // Close fullscreen if viewing this user's stream
            if (IsVideoFullscreen && FullscreenStream?.UserId == userId)
            {
                CloseFullscreen();
            }
        };

        // Create voice channel ViewModel manager
        _voiceChannelManager = new VoiceChannelViewModelManager(
            _stores.VoiceStore,
            auth.UserId,
            onVolumeChanged: (userId, volume) => _webRtc.SetUserVolume(userId, volume),
            getInitialVolume: userId => _webRtc.GetUserVolume(userId));

        // Initialize capability warning commands
        DismissCapabilityWarningCommand = ReactiveCommand.Create(() =>
        {
            Program.CapabilityService?.DismissValidationWarning();
            this.RaisePropertyChanged(nameof(HasCapabilityWarnings));
        });

        ShowCapabilityDetailsCommand = ReactiveCommand.Create(() =>
        {
            IsCapabilityDetailsOpen = true;
        });

        CloseCapabilityDetailsCommand = ReactiveCommand.Create(() =>
        {
            IsCapabilityDetailsOpen = false;
        });

        // Wire up gaming station remote control callbacks
        _voiceChannelContent.OnGamingStationShareScreen = async machineId =>
        {
            try
            {
                await _signalR.CommandStationStartScreenShareAsync(machineId);
            }
            catch
            {
                ErrorMessage = "Failed to start screen share on gaming station";
            }
        };
        _voiceChannelContent.OnGamingStationStopShareScreen = async machineId =>
        {
            try
            {
                await _signalR.CommandStationStopScreenShareAsync(machineId);
            }
            catch
            {
                ErrorMessage = "Failed to stop screen share on gaming station";
            }
        };

        // Create annotation service for drawing on screen shares
        _annotationService = new AnnotationService(_signalR);

        // Create video fullscreen ViewModel (handles fullscreen state, annotations, input capture)
        _videoFullscreen = new VideoFullscreenViewModel(
            _webRtc,
            _annotationService,
            _controllerStreamingService,
            _stores.VoiceStore,
            _auth.UserId);
        _videoFullscreen.GpuFrameReceived += (w, h, data) => GpuFullscreenFrameReceived?.Invoke(w, h, data);

        // Subscribe to WebRTC connection status changes
        _webRtc.ConnectionStatusChanged += status =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                VoiceConnectionStatus = status;
                // Also update voice store (for migration to Redux-style architecture)
                _stores.VoiceStore.SetConnectionStatus(status);
            });
        };

        // Subscribe to SignalR connection state changes
        _signalR.ConnectionStateChanged += state =>
        {
            Dispatcher.UIThread.Post(() => ConnectionState = state);
        };
        _signalR.ReconnectCountdownChanged += seconds =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ReconnectSecondsRemaining = seconds;
                this.RaisePropertyChanged(nameof(ReconnectStatusText));
            });
        };
        ConnectionState = _signalR.State;

        // Subscribe to store changes (for Redux-style state management migration)
        // These subscriptions complement the existing SignalR handlers and will eventually replace them
        _storeSubscriptions.Add(
            _stores.PresenceStore.ConnectionStatus
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(status =>
                {
                    // Update ViewModel state from store (eventually this will be the only source)
                    if (_connectionState != status)
                    {
                        ConnectionState = status;
                    }
                }));

        _storeSubscriptions.Add(
            _stores.PresenceStore.ReconnectSecondsRemaining
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(seconds =>
                {
                    if (_reconnectSecondsRemaining != seconds)
                    {
                        ReconnectSecondsRemaining = seconds;
                        this.RaisePropertyChanged(nameof(ReconnectStatusText));
                    }
                }));

        _storeSubscriptions.Add(
            _stores.VoiceStore.ConnectionStatus
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(status =>
                {
                    if (_voiceConnectionStatus != status)
                    {
                        VoiceConnectionStatus = status;
                    }
                }));

        // Subscribe to voice-on-other-device state changes
        _storeSubscriptions.Add(
            _stores.VoiceStore.VoiceOnOtherDevice
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    this.RaisePropertyChanged(nameof(VoiceOnOtherDeviceChannelId));
                    this.RaisePropertyChanged(nameof(VoiceOnOtherDeviceChannelName));
                    this.RaisePropertyChanged(nameof(IsInVoiceOnOtherDevice));
                }));

        // Create store-backed bindable collections for channels
        // These use DynamicData to transform ChannelState -> ChannelResponse and bind to ReadOnlyObservableCollection
        _storeSubscriptions.Add(
            _stores.ChannelStore.Connect()
                .Transform(ToChannelResponse)
                .SortBy(c => c.Position)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _storeAllChannels)
                .Subscribe());

        _storeSubscriptions.Add(
            _stores.ChannelStore.Connect()
                .Filter(c => c.Type == ChannelType.Text)
                .Transform(ToChannelResponse)
                .SortBy(c => c.Position)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _storeTextChannels)
                .Subscribe());

        _storeSubscriptions.Add(
            _stores.ChannelStore.Connect()
                .Filter(c => c.Type == ChannelType.Voice)
                .Transform(ToChannelResponse)
                .SortBy(c => c.Position)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _storeVoiceChannels)
                .Subscribe());

        // Create store-backed bindable collection for messages
        // Filter to current channel and exclude thread replies (shown in thread view)
        _storeSubscriptions.Add(
            _stores.MessageStore.Connect()
                .AutoRefreshOnObservable(_ => _stores.MessageStore.CurrentChannelId)
                .Filter(m =>
                {
                    var currentChannelId = _stores.MessageStore.GetCurrentChannelId();
                    return currentChannelId.HasValue &&
                           m.ChannelId == currentChannelId.Value &&
                           m.ThreadParentMessageId == null; // Exclude thread replies
                })
                .Transform(ToMessageResponse)
                .SortBy(m => m.CreatedAt)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _storeMessages)
                .Subscribe());

        // Create store-backed bindable collection for community members
        _storeSubscriptions.Add(
            _stores.CommunityStore.ConnectMembers()
                .Transform(ToCommunityMemberResponse)
                .SortBy(m => m.Username)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _storeMembers)
                .Subscribe());

        // Create store-backed bindable collection for communities
        _storeSubscriptions.Add(
            _stores.CommunityStore.Connect()
                .Transform(ToCommunityResponse)
                .SortBy(c => c.Name)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _storeCommunities)
                .Subscribe());

        // Sync SelectedChannel from store to ViewModel (store → ViewModel)
        _storeSubscriptions.Add(
            _stores.ChannelStore.SelectedChannel
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(state =>
                {
                    var response = state is not null ? ToChannelResponse(state) : null;
                    if (_selectedChannel?.Id != response?.Id)
                    {
                        _selectedChannel = response;
                        this.RaisePropertyChanged(nameof(SelectedChannel));
                    }
                }));

        // Sync SelectedCommunity from store to ViewModel (store → ViewModel)
        _storeSubscriptions.Add(
            _stores.CommunityStore.SelectedCommunity
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(state =>
                {
                    var response = state is not null ? ToCommunityResponse(state) : null;
                    if (_selectedCommunity?.Id != response?.Id)
                    {
                        _selectedCommunity = response;
                        this.RaisePropertyChanged(nameof(SelectedCommunity));
                        this.RaisePropertyChanged(nameof(IsVoiceInDifferentCommunity));
                        this.RaisePropertyChanged(nameof(VoiceCommunityName));
                    }
                }));

        // Initialize GIF ViewModels (only if GIFs are enabled)
        if (_isGifsEnabled)
        {
            _gifPicker = new GifPickerViewModel(apiClient);
            _gifPicker.SendRequested += OnGifPickerSendRequested;

            _gifPanel = new GifPanelViewModel(apiClient);
            _gifPanel.GifSelected += OnGifPanelGifSelected;
        }

        // Create the audio device quick select ViewModel
        _audioDeviceQuickSelect = new AudioDeviceQuickSelectViewModel(settingsStore, audioDeviceService);
        _audioDeviceQuickSelect.PushToTalkMuteChanged += unmute => _ = _voiceControl?.SetMutedAsync(!unmute);

        // Create the inline DM ViewModel
        _dmContent = new DMContentViewModel(
            apiClient,
            signalR,
            _conversationStateService,
            auth.UserId,
            error => ErrorMessage = error);

        // Forward IsOpen changes to IsViewingDM property changed notification
        _dmContent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DMContentViewModel.IsOpen))
            {
                this.RaisePropertyChanged(nameof(IsViewingDM));
            }
        };

        // Create the members list ViewModel (using store-backed collection)
        _membersListViewModel = new MembersListViewModel(
            apiClient,
            _stores.CommunityStore,
            _stores.VoiceStore,
            auth.UserId,
            _storeMembers,
            _myGamingStations,
            member => StartDMWithMember(member),
            userId => _conversationStateService.GetUnreadCountForUser(userId),
            machineId => CommandStationJoinCurrentChannelAsync(machineId),
            error => ErrorMessage = error);

        // Create the activity feed ViewModel
        _activityFeed = new ActivityFeedViewModel(
            signalR,
            apiClient,
            _stores.CommunityStore,
            auth.UserId,
            LoadCommunitiesAsync,
            (userId, displayName) => OpenDmFromActivity(userId, displayName));

        // Create extracted popup ViewModels
        _pinnedMessagesPopup = new PinnedMessagesPopupViewModel(
            apiClient,
            _stores.ChannelStore);

        _inviteUserPopup = new InviteUserPopupViewModel(
            apiClient,
            _stores.CommunityStore);

        _communityDiscovery = new CommunityDiscoveryViewModel(
            apiClient,
            LoadCommunitiesAsync);

        // Create gaming station ViewModel (handles its own SignalR status event subscription)
        // Reads current voice channel from VoiceStore (Redux-style)
        _gamingStation = new GamingStationViewModel(
            apiClient,
            signalR,
            settingsStore,
            _stores.VoiceStore,
            _myGamingStations,
            _currentMachineId,
            auth.UserId);
        _gamingStation.ViewOpening += () =>
        {
            _dmContent?.Close();
            SelectedVoiceChannelForViewing = null;
        };
        _gamingStation.ErrorOccurred += error => ErrorMessage = error;

        // Create voice control ViewModel
        // Reads current voice channel from VoiceStore (Redux-style)
        _voiceControl = new VoiceControlViewModel(
            _stores.VoiceStore,
            settingsStore,
            webRtc,
            signalR,
            auth.UserId);

        // Sync VoiceControlViewModel state with local fields (for backwards compatibility)
        _voiceControl.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VoiceControlViewModel.IsMuted):
                    _isMuted = _voiceControl.IsMuted;
                    this.RaisePropertyChanged(nameof(IsMuted));
                    break;
                case nameof(VoiceControlViewModel.IsDeafened):
                    _isDeafened = _voiceControl.IsDeafened;
                    this.RaisePropertyChanged(nameof(IsDeafened));
                    break;
                case nameof(VoiceControlViewModel.IsSpeaking):
                    _isSpeaking = _voiceControl.IsSpeaking;
                    this.RaisePropertyChanged(nameof(IsSpeaking));
                    break;
                case nameof(VoiceControlViewModel.IsCameraOn):
                    _isCameraOn = _voiceControl.IsCameraOn;
                    this.RaisePropertyChanged(nameof(IsCameraOn));
                    break;
                case nameof(VoiceControlViewModel.IsScreenSharing):
                    _isScreenSharing = _voiceControl.IsScreenSharing;
                    this.RaisePropertyChanged(nameof(IsScreenSharing));
                    break;
                case nameof(VoiceControlViewModel.VoiceConnectionStatus):
                    _voiceConnectionStatus = _voiceControl.VoiceConnectionStatus;
                    this.RaisePropertyChanged(nameof(VoiceConnectionStatus));
                    this.RaisePropertyChanged(nameof(VoiceConnectionStatusText));
                    this.RaisePropertyChanged(nameof(IsVoiceConnecting));
                    this.RaisePropertyChanged(nameof(IsVoiceConnected));
                    break;
            }
        };

        // Create screen share ViewModel
        // Reads current voice channel from VoiceStore (Redux-style)
        _screenShare = new ScreenShareViewModel(
            screenCaptureService,
            signalR,
            webRtc,
            _annotationService,
            _stores.VoiceStore,
            auth.UserId,
            auth.Username);

        // Sync ScreenShareViewModel state with local fields
        _screenShare.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ScreenShareViewModel.IsScreenSharing):
                    _isScreenSharing = _screenShare.IsScreenSharing;
                    this.RaisePropertyChanged(nameof(IsScreenSharing));
                    break;
                case nameof(ScreenShareViewModel.IsScreenSharePickerOpen):
                    _isScreenSharePickerOpen = _screenShare.IsScreenSharePickerOpen;
                    this.RaisePropertyChanged(nameof(IsScreenSharePickerOpen));
                    break;
                case nameof(ScreenShareViewModel.ScreenSharePicker):
                    _screenSharePicker = _screenShare.ScreenSharePicker;
                    this.RaisePropertyChanged(nameof(ScreenSharePicker));
                    break;
                case nameof(ScreenShareViewModel.IsDrawingAllowedForViewers):
                    this.RaisePropertyChanged(nameof(IsDrawingAllowedForViewers));
                    break;
            }
        };

        // Create sharer annotation overlay manager
        _sharerAnnotationOverlay = new SharerAnnotationOverlayManager();
        _sharerAnnotationOverlay.CloseRequested += () => _screenShare?.OnAnnotationToolbarCloseRequested();

        // Wire up screen share events to overlay manager
        _screenShare.ShowAnnotationOverlayRequested += (settings, annotationVm) =>
            _sharerAnnotationOverlay.Show(settings, annotationVm);
        _screenShare.HideAnnotationOverlayRequested += () =>
            _sharerAnnotationOverlay.Hide();
        _screenShare.ErrorOccurred += error => ErrorMessage = error;

        // Create message input ViewModel
        _messageInputVm = new MessageInputViewModel(
            apiClient,
            signalR,
            _stores.MessageStore,
            _stores.ChannelStore,
            auth.UserId,
            () => _storeMembers,
            _isGifsEnabled);

        // Sync MessageInputViewModel state with local fields
        _messageInputVm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MessageInputViewModel.MessageInput):
                    _messageInput = _messageInputVm.MessageInput;
                    this.RaisePropertyChanged(nameof(MessageInput));
                    break;
                case nameof(MessageInputViewModel.EditingMessage):
                    _editingMessage = _messageInputVm.EditingMessage;
                    this.RaisePropertyChanged(nameof(EditingMessage));
                    break;
                case nameof(MessageInputViewModel.EditingMessageContent):
                    _editingMessageContent = _messageInputVm.EditingMessageContent;
                    this.RaisePropertyChanged(nameof(EditingMessageContent));
                    break;
                case nameof(MessageInputViewModel.ReplyingToMessage):
                    _replyingToMessage = _messageInputVm.ReplyingToMessage;
                    this.RaisePropertyChanged(nameof(ReplyingToMessage));
                    this.RaisePropertyChanged(nameof(IsReplying));
                    break;
                case nameof(MessageInputViewModel.IsLoading):
                    IsMessagesLoading = _messageInputVm.IsLoading;
                    break;
                case nameof(MessageInputViewModel.IsAutocompletePopupOpen):
                    this.RaisePropertyChanged(nameof(IsAutocompletePopupOpen));
                    break;
                case nameof(MessageInputViewModel.SelectedAutocompleteIndex):
                    this.RaisePropertyChanged(nameof(SelectedAutocompleteIndex));
                    break;
                case nameof(MessageInputViewModel.HasPendingAttachments):
                    this.RaisePropertyChanged(nameof(HasPendingAttachments));
                    break;
            }
        };

        // Wire up events
        _messageInputVm.ErrorOccurred += error => ErrorMessage = error;
        _messageInputVm.GifPreviewRequested += async query => await ShowGifPreviewAsync(query);

        // Create channel management ViewModel
        _channelManagementVm = new ChannelManagementViewModel(
            _channelCoordinator,
            _stores.ChannelStore,
            _stores.CommunityStore,
            () => _voiceChannelManager);

        // Sync ChannelManagementViewModel state with local fields
        _channelManagementVm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ChannelManagementViewModel.EditingChannel):
                    _editingChannel = _channelManagementVm.EditingChannel;
                    this.RaisePropertyChanged(nameof(EditingChannel));
                    break;
                case nameof(ChannelManagementViewModel.EditingChannelName):
                    _editingChannelName = _channelManagementVm.EditingChannelName;
                    this.RaisePropertyChanged(nameof(EditingChannelName));
                    break;
                case nameof(ChannelManagementViewModel.ChannelPendingDelete):
                    _channelPendingDelete = _channelManagementVm.ChannelPendingDelete;
                    this.RaisePropertyChanged(nameof(ChannelPendingDelete));
                    this.RaisePropertyChanged(nameof(ShowChannelDeleteConfirmation));
                    break;
            }
        };

        // Wire up channel management events
        _channelManagementVm.ErrorOccurred += error => ErrorMessage = error;
        _channelManagementVm.LoadingChanged += loading => IsLoading = loading;

        // Create welcome modal ViewModel
        _welcomeModal = new WelcomeModalViewModel(_settingsStore);

        // Sync WelcomeModalViewModel state with local field
        _welcomeModal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WelcomeModalViewModel.IsOpen))
            {
                _isWelcomeModalOpen = _welcomeModal.IsOpen;
                this.RaisePropertyChanged(nameof(IsWelcomeModalOpen));
            }
        };

        // Wire up welcome modal events
        _welcomeModal.BrowseCommunitiesRequested += async () => await _communityDiscovery!.OpenAsync();
        _welcomeModal.CreateCommunityRequested += async () => await CreateCommunityAsync();

        // Create controller host ViewModel
        _controllerHost = new ControllerHostViewModel(
            _controllerHostService,
            _stores.VoiceStore);

        // Create thread panel ViewModel (handles its own SignalR event subscriptions)
        _threadPanel = new ThreadPanelViewModel(_apiClient, _stores.MessageStore, _signalR, _auth.UserId);

        // Commands
        LogoutCommand = ReactiveCommand.Create(_onLogout);
        SwitchServerCommand = _onSwitchServer is not null
            ? ReactiveCommand.Create(_onSwitchServer)
            : null;
        OpenSettingsCommand = _onOpenSettings is not null
            ? ReactiveCommand.Create(_onOpenSettings)
            : null;
        OpenGamingStationsCommand = _gamingStation.OpenCommand;
        RegisterStationCommand = _gamingStation.RegisterCommand;
        ConnectToStationCommand = _gamingStation.ConnectCommand;
        ManageStationCommand = _gamingStation.ManageCommand;
        DisconnectFromStationCommand = _gamingStation.DisconnectCommand;
        ToggleStationFullscreenCommand = _gamingStation.ToggleFullscreenCommand;
        CreateCommunityCommand = ReactiveCommand.CreateFromTask(CreateCommunityAsync);
        RefreshCommunitiesCommand = ReactiveCommand.CreateFromTask(LoadCommunitiesAsync);
        SelectCommunityCommand = ReactiveCommand.Create<CommunityResponse>(community =>
        {
            _gamingStation?.Close();
            SelectedCommunity = community;
        });
        SelectChannelCommand = ReactiveCommand.Create<ChannelResponse>(channel =>
        {
            // Close the DM view when selecting a text channel
            _dmContent?.Close();
            // Clear voice channel viewing when selecting a text channel
            SelectedVoiceChannelForViewing = null;
            // Close gaming stations view
            _gamingStation?.Close();
            SelectedChannel = channel;
        });

        // Channel creation canExecute - prevents rapid clicks
        var canCreateChannel = this.WhenAnyValue(
            x => x.SelectedCommunity,
            x => x.IsLoading,
            (community, isLoading) => community is not null && !isLoading);

        CreateChannelCommand = ReactiveCommand.CreateFromTask(CreateChannelAsync, canCreateChannel);

        // Channel management commands (delegated to ChannelManagementViewModel)
        StartEditChannelCommand = _channelManagementVm.StartEditChannelCommand;
        SaveChannelNameCommand = _channelManagementVm.SaveChannelNameCommand;
        CancelEditChannelCommand = _channelManagementVm.CancelEditChannelCommand;
        DeleteChannelCommand = _channelManagementVm.DeleteChannelCommand;
        ConfirmDeleteChannelCommand = _channelManagementVm.ConfirmDeleteChannelCommand;
        CancelDeleteChannelCommand = _channelManagementVm.CancelDeleteChannelCommand;
        ReorderChannelsCommand = _channelManagementVm.ReorderChannelsCommand;
        PreviewReorderCommand = _channelManagementVm.PreviewReorderCommand;
        CancelPreviewCommand = _channelManagementVm.CancelPreviewCommand;

        // Message commands (delegated to MessageInputViewModel)
        StartEditMessageCommand = _messageInputVm.StartEditMessageCommand;
        SaveMessageEditCommand = _messageInputVm.SaveMessageEditCommand;
        CancelEditMessageCommand = _messageInputVm.CancelEditMessageCommand;
        DeleteMessageCommand = _messageInputVm.DeleteMessageCommand;
        ReplyToMessageCommand = _messageInputVm.ReplyToMessageCommand;
        CancelReplyCommand = _messageInputVm.CancelReplyCommand;
        ToggleReactionCommand = ReactiveCommand.CreateFromTask<(MessageResponse Message, string Emoji)>(ToggleReactionAsync);
        AddReactionCommand = ReactiveCommand.CreateFromTask<(MessageResponse Message, string Emoji)>(AddReactionAsync);
        TogglePinCommand = ReactiveCommand.CreateFromTask<MessageResponse>(TogglePinAsync);
        ShowPinnedMessagesCommand = _pinnedMessagesPopup!.ShowCommand;
        ClosePinnedPopupCommand = _pinnedMessagesPopup!.CloseCommand;

        // Invite user commands (delegated to InviteUserPopupViewModel)
        OpenInviteUserPopupCommand = _inviteUserPopup!.OpenCommand;
        CloseInviteUserPopupCommand = _inviteUserPopup!.CloseCommand;
        InviteUserCommand = _inviteUserPopup!.InviteUserCommand;

        // Recent DMs commands (sidebar section)
        SelectRecentDmCommand = ReactiveCommand.Create<ConversationSummaryResponse>(SelectRecentDm);
        ToggleRecentDmsExpandedCommand = ReactiveCommand.Create(() => { IsRecentDmsExpanded = !IsRecentDmsExpanded; });

        // Community discovery commands (delegated to CommunityDiscoveryViewModel)
        OpenCommunityDiscoveryCommand = _communityDiscovery!.OpenCommand;
        CloseCommunityDiscoveryCommand = _communityDiscovery!.CloseCommand;
        JoinCommunityCommand = _communityDiscovery!.JoinCommunityCommand;
        RefreshDiscoverableCommunitiesCommand = _communityDiscovery!.OpenCommand;

        // Welcome modal commands (delegated to WelcomeModalViewModel)
        CloseWelcomeModalCommand = _welcomeModal.CloseCommand;
        WelcomeBrowseCommunitiesCommand = _welcomeModal.BrowseCommunitiesCommand;
        WelcomeCreateCommunityCommand = _welcomeModal.CreateCommunityCommand;

        // Controller access request commands (delegated to ControllerHostViewModel)
        AcceptControllerRequestCommand = _controllerHost.AcceptRequestCommand;
        DeclineControllerRequestCommand = _controllerHost.DeclineRequestCommand;
        StopControllerSessionCommand = _controllerHost.StopSessionCommand;
        ToggleMuteControllerSessionCommand = _controllerHost.ToggleMuteSessionCommand;

        // Thread commands (delegated to ThreadPanelViewModel)
        OpenThreadCommand = _threadPanel.OpenCommand;
        CloseThreadCommand = _threadPanel.CloseCommand;

        // Voice commands
        CreateVoiceChannelCommand = ReactiveCommand.CreateFromTask(CreateVoiceChannelAsync, canCreateChannel);
        JoinVoiceChannelCommand = ReactiveCommand.CreateFromTask<ChannelResponse>(JoinVoiceChannelAsync);
        LeaveVoiceChannelCommand = ReactiveCommand.CreateFromTask(LeaveVoiceChannelAsync);
        ToggleMuteCommand = _voiceControl.ToggleMuteCommand;
        ToggleDeafenCommand = _voiceControl.ToggleDeafenCommand;
        ToggleCameraCommand = _voiceControl.ToggleCameraCommand;
        ToggleScreenShareCommand = _screenShare.ToggleScreenShareCommand;

        // Voice video overlay commands (show/hide video grid while navigating)
        ShowVoiceVideoOverlayCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentVoiceChannel != null)
            {
                SelectedVoiceChannelForViewing = CurrentVoiceChannel;
                IsVoiceVideoOverlayOpen = true;
            }
        });
        HideVoiceVideoOverlayCommand = ReactiveCommand.Create(() =>
        {
            IsVoiceVideoOverlayOpen = false;
        });

        // Gaming station commands (delegate to GamingStationViewModel)
        DisableGamingStationCommand = _gamingStation.DisableCommand;

        // Admin voice commands
        ServerMuteUserCommand = ReactiveCommand.CreateFromTask<VoiceParticipantViewModel>(ServerMuteUserAsync);
        ServerDeafenUserCommand = ReactiveCommand.CreateFromTask<VoiceParticipantViewModel>(ServerDeafenUserAsync);
        MoveUserToChannelCommand = ReactiveCommand.CreateFromTask<(VoiceParticipantViewModel, VoiceChannelViewModel)>(MoveUserToChannelAsync);

        // SendMessageCommand is delegated to MessageInputViewModel
        SendMessageCommand = _messageInputVm.SendMessageCommand;

        // React to community selection changes
        this.WhenAnyValue(x => x.SelectedCommunity)
            .Where(c => c is not null)
            .SelectMany(_ => Observable.FromAsync(OnCommunitySelectedAsync))
            .Subscribe();

        // React to channel selection changes
        this.WhenAnyValue(x => x.SelectedChannel)
            .Where(c => c is not null)
            .SelectMany(_ => Observable.FromAsync(OnChannelSelectedAsync))
            .Subscribe();

        // Set up SignalR event handlers
        SetupSignalRHandlers();

        // Initialize the gaming station command handler with callbacks
        _gamingStationCommandHandler.Initialize(
            isGamingStationEnabled: () => IsGamingStationEnabled,
            joinVoiceChannel: JoinVoiceChannelAsync,
            leaveVoiceChannel: LeaveVoiceChannelAsync,
            getScreenShare: () => _screenShare,
            reportGamingStationStatus: async () =>
            {
                await ReportGamingStationStatusAsync();
                this.RaisePropertyChanged(nameof(IsGamingStationEnabled));
            });

        // Connect to SignalR and load servers on initialization
        Observable.FromAsync(InitializeAsync).Subscribe();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _signalR.ConnectAsync(_baseUrl, _auth.AccessToken);

            // Report gaming station status if enabled
            await ReportGamingStationStatusAsync();
        }
        catch
        {
            // SignalR connection failure handled gracefully
        }

        await LoadCommunitiesAsync();

        // Load recent DMs for the sidebar
        await LoadRecentDmsAsync();
    }

    private void SetupSignalRHandlers()
    {
        _signalR.ChannelCreated += channel => Dispatcher.UIThread.Post(() =>
        {
            // Channel list updates handled by SignalREventDispatcher -> ChannelStore
            // StoreTextChannels auto-updates via DynamicData binding

            if (SelectedCommunity is not null && channel.CommunityId == SelectedCommunity.Id)
            {
                // Add VoiceChannelViewModel for voice channels (view-specific, not in store)
                _voiceChannelManager?.AddChannel(channel);
            }
        });

        _signalR.ChannelUpdated += channel => Dispatcher.UIThread.Post(() =>
        {
            // Channel list updates handled by SignalREventDispatcher -> ChannelStore
            // Update SelectedChannel if it's the one that changed
            if (SelectedChannel?.Id == channel.Id)
                SelectedChannel = channel;
        });

        _signalR.ChannelDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            // Channel list updates handled by SignalREventDispatcher -> ChannelStore

            // Remove VoiceChannelViewModel if it was a voice channel (view-specific, not in store)
            _voiceChannelManager?.RemoveChannel(e.ChannelId);

            // Select a different channel if the deleted one was selected
            if (SelectedChannel?.Id == e.ChannelId && _storeTextChannels.Count > 0)
                SelectedChannel = _storeTextChannels[0];
        });

        _signalR.ChannelsReordered += e => Dispatcher.UIThread.Post(() =>
        {
            // Only update if it's for the current community
            if (SelectedCommunity?.Id != e.CommunityId) return;

            // Skip if we just initiated this reorder (we already updated optimistically)
            if (_channelManagementVm?.PendingReorderCommunityId == e.CommunityId)
            {
                _channelManagementVm?.ClearPendingReorder();
                return;
            }

            // Channel list updates handled by SignalREventDispatcher -> ChannelStore
            // StoreTextChannels auto-updates via DynamicData binding

            // Update VoiceChannelViewModels positions and re-sort (view-specific, not in store)
            _voiceChannelManager?.UpdatePositions(e.Channels);
        });

        // MessageReceived handled entirely by SignalREventDispatcher -> MessageStore, TypingStore, ChannelStore
        // MessageEdited, MessageDeleted, ThreadReplyReceived, ReactionUpdated handled by ThreadPanelViewModel
        // ThreadMetadataUpdated handled by SignalREventDispatcher -> MessageStore

        _signalR.MessagePinned += e => Dispatcher.UIThread.Post(() =>
        {
            // Update pinned messages popup if open (view-specific; store update via SignalREventDispatcher)
            _pinnedMessagesPopup?.OnMessagePinStatusChanged(e.MessageId, e.IsPinned);
        });

        // UserOnline/UserOffline handled by SignalREventDispatcher -> CommunityStore/PresenceStore

        _signalR.CommunityMemberAdded += e =>
        {
            // If this is for the currently selected community, reload members into the store
            if (SelectedCommunity is not null && e.CommunityId == SelectedCommunity.Id)
            {
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var result = await _apiClient.GetMembersAsync(SelectedCommunity.Id);
                        if (result.Success && result.Data is not null)
                        {
                            // Update the store - StoreMembers will auto-update via DynamicData
                            _stores.CommunityStore.SetMembers(SelectedCommunity.Id, result.Data);
                        }
                    }
                    catch
                    {
                        // Silently ignore member reload failures
                    }
                });
            }
        };

        // CommunityMemberRemoved event is now handled by SignalREventDispatcher -> CommunityStore
        // StoreMembers auto-updates via DynamicData binding

        // Voice channel events (VoiceParticipantJoined, VoiceParticipantLeft, VoiceStateChanged, SpeakingStateChanged)
        // now handled by VoiceChannelContentViewModel which subscribes to SignalR events directly
        // VoiceChannelViewModels auto-update via VoiceStore subscription

        // VoiceSessionActiveOnOtherDevice is handled by SignalREventDispatcher -> VoiceStore

        _signalR.DisconnectedFromVoice += e => Dispatcher.UIThread.Post(async () =>
        {
            if (CurrentVoiceChannel is not null && CurrentVoiceChannel.Id == e.ChannelId)
            {
                // Stop screen sharing first
                _screenShare?.ForceStop();
                await _webRtc.SetScreenSharingAsync(false);

                // Leave WebRTC connections (but don't notify server - it already knows)
                await _webRtc.LeaveVoiceChannelAsync();

                // VoiceChannelViewModel auto-updates via VoiceStore subscription
                // Get channel name before clearing state
                var voiceChannelVm = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);

                // Clear local state
                // VoiceChannelContentViewModel clears reactively when VoiceStore.CurrentChannelId becomes null
                CurrentVoiceChannel = null;
                IsCameraOn = false;
                IsScreenSharing = false;
                IsVoiceVideoOverlayOpen = false;
                SelectedVoiceChannelForViewing = null;

                // Track that we're now in voice on another device (via VoiceStore)
                _stores.VoiceStore.SetVoiceOnOtherDevice(e.ChannelId, voiceChannelVm?.Name);
            }
        });

        // Local speaking detection - broadcast to others and update own state
        _webRtc.SpeakingChanged += isSpeaking => Dispatcher.UIThread.Post(async () =>
        {
            // Update local speaking state via VoiceControlViewModel
            _voiceControl?.UpdateSpeakingState(isSpeaking);

            // Capture channel reference to avoid race condition during leave
            var currentChannel = CurrentVoiceChannel;
            if (currentChannel is not null)
            {
                // Broadcast to others - VoiceStore will be updated via SignalR event response
                await _signalR.UpdateSpeakingStateAsync(currentChannel.Id, isSpeaking);

                // VoiceChannelViewModel and VoiceChannelContentViewModel auto-update via VoiceStore subscription
            }
        });

        // Admin voice state changed (server mute/deafen)
        _signalR.ServerVoiceStateChanged += e => Dispatcher.UIThread.Post(() =>
        {

            // VoiceChannelViewModel auto-updates via VoiceStore subscription

            // If this is the current user being server-muted, update local state
            if (e.TargetUserId == _auth.UserId)
            {
                _voiceControl?.HandleServerVoiceStateUpdate(e.IsServerMuted, e.IsServerDeafened);

                // Apply to WebRTC
                if (e.IsServerMuted == true)
                    _webRtc.SetMuted(true);
                if (e.IsServerDeafened == true)
                    _webRtc.SetMuted(true);
            }
        });

        // User moved by admin
        _signalR.UserMoved += e => Dispatcher.UIThread.Post(async () =>
        {

            // If the current user was moved
            if (e.UserId == _auth.UserId)
            {
                // Leave current channel and join new one
                await LeaveVoiceChannelAsync();
                var channel = _storeAllChannels.FirstOrDefault(c => c.Id == e.ToChannelId);
                if (channel != null)
                {
                    await JoinVoiceChannelAsync(channel);
                }
            }
            else
            {
                // Update VoiceChannelViewModels - remove from old, add to new
                var oldChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.FromChannelId);
                oldChannel?.RemoveParticipant(e.UserId);
            }
        });

        // Video stream stopped - close fullscreen if viewing that stream
        _signalR.VideoStreamStopped += e => Dispatcher.UIThread.Post(() =>
        {
            // If we're viewing this stream in fullscreen, close it
            if (IsVideoFullscreen && FullscreenStream?.UserId == e.UserId)
            {
                CloseFullscreen();
            }
        });

        // Typing indicator events now handled by SignalREventDispatcher -> TypingStore
        // UI subscribes to store via _typingSubscription (set up in SubscribeToTypingStore)

        // Conversation events handled by ConversationStateService directly (subscribes to SignalR)
        // Subscribe to unread count changes to update UI
        _conversationStateService.TotalUnreadCount
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(TotalDmUnreadCount)));

        // GamingStationStatusChanged now handled by GamingStationViewModel directly
        // Gaming Station command events now handled by IGamingStationCommandHandler

        // Channel typing cleanup now handled by TypingStore
        // DM typing cleanup is handled internally by DMContentViewModel
    }

    /// <summary>
    /// Sets up typing indicator subscriptions for the given channel.
    /// Disposes any previous subscription and creates new ones.
    /// </summary>
    private void SubscribeToTypingStore(Guid channelId)
    {
        // Dispose previous subscription
        _typingSubscription?.Dispose();

        // Create a composite disposable to hold both subscriptions
        var disposables = new CompositeDisposable();

        // Subscribe to IsAnyoneTyping
        _stores.TypingStore.IsAnyoneTyping(channelId)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isTyping =>
            {
                _isAnyoneTyping = isTyping;
                this.RaisePropertyChanged(nameof(IsAnyoneTyping));
            })
            .DisposeWith(disposables);

        // Subscribe to TypingIndicatorText
        _stores.TypingStore.GetTypingIndicatorText(channelId)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text =>
            {
                _typingIndicatorText = text;
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
            })
            .DisposeWith(disposables);

        _typingSubscription = disposables;
    }

    /// <summary>
    /// Clears the typing indicator state and subscriptions.
    /// </summary>
    private void ClearTypingSubscription()
    {
        _typingSubscription?.Dispose();
        _typingSubscription = null;
        _isAnyoneTyping = false;
        _typingIndicatorText = string.Empty;
        this.RaisePropertyChanged(nameof(IsAnyoneTyping));
        this.RaisePropertyChanged(nameof(TypingIndicatorText));
    }

    private async Task OnCommunitySelectedAsync()
    {
        if (SelectedCommunity is null) return;

        IsLoading = true;
        try
        {
            // Delegate community selection to coordinator (handles SignalR, channels, members)
            await _communityCoordinator.SelectCommunityAsync(SelectedCommunity.Id);

            // ViewModel-specific: Create VoiceChannelViewModels from store data
            await SetupVoiceChannelViewModelsAsync();

            // ViewModel-specific: Set current user role
            var currentMember = _stores.CommunityStore.GetMember(_auth.UserId);
            CurrentUserRole = currentMember?.Role;
            _membersListViewModel?.UpdateCurrentUserRole(currentMember?.Role);

            // Select first text channel if none selected or belongs to different community
            var firstTextChannel = TextChannels.FirstOrDefault();
            if (firstTextChannel is not null && (SelectedChannel is null || SelectedChannel.CommunityId != SelectedCommunity.Id))
                SelectedChannel = firstTextChannel;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SetupVoiceChannelViewModelsAsync()
    {
        var voiceChannels = _storeVoiceChannels.ToList();

        // Initialize the manager with voice channels
        _voiceChannelManager?.Initialize(voiceChannels);

        // Load participants for each voice channel into VoiceStore
        foreach (var voiceChannel in voiceChannels)
        {
            var participants = await _signalR.GetVoiceParticipantsAsync(voiceChannel.Id);
            _stores.VoiceStore.SetParticipants(voiceChannel.Id, participants);
        }
    }

    private async Task OnChannelSelectedAsync()
    {
        if (SelectedChannel is not null && SelectedCommunity is not null)
        {
            // Delegate channel selection to coordinator (handles SignalR, API, store updates, messages)
            await _channelCoordinator.SelectTextChannelAsync(SelectedCommunity.Id, SelectedChannel.Id);

            // Update SelectedChannel with zero unread count (for UI binding)
            SelectedChannel = SelectedChannel with { UnreadCount = 0 };

            // Subscribe to typing indicators for this channel (ViewModel-specific)
            SubscribeToTypingStore(SelectedChannel.Id);
        }
        else
        {
            // No channel selected, clear typing state and coordinator selection
            ClearTypingSubscription();
            _channelCoordinator.ClearSelection();
        }
    }

    public string Username => _auth.Username;
    public string Email => _auth.Email;
    public Guid UserId => _auth.UserId;
    public ISettingsStore SettingsStore => _settingsStore;
    public IApiClient ApiClient => _apiClient;
    public string BaseUrl => _baseUrl;
    public string AccessToken => _auth.AccessToken;

    /// <summary>
    /// Gaming stations owned by the current user, across all their devices.
    /// </summary>
    public ObservableCollection<MyGamingStationInfo> MyGamingStations => _gamingStation?.MyGamingStations ?? _myGamingStations;

    /// <summary>
    /// The unique machine ID for this device.
    /// </summary>
    public string CurrentMachineId => _gamingStation?.CurrentMachineId ?? _currentMachineId;

    /// <summary>
    /// Whether this client has gaming station mode enabled.
    /// </summary>
    public bool IsGamingStationEnabled => _gamingStation?.IsGamingStationEnabled ?? false;

    /// <summary>
    /// Whether to show the gaming station status banner.
    /// Shows when gaming station mode is enabled.
    /// </summary>
    public bool ShowGamingStationBanner => _gamingStation?.ShowGamingStationBanner ?? false;

    /// <summary>
    /// Status text for the gaming station banner showing current channel.
    /// </summary>
    public string GamingStationChannelStatus =>
        CurrentVoiceChannel is not null
            ? $"In: {CurrentVoiceChannel.Name}"
            : "Not in a voice channel";

    /// <summary>
    /// Recent DM conversations for the sidebar, sorted by last message time.
    /// Uses the centralized ConversationStateService.
    /// </summary>
    public ReadOnlyObservableCollection<ConversationSummaryResponse> RecentDms => _conversationStateService.Conversations;

    /// <summary>
    /// Whether the Recent DMs section in the sidebar is expanded.
    /// </summary>
    public bool IsRecentDmsExpanded
    {
        get => _isRecentDmsExpanded;
        set => this.RaiseAndSetIfChanged(ref _isRecentDmsExpanded, value);
    }

    /// <summary>
    /// Total unread count across all DM conversations.
    /// Uses the centralized ConversationStateService.
    /// </summary>
    public int TotalDmUnreadCount => _conversationStateService.Conversations.Sum(c => c.UnreadCount);

    /// <summary>
    /// The ViewModel for the inline DM content area.
    /// </summary>
    public DMContentViewModel? DMContent => _dmContent;

    /// <summary>
    /// The ViewModel for the members list component.
    /// </summary>
    public MembersListViewModel? MembersList => _membersListViewModel;

    /// <summary>
    /// The ViewModel for the activity/notifications feed.
    /// </summary>
    public ActivityFeedViewModel? ActivityFeed => _activityFeed;

    public CommunityResponse? SelectedCommunity
    {
        get => _selectedCommunity;
        set
        {
            var idChanged = _selectedCommunity?.Id != value?.Id;
            this.RaiseAndSetIfChanged(ref _selectedCommunity, value);
            if (idChanged)
            {
                this.RaisePropertyChanged(nameof(IsVoiceInDifferentCommunity));
                this.RaisePropertyChanged(nameof(VoiceCommunityName));
                // Sync to store (store → ViewModel sync will skip due to ID check)
                _stores.CommunityStore.SelectCommunity(value?.Id);
            }
        }
    }

    public ChannelResponse? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            var idChanged = _selectedChannel?.Id != value?.Id;
            this.RaiseAndSetIfChanged(ref _selectedChannel, value);
            if (idChanged)
            {
                // Sync to store (store → ViewModel sync will skip due to ID check)
                _stores.ChannelStore.SelectChannel(value?.Id);
            }
        }
    }

    public string MessageInput
    {
        get => _messageInput;
        set
        {
            if (_messageInputVm != null)
            {
                _messageInputVm.MessageInput = value;
            }
            else
            {
                this.RaiseAndSetIfChanged(ref _messageInput, value);
            }
        }
    }

    // Typing indicator properties (backed by TypingStore)
    public bool IsAnyoneTyping => _isAnyoneTyping;

    public string TypingIndicatorText => _typingIndicatorText;

    // Unified autocomplete properties (delegated to MessageInputViewModel)
    public bool IsAutocompletePopupOpen => _messageInputVm?.IsAutocompletePopupOpen ?? false;

    public ObservableCollection<IAutocompleteSuggestion> AutocompleteSuggestions =>
        _messageInputVm?.AutocompleteSuggestions ?? new ObservableCollection<IAutocompleteSuggestion>();

    public int SelectedAutocompleteIndex
    {
        get => _messageInputVm?.SelectedAutocompleteIndex ?? -1;
        set
        {
            if (_messageInputVm != null)
                _messageInputVm.SelectedAutocompleteIndex = value;
        }
    }

    /// <summary>
    /// Self-contained GIF picker ViewModel (appears in message list).
    /// </summary>
    public GifPickerViewModel? GifPicker => _gifPicker;

    /// <summary>
    /// Pinned messages popup ViewModel.
    /// </summary>
    public PinnedMessagesPopupViewModel? PinnedMessagesPopup => _pinnedMessagesPopup;

    public int PinnedCount => _storeMessages.Count(m => m.IsPinned);

    /// <summary>
    /// Invite user popup ViewModel.
    /// </summary>
    public InviteUserPopupViewModel? InviteUserPopup => _inviteUserPopup;

    // Community discovery
    /// <summary>
    /// Whether the user has no communities (used to show empty state).
    /// </summary>
    public bool HasNoCommunities => _storeCommunities.Count == 0;

    /// <summary>
    /// Whether the welcome modal is open (first-time user experience).
    /// </summary>
    public bool IsWelcomeModalOpen
    {
        get => _isWelcomeModalOpen;
        set
        {
            if (_welcomeModal != null)
            {
                _welcomeModal.IsOpen = value;
            }
            this.RaiseAndSetIfChanged(ref _isWelcomeModalOpen, value);
        }
    }

    /// <summary>
    /// Community discovery ViewModel.
    /// </summary>
    public CommunityDiscoveryViewModel? CommunityDiscovery => _communityDiscovery;

    // Controller access request properties (delegated to ControllerHostViewModel)
    public ObservableCollection<ControllerAccessRequest> PendingControllerRequests =>
        _controllerHost?.PendingRequests ?? new ObservableCollection<ControllerAccessRequest>();

    public ObservableCollection<ActiveControllerSession> ActiveControllerSessions =>
        _controllerHost?.ActiveSessions ?? new ObservableCollection<ActiveControllerSession>();

    public bool HasPendingControllerRequests => _controllerHost?.HasPendingRequests ?? false;
    public bool HasActiveControllerSessions => _controllerHost?.HasActiveSessions ?? false;

    public byte SelectedControllerSlot
    {
        get => _controllerHost?.SelectedControllerSlot ?? 0;
        set { if (_controllerHost != null) _controllerHost.SelectedControllerSlot = value; }
    }

    public byte[] AvailableControllerSlots => _controllerHost?.AvailableSlots ?? [0, 1, 2, 3];

    // Thread properties (delegated to ThreadPanelViewModel)
    public ThreadViewModel? CurrentThread => _threadPanel?.CurrentThread;

    public bool IsThreadOpen => _threadPanel?.IsOpen ?? false;

    public double ThreadPanelWidth
    {
        get => _threadPanel?.PanelWidth ?? 400;
        set { if (_threadPanel != null) _threadPanel.PanelWidth = Math.Max(280, Math.Min(600, value)); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool IsMessagesLoading
    {
        get => _isMessagesLoading;
        set => this.RaiseAndSetIfChanged(ref _isMessagesLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    // Capability warning properties (for hardware encoding validation)
    /// <summary>
    /// Gets whether there are capability validation issues that should be shown to the user.
    /// </summary>
    public bool HasCapabilityWarnings =>
        OperatingSystem.IsLinux() &&
        Program.CapabilityService?.HasValidationIssues == true &&
        Program.CapabilityService?.IsValidationWarningDismissed != true;

    /// <summary>
    /// Gets the title of the first capability issue (for banner display).
    /// </summary>
    public string? CapabilityWarningTitle =>
        Program.CapabilityService?.ValidationResult?.Issues
            .FirstOrDefault(i => i.IsError || i.IsWarning)?.Title;

    /// <summary>
    /// Gets the full validation result for detailed display.
    /// </summary>
    public CaptureValidationResult? CapabilityValidationResult =>
        Program.CapabilityService?.ValidationResult;

    /// <summary>
    /// Command to dismiss the capability warning banner.
    /// </summary>
    public ReactiveCommand<Unit, Unit> DismissCapabilityWarningCommand { get; }

    /// <summary>
    /// Command to show capability warning details.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ShowCapabilityDetailsCommand { get; }

    /// <summary>
    /// Command to close the capability details modal.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CloseCapabilityDetailsCommand { get; }

    private bool _isCapabilityDetailsOpen;
    public bool IsCapabilityDetailsOpen
    {
        get => _isCapabilityDetailsOpen;
        set => this.RaiseAndSetIfChanged(ref _isCapabilityDetailsOpen, value);
    }

    // Connection state properties
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            this.RaiseAndSetIfChanged(ref _connectionState, value);
            this.RaisePropertyChanged(nameof(IsConnected));
            this.RaisePropertyChanged(nameof(IsReconnecting));
            this.RaisePropertyChanged(nameof(IsDisconnected));
            this.RaisePropertyChanged(nameof(ConnectionStatusText));
        }
    }

    public bool IsConnected => _connectionState == ConnectionState.Connected;
    public bool IsReconnecting => _connectionState == ConnectionState.Reconnecting;
    public bool IsDisconnected => _connectionState == ConnectionState.Disconnected;

    public string ConnectionStatusText => _connectionState switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting...",
        ConnectionState.Reconnecting => "Reconnecting...",
        ConnectionState.Disconnected => "Disconnected",
        _ => ""
    };

    public int ReconnectSecondsRemaining
    {
        get => _reconnectSecondsRemaining;
        set => this.RaiseAndSetIfChanged(ref _reconnectSecondsRemaining, value);
    }

    public string ReconnectStatusText => _reconnectSecondsRemaining > 0
        ? $"Reconnecting in {_reconnectSecondsRemaining}s..."
        : "Reconnecting...";

    #region Store-Backed Observables (Redux-style state management)

    /// <summary>
    /// Observable total unread count from ChannelStore.
    /// Views can bind to this for notification badges.
    /// </summary>
    public IObservable<int> TotalChannelUnreadCount => _stores.ChannelStore.TotalUnreadCount;

    /// <summary>
    /// Observable connection status from PresenceStore.
    /// </summary>
    public IObservable<ConnectionState> ConnectionStatusObservable => _stores.PresenceStore.ConnectionStatus;

    /// <summary>
    /// Observable voice connection status from VoiceStore.
    /// </summary>
    public IObservable<VoiceConnectionStatus> VoiceConnectionStatusObservable => _stores.VoiceStore.ConnectionStatus;

    /// <summary>
    /// Observable list of online user IDs from PresenceStore.
    /// </summary>
    public IObservable<IReadOnlyCollection<Guid>> OnlineUserIds => _stores.PresenceStore.OnlineUserIds;

    /// <summary>
    /// Observable voice participants for current channel from VoiceStore.
    /// </summary>
    public IObservable<IReadOnlyCollection<VoiceParticipantState>> VoiceParticipantsObservable =>
        _stores.VoiceStore.CurrentChannelParticipants;

    /// <summary>
    /// Store-backed text channels collection.
    /// Views can bind to this instead of the TextChannels computed property.
    /// </summary>
    public ReadOnlyObservableCollection<ChannelResponse> StoreTextChannels => _storeTextChannels;

    /// <summary>
    /// Store-backed voice channels collection.
    /// Views can bind to this instead of getting VoiceChannelViewModels.
    /// </summary>
    public ReadOnlyObservableCollection<ChannelResponse> StoreVoiceChannels => _storeVoiceChannels;

    /// <summary>
    /// Store-backed all channels collection (text and voice).
    /// </summary>
    public ReadOnlyObservableCollection<ChannelResponse> StoreAllChannels => _storeAllChannels;

    /// <summary>
    /// Store-backed messages collection for the current channel.
    /// Views can bind to this instead of the Messages property.
    /// </summary>
    public ReadOnlyObservableCollection<MessageResponse> StoreMessages => _storeMessages;

    /// <summary>
    /// Store-backed members collection for the current community.
    /// Views can bind to this instead of the Members property.
    /// </summary>
    public ReadOnlyObservableCollection<CommunityMemberResponse> StoreMembers => _storeMembers;

    /// <summary>
    /// Store-backed communities collection.
    /// Views can bind to this instead of the Communities property.
    /// </summary>
    public ReadOnlyObservableCollection<CommunityResponse> StoreCommunities => _storeCommunities;

    #endregion

    public ChannelState? EditingChannel
    {
        get => _editingChannel;
        set
        {
            if (_channelManagementVm != null && _channelManagementVm.EditingChannel != value)
            {
                // Start or cancel edit via ViewModel
                if (value != null)
                    _channelManagementVm.StartEditChannel(value);
                else
                    _channelManagementVm.CancelEditChannel();
            }
            this.RaiseAndSetIfChanged(ref _editingChannel, value);
        }
    }

    public string EditingChannelName
    {
        get => _editingChannelName;
        set
        {
            if (_channelManagementVm != null)
            {
                _channelManagementVm.EditingChannelName = value;
            }
            this.RaiseAndSetIfChanged(ref _editingChannelName, value);
        }
    }

    /// <summary>
    /// The channel pending deletion (shown in confirmation dialog).
    /// </summary>
    public ChannelState? ChannelPendingDelete
    {
        get => _channelPendingDelete;
        set
        {
            if (_channelManagementVm != null && _channelManagementVm.ChannelPendingDelete != value)
            {
                _channelManagementVm.ChannelPendingDelete = value;
            }
            this.RaiseAndSetIfChanged(ref _channelPendingDelete, value);
            this.RaisePropertyChanged(nameof(ShowChannelDeleteConfirmation));
        }
    }

    /// <summary>
    /// Whether to show the channel deletion confirmation dialog.
    /// </summary>
    public bool ShowChannelDeleteConfirmation => ChannelPendingDelete is not null;

    public MessageResponse? EditingMessage
    {
        get => _editingMessage;
        set => this.RaiseAndSetIfChanged(ref _editingMessage, value);
    }

    public string EditingMessageContent
    {
        get => _editingMessageContent;
        set
        {
            if (_messageInputVm != null)
            {
                _messageInputVm.EditingMessageContent = value;
            }
            else
            {
                this.RaiseAndSetIfChanged(ref _editingMessageContent, value);
            }
        }
    }

    public MessageResponse? ReplyingToMessage
    {
        get => _replyingToMessage;
        set => this.RaiseAndSetIfChanged(ref _replyingToMessage, value);
    }

    public bool IsReplying => ReplyingToMessage is not null;

    // File attachment properties (delegated to MessageInputViewModel)
    public ObservableCollection<PendingAttachment> PendingAttachments =>
        _messageInputVm?.PendingAttachments ?? _pendingAttachments;
    public bool HasPendingAttachments => _messageInputVm?.HasPendingAttachments ?? _pendingAttachments.Count > 0;

    public AttachmentResponse? LightboxImage
    {
        get => _lightboxImage;
        set
        {
            this.RaiseAndSetIfChanged(ref _lightboxImage, value);
            this.RaisePropertyChanged(nameof(IsLightboxOpen));
        }
    }

    public bool IsLightboxOpen => LightboxImage is not null;

    public void AddPendingAttachment(string fileName, Stream stream, long size, string contentType)
    {
        _messageInputVm?.AddPendingAttachment(fileName, stream, size, contentType);
    }

    public void RemovePendingAttachment(PendingAttachment attachment)
    {
        _messageInputVm?.RemovePendingAttachment(attachment);
    }

    public void ClearPendingAttachments()
    {
        _messageInputVm?.ClearPendingAttachments();
    }

    public void OpenLightbox(AttachmentResponse attachment)
    {
        LightboxImage = attachment;
    }

    public void CloseLightbox()
    {
        LightboxImage = null;
    }

    // GIF panel ViewModel (for the GIF search panel)
    private GifPanelViewModel? _gifPanel;

    public bool IsGifsEnabled => _isGifsEnabled;

    // GIF panel properties (delegate to GifPanelViewModel)
    public GifPanelViewModel? GifPanel => _gifPanel;
    public ObservableCollection<GifResult> GifResults => _gifPanel?.Results ?? new ObservableCollection<GifResult>();

    public string GifSearchQuery
    {
        get => _gifPanel?.SearchQuery ?? string.Empty;
        set { if (_gifPanel != null) _gifPanel.SearchQuery = value; }
    }

    public bool IsLoadingGifs => _gifPanel?.IsLoading ?? false;

    public Task LoadTrendingGifsAsync() => _gifPanel?.LoadTrendingAsync() ?? Task.CompletedTask;

    public Task SearchGifsAsync() => _gifPanel?.SearchAsync() ?? Task.CompletedTask;

    public async Task SendGifMessageAsync(GifResult gif)
    {
        if (SelectedChannel is null) return;

        try
        {
            // Send the GIF URL as the message content
            var result = await _apiClient.SendMessageAsync(SelectedChannel.Id, gif.Url, ReplyingToMessage?.Id);
            if (result.Success)
            {
                _messageInputVm?.CancelReply();
            }
        }
        catch
        {
            // GIF send failures are non-critical
        }
    }

    /// <summary>
    /// Handler for when user clicks Send in the inline GIF picker (/gif command).
    /// </summary>
    private async void OnGifPickerSendRequested(GifResult gif) => await SendGifMessageAsync(gif);

    /// <summary>
    /// Handler for when user selects a GIF in the GIF panel.
    /// </summary>
    private async void OnGifPanelGifSelected(GifResult gif) => await SendGifMessageAsync(gif);

    public void ClearGifResults() => _gifPanel?.Clear();

    /// <summary>
    /// Initiates a GIF preview search for the /gif command.
    /// Uses the self-contained GIF picker that appears in the message list.
    /// </summary>
    private async Task ShowGifPreviewAsync(string query)
    {
        if (_gifPicker == null || string.IsNullOrWhiteSpace(query))
            return;

        await _gifPicker.StartSearchAsync(query);
    }

    // Quick audio device switcher (delegated to AudioDeviceQuickSelectViewModel)
    public AudioDeviceQuickSelectViewModel? AudioDeviceQuickSelect => _audioDeviceQuickSelect;
    public ObservableCollection<AudioDeviceItem> InputDevices => _audioDeviceQuickSelect?.InputDevices ?? new();
    public ObservableCollection<AudioDeviceItem> OutputDevices => _audioDeviceQuickSelect?.OutputDevices ?? new();
    public float InputLevel => _audioDeviceQuickSelect?.InputLevel ?? 0;

    public bool IsAudioDevicePopupOpen
    {
        get => _audioDeviceQuickSelect?.IsOpen ?? false;
        set { if (_audioDeviceQuickSelect != null) _audioDeviceQuickSelect.IsOpen = value; }
    }

    public AudioDeviceItem? SelectedInputDeviceItem
    {
        get => _audioDeviceQuickSelect?.SelectedInputDeviceItem;
        set
        {
            if (_audioDeviceQuickSelect != null) _audioDeviceQuickSelect.SelectedInputDeviceItem = value;
            this.RaisePropertyChanged(nameof(ShowAudioDeviceWarning));
        }
    }

    public AudioDeviceItem? SelectedOutputDeviceItem
    {
        get => _audioDeviceQuickSelect?.SelectedOutputDeviceItem;
        set
        {
            if (_audioDeviceQuickSelect != null) _audioDeviceQuickSelect.SelectedOutputDeviceItem = value;
            this.RaisePropertyChanged(nameof(ShowAudioDeviceWarning));
        }
    }

    public string SelectedInputDeviceDisplay => _audioDeviceQuickSelect?.SelectedInputDeviceDisplay ?? "Default";
    public string SelectedOutputDeviceDisplay => _audioDeviceQuickSelect?.SelectedOutputDeviceDisplay ?? "Default";

    public bool PushToTalkEnabled
    {
        get => _audioDeviceQuickSelect?.PushToTalkEnabled ?? false;
        set
        {
            if (_audioDeviceQuickSelect != null)
            {
                _audioDeviceQuickSelect.PushToTalkEnabled = value;
                // When PTT is enabled while in voice channel, start muted
                if (value && IsInVoiceChannel)
                {
                    _ = _voiceControl?.SetMutedAsync(true);
                }
            }
        }
    }

    public string VoiceModeDescription => _audioDeviceQuickSelect?.VoiceModeDescription ?? "Voice activity: Speak to transmit";

    public void HandlePushToTalk(bool isPressed) => _audioDeviceQuickSelect?.HandlePushToTalk(isPressed, IsInVoiceChannel);

    public void OpenAudioDevicePopup() => _audioDeviceQuickSelect?.Open();

    public void RefreshAudioDevices() => _audioDeviceQuickSelect?.RefreshDevices();

    // Voice channel properties
    public ChannelResponse? CurrentVoiceChannel
    {
        get => _currentVoiceChannel;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentVoiceChannel, value);
            this.RaisePropertyChanged(nameof(IsInVoiceChannel));
            this.RaisePropertyChanged(nameof(ShowAudioDeviceWarning));
            this.RaisePropertyChanged(nameof(IsVoiceInDifferentCommunity));
            this.RaisePropertyChanged(nameof(VoiceCommunityName));
            this.RaisePropertyChanged(nameof(GamingStationChannelStatus));
        }
    }

    public bool IsInVoiceChannel => CurrentVoiceChannel is not null;

    /// <summary>
    /// The SignalR service for real-time communication.
    /// Exposed for input forwarding to gaming stations.
    /// </summary>
    public ISignalRService SignalRService => _signalR;

    /// <summary>
    /// Channel ID where the user is in voice on another device (null if not in voice elsewhere).
    /// Backed by VoiceStore.
    /// </summary>
    public Guid? VoiceOnOtherDeviceChannelId => _stores.VoiceStore.GetVoiceOnOtherDevice().ChannelId;

    /// <summary>
    /// Name of the channel where the user is in voice on another device.
    /// Backed by VoiceStore.
    /// </summary>
    public string? VoiceOnOtherDeviceChannelName => _stores.VoiceStore.GetVoiceOnOtherDevice().ChannelName;

    /// <summary>
    /// Whether the user is in a voice channel on another device.
    /// </summary>
    public bool IsInVoiceOnOtherDevice => VoiceOnOtherDeviceChannelId.HasValue;

    /// <summary>
    /// Whether the user is in a voice channel in a different community than currently selected.
    /// </summary>
    public bool IsVoiceInDifferentCommunity =>
        CurrentVoiceChannel != null &&
        SelectedCommunity != null &&
        CurrentVoiceChannel.CommunityId != SelectedCommunity.Id;

    /// <summary>
    /// The name of the community where the current voice channel is located.
    /// </summary>
    public string? VoiceCommunityName =>
        _storeCommunities.FirstOrDefault(c => c.Id == CurrentVoiceChannel?.CommunityId)?.Name;

    /// <summary>
    /// Whether the user has not configured an audio input device.
    /// </summary>
    public bool HasNoInputDevice => _audioDeviceQuickSelect?.HasNoInputDevice ?? true;

    /// <summary>
    /// Whether the user has not configured an audio output device.
    /// </summary>
    public bool HasNoOutputDevice => _audioDeviceQuickSelect?.HasNoOutputDevice ?? true;

    /// <summary>
    /// Whether to show the audio device warning banner in the voice channel view.
    /// Shown when in a voice channel and either input or output device is not configured.
    /// </summary>
    public bool ShowAudioDeviceWarning => IsInVoiceChannel && (HasNoInputDevice || HasNoOutputDevice);

    public bool IsMuted
    {
        get => _voiceControl?.IsMuted ?? _isMuted;
        set
        {
            if (_voiceControl is not null) _voiceControl.IsMuted = value;
            else this.RaiseAndSetIfChanged(ref _isMuted, value);
        }
    }

    public bool IsDeafened
    {
        get => _voiceControl?.IsDeafened ?? _isDeafened;
        set
        {
            if (_voiceControl is not null) _voiceControl.IsDeafened = value;
            else this.RaiseAndSetIfChanged(ref _isDeafened, value);
        }
    }

    public bool IsCameraOn
    {
        get => _voiceControl?.IsCameraOn ?? _isCameraOn;
        set
        {
            if (_voiceControl is not null) _voiceControl.IsCameraOn = value;
            else this.RaiseAndSetIfChanged(ref _isCameraOn, value);
        }
    }

    public bool IsScreenSharing
    {
        get => _voiceControl?.IsScreenSharing ?? _isScreenSharing;
        set
        {
            if (_voiceControl is not null) _voiceControl.IsScreenSharing = value;
            else this.RaiseAndSetIfChanged(ref _isScreenSharing, value);
        }
    }

    public bool IsSpeaking
    {
        get => _voiceControl?.IsSpeaking ?? _isSpeaking;
        set
        {
            if (_voiceControl is not null) _voiceControl.IsSpeaking = value;
            else this.RaiseAndSetIfChanged(ref _isSpeaking, value);
        }
    }

    /// <summary>
    /// Whether the host (sharer) allows viewers to draw on the screen share.
    /// This is only relevant when this user is sharing their screen.
    /// </summary>
    public bool IsDrawingAllowedForViewers
    {
        get => _screenShare?.IsDrawingAllowedForViewers ?? false;
        set
        {
            if (_screenShare != null)
            {
                _screenShare.IsDrawingAllowedForViewers = value;
            }
        }
    }

    public VoiceConnectionStatus VoiceConnectionStatus
    {
        get => _voiceConnectionStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _voiceConnectionStatus, value);
            this.RaisePropertyChanged(nameof(VoiceConnectionStatusText));
            this.RaisePropertyChanged(nameof(IsVoiceConnecting));
            this.RaisePropertyChanged(nameof(IsVoiceConnected));
        }
    }

    public string VoiceConnectionStatusText => VoiceConnectionStatus switch
    {
        VoiceConnectionStatus.Connected => "Voice Connected",
        VoiceConnectionStatus.Connecting => "Connecting...",
        _ => ""
    };

    public bool IsVoiceConnecting => VoiceConnectionStatus == VoiceConnectionStatus.Connecting;
    public bool IsVoiceConnected => VoiceConnectionStatus == VoiceConnectionStatus.Connected;

    // Computed properties for channel filtering
    // Now using store-backed collection for reactive updates
    public IEnumerable<ChannelResponse> TextChannels => _storeTextChannels;

    // Voice channels with reactive participant tracking (managed by VoiceChannelViewModelManager)
    public ObservableCollection<VoiceChannelViewModel> VoiceChannelViewModels =>
        _voiceChannelManager?.ViewModels ?? new ObservableCollection<VoiceChannelViewModel>();

    // Voice channel content view for video grid
    public VoiceChannelContentViewModel? VoiceChannelContent => _voiceChannelContent;

    public ChannelResponse? SelectedVoiceChannelForViewing
    {
        get => _selectedVoiceChannelForViewing;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedVoiceChannelForViewing, value);
            this.RaisePropertyChanged(nameof(IsViewingVoiceChannel));
            // VoiceChannelContentViewModel now reads from VoiceStore directly
        }
    }

    public bool IsViewingVoiceChannel => SelectedVoiceChannelForViewing != null;

    /// <summary>
    /// Returns true if the inline DM conversation is open.
    /// </summary>
    public bool IsViewingDM => _dmContent?.IsOpen ?? false;

    // Gaming Stations properties (delegate to GamingStationViewModel)
    public GamingStationViewModel? GamingStation => _gamingStation;
    public VoiceControlViewModel? VoiceControl => _voiceControl;
    public bool IsViewingGamingStations => _gamingStation?.IsViewingGamingStations ?? false;
    public bool IsLoadingStations => _gamingStation?.IsLoadingStations ?? false;
    public ObservableCollection<GamingStationResponse> MyStations => _gamingStation?.MyStations ?? new ObservableCollection<GamingStationResponse>();
    public ObservableCollection<GamingStationResponse> SharedStations => _gamingStation?.SharedStations ?? new ObservableCollection<GamingStationResponse>();
    public bool HasNoStations => _gamingStation?.HasNoStations ?? true;
    public bool HasMyStations => _gamingStation?.HasMyStations ?? false;
    public bool HasSharedStations => _gamingStation?.HasSharedStations ?? false;
    public bool IsCurrentMachineRegistered => _gamingStation?.IsCurrentMachineRegistered ?? false;

    // Station stream properties (delegate to GamingStationViewModel)
    public bool IsViewingStationStream => _gamingStation?.IsViewingStationStream ?? false;
    public bool IsConnectingToStation => _gamingStation?.IsConnectingToStation ?? false;
    public Guid? ConnectedStationId => _gamingStation?.ConnectedStationId;
    public string ConnectedStationName => _gamingStation?.ConnectedStationName ?? "";
    public string StationConnectionStatus => _gamingStation?.StationConnectionStatus ?? "Disconnected";
    public int StationConnectedUserCount => _gamingStation?.StationConnectedUserCount ?? 0;
    public int StationLatency => _gamingStation?.StationLatency ?? 0;
    public string StationResolution => _gamingStation?.StationResolution ?? "—";
    public int? StationPlayerSlot => _gamingStation?.StationPlayerSlot;

    // Screen share picker properties (delegate to ScreenShareViewModel)
    public bool IsScreenSharePickerOpen
    {
        get => _screenShare?.IsScreenSharePickerOpen ?? _isScreenSharePickerOpen;
        set
        {
            if (_screenShare != null) _screenShare.IsScreenSharePickerOpen = value;
            else this.RaiseAndSetIfChanged(ref _isScreenSharePickerOpen, value);
        }
    }

    public ScreenSharePickerViewModel? ScreenSharePicker
    {
        get => _screenShare?.ScreenSharePicker ?? _screenSharePicker;
        set
        {
            if (_screenShare != null) _screenShare.ScreenSharePicker = value;
            else this.RaiseAndSetIfChanged(ref _screenSharePicker, value);
        }
    }

    /// <summary>
    /// The ScreenShareViewModel (for direct binding if needed).
    /// </summary>
    public ScreenShareViewModel? ScreenShare => _screenShare;

    /// <summary>
    /// True when the voice video overlay is open (for viewing video grid while navigating text channels).
    /// </summary>
    public bool IsVoiceVideoOverlayOpen
    {
        get => _isVoiceVideoOverlayOpen;
        set => this.RaiseAndSetIfChanged(ref _isVoiceVideoOverlayOpen, value);
    }

    /// <summary>
    /// Whether fullscreen video is open. Delegates to VideoFullscreenViewModel.
    /// </summary>
    public bool IsVideoFullscreen => _videoFullscreen?.IsOpen ?? false;

    /// <summary>
    /// The stream being viewed in fullscreen. Delegates to VideoFullscreenViewModel.
    /// </summary>
    public VideoStreamViewModel? FullscreenStream => _videoFullscreen?.Stream;

    /// <summary>
    /// Whether GPU-accelerated fullscreen rendering is active.
    /// </summary>
    public bool IsGpuFullscreenActive => _videoFullscreen?.IsGpuFullscreenActive ?? false;

    /// <summary>
    /// The hardware decoder for fullscreen rendering.
    /// </summary>
    public IHardwareVideoDecoder? FullscreenHardwareDecoder => _videoFullscreen?.HardwareDecoder;

    /// <summary>
    /// Whether keyboard input capture is enabled for gaming station remote control.
    /// </summary>
    public bool IsKeyboardCaptureEnabled
    {
        get => _videoFullscreen?.IsKeyboardCaptureEnabled ?? false;
        set { if (_videoFullscreen != null) _videoFullscreen.IsKeyboardCaptureEnabled = value; }
    }

    /// <summary>
    /// Whether mouse input capture is enabled for gaming station remote control.
    /// </summary>
    public bool IsMouseCaptureEnabled
    {
        get => _videoFullscreen?.IsMouseCaptureEnabled ?? false;
        set { if (_videoFullscreen != null) _videoFullscreen.IsMouseCaptureEnabled = value; }
    }

    /// <summary>
    /// Whether the fullscreen stream is from a gaming station that supports remote input.
    /// </summary>
    public bool IsFullscreenGamingStation => _videoFullscreen?.IsGamingStation ?? false;

    /// <summary>
    /// The machine ID of the gaming station being viewed in fullscreen.
    /// </summary>
    public string? FullscreenGamingStationMachineId => _videoFullscreen?.GamingStationMachineId;

    public void OpenFullscreen(VideoStreamViewModel stream)
    {
        _videoFullscreen?.Open(stream);
        this.RaisePropertyChanged(nameof(IsVideoFullscreen));
        this.RaisePropertyChanged(nameof(FullscreenStream));
        this.RaisePropertyChanged(nameof(IsGpuFullscreenActive));
        this.RaisePropertyChanged(nameof(FullscreenHardwareDecoder));
        this.RaisePropertyChanged(nameof(IsFullscreenGamingStation));
        this.RaisePropertyChanged(nameof(FullscreenGamingStationMachineId));
    }

    public void CloseFullscreen()
    {
        _videoFullscreen?.Close();
        this.RaisePropertyChanged(nameof(IsVideoFullscreen));
        this.RaisePropertyChanged(nameof(FullscreenStream));
        this.RaisePropertyChanged(nameof(IsGpuFullscreenActive));
        this.RaisePropertyChanged(nameof(FullscreenHardwareDecoder));
        this.RaisePropertyChanged(nameof(IsFullscreenGamingStation));
        this.RaisePropertyChanged(nameof(FullscreenGamingStationMachineId));
    }

    /// <summary>
    /// Toggle controller access for a user who is sharing their screen.
    /// If already streaming to this host, stop streaming. Otherwise, request access.
    /// </summary>
    public async Task ToggleControllerAccessAsync(VideoStreamViewModel stream)
    {
        if (_currentVoiceChannel == null) return;
        if (stream.StreamType != VideoStreamType.ScreenShare) return;
        if (stream.UserId == _auth.UserId) return;

        // Check if already streaming to this host - if so, stop
        if (IsStreamingControllerTo(stream.UserId))
        {
            await _controllerStreamingService.StopStreamingAsync();
            return;
        }

        await _controllerStreamingService.RequestAccessAsync(_currentVoiceChannel.Id, stream.UserId);
    }

    /// <summary>
    /// Toggle controller access for the current fullscreen stream.
    /// </summary>
    public async Task ToggleFullscreenControllerAccessAsync()
    {
        if (_videoFullscreen != null)
        {
            await _videoFullscreen.ToggleControllerAccessAsync();
        }
    }

    /// <summary>
    /// Check if we're currently streaming controller input to a specific user.
    /// </summary>
    public bool IsStreamingControllerTo(Guid userId)
    {
        return _controllerStreamingService.IsStreaming && _controllerStreamingService.StreamingHostUserId == userId;
    }

    /// <summary>
    /// Whether we're currently streaming controller input to anyone.
    /// </summary>
    public bool IsControllerStreaming => _controllerStreamingService.IsStreaming;

    /// <summary>
    /// The host user ID we're streaming controller to, if any.
    /// </summary>
    public Guid? ControllerStreamingHostUserId => _controllerStreamingService.StreamingHostUserId;

    // Drawing annotation properties (delegate to VideoFullscreenViewModel for viewer state)
    public bool IsAnnotationEnabled
    {
        get => _videoFullscreen?.IsAnnotationEnabled ?? false;
        set { if (_videoFullscreen != null) _videoFullscreen.IsAnnotationEnabled = value; }
    }

    /// <summary>
    /// Whether the host (sharer) has allowed viewers to draw on the screen share.
    /// </summary>
    public bool IsDrawingAllowedByHost => _videoFullscreen?.IsDrawingAllowedByHost ?? false;

    public string AnnotationColor
    {
        get => _videoFullscreen?.AnnotationColor ?? "#FF0000";
        set { if (_videoFullscreen != null) _videoFullscreen.AnnotationColor = value; }
    }

    public string[] AvailableAnnotationColors => AnnotationService.AvailableColors;

    public AnnotationService AnnotationService => _annotationService;

    public List<DrawingStroke> CurrentAnnotationStrokes => _videoFullscreen?.CurrentAnnotationStrokes ?? new List<DrawingStroke>();

    public async Task AddAnnotationStrokeAsync(DrawingStroke stroke)
    {
        if (_videoFullscreen != null)
        {
            await _videoFullscreen.AddAnnotationStrokeAsync(stroke);
        }
    }

    public async Task UpdateAnnotationStrokeAsync(DrawingStroke stroke)
    {
        if (_videoFullscreen != null)
        {
            await _videoFullscreen.UpdateAnnotationStrokeAsync(stroke);
        }
    }

    public async Task ClearAnnotationsAsync()
    {
        if (_videoFullscreen != null)
        {
            await _videoFullscreen.ClearAnnotationsAsync();
        }
    }

    /// <summary>
    /// Gets voice participants for a channel. Used by legacy converters.
    /// The new architecture uses VoiceChannelViewModels with direct binding.
    /// </summary>
    public ObservableCollection<VoiceParticipantResponse> GetChannelVoiceParticipants(Guid channelId)
    {
        var vm = VoiceChannelViewModels.FirstOrDefault(v => v.Id == channelId);
        if (vm is null) return new ObservableCollection<VoiceParticipantResponse>();

        // Map VoiceParticipantViewModels back to VoiceParticipantResponses for legacy converters
        var responses = new ObservableCollection<VoiceParticipantResponse>();
        foreach (var p in vm.Participants)
        {
            responses.Add(p.Participant);
        }
        return responses;
    }

    // Permission properties
    public UserRole? CurrentUserRole
    {
        get => _currentUserRole;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentUserRole, value);
            this.RaisePropertyChanged(nameof(CanManageChannels));
            this.RaisePropertyChanged(nameof(CanManageMembers));
            this.RaisePropertyChanged(nameof(CanManageVoice));
        }
    }

    public bool CanManageChannels => CurrentUserRole is UserRole.Owner or UserRole.Admin;
    public bool CanManageMembers => CurrentUserRole is UserRole.Owner;
    public bool CanManageVoice => CurrentUserRole is UserRole.Owner or UserRole.Admin;

    public ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveCommand<Unit, Unit>? SwitchServerCommand { get; }
    public ReactiveCommand<Unit, Unit>? OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenGamingStationsCommand { get; }
    public ReactiveCommand<Unit, Unit> RegisterStationCommand { get; }
    public ReactiveCommand<GamingStationResponse, Unit> ConnectToStationCommand { get; }
    public ReactiveCommand<GamingStationResponse, Unit> ManageStationCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectFromStationCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleStationFullscreenCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateCommunityCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommunitiesCommand { get; }
    public ReactiveCommand<CommunityResponse, Unit> SelectCommunityCommand { get; }
    public ReactiveCommand<ChannelResponse, Unit> SelectChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateChannelCommand { get; }
    public ReactiveCommand<ChannelState, Unit> StartEditChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveChannelNameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditChannelCommand { get; }
    public ReactiveCommand<ChannelState, Unit> DeleteChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmDeleteChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteChannelCommand { get; }
    public ReactiveCommand<List<Guid>, Unit> ReorderChannelsCommand { get; }
    public ReactiveCommand<(Guid DraggedId, Guid TargetId, bool DropBefore), Unit> PreviewReorderCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<MessageResponse, Unit> StartEditMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveMessageEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditMessageCommand { get; }
    public ReactiveCommand<MessageResponse, Unit> DeleteMessageCommand { get; }
    public ReactiveCommand<MessageResponse, Unit> ReplyToMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelReplyCommand { get; }
    public ReactiveCommand<(MessageResponse Message, string Emoji), Unit> ToggleReactionCommand { get; }
    public ReactiveCommand<(MessageResponse Message, string Emoji), Unit> AddReactionCommand { get; }
    public ReactiveCommand<MessageResponse, Unit> TogglePinCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPinnedMessagesCommand { get; }
    public ReactiveCommand<Unit, Unit> ClosePinnedPopupCommand { get; }

    // Invite user commands
    public ReactiveCommand<Unit, Unit> OpenInviteUserPopupCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseInviteUserPopupCommand { get; }
    public ReactiveCommand<UserSearchResult, Unit> InviteUserCommand { get; }

    // Recent DMs sidebar commands
    public ReactiveCommand<ConversationSummaryResponse, Unit> SelectRecentDmCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleRecentDmsExpandedCommand { get; }

    // Community discovery commands
    public ReactiveCommand<Unit, Unit> OpenCommunityDiscoveryCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommunityDiscoveryCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseWelcomeModalCommand { get; }
    public ReactiveCommand<CommunityResponse, Unit> JoinCommunityCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshDiscoverableCommunitiesCommand { get; }
    public ReactiveCommand<Unit, Unit> WelcomeBrowseCommunitiesCommand { get; }
    public ReactiveCommand<Unit, Unit> WelcomeCreateCommunityCommand { get; }

    // Controller access request commands
    public ReactiveCommand<ControllerAccessRequest, Unit> AcceptControllerRequestCommand { get; }
    public ReactiveCommand<ControllerAccessRequest, Unit> DeclineControllerRequestCommand { get; }
    public ReactiveCommand<ActiveControllerSession, Unit> StopControllerSessionCommand { get; }
    public ReactiveCommand<ActiveControllerSession, Unit> ToggleMuteControllerSessionCommand { get; }

    // Thread commands
    public ReactiveCommand<MessageResponse, Unit> OpenThreadCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseThreadCommand { get; }

    // Voice commands
    public ReactiveCommand<Unit, Unit> CreateVoiceChannelCommand { get; }
    public ReactiveCommand<ChannelResponse, Unit> JoinVoiceChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveVoiceChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDeafenCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCameraCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleScreenShareCommand { get; }

    // Voice video overlay commands (for viewing video grid while navigating elsewhere)
    public ReactiveCommand<Unit, Unit> ShowVoiceVideoOverlayCommand { get; }
    public ReactiveCommand<Unit, Unit> HideVoiceVideoOverlayCommand { get; }

    // Gaming station commands
    public ReactiveCommand<Unit, Unit> DisableGamingStationCommand { get; }

    // Admin voice commands
    public ReactiveCommand<VoiceParticipantViewModel, Unit> ServerMuteUserCommand { get; }
    public ReactiveCommand<VoiceParticipantViewModel, Unit> ServerDeafenUserCommand { get; }
    public ReactiveCommand<(VoiceParticipantViewModel, VoiceChannelViewModel), Unit> MoveUserToChannelCommand { get; }

    public bool CanSwitchServer => _onSwitchServer is not null;

    private void StartDMWithMember(CommunityMemberResponse member)
    {
        // Note: Voice state is now independent of DM navigation
        // Voice video overlay can remain open while viewing DMs
        _dmContent?.OpenConversation(member.UserId, member.Username);
    }

    private void OpenDmFromActivity(Guid userId, string displayName)
    {
        // Open DM conversation from activity feed click
        _dmContent?.OpenConversation(userId, displayName);
    }

    private async Task LoadCommunitiesAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var success = await _communityCoordinator.LoadCommunitiesAsync();
            if (success)
            {
                // Notify HasNoCommunities property
                this.RaisePropertyChanged(nameof(HasNoCommunities));

                // Select first community if none selected
                if (SelectedCommunity is null && _storeCommunities.Count > 0)
                    SelectedCommunity = _storeCommunities[0];

                // Show welcome modal for first-time users with no communities
                if (_storeCommunities.Count == 0)
                {
                    _welcomeModal?.ShowIfFirstTime();
                }
            }
            else
            {
                ErrorMessage = "Failed to load communities";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Gaming station input methods (delegate to GamingStationViewModel)
    public Task SendStationKeyboardInputAsync(StationKeyboardInput input) =>
        _gamingStation?.SendKeyboardInputAsync(input) ?? Task.CompletedTask;

    public Task SendStationMouseInputAsync(StationMouseInput input) =>
        _gamingStation?.SendMouseInputAsync(input) ?? Task.CompletedTask;

    private async Task CreateCommunityAsync()
    {
        var community = await _communityCoordinator.CreateCommunityAsync("New Community", null);
        if (community is not null)
        {
            SelectedCommunity = community;
        }
        else
        {
            ErrorMessage = "Failed to create community";
        }
    }

    private async Task CreateChannelAsync()
    {
        if (SelectedCommunity is null || IsLoading) return;

        IsLoading = true;
        try
        {
            // Generate unique name based on existing text channels
            var existingNames = TextChannels.Select(c => c.Name).ToHashSet();
            var baseName = "new-channel";
            var counter = 1;
            string channelName;
            do
            {
                channelName = $"{baseName}-{counter}";
                counter++;
            } while (existingNames.Contains(channelName));

            var channel = await _channelCoordinator.CreateChannelAsync(SelectedCommunity.Id, channelName, null, ChannelType.Text);
            if (channel is not null)
            {
                SelectedChannel = channel;
            }
            else
            {
                ErrorMessage = "Failed to create channel";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Voice channel methods
    private async Task CreateVoiceChannelAsync()
    {
        if (SelectedCommunity is null || IsLoading) return;

        IsLoading = true;
        try
        {
            // Generate unique name based on existing voice channels
            var existingNames = VoiceChannelViewModels.Select(c => c.Name).ToHashSet();
            var baseName = "Voice";
            var counter = 1;
            string channelName;
            do
            {
                channelName = $"{baseName} {counter}";
                counter++;
            } while (existingNames.Contains(channelName));

            var channel = await _channelCoordinator.CreateChannelAsync(SelectedCommunity.Id, channelName, null, ChannelType.Voice);
            if (channel is not null)
            {
                // Add to VoiceChannelViewModels (coordinator already updated store)
                _voiceChannelManager?.AddChannel(channel);
            }
            else
            {
                ErrorMessage = "Failed to create voice channel";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task JoinVoiceChannelAsync(ChannelResponse channel)
    {
        if (channel.Type != ChannelType.Voice) return;

        // Clear "in voice on other device" state since we're joining from this device
        _stores.VoiceStore.SetVoiceOnOtherDevice(null, null);

        // Leave current voice channel if any
        if (CurrentVoiceChannel is not null)
        {
            await LeaveVoiceChannelAsync();
        }

        // Show connecting state immediately for better UX
        CurrentVoiceChannel = channel;
        VoiceConnectionStatus = VoiceConnectionStatus.Connecting;

        // Delegate to coordinator (handles SignalR, store, and WebRTC)
        var result = await _voiceCoordinator.JoinVoiceChannelAsync(channel.Id);

        if (result.Success && result.Participants is not null)
        {
            // Apply persisted mute/deafen state via VoiceControlViewModel
            if (_voiceControl is not null)
            {
                await _voiceControl.ApplyPersistedStateAsync(channel.Id);
            }

            // VoiceChannelContentViewModel now updates reactively from VoiceStore
        }
        else
        {
            // Join failed - reset view state
            CurrentVoiceChannel = null;
            VoiceConnectionStatus = VoiceConnectionStatus.Disconnected;
        }
    }

    private async Task LeaveVoiceChannelAsync()
    {
        if (CurrentVoiceChannel is null) return;

        // Stop screen sharing first (this closes the annotation overlay)
        _screenShare?.ForceStop();

        // Delegate to coordinator (handles SignalR, store, and WebRTC)
        await _voiceCoordinator.LeaveVoiceChannelAsync(stopScreenShare: true);

        // Update view state
        CurrentVoiceChannel = null;

        // Reset transient state via VoiceControlViewModel
        // Note: IsMuted and IsDeafened are persisted and NOT reset when leaving
        _voiceControl?.ResetTransientState();

        // Close voice video overlay
        // VoiceChannelContentViewModel clears reactively when VoiceStore.CurrentChannelId becomes null
        IsVoiceVideoOverlayOpen = false;
        SelectedVoiceChannelForViewing = null;
    }

    // Note: ToggleMuteAsync, ToggleDeafenAsync, and ToggleCameraAsync are now handled by VoiceControlViewModel
    // Note: ToggleScreenShareAsync, StartScreenShareWithSettingsAsync, and StopScreenShareAsync are now handled by ScreenShareViewModel

    /// <summary>
    /// Server mute/unmute a user (admin action).
    /// Delegates to VoiceCoordinator.
    /// </summary>
    private async Task ServerMuteUserAsync(VoiceParticipantViewModel participant)
    {
        if (CurrentVoiceChannel is null || !CanManageVoice) return;
        if (participant.UserId == _auth.UserId) return;

        await _voiceCoordinator.ServerMuteUserAsync(
            CurrentVoiceChannel.Id,
            participant.UserId,
            !participant.IsServerMuted);
    }

    /// <summary>
    /// Server deafen/undeafen a user (admin action).
    /// Delegates to VoiceCoordinator.
    /// </summary>
    private async Task ServerDeafenUserAsync(VoiceParticipantViewModel participant)
    {
        if (CurrentVoiceChannel is null || !CanManageVoice) return;
        if (participant.UserId == _auth.UserId) return;

        await _voiceCoordinator.ServerDeafenUserAsync(
            CurrentVoiceChannel.Id,
            participant.UserId,
            !participant.IsServerDeafened);
    }

    /// <summary>
    /// Move a user to a different voice channel (admin action).
    /// Delegates to VoiceCoordinator.
    /// </summary>
    private async Task MoveUserToChannelAsync((VoiceParticipantViewModel Participant, VoiceChannelViewModel TargetChannel) args)
    {
        if (!CanManageVoice) return;
        if (args.Participant.UserId == _auth.UserId) return;

        await _voiceCoordinator.MoveUserAsync(args.Participant.UserId, args.TargetChannel.Id);
    }

    // Unified autocomplete methods (delegated to MessageInputViewModel)

    /// <summary>
    /// Closes the autocomplete popup.
    /// </summary>
    public void CloseAutocompletePopup()
    {
        _messageInputVm?.CloseAutocompletePopup();
    }

    /// <summary>
    /// Selects a suggestion and returns the cursor position where the caret should be placed.
    /// Returns -1 if no suggestion was inserted (e.g., command was executed).
    /// </summary>
    public int SelectAutocompleteSuggestion(IAutocompleteSuggestion suggestion)
    {
        return _messageInputVm?.SelectAutocompleteSuggestion(suggestion) ?? -1;
    }

    /// <summary>
    /// Selects a suggestion and returns both the new text and cursor position.
    /// The caller is responsible for updating the UI directly.
    /// </summary>
    public (string newText, int cursorPosition)? SelectAutocompleteSuggestionWithText(IAutocompleteSuggestion suggestion)
    {
        return _messageInputVm?.SelectAutocompleteSuggestionWithText(suggestion);
    }

    /// <summary>
    /// Selects the currently highlighted suggestion and returns the cursor position.
    /// Returns -1 if no suggestion was selected.
    /// </summary>
    public int SelectCurrentAutocompleteSuggestion()
    {
        return _messageInputVm?.SelectCurrentAutocompleteSuggestion() ?? -1;
    }

    /// <summary>
    /// Selects the currently highlighted suggestion and returns both the new text and cursor position.
    /// The caller is responsible for updating the UI directly (setting TextBox.Text).
    /// The TwoWay binding will push the value back to MessageInput.
    /// </summary>
    public (string newText, int cursorPosition)? SelectCurrentAutocompleteSuggestionWithText()
    {
        return _messageInputVm?.SelectCurrentAutocompleteSuggestionWithText();
    }

    /// <summary>
    /// Navigates to the previous autocomplete suggestion.
    /// </summary>
    public void NavigateAutocompleteUp()
    {
        _messageInputVm?.NavigateAutocompleteUp();
    }

    /// <summary>
    /// Navigates to the next autocomplete suggestion.
    /// </summary>
    public void NavigateAutocompleteDown()
    {
        _messageInputVm?.NavigateAutocompleteDown();
    }

    // Reaction methods
    private async Task ToggleReactionAsync((MessageResponse Message, string Emoji) args)
    {
        if (SelectedChannel is null) return;

        var (message, emoji) = args;
        var hasReacted = message.Reactions?.FirstOrDefault(r => r.Emoji == emoji)?.HasReacted ?? false;
        await _messageCoordinator.ToggleReactionAsync(SelectedChannel.Id, message.Id, emoji, hasReacted);
    }

    private async Task AddReactionAsync((MessageResponse Message, string Emoji) args)
    {
        if (SelectedChannel is null) return;

        var (message, emoji) = args;
        await _messageCoordinator.AddReactionAsync(SelectedChannel.Id, message.Id, emoji);
    }

    private async Task TogglePinAsync(MessageResponse message)
    {
        if (SelectedChannel is null) return;
        await _messageCoordinator.TogglePinAsync(SelectedChannel.Id, message.Id, message.IsPinned);
    }

    /// <summary>
    /// Searches for users that can be invited to the current community.
    /// Called from XAML TextBox binding.
    /// </summary>
    public async Task SearchUsersToInviteAsync(string query)
    {
        await _inviteUserPopup!.SearchUsersAsync(query);
    }

    // Recent DMs Methods
    private async Task LoadRecentDmsAsync()
    {
        try
        {
            await _conversationStateService.LoadConversationsAsync();
        }
        catch
        {
            // DM loading failure is non-critical
        }
    }

    private void SelectRecentDm(ConversationSummaryResponse conversation)
    {
        // Close voice channel view and clear channel selection
        SelectedVoiceChannelForViewing = null;
        SelectedChannel = null;

        // Open the DM content view for this conversation
        _dmContent?.OpenConversationById(conversation.Id, conversation.DisplayName);

        // Mark conversation as read via the service (updates all subscribers via reactive subscription)
        if (conversation.UnreadCount > 0)
        {
            _ = _conversationStateService.MarkConversationAsReadAsync(conversation.Id);
        }
    }

    /// <summary>
    /// Check if a controller session is muted.
    /// Delegates to ControllerHostViewModel.
    /// </summary>
    public bool IsControllerSessionMuted(Guid guestUserId)
    {
        return _controllerHost?.IsSessionMuted(guestUserId) ?? false;
    }

    // ==================== Gaming Station Helpers ====================

    /// <summary>
    /// Gets or creates a unique machine ID for this device.
    /// Uses the machine name combined with a hash for uniqueness.
    /// </summary>
    private string GetOrCreateMachineId()
    {
        // Use machine name + a stable hash to create unique ID
        var machineName = Environment.MachineName;
        var userName = Environment.UserName;
        var combined = $"{machineName}-{userName}";

        // Create a simple hash to make the ID more unique while keeping it readable
        var hash = combined.GetHashCode().ToString("X8");
        return $"{machineName}-{hash}";
    }

    /// <summary>
    /// Reports this client's gaming station status to the server.
    /// </summary>
    public Task ReportGamingStationStatusAsync() =>
        _gamingStation?.ReportStatusAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Commands a gaming station to join the current voice channel.
    /// </summary>
    public Task CommandStationJoinCurrentChannelAsync(string machineId) =>
        _gamingStation?.CommandJoinCurrentChannelAsync(machineId) ?? Task.CompletedTask;

    /// <summary>
    /// Commands a gaming station to leave its current voice channel.
    /// </summary>
    public Task CommandStationLeaveChannelAsync(string machineId) =>
        _gamingStation?.CommandLeaveChannelAsync(machineId) ?? Task.CompletedTask;

    public void Dispose()
    {
        _sharerAnnotationOverlay?.Dispose();
        _messageInputVm?.Dispose();
        _screenShare?.Dispose();
        _voiceControl?.Dispose();
        _videoFullscreen?.Dispose();
        _controllerHost?.Dispose();
        _threadPanel?.Dispose();
        _typingSubscription?.Dispose();
        _storeSubscriptions.Dispose();
        _ = _signalR.DisposeAsync();
    }

    #region Store State Conversion Helpers

    /// <summary>
    /// Converts a ChannelState (from store) to ChannelResponse (for view binding).
    /// </summary>
    private static ChannelResponse ToChannelResponse(ChannelState state) =>
        new(
            Id: state.Id,
            Name: state.Name,
            Topic: state.Topic,
            CommunityId: state.CommunityId,
            Type: state.Type,
            Position: state.Position,
            CreatedAt: state.CreatedAt,
            UnreadCount: state.UnreadCount
        );

    /// <summary>
    /// Converts a CommunityState (from store) to CommunityResponse (for view binding).
    /// </summary>
    private static CommunityResponse ToCommunityResponse(CommunityState state) =>
        new(
            Id: state.Id,
            Name: state.Name,
            Description: state.Description,
            Icon: state.Icon,
            OwnerId: state.OwnerId,
            OwnerUsername: state.OwnerUsername,
            OwnerEffectiveDisplayName: state.OwnerEffectiveDisplayName,
            CreatedAt: state.CreatedAt,
            MemberCount: state.MemberCount
        );

    /// <summary>
    /// Converts a MessageState (from store) to MessageResponse (for view binding).
    /// </summary>
    private static MessageResponse ToMessageResponse(MessageState state) =>
        new(
            Id: state.Id,
            Content: state.Content,
            AuthorId: state.AuthorId,
            AuthorUsername: state.AuthorUsername,
            AuthorEffectiveDisplayName: state.AuthorEffectiveDisplayName,
            AuthorAvatar: state.AuthorAvatar,
            ChannelId: state.ChannelId,
            CreatedAt: state.CreatedAt,
            UpdatedAt: state.UpdatedAt,
            IsEdited: state.IsEdited,
            ReplyToId: state.ReplyToId,
            ReplyTo: state.ReplyTo != null ? ToReplyPreview(state.ReplyTo) : null,
            Reactions: state.Reactions.Select(ToReactionSummary).ToList(),
            IsPinned: state.IsPinned,
            PinnedAt: state.PinnedAt,
            PinnedByUsername: state.PinnedByUsername,
            PinnedByEffectiveDisplayName: state.PinnedByEffectiveDisplayName,
            Attachments: state.Attachments.Select(ToAttachmentResponse).ToList(),
            ThreadParentMessageId: state.ThreadParentMessageId,
            ReplyCount: state.ReplyCount,
            LastReplyAt: state.LastReplyAt
        );

    private static ReplyPreview ToReplyPreview(ReplyPreviewState state) =>
        new(
            Id: state.Id,
            Content: state.Content,
            AuthorId: state.AuthorId,
            AuthorUsername: state.AuthorUsername,
            AuthorEffectiveDisplayName: state.AuthorEffectiveDisplayName
        );

    private static ReactionSummary ToReactionSummary(ReactionState state) =>
        new(
            Emoji: state.Emoji,
            Count: state.Count,
            HasReacted: state.HasReacted,
            Users: state.Users.Select(u => new ReactionUser(u.UserId, u.Username, u.EffectiveDisplayName)).ToList()
        );

    private static AttachmentResponse ToAttachmentResponse(AttachmentState state) =>
        new(
            Id: state.Id,
            FileName: state.FileName,
            ContentType: state.ContentType,
            FileSize: state.FileSize,
            IsImage: state.IsImage,
            IsAudio: state.IsAudio,
            Url: state.Url
        );

    /// <summary>
    /// Converts a CommunityMemberState (from store) to CommunityMemberResponse (for view binding).
    /// </summary>
    private static CommunityMemberResponse ToCommunityMemberResponse(CommunityMemberState state) =>
        new(
            UserId: state.UserId,
            Username: state.Username,
            DisplayName: state.DisplayName,
            DisplayNameOverride: state.DisplayNameOverride,
            EffectiveDisplayName: state.EffectiveDisplayName,
            Avatar: state.Avatar,
            IsOnline: state.IsOnline,
            Role: state.Role,
            JoinedAt: state.JoinedAt
        );

    private void SortVoiceChannelViewModelsByPosition() => _voiceChannelManager?.SortByPosition();

    #endregion
}
