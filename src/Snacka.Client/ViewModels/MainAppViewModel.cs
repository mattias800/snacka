using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Snacka.Client.Controls;
using Snacka.Client.Services;
using Snacka.Client.Services.Autocomplete;
using Snacka.Client.Services.HardwareVideo;
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
    private ChannelResponse? _editingChannel;
    private string _editingChannelName = string.Empty;
    private ChannelResponse? _channelPendingDelete;
    private MessageResponse? _editingMessage;
    private string _editingMessageContent = string.Empty;
    private MessageResponse? _replyingToMessage;
    private Guid? _previousChannelId;

    // Voice channel state
    private ChannelResponse? _currentVoiceChannel;
    private bool _isMuted;
    private bool _isDeafened;
    private bool _isCameraOn;
    private bool _isScreenSharing;
    private bool _isSpeaking;
    private VoiceConnectionStatus _voiceConnectionStatus = VoiceConnectionStatus.Disconnected;
    private ObservableCollection<VoiceParticipantResponse> _voiceParticipants = new();

    // Multi-device voice state (tracks when user is in voice on another device)
    private Guid? _voiceOnOtherDeviceChannelId;
    private string? _voiceOnOtherDeviceChannelName;

    // Drag preview state
    private List<VoiceChannelViewModel>? _originalVoiceChannelOrder;
    private Guid? _currentPreviewDraggedId;

    // Track pending reorder to skip redundant SignalR updates
    private Guid? _pendingReorderCommunityId;

    // Voice channels with participant tracking (robust reactive approach)
    private ObservableCollection<VoiceChannelViewModel> _voiceChannelViewModels = new();

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

    // Video fullscreen state
    private bool _isVideoFullscreen;
    private VideoStreamViewModel? _fullscreenStream;
    private bool _isGpuFullscreenActive;
    private IHardwareVideoDecoder? _fullscreenHardwareDecoder;

    // Gaming station input capture state (when viewing gaming station stream in fullscreen)
    private bool _isKeyboardCaptureEnabled;
    private bool _isMouseCaptureEnabled;

    // Gaming stations state (old architecture - to be replaced)
    private bool _isViewingGamingStations;
    private bool _isLoadingStations;
    private ObservableCollection<GamingStationResponse> _myStations = new();
    private ObservableCollection<GamingStationResponse> _sharedStations = new();

    // Gaming stations state (new architecture)
    // Tracks all gaming stations owned by the current user (on all their devices)
    private readonly ObservableCollection<MyGamingStationInfo> _myGamingStations = new();
    private string _currentMachineId = "";  // Unique ID for this machine

    // Station stream state
    private bool _isViewingStationStream;
    private bool _isConnectingToStation;
    private Guid? _connectedStationId;
    private string _connectedStationName = "";
    private string _stationConnectionStatus = "Disconnected";
    private int _stationConnectedUserCount;
    private int _stationLatency;
    private string _stationResolution = "—";
    private int? _stationPlayerSlot;

    /// <summary>
    /// Fired when an NV12 frame should be rendered to GPU fullscreen view.
    /// Args: (width, height, nv12Data)
    /// </summary>
    public event Action<int, int, byte[]>? GpuFullscreenFrameReceived;

    // Drawing annotation state
    private readonly AnnotationService _annotationService;
    private bool _isAnnotationEnabled;
    private bool _isDrawingAllowedByHost;
    private string _annotationColor = "#FF0000";
    private List<DrawingStroke> _currentStrokes = new();

    // Sharer annotation overlay state
    private ScreenShareSettings? _currentScreenShareSettings;
    private Views.ScreenAnnotationWindow? _screenAnnotationWindow;
    private Views.AnnotationToolbarWindow? _annotationToolbarWindow;

    // Typing indicator state (channel typing only - DM typing is in DMContentViewModel)
    private ObservableCollection<TypingUser> _typingUsers = new();
    private DateTime _lastTypingSent = DateTime.MinValue;
    private const int TypingThrottleMs = 3000; // Send typing event every 3 seconds
    private const int TypingTimeoutMs = 5000; // Clear typing after 5 seconds of inactivity
    private System.Timers.Timer? _typingCleanupTimer;
    private ScreenAnnotationViewModel? _screenAnnotationViewModel;

    // Unified autocomplete for @ mentions and / commands
    private readonly AutocompleteManager _autocomplete = new();
    private bool _isSelectingAutocomplete;

    // Self-contained GIF picker (appears in message list)
    private GifPickerViewModel? _gifPicker;

    // File attachment state
    private ObservableCollection<PendingAttachment> _pendingAttachments = new();
    private AttachmentResponse? _lightboxImage;

    // Pinned messages state
    private bool _isPinnedPopupOpen;
    private ObservableCollection<MessageResponse> _pinnedMessages = new();

    // Invite user popup state
    private bool _isInviteUserPopupOpen;
    private string _inviteSearchQuery = string.Empty;
    private bool _isSearchingUsersToInvite;
    private ObservableCollection<UserSearchResult> _inviteSearchResults = new();
    private bool _inviteHasNoResults;
    private string? _inviteStatusMessage;
    private bool _isInviteStatusError;

    // Recent DMs state (shown in channel list sidebar)
    private ObservableCollection<ConversationSummaryResponse> _recentDms = new();
    private bool _isRecentDmsExpanded = true;

    // Activity feed state (right panel)
    private ActivityFeedViewModel? _activityFeed;

    // Controller access request state
    private byte _selectedControllerSlot = 0;

    // Thread state
    private ThreadViewModel? _currentThread;
    private double _threadPanelWidth = 400;

    // Members list ViewModel (encapsulates member operations and nickname editing)
    private MembersListViewModel? _membersListViewModel;

    // Server feature flags
    private readonly bool _isGifsEnabled;

    // Connection state
    private ConnectionState _connectionState = ConnectionState.Connected;
    private int _reconnectSecondsRemaining;

    // Community discovery state
    private bool _isWelcomeModalOpen;
    private bool _isCommunityDiscoveryOpen;
    private bool _isLoadingDiscovery;
    private ObservableCollection<CommunityResponse> _discoverableCommunities = new();
    private string? _discoveryError;
    private Guid? _joiningCommunityId;

    public MainAppViewModel(IApiClient apiClient, ISignalRService signalR, IWebRtcService webRtc, IScreenCaptureService screenCaptureService, ISettingsStore settingsStore, IAudioDeviceService audioDeviceService, IControllerStreamingService controllerStreamingService, IControllerHostService controllerHostService, string baseUrl, AuthResponse auth, Action onLogout, Action? onSwitchServer = null, Action? onOpenSettings = null, bool gifsEnabled = false)
    {
        _apiClient = apiClient;
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
        _voiceChannelContent = new VoiceChannelContentViewModel(_webRtc, _signalR, auth.UserId);

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
                Console.WriteLine($"MainAppVM: Sending share screen command to gaming station {machineId}");
                await _signalR.CommandStationStartScreenShareAsync(machineId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainAppVM: Failed to command gaming station share screen: {ex.Message}");
                ErrorMessage = "Failed to start screen share on gaming station";
            }
        };
        _voiceChannelContent.OnGamingStationStopShareScreen = async machineId =>
        {
            try
            {
                Console.WriteLine($"MainAppVM: Sending stop share screen command to gaming station {machineId}");
                await _signalR.CommandStationStopScreenShareAsync(machineId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MainAppVM: Failed to command gaming station stop screen share: {ex.Message}");
                ErrorMessage = "Failed to stop screen share on gaming station";
            }
        };

        // Create annotation service for drawing on screen shares
        _annotationService = new AnnotationService(_signalR);
        _annotationService.StrokeAdded += OnAnnotationStrokeAdded;
        _annotationService.StrokesCleared += OnAnnotationStrokesCleared;
        _annotationService.DrawingAllowedChanged += OnDrawingAllowedChanged;

        // Subscribe to WebRTC connection status changes
        _webRtc.ConnectionStatusChanged += status =>
        {
            Dispatcher.UIThread.Post(() => VoiceConnectionStatus = status);
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

        Communities = new ObservableCollection<CommunityResponse>();
        Channels = new ObservableCollection<ChannelResponse>();
        Messages = new ObservableCollection<MessageResponse>();
        Members = new ObservableCollection<CommunityMemberResponse>();

        // Initialize unified autocomplete with @ mentions, / commands, and :emojis
        _autocomplete.RegisterSource(new MentionAutocompleteSource(() => Members, auth.UserId));
        _autocomplete.RegisterSource(new SlashCommandAutocompleteSource(gifsEnabled: _isGifsEnabled));
        _autocomplete.RegisterSource(new EmojiAutocompleteSource());

        _autocomplete.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutocompleteManager.IsPopupOpen))
                this.RaisePropertyChanged(nameof(IsAutocompletePopupOpen));
            else if (e.PropertyName == nameof(AutocompleteManager.SelectedIndex))
                this.RaisePropertyChanged(nameof(SelectedAutocompleteIndex));
        };

        // Initialize GIF picker (only if GIFs are enabled)
        if (_isGifsEnabled)
        {
            _gifPicker = new GifPickerViewModel(apiClient);
            _gifPicker.SendRequested += OnGifPickerSendRequested;
        }

        // Create the inline DM ViewModel
        _dmContent = new DMContentViewModel(
            apiClient,
            signalR,
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

        // Create the members list ViewModel
        _membersListViewModel = new MembersListViewModel(
            apiClient,
            auth.UserId,
            Members,
            _myGamingStations,
            () => SelectedCommunity?.Id ?? Guid.Empty,
            member => StartDMWithMember(member),
            _ => 0, // Unread count per user not available in new conversation model
            () => Task.FromResult(_currentVoiceChannel is not null),
            machineId => CommandStationJoinCurrentChannelAsync(machineId),
            error => ErrorMessage = error);

        // Create the activity feed ViewModel
        _activityFeed = new ActivityFeedViewModel(
            signalR,
            apiClient,
            auth.UserId,
            () => SelectedCommunity?.Id,
            () => CanManageChannels,
            LoadCommunitiesAsync,
            (userId, displayName) => OpenDmFromActivity(userId, displayName));

        // Commands
        LogoutCommand = ReactiveCommand.Create(_onLogout);
        SwitchServerCommand = _onSwitchServer is not null
            ? ReactiveCommand.Create(_onSwitchServer)
            : null;
        OpenSettingsCommand = _onOpenSettings is not null
            ? ReactiveCommand.Create(_onOpenSettings)
            : null;
        OpenGamingStationsCommand = ReactiveCommand.CreateFromTask(OpenGamingStationsAsync);
        RegisterStationCommand = ReactiveCommand.CreateFromTask(RegisterStationAsync);
        ConnectToStationCommand = ReactiveCommand.CreateFromTask<GamingStationResponse>(ConnectToStationAsync);
        ManageStationCommand = ReactiveCommand.Create<GamingStationResponse>(ManageStation);
        DisconnectFromStationCommand = ReactiveCommand.CreateFromTask(DisconnectFromStationAsync);
        ToggleStationFullscreenCommand = ReactiveCommand.Create(ToggleStationFullscreen);
        CreateCommunityCommand = ReactiveCommand.CreateFromTask(CreateCommunityAsync);
        RefreshCommunitiesCommand = ReactiveCommand.CreateFromTask(LoadCommunitiesAsync);
        SelectCommunityCommand = ReactiveCommand.Create<CommunityResponse>(community =>
        {
            IsViewingGamingStations = false;
            SelectedCommunity = community;
        });
        SelectChannelCommand = ReactiveCommand.Create<ChannelResponse>(channel =>
        {
            // Close the DM view when selecting a text channel
            _dmContent?.Close();
            // Clear voice channel viewing when selecting a text channel
            SelectedVoiceChannelForViewing = null;
            // Close gaming stations view
            IsViewingGamingStations = false;
            // Close station stream view
            IsViewingStationStream = false;
            SelectedChannel = channel;
        });

        // Channel creation canExecute - prevents rapid clicks
        var canCreateChannel = this.WhenAnyValue(
            x => x.SelectedCommunity,
            x => x.IsLoading,
            (community, isLoading) => community is not null && !isLoading);

        CreateChannelCommand = ReactiveCommand.CreateFromTask(CreateChannelAsync, canCreateChannel);
        StartEditChannelCommand = ReactiveCommand.Create<ChannelResponse>(StartEditChannel);
        SaveChannelNameCommand = ReactiveCommand.CreateFromTask(SaveChannelNameAsync);
        CancelEditChannelCommand = ReactiveCommand.Create(CancelEditChannel);
        DeleteChannelCommand = ReactiveCommand.Create<ChannelResponse>(RequestDeleteChannel);
        ConfirmDeleteChannelCommand = ReactiveCommand.CreateFromTask(ConfirmDeleteChannelAsync);
        CancelDeleteChannelCommand = ReactiveCommand.Create(CancelDeleteChannel);
        ReorderChannelsCommand = ReactiveCommand.CreateFromTask<List<Guid>>(ReorderChannelsAsync);
        PreviewReorderCommand = ReactiveCommand.Create<(Guid DraggedId, Guid TargetId, bool DropBefore)>(PreviewReorder);
        CancelPreviewCommand = ReactiveCommand.Create(CancelPreview);

        // Message commands
        StartEditMessageCommand = ReactiveCommand.Create<MessageResponse>(StartEditMessage);
        SaveMessageEditCommand = ReactiveCommand.CreateFromTask(SaveMessageEditAsync);
        CancelEditMessageCommand = ReactiveCommand.Create(CancelEditMessage);
        DeleteMessageCommand = ReactiveCommand.CreateFromTask<MessageResponse>(DeleteMessageAsync);
        ReplyToMessageCommand = ReactiveCommand.Create<MessageResponse>(StartReplyToMessage);
        CancelReplyCommand = ReactiveCommand.Create(CancelReply);
        ToggleReactionCommand = ReactiveCommand.CreateFromTask<(MessageResponse Message, string Emoji)>(ToggleReactionAsync);
        AddReactionCommand = ReactiveCommand.CreateFromTask<(MessageResponse Message, string Emoji)>(AddReactionAsync);
        TogglePinCommand = ReactiveCommand.CreateFromTask<MessageResponse>(TogglePinAsync);
        ShowPinnedMessagesCommand = ReactiveCommand.CreateFromTask(ShowPinnedMessagesAsync);
        ClosePinnedPopupCommand = ReactiveCommand.Create(() => { IsPinnedPopupOpen = false; });

        // GIF preview commands
        SendGifPreviewCommand = ReactiveCommand.CreateFromTask(SendGifPreviewAsync);
        ShuffleGifPreviewCommand = ReactiveCommand.Create(ShuffleGifPreview);
        CancelGifPreviewCommand = ReactiveCommand.Create(CancelGifPreview);

        // Invite user commands
        OpenInviteUserPopupCommand = ReactiveCommand.Create(OpenInviteUserPopup);
        CloseInviteUserPopupCommand = ReactiveCommand.Create(CloseInviteUserPopup);
        InviteUserCommand = ReactiveCommand.CreateFromTask<UserSearchResult>(InviteUserAsync);

        // Recent DMs commands (sidebar section)
        SelectRecentDmCommand = ReactiveCommand.Create<ConversationSummaryResponse>(SelectRecentDm);
        ToggleRecentDmsExpandedCommand = ReactiveCommand.Create(() => { IsRecentDmsExpanded = !IsRecentDmsExpanded; });

        // Community discovery commands
        OpenCommunityDiscoveryCommand = ReactiveCommand.CreateFromTask(OpenCommunityDiscoveryAsync);
        CloseCommunityDiscoveryCommand = ReactiveCommand.Create(() => { IsCommunityDiscoveryOpen = false; });
        CloseWelcomeModalCommand = ReactiveCommand.Create(CloseWelcomeModal);
        JoinCommunityCommand = ReactiveCommand.CreateFromTask<CommunityResponse>(JoinDiscoveredCommunityAsync);
        RefreshDiscoverableCommunitiesCommand = ReactiveCommand.CreateFromTask(LoadDiscoverableCommunitiesAsync);
        WelcomeBrowseCommunitiesCommand = ReactiveCommand.CreateFromTask(WelcomeBrowseCommunitiesAsync);
        WelcomeCreateCommunityCommand = ReactiveCommand.CreateFromTask(WelcomeCreateCommunityAsync);

        // Controller access request commands
        AcceptControllerRequestCommand = ReactiveCommand.CreateFromTask<ControllerAccessRequest>(AcceptControllerRequestAsync);
        DeclineControllerRequestCommand = ReactiveCommand.CreateFromTask<ControllerAccessRequest>(DeclineControllerRequestAsync);
        StopControllerSessionCommand = ReactiveCommand.CreateFromTask<ActiveControllerSession>(StopControllerSessionAsync);
        ToggleMuteControllerSessionCommand = ReactiveCommand.Create<ActiveControllerSession>(ToggleMuteControllerSession);

        // Subscribe to controller host service events for UI updates
        _controllerHostService.PendingRequests.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(HasPendingControllerRequests)));
        };
        _controllerHostService.ActiveSessions.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(HasActiveControllerSessions)));
        };
        _controllerHostService.MutedSessionsChanged += () =>
        {
            // Force UI update when mute state changes
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(ActiveControllerSessions)));
        };

        // Thread commands
        OpenThreadCommand = ReactiveCommand.CreateFromTask<MessageResponse>(OpenThreadAsync);
        CloseThreadCommand = ReactiveCommand.Create(CloseThread);

        // Voice commands
        CreateVoiceChannelCommand = ReactiveCommand.CreateFromTask(CreateVoiceChannelAsync, canCreateChannel);
        JoinVoiceChannelCommand = ReactiveCommand.CreateFromTask<ChannelResponse>(JoinVoiceChannelAsync);
        LeaveVoiceChannelCommand = ReactiveCommand.CreateFromTask(LeaveVoiceChannelAsync);
        ToggleMuteCommand = ReactiveCommand.CreateFromTask(ToggleMuteAsync);
        ToggleDeafenCommand = ReactiveCommand.CreateFromTask(ToggleDeafenAsync);
        ToggleCameraCommand = ReactiveCommand.CreateFromTask(ToggleCameraAsync);
        ToggleScreenShareCommand = ReactiveCommand.CreateFromTask(ToggleScreenShareAsync);

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

        // Gaming station commands
        DisableGamingStationCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            // Disable gaming station mode locally
            _settingsStore.Settings.IsGamingStationEnabled = false;
            _settingsStore.Save();

            // Report the status change to the server
            await ReportGamingStationStatusAsync();

            // Update UI
            this.RaisePropertyChanged(nameof(IsGamingStationEnabled));
            this.RaisePropertyChanged(nameof(ShowGamingStationBanner));
            Console.WriteLine("Gaming station mode disabled locally");
        });

        // Admin voice commands
        ServerMuteUserCommand = ReactiveCommand.CreateFromTask<VoiceParticipantViewModel>(ServerMuteUserAsync);
        ServerDeafenUserCommand = ReactiveCommand.CreateFromTask<VoiceParticipantViewModel>(ServerDeafenUserAsync);
        MoveUserToChannelCommand = ReactiveCommand.CreateFromTask<(VoiceParticipantViewModel, VoiceChannelViewModel)>(MoveUserToChannelAsync);

        var canSendMessage = this.WhenAnyValue(
            x => x.MessageInput,
            x => x.SelectedChannel,
            x => x.IsLoading,
            (message, channel, isLoading) =>
                !string.IsNullOrWhiteSpace(message) &&
                channel is not null &&
                !isLoading);

        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSendMessage);

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
        catch (Exception ex)
        {
            Console.WriteLine($"SignalR connection failed: {ex.Message}");
        }

        await LoadCommunitiesAsync();

        // Load recent DMs for the sidebar
        await LoadRecentDmsAsync();
    }

    /// <summary>
    /// Creates a VoiceChannelViewModel with proper callbacks for per-user volume control.
    /// </summary>
    private VoiceChannelViewModel CreateVoiceChannelViewModel(ChannelResponse channel)
    {
        return new VoiceChannelViewModel(
            channel,
            _auth.UserId,
            onVolumeChanged: (userId, volume) => _webRtc.SetUserVolume(userId, volume),
            getInitialVolume: userId => _webRtc.GetUserVolume(userId)
        );
    }

    private void SetupSignalRHandlers()
    {
        _signalR.ChannelCreated += channel => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedCommunity is not null && channel.CommunityId == SelectedCommunity.Id)
            {
                if (!Channels.Any(c => c.Id == channel.Id))
                {
                    Channels.Add(channel);
                    this.RaisePropertyChanged(nameof(TextChannels));
                }

                // Add VoiceChannelViewModel for voice channels
                if (channel.Type == ChannelType.Voice && !VoiceChannelViewModels.Any(v => v.Id == channel.Id))
                {
                    var vm = CreateVoiceChannelViewModel(channel);
                    VoiceChannelViewModels.Add(vm);
                    Console.WriteLine($"SignalR ChannelCreated: Added VoiceChannelViewModel for {channel.Name}");
                }
            }
        });

        _signalR.ChannelUpdated += channel => Dispatcher.UIThread.Post(() =>
        {
            var index = Channels.ToList().FindIndex(c => c.Id == channel.Id);
            if (index >= 0)
            {
                Channels[index] = channel;
                if (SelectedChannel?.Id == channel.Id)
                    SelectedChannel = channel;
            }
        });

        _signalR.ChannelDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.Id == e.ChannelId);
            if (channel is not null)
            {
                Channels.Remove(channel);
                this.RaisePropertyChanged(nameof(TextChannels));

                // Remove VoiceChannelViewModel if it was a voice channel
                var voiceVm = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
                if (voiceVm is not null)
                {
                    VoiceChannelViewModels.Remove(voiceVm);
                    Console.WriteLine($"SignalR ChannelDeleted: Removed VoiceChannelViewModel for {voiceVm.Name}");
                }

                if (SelectedChannel?.Id == e.ChannelId && Channels.Count > 0)
                    SelectedChannel = Channels[0];
            }
        });

        _signalR.ChannelsReordered += e => Dispatcher.UIThread.Post(() =>
        {
            // Only update if it's for the current community
            if (SelectedCommunity?.Id != e.CommunityId) return;

            // Skip if we just initiated this reorder (we already updated optimistically)
            if (_pendingReorderCommunityId == e.CommunityId)
            {
                Console.WriteLine($"SignalR ChannelsReordered: Skipping (we initiated this reorder)");
                _pendingReorderCommunityId = null;
                return;
            }

            Console.WriteLine($"SignalR ChannelsReordered: Received {e.Channels.Count} channels for community {e.CommunityId}");

            // Update channel list with new order
            Channels.Clear();
            foreach (var channel in e.Channels)
            {
                Channels.Add(channel);
            }

            // Update VoiceChannelViewModels positions and re-sort
            foreach (var voiceVm in VoiceChannelViewModels)
            {
                var updatedChannel = e.Channels.FirstOrDefault(c => c.Id == voiceVm.Id);
                if (updatedChannel is not null)
                {
                    voiceVm.Position = updatedChannel.Position;
                }
            }

            // Re-sort VoiceChannelViewModels by Position
            var sortedVoiceChannels = VoiceChannelViewModels.OrderBy(v => v.Position).ToList();
            VoiceChannelViewModels.Clear();
            foreach (var vm in sortedVoiceChannels)
            {
                VoiceChannelViewModels.Add(vm);
            }

            this.RaisePropertyChanged(nameof(TextChannels));
            this.RaisePropertyChanged(nameof(VoiceChannelViewModels));
        });

        _signalR.MessageReceived += message => Dispatcher.UIThread.Post(() =>
        {
            // Clear typing indicator for this user since they sent a message
            var typingUser = _typingUsers.FirstOrDefault(t => t.UserId == message.AuthorId);
            if (typingUser != null)
            {
                _typingUsers.Remove(typingUser);
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
                this.RaisePropertyChanged(nameof(IsAnyoneTyping));
            }

            if (SelectedChannel is not null && message.ChannelId == SelectedChannel.Id)
            {
                // Don't add if it's our own message (we already added it optimistically)
                if (!Messages.Any(m => m.Id == message.Id))
                    Messages.Add(message);
            }
            else
            {
                // Update unread count for channels not currently selected
                var channelIndex = Channels.ToList().FindIndex(c => c.Id == message.ChannelId);
                if (channelIndex >= 0 && message.AuthorId != _auth.UserId)
                {
                    var channel = Channels[channelIndex];
                    Channels[channelIndex] = channel with { UnreadCount = channel.UnreadCount + 1 };
                }
            }
        });

        _signalR.MessageEdited += message => Dispatcher.UIThread.Post(() =>
        {
            // Update in main message list
            var index = Messages.ToList().FindIndex(m => m.Id == message.Id);
            if (index >= 0)
                Messages[index] = message;

            // Update in thread replies if thread is open
            CurrentThread?.UpdateReply(message);
        });

        _signalR.MessageDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            var message = Messages.FirstOrDefault(m => m.Id == e.MessageId);
            if (message is not null)
                Messages.Remove(message);

            // Also remove from thread if open
            CurrentThread?.RemoveReply(e.MessageId);
        });

        // Thread events
        _signalR.ThreadReplyReceived += e => Dispatcher.UIThread.Post(() =>
        {
            // If this thread is currently open, add the reply
            if (CurrentThread?.ParentMessage?.Id == e.ParentMessageId)
            {
                CurrentThread.AddReply(e.Reply);
            }

            // Update the parent message's reply count in the main message list
            var index = Messages.ToList().FindIndex(m => m.Id == e.ParentMessageId);
            if (index >= 0)
            {
                var message = Messages[index];
                Messages[index] = message with
                {
                    ReplyCount = message.ReplyCount + 1,
                    LastReplyAt = e.Reply.CreatedAt
                };
            }
        });

        _signalR.ThreadMetadataUpdated += e => Dispatcher.UIThread.Post(() =>
        {
            // Update the message's thread metadata in the main message list
            var index = Messages.ToList().FindIndex(m => m.Id == e.MessageId);
            if (index >= 0)
            {
                var message = Messages[index];
                Messages[index] = message with
                {
                    ReplyCount = e.ReplyCount,
                    LastReplyAt = e.LastReplyAt
                };
            }
        });

        _signalR.ReactionUpdated += e => Dispatcher.UIThread.Post(() =>
        {
            // Helper to update reactions on a message
            List<ReactionSummary> UpdateReactions(MessageResponse message)
            {
                var reactions = message.Reactions?.ToList() ?? new List<ReactionSummary>();
                var reactionIndex = reactions.FindIndex(r => r.Emoji == e.Emoji);

                if (e.Added)
                {
                    if (reactionIndex >= 0)
                    {
                        var existing = reactions[reactionIndex];
                        var users = existing.Users.ToList();
                        if (!users.Any(u => u.UserId == e.UserId))
                            users.Add(new ReactionUser(e.UserId, e.Username, e.EffectiveDisplayName));
                        reactions[reactionIndex] = existing with
                        {
                            Count = e.Count,
                            HasReacted = existing.HasReacted || e.UserId == _auth.UserId,
                            Users = users
                        };
                    }
                    else
                    {
                        reactions.Add(new ReactionSummary(
                            e.Emoji,
                            e.Count,
                            e.UserId == _auth.UserId,
                            new List<ReactionUser> { new(e.UserId, e.Username, e.EffectiveDisplayName) }
                        ));
                    }
                }
                else
                {
                    if (reactionIndex >= 0)
                    {
                        if (e.Count == 0)
                        {
                            reactions.RemoveAt(reactionIndex);
                        }
                        else
                        {
                            var existing = reactions[reactionIndex];
                            var users = existing.Users.Where(u => u.UserId != e.UserId).ToList();
                            reactions[reactionIndex] = existing with
                            {
                                Count = e.Count,
                                HasReacted = e.UserId == _auth.UserId ? false : existing.HasReacted,
                                Users = users
                            };
                        }
                    }
                }
                return reactions;
            }

            // Update in main message list
            var index = Messages.ToList().FindIndex(m => m.Id == e.MessageId);
            if (index >= 0)
            {
                var message = Messages[index];
                var reactions = UpdateReactions(message);
                Messages[index] = message with { Reactions = reactions.Count > 0 ? reactions : null };
            }

            // Update in thread replies if thread is open
            if (CurrentThread != null)
            {
                var replyIndex = CurrentThread.Replies.ToList().FindIndex(m => m.Id == e.MessageId);
                if (replyIndex >= 0)
                {
                    var reply = CurrentThread.Replies[replyIndex];
                    var reactions = UpdateReactions(reply);
                    CurrentThread.Replies[replyIndex] = reply with { Reactions = reactions.Count > 0 ? reactions : null };
                }
            }
        });

        _signalR.MessagePinned += e => Dispatcher.UIThread.Post(() =>
        {
            // Update message in the main list
            var index = Messages.ToList().FindIndex(m => m.Id == e.MessageId);
            if (index >= 0)
            {
                var message = Messages[index];
                Messages[index] = message with
                {
                    IsPinned = e.IsPinned,
                    PinnedAt = e.PinnedAt,
                    PinnedByUsername = e.PinnedByUsername
                };
            }

            // Update pinned messages list if popup is open
            if (IsPinnedPopupOpen)
            {
                if (e.IsPinned)
                {
                    // Message was pinned - reload to get full message data
                    _ = LoadPinnedMessagesAsync();
                }
                else
                {
                    // Message was unpinned - remove from list
                    var pinnedIndex = PinnedMessages.ToList().FindIndex(m => m.Id == e.MessageId);
                    if (pinnedIndex >= 0)
                        PinnedMessages.RemoveAt(pinnedIndex);
                }
            }
        });

        _signalR.UserOnline += e => Dispatcher.UIThread.Post(() =>
        {
            var index = Members.ToList().FindIndex(m => m.UserId == e.UserId);
            if (index >= 0)
                Members[index] = Members[index] with { IsOnline = true };
        });

        _signalR.UserOffline += e => Dispatcher.UIThread.Post(() =>
        {
            var index = Members.ToList().FindIndex(m => m.UserId == e.UserId);
            if (index >= 0)
                Members[index] = Members[index] with { IsOnline = false };
        });

        _signalR.CommunityMemberAdded += e =>
        {
            Console.WriteLine($"CommunityMemberAdded event received: communityId={e.CommunityId}, userId={e.UserId}");
            Console.WriteLine($"SelectedCommunity: {SelectedCommunity?.Id} ({SelectedCommunity?.Name})");

            // If this is for the currently selected community, reload members
            if (SelectedCommunity is not null && e.CommunityId == SelectedCommunity.Id)
            {
                Console.WriteLine("Reloading members list...");
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var result = await _apiClient.GetMembersAsync(SelectedCommunity.Id);
                        if (result.Success && result.Data is not null)
                        {
                            Console.WriteLine($"Loaded {result.Data.Count()} members");
                            Members.Clear();
                            foreach (var member in result.Data)
                                Members.Add(member);
                            this.RaisePropertyChanged(nameof(SortedMembers));
                        }
                        else
                        {
                            Console.WriteLine($"Failed to load members: {result.Error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception while reloading members: {ex.Message}");
                    }
                });
            }
            else
            {
                Console.WriteLine("Community doesn't match selected community, skipping reload");
            }
        };

        _signalR.CommunityMemberRemoved += e => Dispatcher.UIThread.Post(() =>
        {
            // If this is for the currently selected community, remove the member
            if (SelectedCommunity is not null && e.CommunityId == SelectedCommunity.Id)
            {
                var member = Members.FirstOrDefault(m => m.UserId == e.UserId);
                if (member is not null)
                {
                    Members.Remove(member);
                    this.RaisePropertyChanged(nameof(SortedMembers));
                }
            }
        });

        // Voice channel events - update VoiceChannelViewModels, VoiceParticipants, and VoiceChannelContent
        _signalR.VoiceParticipantJoined += e => Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"EVENT VoiceParticipantJoined: {e.Participant.Username} joined channel {e.ChannelId}");

            // Update VoiceChannelViewModel
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
            if (voiceChannel is not null)
            {
                voiceChannel.AddParticipant(e.Participant);
            }
            else
            {
                Console.WriteLine($"WARNING: VoiceChannelViewModel for {e.ChannelId} not found!");
            }

            // Also update current VoiceParticipants if this is our channel
            if (CurrentVoiceChannel is not null && e.ChannelId == CurrentVoiceChannel.Id)
            {
                if (!VoiceParticipants.Any(p => p.UserId == e.Participant.UserId))
                    VoiceParticipants.Add(e.Participant);

                // Update video grid
                _voiceChannelContent?.AddParticipant(e.Participant);
            }
        });

        _signalR.VoiceParticipantLeft += e => Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"EVENT VoiceParticipantLeft: User {e.UserId} left channel {e.ChannelId}");

            // Update VoiceChannelViewModel
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
            voiceChannel?.RemoveParticipant(e.UserId);

            // Also update current VoiceParticipants if this is our channel
            if (CurrentVoiceChannel is not null && e.ChannelId == CurrentVoiceChannel.Id)
            {
                var participant = VoiceParticipants.FirstOrDefault(p => p.UserId == e.UserId);
                if (participant is not null)
                    VoiceParticipants.Remove(participant);

                // Update video grid
                _voiceChannelContent?.RemoveParticipant(e.UserId);

                // Close fullscreen if viewing this user's stream
                if (IsVideoFullscreen && FullscreenStream?.UserId == e.UserId)
                {
                    CloseFullscreen();
                }
            }
        });

        _signalR.VoiceStateChanged += e => Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"EVENT VoiceStateChanged: User {e.UserId} in channel {e.ChannelId}");

            // Update VoiceChannelViewModel
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
            voiceChannel?.UpdateParticipantState(e.UserId, e.State);

            // Also update current VoiceParticipants if this is our channel
            if (CurrentVoiceChannel is not null && e.ChannelId == CurrentVoiceChannel.Id)
            {
                var index = VoiceParticipants.ToList().FindIndex(p => p.UserId == e.UserId);
                if (index >= 0)
                {
                    var current = VoiceParticipants[index];
                    VoiceParticipants[index] = current with
                    {
                        IsMuted = e.State.IsMuted ?? current.IsMuted,
                        IsDeafened = e.State.IsDeafened ?? current.IsDeafened,
                        IsScreenSharing = e.State.IsScreenSharing ?? current.IsScreenSharing,
                        IsCameraOn = e.State.IsCameraOn ?? current.IsCameraOn
                    };
                }

                // Update video grid
                _voiceChannelContent?.UpdateParticipantState(e.UserId, e.State);
            }
        });

        // Speaking state from other users
        _signalR.SpeakingStateChanged += e => Dispatcher.UIThread.Post(() =>
        {
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
            voiceChannel?.UpdateSpeakingState(e.UserId, e.IsSpeaking);

            // Update video grid
            if (CurrentVoiceChannel is not null && e.ChannelId == CurrentVoiceChannel.Id)
            {
                _voiceChannelContent?.UpdateSpeakingState(e.UserId, e.IsSpeaking);
            }
        });

        // Multi-device voice events
        _signalR.VoiceSessionActiveOnOtherDevice += e => Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"EVENT VoiceSessionActiveOnOtherDevice: In voice on another device (channel {e.ChannelId}, {e.ChannelName})");
            VoiceOnOtherDeviceChannelId = e.ChannelId;
            VoiceOnOtherDeviceChannelName = e.ChannelName;
        });

        _signalR.DisconnectedFromVoice += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT DisconnectedFromVoice: {e.Reason}, channel {e.ChannelId}");

            if (CurrentVoiceChannel is not null && CurrentVoiceChannel.Id == e.ChannelId)
            {
                // Stop screen sharing first
                if (IsScreenSharing)
                {
                    HideSharerAnnotationOverlay();
                    await _webRtc.SetScreenSharingAsync(false);
                    _currentScreenShareSettings = null;
                    _annotationService.OnScreenShareEnded(_auth.UserId);
                }

                // Leave WebRTC connections (but don't notify server - it already knows)
                await _webRtc.LeaveVoiceChannelAsync();

                // Remove ourselves from the VoiceChannelViewModel
                var voiceChannelVm = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
                voiceChannelVm?.RemoveParticipant(_auth.UserId);

                // Clear local state
                CurrentVoiceChannel = null;
                VoiceParticipants.Clear();
                IsCameraOn = false;
                IsScreenSharing = false;
                IsVoiceVideoOverlayOpen = false;
                SelectedVoiceChannelForViewing = null;
                _voiceChannelContent?.SetParticipants(Enumerable.Empty<VoiceParticipantResponse>());

                // Track that we're now in voice on another device
                VoiceOnOtherDeviceChannelId = e.ChannelId;
                VoiceOnOtherDeviceChannelName = voiceChannelVm?.Name;

                Console.WriteLine("Disconnected from voice: joined from another device");
            }
        });

        // Local speaking detection - broadcast to others and update own state
        _webRtc.SpeakingChanged += isSpeaking => Dispatcher.UIThread.Post(async () =>
        {
            // Update local speaking state (for avatar indicator)
            IsSpeaking = isSpeaking;

            // Capture channel reference to avoid race condition during leave
            var currentChannel = CurrentVoiceChannel;
            if (currentChannel is not null)
            {
                // Broadcast to others
                await _signalR.UpdateSpeakingStateAsync(currentChannel.Id, isSpeaking);

                // Update our own speaking state in the ViewModel
                var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == currentChannel.Id);
                voiceChannel?.UpdateSpeakingState(_auth.UserId, isSpeaking);

                // Update video grid
                _voiceChannelContent?.UpdateSpeakingState(_auth.UserId, isSpeaking);
            }
        });

        // Admin voice state changed (server mute/deafen)
        _signalR.ServerVoiceStateChanged += e => Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"EVENT ServerVoiceStateChanged: User {e.TargetUserId} in channel {e.ChannelId}");

            // Update VoiceChannelViewModel
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
            voiceChannel?.UpdateServerState(e.TargetUserId, e.IsServerMuted, e.IsServerDeafened);

            // If this is the current user being server-muted, update local state
            if (e.TargetUserId == _auth.UserId)
            {
                // If server-muted, ensure we're actually muted
                if (e.IsServerMuted == true && !IsMuted)
                {
                    // Force mute
                    IsMuted = true;
                    _webRtc.SetMuted(true);
                }
                // If server-deafened, ensure we're actually deafened
                if (e.IsServerDeafened == true && !IsDeafened)
                {
                    // Force deafen (and mute)
                    IsDeafened = true;
                    IsMuted = true;
                    _webRtc.SetMuted(true);
                }
            }
        });

        // User moved by admin
        _signalR.UserMoved += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT UserMoved: {e.Username} moved from {e.FromChannelId} to {e.ToChannelId} by {e.AdminUsername}");

            // If the current user was moved
            if (e.UserId == _auth.UserId)
            {
                // Leave current channel and join new one
                await LeaveVoiceChannelAsync();
                var channel = Channels.FirstOrDefault(c => c.Id == e.ToChannelId);
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

        // Typing indicator events (DM typing is handled by DMContentViewModel)
        _signalR.UserTyping += e => Dispatcher.UIThread.Post(() =>
        {
            // Only show typing for the currently selected channel
            if (SelectedChannel is not null && e.ChannelId == SelectedChannel.Id && e.UserId != _auth.UserId)
            {
                var existing = _typingUsers.FirstOrDefault(t => t.UserId == e.UserId);
                if (existing != null)
                    _typingUsers.Remove(existing);
                _typingUsers.Add(new TypingUser(e.UserId, e.Username, DateTime.UtcNow));
                this.RaisePropertyChanged(nameof(TypingIndicatorText));
                this.RaisePropertyChanged(nameof(IsAnyoneTyping));
            }
        });

        // Conversation message received - update recent DMs list
        _signalR.ConversationMessageReceived += message => Dispatcher.UIThread.Post(() =>
        {
            UpdateRecentDmWithMessage(message);
        });

        // Gaming Station events (new simplified architecture)
        _signalR.GamingStationStatusChanged += e => Dispatcher.UIThread.Post(() =>
        {
            Console.WriteLine($"EVENT GamingStationStatusChanged: user {e.Username} station {e.MachineId} available={e.IsAvailable}");

            // Only track stations that belong to the current user
            if (e.UserId != _auth.UserId) return;

            var existingIndex = -1;
            for (int i = 0; i < _myGamingStations.Count; i++)
            {
                if (_myGamingStations[i].MachineId == e.MachineId)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (e.IsAvailable)
            {
                var stationInfo = new MyGamingStationInfo(
                    MachineId: e.MachineId,
                    DisplayName: e.DisplayName,
                    IsAvailable: e.IsAvailable,
                    IsInVoiceChannel: e.IsInVoiceChannel,
                    CurrentChannelId: e.CurrentChannelId,
                    IsScreenSharing: e.IsScreenSharing,
                    IsCurrentMachine: e.MachineId == _currentMachineId
                );

                if (existingIndex >= 0)
                    _myGamingStations[existingIndex] = stationInfo;
                else
                    _myGamingStations.Add(stationInfo);
            }
            else
            {
                // Station went offline - remove from list
                if (existingIndex >= 0)
                    _myGamingStations.RemoveAt(existingIndex);
            }

            this.RaisePropertyChanged(nameof(MyGamingStations));
        });

        // Gaming Station command events (this client is a gaming station receiving commands)
        _signalR.StationCommandJoinChannel += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT StationCommandJoinChannel: commanded to join channel {e.ChannelId} ({e.ChannelName})");

            // Only execute if this client is a gaming station
            if (!IsGamingStationEnabled) return;

            // Try to find the channel in our loaded channels
            var channel = Channels.FirstOrDefault(c => c.Id == e.ChannelId);
            if (channel is null)
            {
                // Channel not in current community, create a minimal ChannelResponse for joining
                // This allows gaming stations to join channels even if they're not viewing that community
                channel = new ChannelResponse(
                    Id: e.ChannelId,
                    Name: e.ChannelName,
                    Topic: null,
                    CommunityId: Guid.Empty,
                    Type: ChannelType.Voice,
                    Position: 0,
                    CreatedAt: DateTime.UtcNow
                );
            }

            await JoinVoiceChannelAsync(channel);
            Console.WriteLine($"Gaming station joined channel: {e.ChannelName}");
        });

        _signalR.StationCommandLeaveChannel += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT StationCommandLeaveChannel: commanded to leave channel");

            // Only execute if this client is a gaming station
            if (!IsGamingStationEnabled) return;

            await LeaveVoiceChannelAsync();
            Console.WriteLine($"Gaming station left voice channel");
        });

        _signalR.StationCommandStartScreenShare += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT StationCommandStartScreenShare: commanded to start screen share");

            // Only execute if this client is a gaming station and in a voice channel
            if (!IsGamingStationEnabled || CurrentVoiceChannel is null) return;

            // Get primary display
            var displays = _screenCaptureService.GetDisplays();
            var primaryDisplay = displays.FirstOrDefault();
            if (primaryDisplay is null)
            {
                Console.WriteLine("Gaming station: No display found for screen sharing");
                return;
            }

            // Create screen share settings with gaming-optimized defaults
            var settings = new ScreenShareSettings(
                Source: primaryDisplay,
                Resolution: ScreenShareResolution.HD1080,
                Framerate: ScreenShareFramerate.Fps60,
                Quality: ScreenShareQuality.Gaming,
                IncludeAudio: true
            );

            await StartScreenShareWithSettingsAsync(settings);
            Console.WriteLine($"Gaming station started screen sharing: {primaryDisplay.Name}");
        });

        _signalR.StationCommandStopScreenShare += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT StationCommandStopScreenShare: commanded to stop screen share");

            // Only execute if this client is a gaming station
            if (!IsGamingStationEnabled) return;

            await StopScreenShareAsync();
            Console.WriteLine($"Gaming station stopped screen sharing");
        });

        _signalR.StationCommandDisable += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"EVENT StationCommandDisable: commanded to disable gaming station mode");

            // Disable gaming station mode in settings
            _settingsStore.Settings.IsGamingStationEnabled = false;
            _settingsStore.Save();

            // Report the status change to the server
            await ReportGamingStationStatusAsync();

            this.RaisePropertyChanged(nameof(IsGamingStationEnabled));
            Console.WriteLine($"Gaming station mode disabled remotely");
        });

        // Gaming Station input events (when this client is a gaming station receiving input from owner)
        _signalR.StationKeyboardInputReceived += e =>
        {
            if (!IsGamingStationEnabled) return;

            var input = e.Input;
            Console.WriteLine($"Gaming station received keyboard input: key={input.Key}, down={input.IsDown}, ctrl={input.Ctrl}, alt={input.Alt}, shift={input.Shift}, meta={input.Meta}");

            // TODO: Inject keyboard input using platform-specific APIs
            // Windows: SendInput with KEYBDINPUT
            // macOS: CGEventPost with CGEventCreateKeyboardEvent
            // Linux: XTest extension or uinput
            InjectKeyboardInput(input);
        };

        _signalR.StationMouseInputReceived += e =>
        {
            if (!IsGamingStationEnabled) return;

            var input = e.Input;
            Console.WriteLine($"Gaming station received mouse input: type={input.Type}, x={input.X:F3}, y={input.Y:F3}, button={input.Button}");

            // TODO: Inject mouse input using platform-specific APIs
            // Windows: SendInput with MOUSEINPUT
            // macOS: CGEventPost with CGEventCreateMouseEvent
            // Linux: XTest extension or uinput
            InjectMouseInput(input);
        };

        // Set up typing cleanup timer
        _typingCleanupTimer = new System.Timers.Timer(1000); // Check every second
        _typingCleanupTimer.Elapsed += (s, e) => Dispatcher.UIThread.Post(CleanupExpiredTypingIndicators);
        _typingCleanupTimer.Start();
    }

    private void CleanupExpiredTypingIndicators()
    {
        var now = DateTime.UtcNow;
        var expiredChannel = _typingUsers.Where(t => (now - t.LastTypingAt).TotalMilliseconds > TypingTimeoutMs).ToList();

        foreach (var user in expiredChannel)
            _typingUsers.Remove(user);

        if (expiredChannel.Count > 0)
        {
            this.RaisePropertyChanged(nameof(TypingIndicatorText));
            this.RaisePropertyChanged(nameof(IsAnyoneTyping));
        }

        // Also cleanup DM typing indicators in the DMContentViewModel
        _dmContent?.CleanupExpiredTypingIndicators();
    }

    private async Task OnCommunitySelectedAsync()
    {
        if (SelectedCommunity is null) return;

        // Join SignalR group for this community
        await _signalR.JoinServerAsync(SelectedCommunity.Id);
        await LoadChannelsAsync();
    }

    private async Task OnChannelSelectedAsync()
    {
        // Leave previous channel group
        if (_previousChannelId.HasValue)
            await _signalR.LeaveChannelAsync(_previousChannelId.Value);

        if (SelectedChannel is not null && SelectedCommunity is not null)
        {
            _previousChannelId = SelectedChannel.Id;
            await _signalR.JoinChannelAsync(SelectedChannel.Id);

            // Mark channel as read and update the local unread count
            await _apiClient.MarkChannelAsReadAsync(SelectedCommunity.Id, SelectedChannel.Id);

            // Update local channel with zero unread count
            var idx = Channels.IndexOf(SelectedChannel);
            if (idx >= 0)
            {
                var updated = SelectedChannel with { UnreadCount = 0 };
                Channels[idx] = updated;
                SelectedChannel = updated;
            }
        }

        await LoadMessagesAsync();
    }

    public string Username => _auth.Username;
    public string Email => _auth.Email;
    public Guid UserId => _auth.UserId;
    public ISettingsStore SettingsStore => _settingsStore;
    public IApiClient ApiClient => _apiClient;
    public string BaseUrl => _baseUrl;
    public string AccessToken => _auth.AccessToken;

    public ObservableCollection<CommunityResponse> Communities { get; }
    public ObservableCollection<ChannelResponse> Channels { get; }
    public ObservableCollection<MessageResponse> Messages { get; }
    public ObservableCollection<CommunityMemberResponse> Members { get; }

    /// <summary>
    /// Gaming stations owned by the current user, across all their devices.
    /// </summary>
    public ObservableCollection<MyGamingStationInfo> MyGamingStations => _myGamingStations;

    /// <summary>
    /// The unique machine ID for this device.
    /// </summary>
    public string CurrentMachineId => _currentMachineId;

    /// <summary>
    /// Whether this client has gaming station mode enabled.
    /// </summary>
    public bool IsGamingStationEnabled => _settingsStore.Settings.IsGamingStationEnabled;

    /// <summary>
    /// Whether to show the gaming station status banner.
    /// Shows when gaming station mode is enabled.
    /// </summary>
    public bool ShowGamingStationBanner => IsGamingStationEnabled;

    /// <summary>
    /// Status text for the gaming station banner showing current channel.
    /// </summary>
    public string GamingStationChannelStatus =>
        CurrentVoiceChannel is not null
            ? $"In: {CurrentVoiceChannel.Name}"
            : "Not in a voice channel";

    /// <summary>
    /// Recent DM conversations for the sidebar, sorted by last message time.
    /// </summary>
    public ObservableCollection<ConversationSummaryResponse> RecentDms => _recentDms;

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
    /// </summary>
    public int TotalDmUnreadCount => _recentDms.Sum(c => c.UnreadCount);

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

    /// <summary>
    /// Returns members sorted with the current user first.
    /// </summary>
    public IEnumerable<CommunityMemberResponse> SortedMembers =>
        Members.OrderByDescending(m => m.UserId == _auth.UserId).ThenBy(m => m.Username);

    public CommunityResponse? SelectedCommunity
    {
        get => _selectedCommunity;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCommunity, value);
            this.RaisePropertyChanged(nameof(IsVoiceInDifferentCommunity));
            this.RaisePropertyChanged(nameof(VoiceCommunityName));
        }
    }

    public ChannelResponse? SelectedChannel
    {
        get => _selectedChannel;
        set => this.RaiseAndSetIfChanged(ref _selectedChannel, value);
    }

    public string MessageInput
    {
        get => _messageInput;
        set
        {
            this.RaiseAndSetIfChanged(ref _messageInput, value);

            // Handle unified autocomplete (@ mentions and / commands)
            // Skip during autocomplete selection to avoid interfering with caret positioning
            if (!_isSelectingAutocomplete)
            {
                _autocomplete.HandleTextChange(value);
            }

            // Send typing indicator (throttled)
            if (!string.IsNullOrEmpty(value) && SelectedChannel is not null)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastTypingSent).TotalMilliseconds > TypingThrottleMs)
                {
                    _lastTypingSent = now;
                    _ = _signalR.SendTypingAsync(SelectedChannel.Id);
                }
            }
        }
    }

    // Typing indicator properties
    public bool IsAnyoneTyping => _typingUsers.Count > 0;

    public string TypingIndicatorText
    {
        get
        {
            if (_typingUsers.Count == 0) return string.Empty;
            if (_typingUsers.Count == 1) return $"{_typingUsers[0].Username} is typing...";
            if (_typingUsers.Count == 2) return $"{_typingUsers[0].Username} and {_typingUsers[1].Username} are typing...";
            return $"{_typingUsers[0].Username} and {_typingUsers.Count - 1} others are typing...";
        }
    }

    // Unified autocomplete properties (for @ mentions and / commands)
    public bool IsAutocompletePopupOpen => _autocomplete.IsPopupOpen;

    public ObservableCollection<IAutocompleteSuggestion> AutocompleteSuggestions => _autocomplete.Suggestions;

    public int SelectedAutocompleteIndex
    {
        get => _autocomplete.SelectedIndex;
        set => _autocomplete.SelectedIndex = value;
    }

    /// <summary>
    /// Self-contained GIF picker ViewModel (appears in message list).
    /// </summary>
    public GifPickerViewModel? GifPicker => _gifPicker;

    public bool IsPinnedPopupOpen
    {
        get => _isPinnedPopupOpen;
        set => this.RaiseAndSetIfChanged(ref _isPinnedPopupOpen, value);
    }

    public ObservableCollection<MessageResponse> PinnedMessages
    {
        get => _pinnedMessages;
        set => this.RaiseAndSetIfChanged(ref _pinnedMessages, value);
    }

    public int PinnedCount => Messages.Count(m => m.IsPinned);

    // Invite user popup properties
    public bool IsInviteUserPopupOpen
    {
        get => _isInviteUserPopupOpen;
        set => this.RaiseAndSetIfChanged(ref _isInviteUserPopupOpen, value);
    }

    public string InviteSearchQuery
    {
        get => _inviteSearchQuery;
        set => this.RaiseAndSetIfChanged(ref _inviteSearchQuery, value);
    }

    public bool IsSearchingUsersToInvite
    {
        get => _isSearchingUsersToInvite;
        set => this.RaiseAndSetIfChanged(ref _isSearchingUsersToInvite, value);
    }

    public ObservableCollection<UserSearchResult> InviteSearchResults
    {
        get => _inviteSearchResults;
        set => this.RaiseAndSetIfChanged(ref _inviteSearchResults, value);
    }

    public bool InviteHasNoResults
    {
        get => _inviteHasNoResults;
        set => this.RaiseAndSetIfChanged(ref _inviteHasNoResults, value);
    }

    public string? InviteStatusMessage
    {
        get => _inviteStatusMessage;
        set => this.RaiseAndSetIfChanged(ref _inviteStatusMessage, value);
    }

    public bool IsInviteStatusError
    {
        get => _isInviteStatusError;
        set => this.RaiseAndSetIfChanged(ref _isInviteStatusError, value);
    }

    // Community discovery properties
    /// <summary>
    /// Whether the user has no communities (used to show empty state).
    /// </summary>
    public bool HasNoCommunities => Communities.Count == 0;

    /// <summary>
    /// Whether the welcome modal is open (first-time user experience).
    /// </summary>
    public bool IsWelcomeModalOpen
    {
        get => _isWelcomeModalOpen;
        set => this.RaiseAndSetIfChanged(ref _isWelcomeModalOpen, value);
    }

    /// <summary>
    /// Whether the community discovery modal is open.
    /// </summary>
    public bool IsCommunityDiscoveryOpen
    {
        get => _isCommunityDiscoveryOpen;
        set => this.RaiseAndSetIfChanged(ref _isCommunityDiscoveryOpen, value);
    }

    /// <summary>
    /// Whether discoverable communities are being loaded.
    /// </summary>
    public bool IsLoadingDiscovery
    {
        get => _isLoadingDiscovery;
        set => this.RaiseAndSetIfChanged(ref _isLoadingDiscovery, value);
    }

    /// <summary>
    /// The list of discoverable communities.
    /// </summary>
    public ObservableCollection<CommunityResponse> DiscoverableCommunities => _discoverableCommunities;

    /// <summary>
    /// Whether there are no discoverable communities.
    /// </summary>
    public bool HasNoDiscoverableCommunities => _discoverableCommunities.Count == 0;

    /// <summary>
    /// Error message from loading discoverable communities.
    /// </summary>
    public string? DiscoveryError
    {
        get => _discoveryError;
        set => this.RaiseAndSetIfChanged(ref _discoveryError, value);
    }

    /// <summary>
    /// The community ID currently being joined (for showing loading state).
    /// </summary>
    public Guid? JoiningCommunityId
    {
        get => _joiningCommunityId;
        set => this.RaiseAndSetIfChanged(ref _joiningCommunityId, value);
    }

    // Controller access request properties
    public ObservableCollection<ControllerAccessRequest> PendingControllerRequests =>
        _controllerHostService.PendingRequests;

    public ObservableCollection<ActiveControllerSession> ActiveControllerSessions =>
        _controllerHostService.ActiveSessions;

    public bool HasPendingControllerRequests => PendingControllerRequests.Count > 0;
    public bool HasActiveControllerSessions => ActiveControllerSessions.Count > 0;

    public byte SelectedControllerSlot
    {
        get => _selectedControllerSlot;
        set => this.RaiseAndSetIfChanged(ref _selectedControllerSlot, value);
    }

    public byte[] AvailableControllerSlots => [0, 1, 2, 3];

    // Thread properties
    public ThreadViewModel? CurrentThread
    {
        get => _currentThread;
        set => this.RaiseAndSetIfChanged(ref _currentThread, value);
    }

    public bool IsThreadOpen => CurrentThread != null;

    public double ThreadPanelWidth
    {
        get => _threadPanelWidth;
        set => this.RaiseAndSetIfChanged(ref _threadPanelWidth, Math.Max(280, Math.Min(600, value)));
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

    public ChannelResponse? EditingChannel
    {
        get => _editingChannel;
        set => this.RaiseAndSetIfChanged(ref _editingChannel, value);
    }

    public string EditingChannelName
    {
        get => _editingChannelName;
        set => this.RaiseAndSetIfChanged(ref _editingChannelName, value);
    }

    /// <summary>
    /// The channel pending deletion (shown in confirmation dialog).
    /// </summary>
    public ChannelResponse? ChannelPendingDelete
    {
        get => _channelPendingDelete;
        set
        {
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
        set => this.RaiseAndSetIfChanged(ref _editingMessageContent, value);
    }

    public MessageResponse? ReplyingToMessage
    {
        get => _replyingToMessage;
        set => this.RaiseAndSetIfChanged(ref _replyingToMessage, value);
    }

    public bool IsReplying => ReplyingToMessage is not null;

    // File attachment properties
    public ObservableCollection<PendingAttachment> PendingAttachments => _pendingAttachments;
    public bool HasPendingAttachments => _pendingAttachments.Count > 0;

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
        _pendingAttachments.Add(new PendingAttachment
        {
            FileName = fileName,
            Stream = stream,
            Size = size,
            ContentType = contentType
        });
        this.RaisePropertyChanged(nameof(HasPendingAttachments));
    }

    public void RemovePendingAttachment(PendingAttachment attachment)
    {
        attachment.Dispose();
        _pendingAttachments.Remove(attachment);
        this.RaisePropertyChanged(nameof(HasPendingAttachments));
    }

    public void ClearPendingAttachments()
    {
        foreach (var attachment in _pendingAttachments)
            attachment.Dispose();
        _pendingAttachments.Clear();
        this.RaisePropertyChanged(nameof(HasPendingAttachments));
    }

    public void OpenLightbox(AttachmentResponse attachment)
    {
        LightboxImage = attachment;
    }

    public void CloseLightbox()
    {
        LightboxImage = null;
    }

    // GIF picker properties
    private ObservableCollection<GifResult> _gifResults = new();
    private string _gifSearchQuery = string.Empty;
    private bool _isLoadingGifs;
    private string? _gifNextPos;

    // GIF preview state (for /gif command inline preview)
    private bool _isGifPreviewVisible;
    private GifResult? _gifPreviewResult;
    private string _gifPreviewQuery = string.Empty;
    private List<GifResult> _gifPreviewResults = new();
    private int _gifPreviewIndex;

    public bool IsGifsEnabled => _isGifsEnabled;

    public ObservableCollection<GifResult> GifResults => _gifResults;

    public string GifSearchQuery
    {
        get => _gifSearchQuery;
        set => this.RaiseAndSetIfChanged(ref _gifSearchQuery, value);
    }

    public bool IsLoadingGifs
    {
        get => _isLoadingGifs;
        set => this.RaiseAndSetIfChanged(ref _isLoadingGifs, value);
    }

    // GIF preview properties (for /gif command)
    public bool IsGifPreviewVisible
    {
        get => _isGifPreviewVisible;
        set => this.RaiseAndSetIfChanged(ref _isGifPreviewVisible, value);
    }

    public GifResult? GifPreviewResult
    {
        get => _gifPreviewResult;
        set => this.RaiseAndSetIfChanged(ref _gifPreviewResult, value);
    }

    public string GifPreviewQuery
    {
        get => _gifPreviewQuery;
        set => this.RaiseAndSetIfChanged(ref _gifPreviewQuery, value);
    }

    public async Task LoadTrendingGifsAsync()
    {
        if (IsLoadingGifs) return;

        IsLoadingGifs = true;
        _gifResults.Clear();
        _gifNextPos = null;

        try
        {
            var result = await _apiClient.GetTrendingGifsAsync(24);
            if (result.Success && result.Data != null)
            {
                foreach (var gif in result.Data.Results)
                {
                    _gifResults.Add(gif);
                }
                _gifNextPos = result.Data.NextPos;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load trending GIFs: {ex.Message}");
        }
        finally
        {
            IsLoadingGifs = false;
        }
    }

    public async Task SearchGifsAsync()
    {
        if (IsLoadingGifs || string.IsNullOrWhiteSpace(GifSearchQuery)) return;

        IsLoadingGifs = true;
        _gifResults.Clear();
        _gifNextPos = null;

        try
        {
            var result = await _apiClient.SearchGifsAsync(GifSearchQuery.Trim(), 24);
            if (result.Success && result.Data != null)
            {
                foreach (var gif in result.Data.Results)
                {
                    _gifResults.Add(gif);
                }
                _gifNextPos = result.Data.NextPos;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to search GIFs: {ex.Message}");
        }
        finally
        {
            IsLoadingGifs = false;
        }
    }

    public async Task SendGifMessageAsync(GifResult gif)
    {
        if (SelectedChannel is null) return;

        try
        {
            // Send the GIF URL as the message content
            var result = await _apiClient.SendMessageAsync(SelectedChannel.Id, gif.Url, ReplyingToMessage?.Id);
            if (result.Success)
            {
                CancelReply();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send GIF: {ex.Message}");
        }
    }

    /// <summary>
    /// Handler for when user clicks Send in the GIF picker.
    /// </summary>
    private async void OnGifPickerSendRequested(GifResult gif)
    {
        await SendGifMessageAsync(gif);
    }

    public void ClearGifResults()
    {
        _gifResults.Clear();
        GifSearchQuery = string.Empty;
        _gifNextPos = null;
    }

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

    /// <summary>
    /// Sends the currently previewed GIF as a message.
    /// </summary>
    public async Task SendGifPreviewAsync()
    {
        if (GifPreviewResult == null || SelectedChannel == null) return;

        var gif = GifPreviewResult;
        CancelGifPreview();

        await SendGifMessageAsync(gif);
    }

    /// <summary>
    /// Shows the next GIF result in the preview.
    /// </summary>
    public void ShuffleGifPreview()
    {
        if (_gifPreviewResults.Count == 0) return;

        _gifPreviewIndex = (_gifPreviewIndex + 1) % _gifPreviewResults.Count;
        GifPreviewResult = _gifPreviewResults[_gifPreviewIndex];
    }

    /// <summary>
    /// Cancels the GIF preview and clears state.
    /// </summary>
    public void CancelGifPreview()
    {
        IsGifPreviewVisible = false;
        GifPreviewResult = null;
        GifPreviewQuery = string.Empty;
        _gifPreviewResults.Clear();
        _gifPreviewIndex = 0;
    }

    // Quick audio device switcher
    private ObservableCollection<AudioDeviceItem> _inputDevices = new();
    private ObservableCollection<AudioDeviceItem> _outputDevices = new();
    private bool _isAudioDevicePopupOpen;
    private float _inputLevel;
    private bool _isRefreshingDevices; // Flag to prevent binding feedback during refresh

    public ObservableCollection<AudioDeviceItem> InputDevices => _inputDevices;
    public ObservableCollection<AudioDeviceItem> OutputDevices => _outputDevices;

    public float InputLevel
    {
        get => _inputLevel;
        set => this.RaiseAndSetIfChanged(ref _inputLevel, value);
    }

    public bool IsAudioDevicePopupOpen
    {
        get => _isAudioDevicePopupOpen;
        set
        {
            if (_isAudioDevicePopupOpen == value) return;
            this.RaiseAndSetIfChanged(ref _isAudioDevicePopupOpen, value);

            // Start/stop audio level monitoring when popup opens/closes
            if (value)
            {
                _ = StartAudioLevelMonitoringAsync();
            }
            else
            {
                _ = StopAudioLevelMonitoringAsync();
            }
        }
    }

    public AudioDeviceItem? SelectedInputDeviceItem
    {
        get => InputDevices.FirstOrDefault(d => d.Value == _settingsStore.Settings.AudioInputDevice)
               ?? InputDevices.FirstOrDefault(); // Fall back to "Default"
        set
        {
            var newValue = value?.Value;

            // Ignore binding updates during device refresh
            if (_isRefreshingDevices) return;

            if (_settingsStore.Settings.AudioInputDevice == newValue) return;
            _settingsStore.Settings.AudioInputDevice = newValue;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(SelectedInputDeviceItem));
            this.RaisePropertyChanged(nameof(SelectedInputDeviceDisplay));
            this.RaisePropertyChanged(nameof(HasNoInputDevice));
            this.RaisePropertyChanged(nameof(ShowAudioDeviceWarning));
        }
    }

    public AudioDeviceItem? SelectedOutputDeviceItem
    {
        get => OutputDevices.FirstOrDefault(d => d.Value == _settingsStore.Settings.AudioOutputDevice)
               ?? OutputDevices.FirstOrDefault(); // Fall back to "Default"
        set
        {
            var newValue = value?.Value;

            // Ignore binding updates during device refresh
            if (_isRefreshingDevices) return;

            if (_settingsStore.Settings.AudioOutputDevice == newValue) return;
            _settingsStore.Settings.AudioOutputDevice = newValue;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(SelectedOutputDeviceItem));
            this.RaisePropertyChanged(nameof(SelectedOutputDeviceDisplay));
            this.RaisePropertyChanged(nameof(HasNoOutputDevice));
            this.RaisePropertyChanged(nameof(ShowAudioDeviceWarning));
        }
    }

    public string SelectedInputDeviceDisplay => _settingsStore.Settings.AudioInputDevice ?? "Default";
    public string SelectedOutputDeviceDisplay => _settingsStore.Settings.AudioOutputDevice ?? "Default";

    // Push-to-talk
    private bool _isPushToTalkActive;

    public bool PushToTalkEnabled
    {
        get => _settingsStore.Settings.PushToTalkEnabled;
        set
        {
            if (_settingsStore.Settings.PushToTalkEnabled == value) return;
            _settingsStore.Settings.PushToTalkEnabled = value;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(PushToTalkEnabled));
            this.RaisePropertyChanged(nameof(VoiceModeDescription));

            // When PTT is enabled, start muted; when disabled, unmute if was PTT muted
            if (value && IsInVoiceChannel)
            {
                IsMuted = true;
            }
        }
    }

    public string VoiceModeDescription => PushToTalkEnabled
        ? "Push-to-talk: Hold Space to talk"
        : "Voice activity: Speak to transmit";

    /// <summary>
    /// Called when push-to-talk key is pressed or released.
    /// </summary>
    public void HandlePushToTalk(bool isPressed)
    {
        if (!PushToTalkEnabled || !IsInVoiceChannel) return;

        _isPushToTalkActive = isPressed;

        // When PTT key is pressed, unmute; when released, mute
        if (isPressed)
        {
            IsMuted = false;
        }
        else
        {
            IsMuted = true;
        }
    }

    public void OpenAudioDevicePopup()
    {
        RefreshAudioDevices();
        IsAudioDevicePopupOpen = true;
    }

    public void RefreshAudioDevices()
    {
        _isRefreshingDevices = true;
        try
        {
            _inputDevices.Clear();
            _outputDevices.Clear();

            // Add default option
            _inputDevices.Add(new AudioDeviceItem(null, "Default"));
            _outputDevices.Add(new AudioDeviceItem(null, "Default"));

            // Add available devices
            foreach (var device in _audioDeviceService.GetInputDevices())
            {
                _inputDevices.Add(new AudioDeviceItem(device, device));
            }

            foreach (var device in _audioDeviceService.GetOutputDevices())
            {
                _outputDevices.Add(new AudioDeviceItem(device, device));
            }
        }
        finally
        {
            _isRefreshingDevices = false;
        }

        // Notify UI to re-sync the selection after items are populated
        this.RaisePropertyChanged(nameof(SelectedInputDeviceItem));
        this.RaisePropertyChanged(nameof(SelectedOutputDeviceItem));
    }

    private async Task StartAudioLevelMonitoringAsync()
    {
        try
        {
            await _audioDeviceService.StartInputTestAsync(
                _settingsStore.Settings.AudioInputDevice,
                level => Avalonia.Threading.Dispatcher.UIThread.Post(() => InputLevel = level)
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MainAppViewModel: Failed to start audio level monitoring - {ex.Message}");
        }
    }

    private async Task StopAudioLevelMonitoringAsync()
    {
        try
        {
            await _audioDeviceService.StopTestAsync();
            InputLevel = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MainAppViewModel: Failed to stop audio level monitoring - {ex.Message}");
        }
    }

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
    /// </summary>
    public Guid? VoiceOnOtherDeviceChannelId
    {
        get => _voiceOnOtherDeviceChannelId;
        set
        {
            this.RaiseAndSetIfChanged(ref _voiceOnOtherDeviceChannelId, value);
            this.RaisePropertyChanged(nameof(IsInVoiceOnOtherDevice));
        }
    }

    /// <summary>
    /// Name of the channel where the user is in voice on another device.
    /// </summary>
    public string? VoiceOnOtherDeviceChannelName
    {
        get => _voiceOnOtherDeviceChannelName;
        set => this.RaiseAndSetIfChanged(ref _voiceOnOtherDeviceChannelName, value);
    }

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
        Communities.FirstOrDefault(c => c.Id == CurrentVoiceChannel?.CommunityId)?.Name;

    /// <summary>
    /// Whether the user has not configured an audio input device.
    /// </summary>
    public bool HasNoInputDevice => string.IsNullOrEmpty(_settingsStore.Settings.AudioInputDevice);

    /// <summary>
    /// Whether the user has not configured an audio output device.
    /// </summary>
    public bool HasNoOutputDevice => string.IsNullOrEmpty(_settingsStore.Settings.AudioOutputDevice);

    /// <summary>
    /// Whether to show the audio device warning banner in the voice channel view.
    /// Shown when in a voice channel and either input or output device is not configured.
    /// </summary>
    public bool ShowAudioDeviceWarning => IsInVoiceChannel && (HasNoInputDevice || HasNoOutputDevice);

    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }

    public bool IsDeafened
    {
        get => _isDeafened;
        set => this.RaiseAndSetIfChanged(ref _isDeafened, value);
    }

    public bool IsCameraOn
    {
        get => _isCameraOn;
        set => this.RaiseAndSetIfChanged(ref _isCameraOn, value);
    }

    public bool IsScreenSharing
    {
        get => _isScreenSharing;
        set => this.RaiseAndSetIfChanged(ref _isScreenSharing, value);
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set => this.RaiseAndSetIfChanged(ref _isSpeaking, value);
    }

    /// <summary>
    /// Whether the host (sharer) allows viewers to draw on the screen share.
    /// This is only relevant when this user is sharing their screen.
    /// </summary>
    public bool IsDrawingAllowedForViewers
    {
        get => _screenAnnotationViewModel?.IsDrawingAllowedForViewers ?? false;
        set
        {
            if (_screenAnnotationViewModel != null)
            {
                _screenAnnotationViewModel.IsDrawingAllowedForViewers = value;
                this.RaisePropertyChanged();
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

    public ObservableCollection<VoiceParticipantResponse> VoiceParticipants
    {
        get => _voiceParticipants;
        set => this.RaiseAndSetIfChanged(ref _voiceParticipants, value);
    }

    // Computed properties for channel filtering
    public IEnumerable<ChannelResponse> TextChannels => Channels.Where(c => c.Type == ChannelType.Text);

    // Voice channels with reactive participant tracking
    public ObservableCollection<VoiceChannelViewModel> VoiceChannelViewModels
    {
        get => _voiceChannelViewModels;
        set => this.RaiseAndSetIfChanged(ref _voiceChannelViewModels, value);
    }

    // Voice channel content view for video grid
    public VoiceChannelContentViewModel? VoiceChannelContent => _voiceChannelContent;

    public ChannelResponse? SelectedVoiceChannelForViewing
    {
        get => _selectedVoiceChannelForViewing;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedVoiceChannelForViewing, value);
            this.RaisePropertyChanged(nameof(IsViewingVoiceChannel));
            if (_voiceChannelContent != null)
            {
                _voiceChannelContent.Channel = value;
            }
        }
    }

    public bool IsViewingVoiceChannel => SelectedVoiceChannelForViewing != null;

    /// <summary>
    /// Returns true if the inline DM conversation is open.
    /// </summary>
    public bool IsViewingDM => _dmContent?.IsOpen ?? false;

    // Gaming Stations properties
    public bool IsViewingGamingStations
    {
        get => _isViewingGamingStations;
        set => this.RaiseAndSetIfChanged(ref _isViewingGamingStations, value);
    }

    public bool IsLoadingStations
    {
        get => _isLoadingStations;
        set => this.RaiseAndSetIfChanged(ref _isLoadingStations, value);
    }

    public ObservableCollection<GamingStationResponse> MyStations
    {
        get => _myStations;
        set => this.RaiseAndSetIfChanged(ref _myStations, value);
    }

    public ObservableCollection<GamingStationResponse> SharedStations
    {
        get => _sharedStations;
        set => this.RaiseAndSetIfChanged(ref _sharedStations, value);
    }

    public bool HasNoStations => !IsLoadingStations && _myStations.Count == 0 && _sharedStations.Count == 0;
    public bool HasMyStations => _myStations.Count > 0;
    public bool HasSharedStations => _sharedStations.Count > 0;
    public bool IsCurrentMachineRegistered => _myStations.Any(s => s.IsOwner);

    // Station stream properties
    public bool IsViewingStationStream
    {
        get => _isViewingStationStream;
        set => this.RaiseAndSetIfChanged(ref _isViewingStationStream, value);
    }

    public bool IsConnectingToStation
    {
        get => _isConnectingToStation;
        set => this.RaiseAndSetIfChanged(ref _isConnectingToStation, value);
    }

    public Guid? ConnectedStationId
    {
        get => _connectedStationId;
        set => this.RaiseAndSetIfChanged(ref _connectedStationId, value);
    }

    public string ConnectedStationName
    {
        get => _connectedStationName;
        set => this.RaiseAndSetIfChanged(ref _connectedStationName, value);
    }

    public string StationConnectionStatus
    {
        get => _stationConnectionStatus;
        set => this.RaiseAndSetIfChanged(ref _stationConnectionStatus, value);
    }

    public int StationConnectedUserCount
    {
        get => _stationConnectedUserCount;
        set => this.RaiseAndSetIfChanged(ref _stationConnectedUserCount, value);
    }

    public int StationLatency
    {
        get => _stationLatency;
        set => this.RaiseAndSetIfChanged(ref _stationLatency, value);
    }

    public string StationResolution
    {
        get => _stationResolution;
        set => this.RaiseAndSetIfChanged(ref _stationResolution, value);
    }

    public int? StationPlayerSlot
    {
        get => _stationPlayerSlot;
        set => this.RaiseAndSetIfChanged(ref _stationPlayerSlot, value);
    }

    // Screen share picker properties
    public bool IsScreenSharePickerOpen
    {
        get => _isScreenSharePickerOpen;
        set => this.RaiseAndSetIfChanged(ref _isScreenSharePickerOpen, value);
    }

    public ScreenSharePickerViewModel? ScreenSharePicker
    {
        get => _screenSharePicker;
        set => this.RaiseAndSetIfChanged(ref _screenSharePicker, value);
    }

    /// <summary>
    /// True when the voice video overlay is open (for viewing video grid while navigating text channels).
    /// </summary>
    public bool IsVoiceVideoOverlayOpen
    {
        get => _isVoiceVideoOverlayOpen;
        set => this.RaiseAndSetIfChanged(ref _isVoiceVideoOverlayOpen, value);
    }

    public bool IsVideoFullscreen
    {
        get => _isVideoFullscreen;
        set => this.RaiseAndSetIfChanged(ref _isVideoFullscreen, value);
    }

    public VideoStreamViewModel? FullscreenStream
    {
        get => _fullscreenStream;
        set => this.RaiseAndSetIfChanged(ref _fullscreenStream, value);
    }

    /// <summary>
    /// Whether GPU-accelerated fullscreen rendering is active.
    /// </summary>
    public bool IsGpuFullscreenActive
    {
        get => _isGpuFullscreenActive;
        private set => this.RaiseAndSetIfChanged(ref _isGpuFullscreenActive, value);
    }

    /// <summary>
    /// The hardware decoder for fullscreen rendering. Set when fullscreen opens, cleared when it closes.
    /// This ensures the native view is released back to the tile when exiting fullscreen.
    /// </summary>
    public IHardwareVideoDecoder? FullscreenHardwareDecoder
    {
        get => _fullscreenHardwareDecoder;
        private set => this.RaiseAndSetIfChanged(ref _fullscreenHardwareDecoder, value);
    }

    /// <summary>
    /// Whether keyboard input capture is enabled for gaming station remote control.
    /// When enabled, keyboard events in fullscreen are forwarded to the gaming station.
    /// </summary>
    public bool IsKeyboardCaptureEnabled
    {
        get => _isKeyboardCaptureEnabled;
        set => this.RaiseAndSetIfChanged(ref _isKeyboardCaptureEnabled, value);
    }

    /// <summary>
    /// Whether mouse input capture is enabled for gaming station remote control.
    /// When enabled, mouse events in fullscreen are forwarded to the gaming station.
    /// </summary>
    public bool IsMouseCaptureEnabled
    {
        get => _isMouseCaptureEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMouseCaptureEnabled, value);
    }

    /// <summary>
    /// Whether the fullscreen stream is from a gaming station that supports remote input.
    /// </summary>
    public bool IsFullscreenGamingStation => FullscreenStream?.IsGamingStation == true;

    /// <summary>
    /// The machine ID of the gaming station being viewed in fullscreen.
    /// </summary>
    public string? FullscreenGamingStationMachineId => FullscreenStream?.GamingStationMachineId;

    public void OpenFullscreen(VideoStreamViewModel stream)
    {
        FullscreenStream = stream;
        IsVideoFullscreen = true;

        // Check if stream is using hardware decoding (zero-copy GPU pipeline)
        if (stream.IsUsingHardwareDecoder)
        {
            // Hardware decoding: frames go directly to native view, no software path needed
            FullscreenHardwareDecoder = stream.HardwareDecoder;
            IsGpuFullscreenActive = false;
            Console.WriteLine("MainApp: Hardware-accelerated fullscreen rendering (VideoToolbox/Metal)");
        }
        else if (_webRtc.IsGpuRenderingAvailable)
        {
            // Software decoding with GPU rendering
            IsGpuFullscreenActive = true;
            _webRtc.Nv12VideoFrameReceived += OnNv12VideoFrameForFullscreen;
            Console.WriteLine("MainApp: Software GPU fullscreen rendering enabled");
        }
        else
        {
            // Pure software rendering (bitmap)
            IsGpuFullscreenActive = false;
            Console.WriteLine("MainApp: Using software fullscreen rendering (bitmap)");
        }

        // Load existing strokes for this screen share
        _currentStrokes = _annotationService.GetStrokes(stream.UserId).ToList();
        this.RaisePropertyChanged(nameof(CurrentAnnotationStrokes));

        // Check if drawing is allowed for this screen share
        var isDrawingAllowed = _annotationService.IsDrawingAllowed(stream.UserId);
        Console.WriteLine($"OpenFullscreen: stream.UserId={stream.UserId}, IsDrawingAllowed={isDrawingAllowed}");
        IsDrawingAllowedByHost = isDrawingAllowed;
    }

    public void CloseFullscreen()
    {
        // Unsubscribe from NV12 frames
        if (IsGpuFullscreenActive)
        {
            _webRtc.Nv12VideoFrameReceived -= OnNv12VideoFrameForFullscreen;
            IsGpuFullscreenActive = false;
            Console.WriteLine("MainApp: GPU fullscreen rendering disabled");
        }

        // Store reference to stream before clearing
        var stream = FullscreenStream;
        var decoder = stream?.HardwareDecoder;

        // Clear fullscreen hardware decoder reference first - this releases the native view
        FullscreenHardwareDecoder = null;

        // Force the tile to reclaim the decoder by re-triggering its binding
        // The native view can only be embedded in one NativeControlHost at a time,
        // so we need to explicitly detach it from the fullscreen parent first.
        if (stream != null && decoder != null)
        {
            Console.WriteLine("MainApp: Clearing hardware decoder from stream before re-attachment");
            stream.HardwareDecoder = null;

            // Explicitly detach the native view from its current parent (fullscreen)
            Console.WriteLine("MainApp: Detaching native view from fullscreen parent");
            decoder.DetachView();

            // Use a delay to ensure the native view is fully released from fullscreen
            // The NativeControlHost needs time to process the removal
            _ = Task.Run(async () =>
            {
                await Task.Delay(150); // Give native view time to be fully released
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Console.WriteLine("MainApp: Re-attaching hardware decoder to tile");
                    stream.HardwareDecoder = decoder;
                    Console.WriteLine("MainApp: Hardware decoder re-attached to tile");
                });
            });
        }

        IsVideoFullscreen = false;
        FullscreenStream = null;
        IsAnnotationEnabled = false; // Disable drawing when exiting fullscreen

        // Disable gaming station input capture when exiting fullscreen
        IsKeyboardCaptureEnabled = false;
        IsMouseCaptureEnabled = false;
    }

    /// <summary>
    /// Toggle controller access for a user who is sharing their screen.
    /// If already streaming to this host, stop streaming. Otherwise, request access.
    /// </summary>
    public async Task ToggleControllerAccessAsync(VideoStreamViewModel stream)
    {
        if (_currentVoiceChannel == null)
        {
            Console.WriteLine("MainApp: Cannot toggle controller access - not in voice channel");
            return;
        }

        if (stream.StreamType != VideoStreamType.ScreenShare)
        {
            Console.WriteLine("MainApp: Cannot toggle controller access - not a screen share stream");
            return;
        }

        if (stream.UserId == _auth.UserId)
        {
            Console.WriteLine("MainApp: Cannot toggle controller access for yourself");
            return;
        }

        // Check if already streaming to this host - if so, stop
        if (IsStreamingControllerTo(stream.UserId))
        {
            Console.WriteLine($"MainApp: Stopping controller streaming to {stream.Username} ({stream.UserId})");
            await _controllerStreamingService.StopStreamingAsync();
            return;
        }

        Console.WriteLine($"MainApp: Requesting controller access from {stream.Username} ({stream.UserId})");
        await _controllerStreamingService.RequestAccessAsync(_currentVoiceChannel.Id, stream.UserId);
    }

    /// <summary>
    /// Toggle controller access for the current fullscreen stream.
    /// </summary>
    public async Task ToggleFullscreenControllerAccessAsync()
    {
        if (FullscreenStream == null)
        {
            Console.WriteLine("MainApp: Cannot toggle controller access - no fullscreen stream");
            return;
        }

        await ToggleControllerAccessAsync(FullscreenStream);
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

    private void OnNv12VideoFrameForFullscreen(Guid userId, VideoStreamType streamType, int width, int height, byte[] nv12Data)
    {
        // Only render frames from the fullscreen stream
        if (FullscreenStream?.UserId == userId && FullscreenStream?.StreamType == streamType)
        {
            GpuFullscreenFrameReceived?.Invoke(width, height, nv12Data);
        }
    }

    // Drawing annotation properties
    public bool IsAnnotationEnabled
    {
        get => _isAnnotationEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAnnotationEnabled, value);
    }

    /// <summary>
    /// Whether the host (sharer) has allowed viewers to draw on the screen share.
    /// </summary>
    public bool IsDrawingAllowedByHost
    {
        get => _isDrawingAllowedByHost;
        private set => this.RaiseAndSetIfChanged(ref _isDrawingAllowedByHost, value);
    }

    public string AnnotationColor
    {
        get => _annotationColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _annotationColor, value);
            _annotationService.CurrentColor = value;
        }
    }

    public string[] AvailableAnnotationColors => AnnotationService.AvailableColors;

    public AnnotationService AnnotationService => _annotationService;

    public List<DrawingStroke> CurrentAnnotationStrokes => _currentStrokes;

    private void OnAnnotationStrokeAdded(Guid sharerId, DrawingStroke stroke)
    {
        // Only update if we're viewing this sharer's screen in fullscreen
        if (FullscreenStream?.UserId == sharerId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentStrokes = _annotationService.GetStrokes(sharerId).ToList();
                this.RaisePropertyChanged(nameof(CurrentAnnotationStrokes));
            });
        }
    }

    private void OnAnnotationStrokesCleared(Guid sharerId)
    {
        // Only update if we're viewing this sharer's screen in fullscreen
        if (FullscreenStream?.UserId == sharerId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentStrokes.Clear();
                this.RaisePropertyChanged(nameof(CurrentAnnotationStrokes));
            });
        }
    }

    private void OnDrawingAllowedChanged(Guid sharerId, bool isAllowed)
    {
        // Only update if we're viewing this sharer's screen in fullscreen
        if (FullscreenStream?.UserId == sharerId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsDrawingAllowedByHost = isAllowed;
                // If drawing was disabled by host, turn off our drawing mode
                if (!isAllowed && IsAnnotationEnabled)
                {
                    IsAnnotationEnabled = false;
                }
            });
        }
    }

    public async Task AddAnnotationStrokeAsync(DrawingStroke stroke)
    {
        if (CurrentVoiceChannel == null || FullscreenStream == null) return;

        await _annotationService.AddStrokeAsync(
            CurrentVoiceChannel.Id,
            FullscreenStream.UserId,
            stroke);
    }

    public async Task UpdateAnnotationStrokeAsync(DrawingStroke stroke)
    {
        if (CurrentVoiceChannel == null || FullscreenStream == null) return;

        await _annotationService.UpdateStrokeAsync(
            CurrentVoiceChannel.Id,
            FullscreenStream.UserId,
            stroke);
    }

    public async Task ClearAnnotationsAsync()
    {
        if (CurrentVoiceChannel == null || FullscreenStream == null) return;

        await _annotationService.ClearStrokesAsync(
            CurrentVoiceChannel.Id,
            FullscreenStream.UserId);
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
    public ReactiveCommand<ChannelResponse, Unit> StartEditChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveChannelNameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditChannelCommand { get; }
    public ReactiveCommand<ChannelResponse, Unit> DeleteChannelCommand { get; }
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

    // GIF preview commands (for /gif inline preview)
    public ReactiveCommand<Unit, Unit> SendGifPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> ShuffleGifPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelGifPreviewCommand { get; }

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
            var result = await _apiClient.GetCommunitiesAsync();
            if (result.Success && result.Data is not null)
            {
                Communities.Clear();
                foreach (var community in result.Data)
                    Communities.Add(community);

                // Notify HasNoCommunities property
                this.RaisePropertyChanged(nameof(HasNoCommunities));

                // Select first community if none selected
                if (SelectedCommunity is null && Communities.Count > 0)
                    SelectedCommunity = Communities[0];

                // Show welcome modal for first-time users with no communities
                if (Communities.Count == 0 && !_settingsStore.Settings.HasSeenWelcome)
                {
                    IsWelcomeModalOpen = true;
                }
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void CloseWelcomeModal()
    {
        IsWelcomeModalOpen = false;
        _settingsStore.Settings.HasSeenWelcome = true;
        _settingsStore.Save();
    }

    private async Task WelcomeBrowseCommunitiesAsync()
    {
        CloseWelcomeModal();
        await OpenCommunityDiscoveryAsync();
    }

    private async Task WelcomeCreateCommunityAsync()
    {
        CloseWelcomeModal();
        await CreateCommunityAsync();
    }

    private async Task OpenCommunityDiscoveryAsync()
    {
        IsCommunityDiscoveryOpen = true;
        await LoadDiscoverableCommunitiesAsync();
    }

    private async Task LoadDiscoverableCommunitiesAsync()
    {
        IsLoadingDiscovery = true;
        DiscoveryError = null;

        try
        {
            var result = await _apiClient.DiscoverCommunitiesAsync();
            if (result.Success && result.Data is not null)
            {
                _discoverableCommunities.Clear();
                foreach (var community in result.Data)
                    _discoverableCommunities.Add(community);

                this.RaisePropertyChanged(nameof(HasNoDiscoverableCommunities));
            }
            else
            {
                DiscoveryError = result.Error ?? "Failed to load communities";
            }
        }
        catch (Exception ex)
        {
            DiscoveryError = ex.Message;
        }
        finally
        {
            IsLoadingDiscovery = false;
        }
    }

    private async Task JoinDiscoveredCommunityAsync(CommunityResponse community)
    {
        if (JoiningCommunityId is not null) return; // Already joining

        JoiningCommunityId = community.Id;

        try
        {
            var result = await _apiClient.JoinCommunityAsync(community.Id);
            if (result.Success)
            {
                // Close the discovery modal
                IsCommunityDiscoveryOpen = false;

                // Reload communities
                await LoadCommunitiesAsync();

                // Select the joined community
                var joinedCommunity = Communities.FirstOrDefault(c => c.Id == community.Id);
                if (joinedCommunity is not null)
                {
                    SelectedCommunity = joinedCommunity;
                }
            }
            else
            {
                DiscoveryError = result.Error ?? "Failed to join community";
            }
        }
        catch (Exception ex)
        {
            DiscoveryError = ex.Message;
        }
        finally
        {
            JoiningCommunityId = null;
        }
    }

    private async Task LoadChannelsAsync()
    {
        if (SelectedCommunity is null) return;

        IsLoading = true;
        try
        {
            Console.WriteLine($"LoadChannelsAsync: Loading channels for community {SelectedCommunity.Id}");
            var channelsResult = await _apiClient.GetChannelsAsync(SelectedCommunity.Id);
            var membersResult = await _apiClient.GetMembersAsync(SelectedCommunity.Id);

            if (channelsResult.Success && channelsResult.Data is not null)
            {
                Channels.Clear();
                foreach (var channel in channelsResult.Data)
                    Channels.Add(channel);

                // Notify computed properties
                this.RaisePropertyChanged(nameof(TextChannels));

                // Create VoiceChannelViewModels for all voice channels
                var voiceChannels = channelsResult.Data.Where(c => c.Type == ChannelType.Voice).ToList();
                Console.WriteLine($"LoadChannelsAsync: Found {voiceChannels.Count} voice channels");

                VoiceChannelViewModels.Clear();
                foreach (var voiceChannel in voiceChannels)
                {
                    var vm = CreateVoiceChannelViewModel(voiceChannel);

                    // Load participants for this voice channel
                    Console.WriteLine($"LoadChannelsAsync: Loading participants for voice channel {voiceChannel.Name} ({voiceChannel.Id})");
                    var participants = await _signalR.GetVoiceParticipantsAsync(voiceChannel.Id);
                    Console.WriteLine($"LoadChannelsAsync: Got {participants.Count()} participants for {voiceChannel.Name}");
                    vm.SetParticipants(participants);

                    VoiceChannelViewModels.Add(vm);
                }
                Console.WriteLine($"LoadChannelsAsync: Created {VoiceChannelViewModels.Count} VoiceChannelViewModels");

                // Select first text channel if none selected or if it belongs to a different community
                var firstTextChannel = TextChannels.FirstOrDefault();
                if (firstTextChannel is not null && (SelectedChannel is null || SelectedChannel.CommunityId != SelectedCommunity.Id))
                    SelectedChannel = firstTextChannel;
            }

            if (membersResult.Success && membersResult.Data is not null)
            {
                Members.Clear();
                foreach (var member in membersResult.Data)
                    Members.Add(member);
                this.RaisePropertyChanged(nameof(SortedMembers));
                _membersListViewModel?.NotifyMembersChanged();

                // Set current user's role
                var currentMember = membersResult.Data.FirstOrDefault(m => m.UserId == _auth.UserId);
                CurrentUserRole = currentMember?.Role;
                _membersListViewModel?.UpdateCurrentUserRole(currentMember?.Role);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadMessagesAsync()
    {
        if (SelectedChannel is null) return;

        IsMessagesLoading = true;
        try
        {
            var result = await _apiClient.GetMessagesAsync(SelectedChannel.Id);
            if (result.Success && result.Data is not null)
            {
                Messages.Clear();
                foreach (var message in result.Data)
                    Messages.Add(message);
            }
        }
        finally
        {
            IsMessagesLoading = false;
        }
    }

    private async Task SendMessageAsync()
    {
        if (SelectedChannel is null) return;

        // Allow empty content if there are attachments
        if (string.IsNullOrWhiteSpace(MessageInput) && !HasPendingAttachments) return;

        var content = MessageInput.Trim();

        // Check for /gif or /giphy command
        if (content.StartsWith("/gif ", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("/giphy ", StringComparison.OrdinalIgnoreCase))
        {
            // Extract search query (everything after the command)
            var spaceIndex = content.IndexOf(' ');
            var query = content.Substring(spaceIndex + 1).Trim();

            if (!string.IsNullOrWhiteSpace(query))
            {
                MessageInput = string.Empty;
                await ShowGifPreviewAsync(query);
                return;
            }
        }

        // Process other slash commands
        content = ProcessSlashCommands(content);

        // If content became empty after processing (e.g., just "/shrug" with no other text), that's fine
        // But if there's nothing to send, return
        if (string.IsNullOrWhiteSpace(content) && !HasPendingAttachments) return;

        var replyToId = ReplyingToMessage?.Id;
        MessageInput = string.Empty;
        ReplyingToMessage = null;
        this.RaisePropertyChanged(nameof(IsReplying));

        ApiResult<MessageResponse> result;

        if (HasPendingAttachments)
        {
            // Send with attachments
            var files = _pendingAttachments.Select(a => new FileAttachment
            {
                FileName = a.FileName,
                Stream = a.Stream,
                ContentType = a.ContentType
            }).ToList();

            result = await _apiClient.SendMessageWithAttachmentsAsync(SelectedChannel.Id, content, replyToId, files);

            // Clear pending attachments (streams are now consumed)
            _pendingAttachments.Clear();
            this.RaisePropertyChanged(nameof(HasPendingAttachments));
        }
        else
        {
            // Send text-only message
            result = await _apiClient.SendMessageAsync(SelectedChannel.Id, content, replyToId);
        }

        if (result.Success && result.Data is not null)
        {
            Messages.Add(result.Data);
        }
        else
        {
            ErrorMessage = result.Error;
            MessageInput = content; // Restore message on failure
        }
    }

    /// <summary>
    /// Processes slash commands in the message content.
    /// Returns the modified content with commands replaced.
    /// </summary>
    private string ProcessSlashCommands(string content)
    {
        // Text face commands - append text (can have other content before)
        // Format: "/shrug" or "some text /shrug" or "/shrug some text"
        foreach (var cmd in SlashCommandRegistry.BaseCommands)
        {
            if (cmd.AppendText == null) continue;

            var cmdPattern = $"/{cmd.Name}";

            // Check if message is just the command
            if (content.Equals(cmdPattern, StringComparison.OrdinalIgnoreCase) ||
                content.Equals($"{cmdPattern} ", StringComparison.OrdinalIgnoreCase))
            {
                return cmd.AppendText;
            }

            // Check if command is at start with text after: "/shrug hello" -> "hello ¯\_(ツ)_/¯"
            if (content.StartsWith($"{cmdPattern} ", StringComparison.OrdinalIgnoreCase))
            {
                var textAfter = content.Substring(cmdPattern.Length + 1).Trim();
                return $"{textAfter} {cmd.AppendText}";
            }

            // Check if command is at end: "hello /shrug" -> "hello ¯\_(ツ)_/¯"
            if (content.EndsWith($" {cmdPattern}", StringComparison.OrdinalIgnoreCase))
            {
                var textBefore = content.Substring(0, content.Length - cmdPattern.Length - 1).Trim();
                return $"{textBefore} {cmd.AppendText}";
            }
        }

        // /me command - format as action (italic)
        if (content.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
        {
            var action = content.Substring(4).Trim();
            if (!string.IsNullOrWhiteSpace(action))
            {
                return $"*{action}*";
            }
        }

        // /spoiler command - wrap in spoiler tags
        if (content.StartsWith("/spoiler ", StringComparison.OrdinalIgnoreCase))
        {
            var spoilerText = content.Substring(9).Trim();
            if (!string.IsNullOrWhiteSpace(spoilerText))
            {
                return $"||{spoilerText}||";
            }
        }

        return content;
    }

    private void StartReplyToMessage(MessageResponse message)
    {
        ReplyingToMessage = message;
        this.RaisePropertyChanged(nameof(IsReplying));
    }

    private void CancelReply()
    {
        ReplyingToMessage = null;
        this.RaisePropertyChanged(nameof(IsReplying));
    }

    // Gaming Stations methods
    private async Task OpenGamingStationsAsync()
    {
        // Close DM and voice channel views
        _dmContent?.Close();
        SelectedVoiceChannelForViewing = null;

        IsViewingGamingStations = true;
        await LoadStationsAsync();
    }

    private async Task LoadStationsAsync()
    {
        IsLoadingStations = true;
        try
        {
            var result = await _apiClient.GetStationsAsync();
            if (result.Success && result.Data is not null)
            {
                _myStations.Clear();
                _sharedStations.Clear();

                foreach (var station in result.Data)
                {
                    if (station.IsOwner)
                        _myStations.Add(station);
                    else
                        _sharedStations.Add(station);
                }

                this.RaisePropertyChanged(nameof(HasNoStations));
                this.RaisePropertyChanged(nameof(HasMyStations));
                this.RaisePropertyChanged(nameof(HasSharedStations));
                this.RaisePropertyChanged(nameof(IsCurrentMachineRegistered));
            }
        }
        finally
        {
            IsLoadingStations = false;
        }
    }

    private async Task RegisterStationAsync()
    {
        // Get a unique machine identifier
        var machineId = Environment.MachineName + "-" + Environment.UserName;

        // For now, use a simple name. In a real app, you'd show a dialog.
        var stationName = Environment.MachineName;

        var result = await _apiClient.RegisterStationAsync(stationName, "Gaming Station", machineId);
        if (result.Success && result.Data is not null)
        {
            _myStations.Add(result.Data);
            this.RaisePropertyChanged(nameof(HasNoStations));
            this.RaisePropertyChanged(nameof(HasMyStations));
            this.RaisePropertyChanged(nameof(IsCurrentMachineRegistered));
        }
        else
        {
            ErrorMessage = result.Error;
        }
    }

    // Gaming Station methods - new architecture uses voice channels
    // These are placeholder methods that will be replaced when Phase 3+ is implemented

    private async Task ConnectToStationAsync(GamingStationResponse station)
    {
        // Old architecture - keeping as placeholder for now
        // In the new architecture, we use voice channels instead of direct station connections
        Console.WriteLine($"ConnectToStationAsync: Old architecture method called for station {station.Name}");
        Console.WriteLine("In the new architecture, use CommandStationJoinChannelAsync to add station to a voice channel");
        await Task.CompletedTask;
    }

    private async Task DisconnectFromStationAsync()
    {
        // Old architecture - keeping as placeholder for now
        // In the new architecture, we command the station to leave the voice channel
        Console.WriteLine("DisconnectFromStationAsync: Old architecture method called");
        Console.WriteLine("In the new architecture, use CommandStationLeaveChannelAsync to remove station from voice channel");
        await Task.CompletedTask;
    }

    private void ToggleStationFullscreen()
    {
        // TODO: Implement fullscreen toggle for station stream
        Console.WriteLine("Toggle station fullscreen");
    }

    private void ManageStation(GamingStationResponse station)
    {
        // TODO: Show station management modal
        Console.WriteLine($"Managing station: {station.Name} (ID: {station.Id})");
    }

    /// <summary>
    /// Sends keyboard input to a gaming station in the current voice channel.
    /// </summary>
    public async Task SendStationKeyboardInputAsync(StationKeyboardInput input)
    {
        // In the new architecture, input is sent to the gaming station in our current voice channel
        if (CurrentVoiceChannel is null) return;

        try
        {
            await _signalR.SendStationKeyboardInputAsync(CurrentVoiceChannel.Id, input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send keyboard input to station: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends mouse input to a gaming station in the current voice channel.
    /// </summary>
    public async Task SendStationMouseInputAsync(StationMouseInput input)
    {
        // In the new architecture, input is sent to the gaming station in our current voice channel
        if (CurrentVoiceChannel is null) return;

        try
        {
            await _signalR.SendStationMouseInputAsync(CurrentVoiceChannel.Id, input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send mouse input to station: {ex.Message}");
        }
    }

    /// <summary>
    /// Injects keyboard input into the local system (for gaming station mode).
    /// This is called when the gaming station receives input from the remote owner.
    /// </summary>
    private void InjectKeyboardInput(StationKeyboardInput input)
    {
        // Platform-specific input injection
        // For a full implementation, this would use:
        // - Windows: SendInput API with KEYBDINPUT structure
        // - macOS: CGEventPost with CGEventCreateKeyboardEvent
        // - Linux: XTest extension or uinput virtual device

        // For now, log that we would inject the input
        // Actual injection requires platform-specific native code
        Console.WriteLine($"[INJECT KEYBOARD] key={input.Key} down={input.IsDown} ctrl={input.Ctrl} alt={input.Alt} shift={input.Shift}");
    }

    /// <summary>
    /// Injects mouse input into the local system (for gaming station mode).
    /// This is called when the gaming station receives input from the remote owner.
    /// </summary>
    private void InjectMouseInput(StationMouseInput input)
    {
        // Platform-specific input injection
        // For a full implementation, this would use:
        // - Windows: SendInput API with MOUSEINPUT structure
        // - macOS: CGEventPost with CGEventCreateMouseEvent
        // - Linux: XTest extension or uinput virtual device

        // The normalized coordinates (0-1) need to be scaled to screen dimensions
        // For now, log that we would inject the input
        Console.WriteLine($"[INJECT MOUSE] type={input.Type} x={input.X:F3} y={input.Y:F3} button={input.Button}");
    }

    private async Task CreateCommunityAsync()
    {
        var result = await _apiClient.CreateCommunityAsync("New Community", null);
        if (result.Success && result.Data is not null)
        {
            Communities.Add(result.Data);
            SelectedCommunity = result.Data;
        }
        else
        {
            ErrorMessage = result.Error;
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

            var result = await _apiClient.CreateChannelAsync(SelectedCommunity.Id, channelName, null);
            if (result.Success && result.Data is not null)
            {
                // Add locally - SignalR handler will detect duplicate and skip
                if (!Channels.Any(c => c.Id == result.Data.Id))
                {
                    Channels.Add(result.Data);
                    this.RaisePropertyChanged(nameof(TextChannels));
                }
                SelectedChannel = result.Data;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void StartEditChannel(ChannelResponse channel)
    {
        EditingChannel = channel;
        EditingChannelName = channel.Name;
    }

    private void CancelEditChannel()
    {
        EditingChannel = null;
        EditingChannelName = string.Empty;
    }

    private async Task SaveChannelNameAsync()
    {
        Console.WriteLine($"SaveChannelNameAsync called. EditingChannel: {EditingChannel?.Name}, EditingChannelName: {EditingChannelName}");

        if (EditingChannel is null || SelectedCommunity is null || string.IsNullOrWhiteSpace(EditingChannelName))
        {
            Console.WriteLine("Early return - EditingChannel/SelectedServer is null or name is empty");
            return;
        }

        IsLoading = true;
        try
        {
            Console.WriteLine($"Calling UpdateChannelAsync for channel {EditingChannel.Id} on community {SelectedCommunity.Id}");
            var result = await _apiClient.UpdateChannelAsync(SelectedCommunity.Id, EditingChannel.Id, EditingChannelName.Trim(), null);
            Console.WriteLine($"UpdateChannelAsync result: Success={result.Success}, Error={result.Error}");

            if (result.Success && result.Data is not null)
            {
                // Update the channel in the list
                var index = Channels.IndexOf(EditingChannel);
                Console.WriteLine($"Channel index in list: {index}");
                if (index >= 0)
                {
                    Channels[index] = result.Data;
                }

                // Update selected channel if it was the one being edited
                if (SelectedChannel?.Id == EditingChannel.Id)
                {
                    SelectedChannel = result.Data;
                }

                EditingChannel = null;
                EditingChannelName = string.Empty;
            }
            else
            {
                ErrorMessage = result.Error;
                Console.WriteLine($"Error: {result.Error}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RequestDeleteChannel(ChannelResponse channel)
    {
        ChannelPendingDelete = channel;
    }

    private void CancelDeleteChannel()
    {
        ChannelPendingDelete = null;
    }

    private async Task ConfirmDeleteChannelAsync()
    {
        if (ChannelPendingDelete is null || SelectedCommunity is null) return;

        var channel = ChannelPendingDelete;

        // Check if this is the currently selected channel
        var wasSelected = SelectedChannel?.Id == channel.Id;

        IsLoading = true;
        try
        {
            var result = await _apiClient.DeleteChannelAsync(channel.Id);
            if (result.Success)
            {
                Console.WriteLine($"Deleted channel: {channel.Name}");

                // Remove from local collection (SignalR should also handle this)
                var toRemove = Channels.FirstOrDefault(c => c.Id == channel.Id);
                if (toRemove is not null)
                {
                    Channels.Remove(toRemove);
                }

                // If the deleted channel was selected, select another one
                if (wasSelected && Channels.Count > 0)
                {
                    var textChannels = Channels.Where(c => c.Type == Shared.Models.ChannelType.Text).ToList();
                    SelectedChannel = textChannels.FirstOrDefault() ?? Channels.FirstOrDefault();
                }
                else if (wasSelected)
                {
                    SelectedChannel = null;
                }

                // Clear the pending delete
                ChannelPendingDelete = null;
            }
            else
            {
                ErrorMessage = result.Error;
                Console.WriteLine($"Error deleting channel: {result.Error}");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReorderChannelsAsync(List<Guid> channelIds)
    {
        if (SelectedCommunity is null) return;

        Console.WriteLine($"Reordering {channelIds.Count} channels");

        // Store original order for rollback
        var originalChannels = Channels.ToList();
        var originalVoiceOrder = VoiceChannelViewModels.ToList();

        // Apply optimistically - update UI immediately
        ApplyChannelOrder(channelIds);
        ClearPreviewState();

        // Mark that we're expecting a SignalR event for this reorder
        _pendingReorderCommunityId = SelectedCommunity.Id;

        try
        {
            var result = await _apiClient.ReorderChannelsAsync(SelectedCommunity.Id, channelIds);
            if (result.Success && result.Data is not null)
            {
                Console.WriteLine($"Successfully reordered channels");
                // Server confirmed - nothing more to do (we already updated optimistically)
            }
            else
            {
                // Server rejected - rollback to original order
                Console.WriteLine($"Error reordering channels: {result.Error}");
                ErrorMessage = result.Error ?? "Failed to reorder channels";
                RollbackChannelOrder(originalChannels, originalVoiceOrder);
                _pendingReorderCommunityId = null;
            }
        }
        catch (Exception ex)
        {
            // Network error - rollback to original order
            Console.WriteLine($"Exception reordering channels: {ex}");
            ErrorMessage = $"Error reordering channels: {ex.Message}";
            RollbackChannelOrder(originalChannels, originalVoiceOrder);
            _pendingReorderCommunityId = null;
        }
    }

    private void ApplyChannelOrder(List<Guid> channelIds)
    {
        // Create a lookup for new positions
        var positionLookup = channelIds.Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        // Sort Channels by the new order
        var sortedChannels = Channels.OrderBy(c => positionLookup.GetValueOrDefault(c.Id, int.MaxValue)).ToList();
        Channels.Clear();
        foreach (var channel in sortedChannels)
        {
            Channels.Add(channel);
        }

        // Update VoiceChannelViewModels positions and re-sort
        foreach (var voiceVm in VoiceChannelViewModels)
        {
            if (positionLookup.TryGetValue(voiceVm.Id, out var newPosition))
            {
                voiceVm.Position = newPosition;
            }
        }

        var sortedVoiceChannels = VoiceChannelViewModels.OrderBy(v => v.Position).ToList();
        VoiceChannelViewModels.Clear();
        foreach (var vm in sortedVoiceChannels)
        {
            VoiceChannelViewModels.Add(vm);
        }

        this.RaisePropertyChanged(nameof(TextChannels));
        this.RaisePropertyChanged(nameof(VoiceChannelViewModels));
    }

    private void RollbackChannelOrder(List<ChannelResponse> originalChannels, List<VoiceChannelViewModel> originalVoiceOrder)
    {
        Console.WriteLine("Rolling back channel order");

        Channels.Clear();
        foreach (var channel in originalChannels)
        {
            Channels.Add(channel);
        }

        VoiceChannelViewModels.Clear();
        foreach (var vm in originalVoiceOrder)
        {
            VoiceChannelViewModels.Add(vm);
        }

        this.RaisePropertyChanged(nameof(TextChannels));
        this.RaisePropertyChanged(nameof(VoiceChannelViewModels));
    }

    // Preview state for drag feedback - tracks gap position, not actual reorder
    private Guid? _previewGapTargetId;
    private bool _previewGapAbove;

    private void PreviewReorder((Guid DraggedId, Guid TargetId, bool DropBefore) args)
    {
        // Store original order on first preview call for this drag
        if (_originalVoiceChannelOrder is null || _currentPreviewDraggedId != args.DraggedId)
        {
            _originalVoiceChannelOrder = VoiceChannelViewModels.ToList();
            _currentPreviewDraggedId = args.DraggedId;
        }

        // Just track where the gap should be - don't reorder yet
        _previewGapTargetId = args.TargetId;
        _previewGapAbove = args.DropBefore;

        // Update gap visibility for all items
        foreach (var vm in VoiceChannelViewModels)
        {
            if (vm.Id == args.TargetId)
            {
                vm.ShowGapAbove = args.DropBefore;
                vm.ShowGapBelow = !args.DropBefore;
            }
            else
            {
                vm.ShowGapAbove = false;
                vm.ShowGapBelow = false;
            }

            // Hide the dragged item
            vm.IsDragSource = vm.Id == args.DraggedId;
        }
    }

    private void CancelPreview()
    {
        // Clear gap visibility
        foreach (var vm in VoiceChannelViewModels)
        {
            vm.ShowGapAbove = false;
            vm.ShowGapBelow = false;
            vm.IsDragSource = false;
        }

        _originalVoiceChannelOrder = null;
        _currentPreviewDraggedId = null;
        _previewGapTargetId = null;
    }

    // Call this after successful reorder to clear preview state
    private void ClearPreviewState()
    {
        foreach (var vm in VoiceChannelViewModels)
        {
            vm.ShowGapAbove = false;
            vm.ShowGapBelow = false;
            vm.IsDragSource = false;
        }

        _originalVoiceChannelOrder = null;
        _currentPreviewDraggedId = null;
        _previewGapTargetId = null;
    }

    // Message edit/delete methods
    private void StartEditMessage(MessageResponse message)
    {
        // Only allow editing own messages
        if (message.AuthorId != UserId) return;

        EditingMessage = message;
        EditingMessageContent = message.Content;
    }

    private void CancelEditMessage()
    {
        EditingMessage = null;
        EditingMessageContent = string.Empty;
    }

    private async Task SaveMessageEditAsync()
    {
        if (EditingMessage is null || SelectedChannel is null || string.IsNullOrWhiteSpace(EditingMessageContent))
            return;

        IsMessagesLoading = true;
        try
        {
            var result = await _apiClient.UpdateMessageAsync(SelectedChannel.Id, EditingMessage.Id, EditingMessageContent.Trim());

            if (result.Success && result.Data is not null)
            {
                // Update the message in the list
                var index = Messages.ToList().FindIndex(m => m.Id == EditingMessage.Id);
                if (index >= 0)
                    Messages[index] = result.Data;

                EditingMessage = null;
                EditingMessageContent = string.Empty;
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsMessagesLoading = false;
        }
    }

    private async Task DeleteMessageAsync(MessageResponse message)
    {
        if (SelectedChannel is null) return;

        IsMessagesLoading = true;
        try
        {
            var result = await _apiClient.DeleteMessageAsync(SelectedChannel.Id, message.Id);

            if (result.Success)
            {
                Messages.Remove(message);
            }
            else
            {
                ErrorMessage = result.Error;
            }
        }
        finally
        {
            IsMessagesLoading = false;
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

            var result = await _apiClient.CreateChannelAsync(SelectedCommunity.Id, channelName, null, ChannelType.Voice);
            if (result.Success && result.Data is not null)
            {
                Console.WriteLine($"CreateVoiceChannelAsync: Created voice channel {result.Data.Name} ({result.Data.Id})");

                // Add to Channels collection
                if (!Channels.Any(c => c.Id == result.Data.Id))
                {
                    Channels.Add(result.Data);
                }

                // Add to VoiceChannelViewModels
                if (!VoiceChannelViewModels.Any(v => v.Id == result.Data.Id))
                {
                    var vm = CreateVoiceChannelViewModel(result.Data);
                    VoiceChannelViewModels.Add(vm);
                    Console.WriteLine($"CreateVoiceChannelAsync: Added VoiceChannelViewModel for {result.Data.Name}");
                }
            }
            else
            {
                ErrorMessage = result.Error;
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
        VoiceOnOtherDeviceChannelId = null;
        VoiceOnOtherDeviceChannelName = null;

        // Leave current voice channel if any
        if (CurrentVoiceChannel is not null)
        {
            await LeaveVoiceChannelAsync();
        }

        // Show connecting state immediately for better UX
        CurrentVoiceChannel = channel;
        VoiceConnectionStatus = VoiceConnectionStatus.Connecting;

        var participant = await _signalR.JoinVoiceChannelAsync(channel.Id);
        if (participant is not null)
        {
            // Apply persisted mute/deafen state (already loaded from settings)
            // and send it to the server
            _webRtc.SetMuted(IsMuted);
            _webRtc.SetDeafened(IsDeafened);
            if (IsMuted || IsDeafened)
            {
                await _signalR.UpdateVoiceStateAsync(channel.Id, new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened));
            }

            // Load existing participants (includes ourselves)
            var participants = await _signalR.GetVoiceParticipantsAsync(channel.Id);
            Console.WriteLine($"JoinVoiceChannelAsync: Got {participants.Count()} participants for {channel.Name}");

            // Update VoiceParticipants (for voice controls panel)
            VoiceParticipants.Clear();
            foreach (var p in participants)
            {
                VoiceParticipants.Add(p);
            }

            // Update VoiceChannelViewModel (for channel list display)
            var voiceChannelVm = VoiceChannelViewModels.FirstOrDefault(v => v.Id == channel.Id);
            if (voiceChannelVm is not null)
            {
                voiceChannelVm.SetParticipants(participants);
                Console.WriteLine($"JoinVoiceChannelAsync: Updated VoiceChannelVM [{channel.Name}] with {participants.Count()} participants");
            }
            else
            {
                Console.WriteLine($"JoinVoiceChannelAsync: WARNING - VoiceChannelVM for {channel.Name} not found!");
            }

            // Update VoiceChannelContent for video grid display (but don't auto-navigate to it)
            // User can open the video overlay manually via ShowVoiceVideoOverlayCommand
            _voiceChannelContent?.SetParticipants(participants);

            // Start WebRTC connections to all existing participants
            await _webRtc.JoinVoiceChannelAsync(channel.Id, participants);

            Console.WriteLine($"Joined voice channel: {channel.Name}");
        }
        else
        {
            // Join failed - reset state
            CurrentVoiceChannel = null;
            VoiceConnectionStatus = VoiceConnectionStatus.Disconnected;
            Console.WriteLine($"Failed to join voice channel: {channel.Name}");
        }
    }

    private async Task LeaveVoiceChannelAsync()
    {
        if (CurrentVoiceChannel is null) return;

        var channelId = CurrentVoiceChannel.Id;
        var channelName = CurrentVoiceChannel.Name;

        // Stop screen sharing first (this closes the annotation overlay)
        if (IsScreenSharing)
        {
            HideSharerAnnotationOverlay();
            await _webRtc.SetScreenSharingAsync(false);
            _currentScreenShareSettings = null;
            _annotationService.OnScreenShareEnded(_auth.UserId);
        }

        // Leave WebRTC connections
        await _webRtc.LeaveVoiceChannelAsync();

        await _signalR.LeaveVoiceChannelAsync(channelId);

        // Remove ourselves from the VoiceChannelViewModel
        var voiceChannelVm = VoiceChannelViewModels.FirstOrDefault(v => v.Id == channelId);
        if (voiceChannelVm is not null)
        {
            voiceChannelVm.RemoveParticipant(_auth.UserId);
            Console.WriteLine($"LeaveVoiceChannelAsync: Removed self from VoiceChannelVM [{channelName}]");
        }

        CurrentVoiceChannel = null;
        VoiceParticipants.Clear();
        // Note: IsMuted and IsDeafened are persisted and NOT reset when leaving
        IsCameraOn = false;
        IsScreenSharing = false;

        // Close voice video overlay and clear content view
        IsVoiceVideoOverlayOpen = false;
        SelectedVoiceChannelForViewing = null;
        _voiceChannelContent?.SetParticipants(Enumerable.Empty<VoiceParticipantResponse>());

        Console.WriteLine("Left voice channel");
    }

    private async Task ToggleMuteAsync()
    {
        // Check if server-muted (cannot unmute if server-muted)
        if (CurrentVoiceChannel is not null && !IsMuted)
        {
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            var currentParticipant = voiceChannel?.Participants.FirstOrDefault(p => p.UserId == _auth.UserId);
            if (currentParticipant?.IsServerMuted == true)
            {
                Console.WriteLine("Cannot unmute: user is server-muted");
                return;
            }
        }

        IsMuted = !IsMuted;

        // Persist to settings
        _settingsStore.Settings.IsMuted = IsMuted;
        _settingsStore.Save();

        // If in a voice channel, apply immediately
        if (CurrentVoiceChannel is not null)
        {
            _webRtc.SetMuted(IsMuted);
            await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsMuted: IsMuted));

            // Update our own state in the local view models
            var state = new VoiceStateUpdate(IsMuted: IsMuted);
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            voiceChannel?.UpdateParticipantState(_auth.UserId, state);
            _voiceChannelContent?.UpdateParticipantState(_auth.UserId, state);
        }
    }

    private async Task ToggleDeafenAsync()
    {
        // Check if server-deafened (cannot undeafen if server-deafened)
        if (CurrentVoiceChannel is not null && !IsDeafened)
        {
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            var currentParticipant = voiceChannel?.Participants.FirstOrDefault(p => p.UserId == _auth.UserId);
            if (currentParticipant?.IsServerDeafened == true)
            {
                Console.WriteLine("Cannot undeafen: user is server-deafened");
                return;
            }
        }

        IsDeafened = !IsDeafened;

        // If deafening, also mute
        if (IsDeafened && !IsMuted)
        {
            IsMuted = true;
            _settingsStore.Settings.IsMuted = IsMuted;
        }

        // Persist to settings
        _settingsStore.Settings.IsDeafened = IsDeafened;
        _settingsStore.Save();

        // If in a voice channel, apply immediately
        if (CurrentVoiceChannel is not null)
        {
            _webRtc.SetMuted(IsMuted);
            _webRtc.SetDeafened(IsDeafened);
            await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened));

            // Update our own state in the local view models
            var state = new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened);
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            voiceChannel?.UpdateParticipantState(_auth.UserId, state);
            _voiceChannelContent?.UpdateParticipantState(_auth.UserId, state);
        }
    }

    private async Task ToggleCameraAsync()
    {
        if (CurrentVoiceChannel is null) return;

        try
        {
            var newState = !IsCameraOn;
            await _webRtc.SetCameraAsync(newState);
            IsCameraOn = newState;
            await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsCameraOn: IsCameraOn));

            // Update our own state in the local view models (we don't receive our own VoiceStateChanged event)
            var state = new VoiceStateUpdate(IsCameraOn: newState);
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            voiceChannel?.UpdateParticipantState(_auth.UserId, state);
            _voiceChannelContent?.UpdateParticipantState(_auth.UserId, state);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to toggle camera: {ex.Message}");
        }
    }

    private async Task ToggleScreenShareAsync()
    {
        if (CurrentVoiceChannel is null) return;

        // If already sharing, stop sharing
        if (IsScreenSharing)
        {
            await StopScreenShareAsync();
            return;
        }

        // Show screen share picker
        ScreenSharePicker = new ScreenSharePickerViewModel(_screenCaptureService, async settings =>
        {
            IsScreenSharePickerOpen = false;
            ScreenSharePicker = null;

            if (settings != null)
            {
                await StartScreenShareWithSettingsAsync(settings);
            }
        });
        IsScreenSharePickerOpen = true;
    }

    private async Task StartScreenShareWithSettingsAsync(ScreenShareSettings settings)
    {
        if (CurrentVoiceChannel is null) return;

        try
        {
            // Update local state BEFORE starting capture so the video stream exists
            // when HardwarePreviewReady fires (otherwise the hardware decoder is dropped)
            IsScreenSharing = true;
            IsCameraOn = false;
            _currentScreenShareSettings = settings;
            var state = new VoiceStateUpdate(IsScreenSharing: true, ScreenShareHasAudio: settings.IncludeAudio, IsCameraOn: false);
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            voiceChannel?.UpdateParticipantState(_auth.UserId, state);
            _voiceChannelContent?.UpdateParticipantState(_auth.UserId, state);

            // Now start the capture (hardware decoder will find the video stream)
            await _webRtc.SetScreenSharingAsync(true, settings);

            // Notify server
            await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, state);

            // Show annotation overlay for monitor (display) sharing only
            if (settings.Source.Type == ScreenCaptureSourceType.Display)
            {
                ShowSharerAnnotationOverlay(settings);
            }

            Console.WriteLine($"Started screen share: {settings.Source.Name} @ {settings.Resolution.Label} {settings.Framerate.Label}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start screen share: {ex.Message}");
        }
    }

    private async Task StopScreenShareAsync()
    {
        if (CurrentVoiceChannel is null) return;

        try
        {
            // Close annotation overlay windows first
            HideSharerAnnotationOverlay();

            await _webRtc.SetScreenSharingAsync(false);
            IsScreenSharing = false;
            _currentScreenShareSettings = null;

            await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsScreenSharing: false, ScreenShareHasAudio: false));

            // Update our own state in the local view models
            var state = new VoiceStateUpdate(IsScreenSharing: false, ScreenShareHasAudio: false);
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            voiceChannel?.UpdateParticipantState(_auth.UserId, state);
            _voiceChannelContent?.UpdateParticipantState(_auth.UserId, state);

            // Clear annotations for this screen share
            _annotationService.OnScreenShareEnded(_auth.UserId);

            Console.WriteLine("Stopped screen share");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop screen share: {ex.Message}");
        }
    }

    /// <summary>
    /// Server mute/unmute a user (admin action).
    /// </summary>
    private async Task ServerMuteUserAsync(VoiceParticipantViewModel participant)
    {
        if (CurrentVoiceChannel is null || !CanManageVoice) return;
        if (participant.UserId == _auth.UserId) return; // Cannot server mute yourself

        try
        {
            var newServerMuted = !participant.IsServerMuted;
            await _signalR.ServerMuteUserAsync(CurrentVoiceChannel.Id, participant.UserId, newServerMuted);
            Console.WriteLine($"Server {(newServerMuted ? "muted" : "unmuted")} user {participant.Username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to server mute user: {ex.Message}");
        }
    }

    /// <summary>
    /// Server deafen/undeafen a user (admin action).
    /// </summary>
    private async Task ServerDeafenUserAsync(VoiceParticipantViewModel participant)
    {
        if (CurrentVoiceChannel is null || !CanManageVoice) return;
        if (participant.UserId == _auth.UserId) return; // Cannot server deafen yourself

        try
        {
            var newServerDeafened = !participant.IsServerDeafened;
            await _signalR.ServerDeafenUserAsync(CurrentVoiceChannel.Id, participant.UserId, newServerDeafened);
            Console.WriteLine($"Server {(newServerDeafened ? "deafened" : "undeafened")} user {participant.Username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to server deafen user: {ex.Message}");
        }
    }

    /// <summary>
    /// Move a user to a different voice channel (admin action).
    /// </summary>
    private async Task MoveUserToChannelAsync((VoiceParticipantViewModel Participant, VoiceChannelViewModel TargetChannel) args)
    {
        if (!CanManageVoice) return;
        if (args.Participant.UserId == _auth.UserId) return; // Cannot move yourself via this action

        try
        {
            await _signalR.MoveUserAsync(args.Participant.UserId, args.TargetChannel.Id);
            Console.WriteLine($"Moved user {args.Participant.Username} to channel {args.TargetChannel.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to move user: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows the annotation overlay and toolbar on the shared monitor.
    /// Only called for display (monitor) sharing, not window sharing.
    /// </summary>
    private void ShowSharerAnnotationOverlay(ScreenShareSettings settings)
    {
        if (CurrentVoiceChannel is null) return;

        Console.WriteLine("ShowSharerAnnotationOverlay: Starting...");
        var logFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snacka-overlay-debug.log");
        void Log(string msg) { System.IO.File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); Console.WriteLine(msg); }
        Log("ShowSharerAnnotationOverlay: Starting...");

        try
        {
            // Step 1: Create the view model
            Log("ShowSharerAnnotationOverlay: Step 1 - Creating ViewModel...");
            _screenAnnotationViewModel = new ScreenAnnotationViewModel(
                _annotationService,
                CurrentVoiceChannel.Id,
                _auth.UserId,
                _auth.Username);
            this.RaisePropertyChanged(nameof(IsDrawingAllowedForViewers));
            Log("ShowSharerAnnotationOverlay: Step 1 - ViewModel created");

            // Step 2: Find the screen
            Log("ShowSharerAnnotationOverlay: Step 2 - Finding screen...");
            Avalonia.PixelRect? targetBounds = null;

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var screensService = desktop.MainWindow?.Screens;
                if (screensService != null && int.TryParse(settings.Source.Id, out var displayIndex))
                {
                    var allScreens = screensService.All.ToList();
                    Log($"ShowSharerAnnotationOverlay: Found {allScreens.Count} screens, target index: {displayIndex}");
                    if (displayIndex < allScreens.Count)
                    {
                        targetBounds = allScreens[displayIndex].Bounds;
                        Log($"ShowSharerAnnotationOverlay: Target screen bounds: {targetBounds}");
                    }
                }
            }

            // Step 3: Create overlay window
            Log("ShowSharerAnnotationOverlay: Step 3 - Creating overlay window...");
            _screenAnnotationWindow = new Views.ScreenAnnotationWindow();
            Log("ShowSharerAnnotationOverlay: Step 3a - Window created, setting DataContext...");
            _screenAnnotationWindow.DataContext = _screenAnnotationViewModel;
            Log("ShowSharerAnnotationOverlay: Step 3b - DataContext set");

            // Step 4: Position the window
            Log("ShowSharerAnnotationOverlay: Step 4 - Positioning window...");
            if (targetBounds.HasValue)
            {
                _screenAnnotationWindow.Position = new Avalonia.PixelPoint(
                    targetBounds.Value.X,
                    targetBounds.Value.Y);
                _screenAnnotationWindow.Width = targetBounds.Value.Width;
                _screenAnnotationWindow.Height = targetBounds.Value.Height;
            }
            else
            {
                _screenAnnotationWindow.WindowState = Avalonia.Controls.WindowState.Maximized;
            }
            Log("ShowSharerAnnotationOverlay: Step 4 - Window positioned");

            // Step 5: Show the overlay window
            // Window is shown on all platforms - input pass-through handles click behavior
            Log("ShowSharerAnnotationOverlay: Step 5 - Showing overlay window...");
            _screenAnnotationWindow.Show();
            Log("ShowSharerAnnotationOverlay: Step 5 - Overlay window shown");

            // Step 6: Create toolbar window
            Log("ShowSharerAnnotationOverlay: Step 6 - Creating toolbar window...");
            _annotationToolbarWindow = new Views.AnnotationToolbarWindow();
            Log("ShowSharerAnnotationOverlay: Step 6a - Toolbar created, setting DataContext...");
            _annotationToolbarWindow.DataContext = _screenAnnotationViewModel;
            Log("ShowSharerAnnotationOverlay: Step 6b - Setting overlay reference...");
            _annotationToolbarWindow.SetOverlayWindow(_screenAnnotationWindow);
            Log("ShowSharerAnnotationOverlay: Step 6 - Toolbar window created");

            // Step 7: Position toolbar
            Log("ShowSharerAnnotationOverlay: Step 7 - Positioning toolbar...");
            if (targetBounds.HasValue)
            {
                var toolbarX = targetBounds.Value.X + (targetBounds.Value.Width - 380) / 2;
                var toolbarY = targetBounds.Value.Y + targetBounds.Value.Height - 80;
                _annotationToolbarWindow.Position = new Avalonia.PixelPoint(toolbarX, toolbarY);
            }
            else
            {
                _annotationToolbarWindow.Position = new Avalonia.PixelPoint(400, 700);
            }
            Log("ShowSharerAnnotationOverlay: Step 7 - Toolbar positioned");

            // Step 8: Setup toolbar event handler
            // Note: We don't call Show() here - the toolbar manages its own visibility
            // based on the IsDrawingAllowedForViewers subscription. It will show when
            // the host clicks "Allow Drawing" in the voice panel.
            Log("ShowSharerAnnotationOverlay: Step 8 - Setting up toolbar event handler...");
            _annotationToolbarWindow.CloseRequested += OnAnnotationToolbarCloseRequested;
            Log("ShowSharerAnnotationOverlay: Step 8 - Toolbar ready (visibility managed by subscription)");

            Log($"ShowSharerAnnotationOverlay: Complete - overlay on {settings.Source.Name}");
        }
        catch (Exception ex)
        {
            Log($"ShowSharerAnnotationOverlay: EXCEPTION: {ex.Message}");
            Log($"ShowSharerAnnotationOverlay: Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Hides and closes the annotation overlay and toolbar windows.
    /// </summary>
    private void HideSharerAnnotationOverlay()
    {
        if (_annotationToolbarWindow != null)
        {
            _annotationToolbarWindow.CloseRequested -= OnAnnotationToolbarCloseRequested;
            _annotationToolbarWindow.Close();
            _annotationToolbarWindow = null;
        }

        if (_screenAnnotationWindow != null)
        {
            _screenAnnotationWindow.Close();
            _screenAnnotationWindow = null;
        }

        if (_screenAnnotationViewModel != null)
        {
            _screenAnnotationViewModel.Cleanup();
            _screenAnnotationViewModel = null;
            this.RaisePropertyChanged(nameof(IsDrawingAllowedForViewers));
        }

        Console.WriteLine("Closed annotation overlay");
    }

    private void OnAnnotationToolbarCloseRequested()
    {
        // User closed the toolbar - stop screen sharing
        _ = StopScreenShareAsync();
    }

    // Unified autocomplete methods (for @ mentions and / commands)

    /// <summary>
    /// Closes the autocomplete popup.
    /// </summary>
    public void CloseAutocompletePopup()
    {
        _autocomplete.Close();
    }

    /// <summary>
    /// Selects a suggestion and returns the cursor position where the caret should be placed.
    /// Returns -1 if no suggestion was inserted (e.g., command was executed).
    /// </summary>
    public int SelectAutocompleteSuggestion(IAutocompleteSuggestion suggestion)
    {
        var result = _autocomplete.Select(suggestion, MessageInput);
        if (result.HasValue)
        {
            _isSelectingAutocomplete = true;
            try
            {
                MessageInput = result.Value.newText;
                return result.Value.cursorPosition;
            }
            finally
            {
                _isSelectingAutocomplete = false;
            }
        }
        return -1;
    }

    /// <summary>
    /// Selects a suggestion and returns both the new text and cursor position.
    /// The caller is responsible for updating the UI directly.
    /// </summary>
    public (string newText, int cursorPosition)? SelectAutocompleteSuggestionWithText(IAutocompleteSuggestion suggestion)
    {
        return _autocomplete.Select(suggestion, MessageInput);
    }

    /// <summary>
    /// Selects the currently highlighted suggestion and returns the cursor position.
    /// Returns -1 if no suggestion was selected.
    /// </summary>
    public int SelectCurrentAutocompleteSuggestion()
    {
        var result = _autocomplete.SelectCurrent(MessageInput);
        if (result.HasValue)
        {
            _isSelectingAutocomplete = true;
            try
            {
                MessageInput = result.Value.newText;
                return result.Value.cursorPosition;
            }
            finally
            {
                _isSelectingAutocomplete = false;
            }
        }
        return -1;
    }

    /// <summary>
    /// Selects the currently highlighted suggestion and returns both the new text and cursor position.
    /// The caller is responsible for updating the UI directly (setting TextBox.Text).
    /// The TwoWay binding will push the value back to MessageInput.
    /// </summary>
    public (string newText, int cursorPosition)? SelectCurrentAutocompleteSuggestionWithText()
    {
        return _autocomplete.SelectCurrent(MessageInput);
    }

    /// <summary>
    /// Navigates to the previous autocomplete suggestion.
    /// </summary>
    public void NavigateAutocompleteUp()
    {
        _autocomplete.NavigateUp();
    }

    /// <summary>
    /// Navigates to the next autocomplete suggestion.
    /// </summary>
    public void NavigateAutocompleteDown()
    {
        _autocomplete.NavigateDown();
    }

    // Reaction methods
    private async Task ToggleReactionAsync((MessageResponse Message, string Emoji) args)
    {
        if (SelectedChannel is null) return;

        var (message, emoji) = args;
        var hasReacted = message.Reactions?.FirstOrDefault(r => r.Emoji == emoji)?.HasReacted ?? false;

        if (hasReacted)
        {
            await _apiClient.RemoveReactionAsync(SelectedChannel.Id, message.Id, emoji);
        }
        else
        {
            await _apiClient.AddReactionAsync(SelectedChannel.Id, message.Id, emoji);
        }
    }

    private async Task AddReactionAsync((MessageResponse Message, string Emoji) args)
    {
        if (SelectedChannel is null) return;

        var (message, emoji) = args;
        await _apiClient.AddReactionAsync(SelectedChannel.Id, message.Id, emoji);
    }

    private async Task TogglePinAsync(MessageResponse message)
    {
        if (SelectedChannel is null) return;

        if (message.IsPinned)
        {
            await _apiClient.UnpinMessageAsync(SelectedChannel.Id, message.Id);
        }
        else
        {
            await _apiClient.PinMessageAsync(SelectedChannel.Id, message.Id);
        }
    }

    private async Task ShowPinnedMessagesAsync()
    {
        if (SelectedChannel is null) return;

        await LoadPinnedMessagesAsync();
        IsPinnedPopupOpen = true;
    }

    private async Task LoadPinnedMessagesAsync()
    {
        if (SelectedChannel is null) return;

        var result = await _apiClient.GetPinnedMessagesAsync(SelectedChannel.Id);
        if (result.Success && result.Data is not null)
        {
            PinnedMessages.Clear();
            foreach (var message in result.Data)
                PinnedMessages.Add(message);
        }
    }

    // Invite user methods
    private void OpenInviteUserPopup()
    {
        if (SelectedCommunity is null) return;

        // Reset state
        InviteSearchQuery = string.Empty;
        InviteSearchResults.Clear();
        InviteHasNoResults = false;
        InviteStatusMessage = null;
        IsInviteStatusError = false;

        IsInviteUserPopupOpen = true;
    }

    private void CloseInviteUserPopup()
    {
        IsInviteUserPopupOpen = false;
    }

    /// <summary>
    /// Searches for users that can be invited to the current community.
    /// </summary>
    public async Task SearchUsersToInviteAsync(string query)
    {
        if (SelectedCommunity is null || string.IsNullOrWhiteSpace(query)) return;

        IsSearchingUsersToInvite = true;
        InviteHasNoResults = false;
        InviteStatusMessage = null;

        try
        {
            var result = await _apiClient.SearchUsersToInviteAsync(SelectedCommunity.Id, query);
            if (result.Success && result.Data is not null)
            {
                InviteSearchResults.Clear();
                foreach (var user in result.Data)
                    InviteSearchResults.Add(user);

                InviteHasNoResults = InviteSearchResults.Count == 0;
            }
            else
            {
                InviteStatusMessage = result.Error ?? "Failed to search users";
                IsInviteStatusError = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error searching users to invite: {ex.Message}");
            InviteStatusMessage = "Failed to search users";
            IsInviteStatusError = true;
        }
        finally
        {
            IsSearchingUsersToInvite = false;
        }
    }

    private async Task InviteUserAsync(UserSearchResult user)
    {
        if (SelectedCommunity is null) return;

        InviteStatusMessage = null;

        try
        {
            var result = await _apiClient.CreateCommunityInviteAsync(SelectedCommunity.Id, user.Id);
            if (result.Success)
            {
                InviteStatusMessage = $"Invite sent to {user.EffectiveDisplayName}";
                IsInviteStatusError = false;

                // Remove the user from search results since they now have a pending invite
                var existingUser = InviteSearchResults.FirstOrDefault(u => u.Id == user.Id);
                if (existingUser != null)
                {
                    InviteSearchResults.Remove(existingUser);
                }
            }
            else
            {
                InviteStatusMessage = result.Error ?? "Failed to send invite";
                IsInviteStatusError = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inviting user: {ex.Message}");
            InviteStatusMessage = "Failed to send invite";
            IsInviteStatusError = true;
        }
    }

    // Recent DMs Methods
    private async Task LoadRecentDmsAsync()
    {
        try
        {
            var result = await _apiClient.GetConversationSummariesAsync();
            if (result.Success && result.Data is not null)
            {
                _recentDms.Clear();
                // Sort by last message time descending (most recent first)
                var sorted = result.Data
                    .OrderByDescending(c => c.LastMessage?.CreatedAt ?? DateTime.MinValue)
                    .Take(10); // Limit to 10 recent conversations in the sidebar

                foreach (var conv in sorted)
                    _recentDms.Add(conv);

                this.RaisePropertyChanged(nameof(TotalDmUnreadCount));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading recent DMs: {ex.Message}");
        }
    }

    private void UpdateRecentDmWithMessage(ConversationMessageResponse message)
    {
        var existing = _recentDms.FirstOrDefault(c => c.Id == message.ConversationId);

        if (existing is not null)
        {
            var index = _recentDms.IndexOf(existing);
            var updated = existing with
            {
                LastMessage = message,
                UnreadCount = message.SenderId != _auth.UserId
                    ? existing.UnreadCount + 1
                    : existing.UnreadCount
            };
            _recentDms.RemoveAt(index);
            _recentDms.Insert(0, updated); // Move to top
        }
        else
        {
            // New conversation - reload the list from server to get full info
            _ = LoadRecentDmsAsync();
        }

        this.RaisePropertyChanged(nameof(TotalDmUnreadCount));
    }

    private void SelectRecentDm(ConversationSummaryResponse conversation)
    {
        // Close voice channel view and clear channel selection
        SelectedVoiceChannelForViewing = null;
        SelectedChannel = null;

        // Open the DM content view for this conversation
        _dmContent?.OpenConversationById(conversation.Id, conversation.DisplayName);

        // Mark conversation as read in the list
        var index = _recentDms.IndexOf(conversation);
        if (index >= 0 && conversation.UnreadCount > 0)
        {
            _recentDms[index] = conversation with { UnreadCount = 0 };
            this.RaisePropertyChanged(nameof(TotalDmUnreadCount));
        }
    }

    // Controller access request methods
    private async Task AcceptControllerRequestAsync(ControllerAccessRequest request)
    {
        if (_currentVoiceChannel == null) return;

        try
        {
            // Use the selected slot or auto-assign
            var slot = _controllerHostService.GetNextAvailableSlot(_currentVoiceChannel.Id) ?? SelectedControllerSlot;
            await _controllerHostService.AcceptRequestAsync(request.ChannelId, request.RequesterUserId, slot);
            Console.WriteLine($"Accepted controller request from {request.RequesterUsername} as Player {slot + 1}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error accepting controller request: {ex.Message}");
        }
    }

    private async Task DeclineControllerRequestAsync(ControllerAccessRequest request)
    {
        try
        {
            await _controllerHostService.DeclineRequestAsync(request.ChannelId, request.RequesterUserId);
            Console.WriteLine($"Declined controller request from {request.RequesterUsername}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error declining controller request: {ex.Message}");
        }
    }

    private async Task StopControllerSessionAsync(ActiveControllerSession session)
    {
        try
        {
            await _controllerHostService.StopSessionAsync(session.ChannelId, session.GuestUserId);
            Console.WriteLine($"Stopped controller session with {session.GuestUsername}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping controller session: {ex.Message}");
        }
    }

    private void ToggleMuteControllerSession(ActiveControllerSession session)
    {
        _controllerHostService.ToggleMuteSession(session.GuestUserId);
        var isMuted = _controllerHostService.IsSessionMuted(session.GuestUserId);
        Console.WriteLine($"{(isMuted ? "Muted" : "Unmuted")} controller session with {session.GuestUsername}");
    }

    /// <summary>
    /// Check if a controller session is muted.
    /// </summary>
    public bool IsControllerSessionMuted(Guid guestUserId)
    {
        return _controllerHostService.IsSessionMuted(guestUserId);
    }

    // Thread methods
    private async Task OpenThreadAsync(MessageResponse parentMessage)
    {
        // Close any existing thread
        CloseThread();

        // Create new thread view model
        CurrentThread = new ThreadViewModel(_apiClient, parentMessage, CloseThread);
        await CurrentThread.LoadAsync();

        // Notify that IsThreadOpen changed
        this.RaisePropertyChanged(nameof(IsThreadOpen));
    }

    private void CloseThread()
    {
        if (CurrentThread != null)
        {
            CurrentThread.Dispose();
            CurrentThread = null;
            this.RaisePropertyChanged(nameof(IsThreadOpen));
        }
    }

    /// <summary>
    /// Updates thread metadata on a parent message when a new reply is added.
    /// Called from SignalR event handler.
    /// </summary>
    public void UpdateThreadMetadata(Guid parentMessageId, int replyCount, DateTime? lastReplyAt)
    {
        var message = Messages.FirstOrDefault(m => m.Id == parentMessageId);
        if (message != null)
        {
            // Create a new MessageResponse with updated thread metadata
            // Since records are immutable, we need to create a new instance
            var index = Messages.IndexOf(message);
            var updatedMessage = message with { ReplyCount = replyCount, LastReplyAt = lastReplyAt };
            Messages[index] = updatedMessage;
        }
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
    /// Call this on connect if gaming station mode is enabled.
    /// </summary>
    public async Task ReportGamingStationStatusAsync()
    {
        if (!_settingsStore.Settings.IsGamingStationEnabled)
        {
            Console.WriteLine("Gaming station mode not enabled, not reporting status");
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(_settingsStore.Settings.GamingStationDisplayName)
            ? null
            : _settingsStore.Settings.GamingStationDisplayName;

        Console.WriteLine($"Reporting gaming station status: enabled, machineId={_currentMachineId}, displayName={displayName ?? "(default)"}");

        try
        {
            await _signalR.SetGamingStationAvailableAsync(true, displayName, _currentMachineId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to report gaming station status: {ex.Message}");
        }
    }

    /// <summary>
    /// Commands a gaming station to join the current voice channel.
    /// </summary>
    public async Task CommandStationJoinCurrentChannelAsync(string machineId)
    {
        if (CurrentVoiceChannel is null)
        {
            Console.WriteLine("Cannot command station to join - not in a voice channel");
            return;
        }

        try
        {
            await _signalR.CommandStationJoinChannelAsync(machineId, CurrentVoiceChannel.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to command station to join channel: {ex.Message}");
        }
    }

    /// <summary>
    /// Commands a gaming station to leave its current voice channel.
    /// </summary>
    public async Task CommandStationLeaveChannelAsync(string machineId)
    {
        try
        {
            await _signalR.CommandStationLeaveChannelAsync(machineId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to command station to leave channel: {ex.Message}");
        }
    }

    public void Dispose()
    {
        HideSharerAnnotationOverlay();
        _typingCleanupTimer?.Dispose();
        _ = _signalR.DisposeAsync();
    }
}

/// <summary>
/// Represents a user who is currently typing.
/// </summary>
public record TypingUser(Guid UserId, string Username, DateTime LastTypingAt);

/// <summary>
/// Represents a gaming station owned by the current user.
/// </summary>
public record MyGamingStationInfo(
    string MachineId,
    string DisplayName,
    bool IsAvailable,
    bool IsInVoiceChannel,
    Guid? CurrentChannelId,
    bool IsScreenSharing,
    bool IsCurrentMachine  // True if this is the machine we're running on
);

/// <summary>
/// Represents a file pending upload with a message.
/// </summary>
public class PendingAttachment : IDisposable
{
    public required string FileName { get; init; }
    public required Stream Stream { get; init; }
    public required long Size { get; init; }
    public required string ContentType { get; init; }

    public string FormattedSize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / (1024.0 * 1024.0):F1} MB"
    };

    public void Dispose()
    {
        Stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
