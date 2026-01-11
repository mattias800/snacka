using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Snacka.Client.Services;
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
    private readonly AuthResponse _auth;
    private readonly Action _onLogout;
    private readonly Action? _onSwitchServer;
    private readonly Action? _onOpenDMs;
    private readonly Action<Guid?, string?>? _onOpenDMsWithUser;
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

    // Drag preview state
    private List<VoiceChannelViewModel>? _originalVoiceChannelOrder;
    private Guid? _currentPreviewDraggedId;

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

    // Video fullscreen state
    private bool _isVideoFullscreen;
    private VideoStreamViewModel? _fullscreenStream;
    private bool _isGpuFullscreenActive;
    private IHardwareVideoDecoder? _fullscreenHardwareDecoder;

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

    // Mention autocomplete state
    private bool _isMentionPopupOpen;

    // File attachment state
    private ObservableCollection<PendingAttachment> _pendingAttachments = new();
    private AttachmentResponse? _lightboxImage;
    private string _mentionFilterText = string.Empty;
    private int _mentionStartIndex = -1;
    private int _selectedMentionIndex;
    private ObservableCollection<CommunityMemberResponse> _mentionSuggestions = new();

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

    // Pending invites popup state
    private bool _isPendingInvitesPopupOpen;
    private bool _isLoadingPendingInvites;
    private ObservableCollection<CommunityInviteResponse> _pendingInvites = new();

    // Thread state
    private ThreadViewModel? _currentThread;
    private double _threadPanelWidth = 400;

    // Members list ViewModel (encapsulates member operations and nickname editing)
    private MembersListViewModel? _membersListViewModel;

    // Server feature flags
    private readonly bool _isGifsEnabled;

    // Connection state
    private ConnectionState _connectionState = ConnectionState.Connected;

    public MainAppViewModel(IApiClient apiClient, ISignalRService signalR, IWebRtcService webRtc, IScreenCaptureService screenCaptureService, ISettingsStore settingsStore, IAudioDeviceService audioDeviceService, string baseUrl, AuthResponse auth, Action onLogout, Action? onSwitchServer = null, Action? onOpenDMs = null, Action<Guid?, string?>? onOpenDMsWithUser = null, Action? onOpenSettings = null, bool gifsEnabled = false)
    {
        _apiClient = apiClient;
        _isGifsEnabled = gifsEnabled;
        _screenCaptureService = screenCaptureService;
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _signalR = signalR;
        _webRtc = webRtc;
        _baseUrl = baseUrl;
        _auth = auth;
        _onLogout = onLogout;
        _onSwitchServer = onSwitchServer;
        _onOpenDMs = onOpenDMs;
        _onOpenDMsWithUser = onOpenDMsWithUser;
        _onOpenSettings = onOpenSettings;

        // Load persisted mute/deafen state
        _isMuted = _settingsStore.Settings.IsMuted;
        _isDeafened = _settingsStore.Settings.IsDeafened;

        // Set local user ID for WebRTC
        if (_webRtc is WebRtcService webRtcService)
        {
            webRtcService.SetLocalUserId(auth.UserId);
        }

        // Create voice channel content view model for video grid
        _voiceChannelContent = new VoiceChannelContentViewModel(_webRtc, _signalR, auth.UserId);

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
        ConnectionState = _signalR.State;

        Communities = new ObservableCollection<CommunityResponse>();
        Channels = new ObservableCollection<ChannelResponse>();
        Messages = new ObservableCollection<MessageResponse>();
        Members = new ObservableCollection<CommunityMemberResponse>();

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
            () => SelectedCommunity?.Id ?? Guid.Empty,
            member => StartDMWithMember(member),
            error => ErrorMessage = error);

        // Commands
        LogoutCommand = ReactiveCommand.Create(_onLogout);
        SwitchServerCommand = _onSwitchServer is not null
            ? ReactiveCommand.Create(_onSwitchServer)
            : null;
        OpenDMsCommand = _onOpenDMs is not null
            ? ReactiveCommand.Create(_onOpenDMs)
            : null;
        OpenSettingsCommand = _onOpenSettings is not null
            ? ReactiveCommand.Create(_onOpenSettings)
            : null;
        CreateCommunityCommand = ReactiveCommand.CreateFromTask(CreateCommunityAsync);
        RefreshCommunitiesCommand = ReactiveCommand.CreateFromTask(LoadCommunitiesAsync);
        SelectCommunityCommand = ReactiveCommand.Create<CommunityResponse>(community => SelectedCommunity = community);
        SelectChannelCommand = ReactiveCommand.Create<ChannelResponse>(channel =>
        {
            // Close the DM view when selecting a text channel
            _dmContent?.Close();
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

        // Invite user commands
        OpenInviteUserPopupCommand = ReactiveCommand.Create(OpenInviteUserPopup);
        CloseInviteUserPopupCommand = ReactiveCommand.Create(CloseInviteUserPopup);
        InviteUserCommand = ReactiveCommand.CreateFromTask<UserSearchResult>(InviteUserAsync);

        // Pending invites commands
        OpenPendingInvitesPopupCommand = ReactiveCommand.CreateFromTask(OpenPendingInvitesPopupAsync);
        ClosePendingInvitesPopupCommand = ReactiveCommand.Create(ClosePendingInvitesPopup);
        AcceptInviteCommand = ReactiveCommand.CreateFromTask<CommunityInviteResponse>(AcceptInviteAsync);
        DeclineInviteCommand = ReactiveCommand.CreateFromTask<CommunityInviteResponse>(DeclineInviteAsync);

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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SignalR connection failed: {ex.Message}");
        }

        await LoadCommunitiesAsync();

        // Load pending invites for the badge count
        await LoadPendingInvitesAsync();
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

        _signalR.CommunityMemberAdded += e => Dispatcher.UIThread.Post(async () =>
        {
            // If this is for the currently selected community, reload members
            if (SelectedCommunity is not null && e.CommunityId == SelectedCommunity.Id)
            {
                var result = await _apiClient.GetMembersAsync(SelectedCommunity.Id);
                if (result.Success && result.Data is not null)
                {
                    Members.Clear();
                    foreach (var member in result.Data)
                        Members.Add(member);
                    this.RaisePropertyChanged(nameof(SortedMembers));
                }
            }
        });

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

        // Community invite events
        _signalR.CommunityInviteReceived += e => Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine($"SignalR: Received invite to {e.CommunityName} from {e.InvitedByUsername}");
            // Reload pending invites to include the new one
            await LoadPendingInvitesAsync();
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
    /// The ViewModel for the inline DM content area.
    /// </summary>
    public DMContentViewModel? DMContent => _dmContent;

    /// <summary>
    /// The ViewModel for the members list component.
    /// </summary>
    public MembersListViewModel? MembersList => _membersListViewModel;

    /// <summary>
    /// Returns members sorted with the current user first.
    /// </summary>
    public IEnumerable<CommunityMemberResponse> SortedMembers =>
        Members.OrderByDescending(m => m.UserId == _auth.UserId).ThenBy(m => m.Username);

    public CommunityResponse? SelectedCommunity
    {
        get => _selectedCommunity;
        set => this.RaiseAndSetIfChanged(ref _selectedCommunity, value);
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
            var oldValue = _messageInput;
            this.RaiseAndSetIfChanged(ref _messageInput, value);

            // Handle mention autocomplete
            HandleMentionAutocomplete(oldValue, value);

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

    // Mention autocomplete properties
    public bool IsMentionPopupOpen
    {
        get => _isMentionPopupOpen;
        set => this.RaiseAndSetIfChanged(ref _isMentionPopupOpen, value);
    }

    public ObservableCollection<CommunityMemberResponse> MentionSuggestions
    {
        get => _mentionSuggestions;
        set => this.RaiseAndSetIfChanged(ref _mentionSuggestions, value);
    }

    public int SelectedMentionIndex
    {
        get => _selectedMentionIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedMentionIndex, value);
    }

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

    // Pending invites popup properties
    public bool IsPendingInvitesPopupOpen
    {
        get => _isPendingInvitesPopupOpen;
        set => this.RaiseAndSetIfChanged(ref _isPendingInvitesPopupOpen, value);
    }

    public bool IsLoadingPendingInvites
    {
        get => _isLoadingPendingInvites;
        set => this.RaiseAndSetIfChanged(ref _isLoadingPendingInvites, value);
    }

    public ObservableCollection<CommunityInviteResponse> PendingInvites
    {
        get => _pendingInvites;
        set => this.RaiseAndSetIfChanged(ref _pendingInvites, value);
    }

    public int PendingInviteCount => _pendingInvites.Count;
    public bool HasPendingInvites => _pendingInvites.Count > 0;
    public bool HasNoPendingInvites => _pendingInvites.Count == 0 && !_isLoadingPendingInvites;

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

    public void ClearGifResults()
    {
        _gifResults.Clear();
        GifSearchQuery = string.Empty;
        _gifNextPos = null;
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
        }
    }

    public bool IsInVoiceChannel => CurrentVoiceChannel is not null;

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
    }

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
    public ReactiveCommand<Unit, Unit>? OpenDMsCommand { get; }
    public ReactiveCommand<Unit, Unit>? OpenSettingsCommand { get; }
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

    // Invite user commands
    public ReactiveCommand<Unit, Unit> OpenInviteUserPopupCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseInviteUserPopupCommand { get; }
    public ReactiveCommand<UserSearchResult, Unit> InviteUserCommand { get; }

    // Pending invites commands
    public ReactiveCommand<Unit, Unit> OpenPendingInvitesPopupCommand { get; }
    public ReactiveCommand<Unit, Unit> ClosePendingInvitesPopupCommand { get; }
    public ReactiveCommand<CommunityInviteResponse, Unit> AcceptInviteCommand { get; }
    public ReactiveCommand<CommunityInviteResponse, Unit> DeclineInviteCommand { get; }

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

    // Admin voice commands
    public ReactiveCommand<VoiceParticipantViewModel, Unit> ServerMuteUserCommand { get; }
    public ReactiveCommand<VoiceParticipantViewModel, Unit> ServerDeafenUserCommand { get; }
    public ReactiveCommand<(VoiceParticipantViewModel, VoiceChannelViewModel), Unit> MoveUserToChannelCommand { get; }

    public bool CanSwitchServer => _onSwitchServer is not null;

    private void StartDMWithMember(CommunityMemberResponse member)
    {
        // Clear voice channel viewing when opening DM
        SelectedVoiceChannelForViewing = null;

        // Delegate to DMContentViewModel
        _dmContent?.OpenConversation(member.UserId, member.Username);
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

                // Select first community if none selected
                if (SelectedCommunity is null && Communities.Count > 0)
                    SelectedCommunity = Communities[0];
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

        var content = MessageInput;
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

        try
        {
            var result = await _apiClient.ReorderChannelsAsync(SelectedCommunity.Id, channelIds);
            if (result.Success && result.Data is not null)
            {
                Console.WriteLine($"Successfully reordered channels");

                // Update local channels with new order
                Channels.Clear();
                foreach (var channel in result.Data)
                {
                    Channels.Add(channel);
                }

                // Update VoiceChannelViewModels positions
                foreach (var voiceVm in VoiceChannelViewModels)
                {
                    var updatedChannel = result.Data.FirstOrDefault(c => c.Id == voiceVm.Id);
                    if (updatedChannel is not null)
                    {
                        voiceVm.Position = updatedChannel.Position;
                    }
                }

                // Re-sort VoiceChannelViewModels by Position (ObservableCollection doesn't auto-sort)
                var sortedVoiceChannels = VoiceChannelViewModels.OrderBy(v => v.Position).ToList();
                VoiceChannelViewModels.Clear();
                foreach (var vm in sortedVoiceChannels)
                {
                    VoiceChannelViewModels.Add(vm);
                }

                // Clear preview state since we've committed the real order
                ClearPreviewState();

                this.RaisePropertyChanged(nameof(TextChannels));
                this.RaisePropertyChanged(nameof(VoiceChannelViewModels));
            }
            else
            {
                ErrorMessage = result.Error ?? "Failed to reorder channels";
                Console.WriteLine($"Error reordering channels: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error reordering channels: {ex.Message}";
            Console.WriteLine($"Exception reordering channels: {ex}");
        }
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

            // Update VoiceChannelContent for video grid display
            SelectedVoiceChannelForViewing = channel;
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

        // Clear voice channel content view
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
            await _webRtc.SetScreenSharingAsync(true, settings);
            IsScreenSharing = true;
            IsCameraOn = false;
            _currentScreenShareSettings = settings;

            await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsScreenSharing: true, ScreenShareHasAudio: settings.IncludeAudio, IsCameraOn: false));

            // Update our own state in the local view models
            var state = new VoiceStateUpdate(IsScreenSharing: true, ScreenShareHasAudio: settings.IncludeAudio, IsCameraOn: false);
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == CurrentVoiceChannel.Id);
            voiceChannel?.UpdateParticipantState(_auth.UserId, state);
            _voiceChannelContent?.UpdateParticipantState(_auth.UserId, state);

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

    // Mention autocomplete methods
    private void HandleMentionAutocomplete(string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(newValue))
        {
            CloseMentionPopup();
            return;
        }

        // Find the last @ symbol that's either at start or preceded by whitespace
        var lastAtIndex = -1;
        for (int i = newValue.Length - 1; i >= 0; i--)
        {
            if (newValue[i] == '@')
            {
                // Check if @ is at start or preceded by whitespace
                if (i == 0 || char.IsWhiteSpace(newValue[i - 1]))
                {
                    lastAtIndex = i;
                    break;
                }
            }
            // Stop searching if we hit whitespace without finding @
            else if (char.IsWhiteSpace(newValue[i]))
            {
                break;
            }
        }

        if (lastAtIndex >= 0)
        {
            // Extract the filter text after @
            var filterText = newValue.Substring(lastAtIndex + 1);

            // Check if there's a space in the filter (means mention is complete)
            if (filterText.Contains(' '))
            {
                CloseMentionPopup();
                return;
            }

            _mentionStartIndex = lastAtIndex;
            _mentionFilterText = filterText.ToLowerInvariant();

            // Filter members based on the filter text
            UpdateMentionSuggestions();
        }
        else
        {
            CloseMentionPopup();
        }
    }

    private void UpdateMentionSuggestions()
    {
        var filtered = Members
            .Where(m => m.UserId != _auth.UserId) // Exclude self
            .Where(m => string.IsNullOrEmpty(_mentionFilterText) ||
                        m.Username.ToLowerInvariant().Contains(_mentionFilterText))
            .Take(5) // Limit to 5 suggestions
            .ToList();

        MentionSuggestions.Clear();
        foreach (var member in filtered)
        {
            MentionSuggestions.Add(member);
        }

        IsMentionPopupOpen = MentionSuggestions.Count > 0;
        SelectedMentionIndex = 0;
    }

    public void CloseMentionPopup()
    {
        IsMentionPopupOpen = false;
        _mentionStartIndex = -1;
        _mentionFilterText = string.Empty;
        MentionSuggestions.Clear();
        SelectedMentionIndex = 0;
    }

    /// <summary>
    /// Selects a mention and returns the cursor position where the caret should be placed.
    /// Returns -1 if no mention was inserted.
    /// </summary>
    public int SelectMention(CommunityMemberResponse member)
    {
        if (_mentionStartIndex < 0 || string.IsNullOrEmpty(MessageInput))
        {
            CloseMentionPopup();
            return -1;
        }

        // Replace @filterText with @username
        var beforeMention = MessageInput.Substring(0, _mentionStartIndex);
        var afterMention = MessageInput.Substring(_mentionStartIndex + 1 + _mentionFilterText.Length);

        MessageInput = $"{beforeMention}@{member.Username} {afterMention}";

        // Calculate cursor position: before + @ + username + space
        var cursorPosition = beforeMention.Length + 1 + member.Username.Length + 1;

        CloseMentionPopup();
        return cursorPosition;
    }

    /// <summary>
    /// Selects the currently highlighted mention and returns the cursor position.
    /// Returns -1 if no mention was selected.
    /// </summary>
    public int SelectCurrentMention()
    {
        if (MentionSuggestions.Count > 0 && SelectedMentionIndex >= 0 && SelectedMentionIndex < MentionSuggestions.Count)
        {
            return SelectMention(MentionSuggestions[SelectedMentionIndex]);
        }
        return -1;
    }

    public void NavigateMentionUp()
    {
        if (MentionSuggestions.Count == 0) return;
        SelectedMentionIndex = (SelectedMentionIndex - 1 + MentionSuggestions.Count) % MentionSuggestions.Count;
    }

    public void NavigateMentionDown()
    {
        if (MentionSuggestions.Count == 0) return;
        SelectedMentionIndex = (SelectedMentionIndex + 1) % MentionSuggestions.Count;
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

    // Pending invites methods
    private async Task OpenPendingInvitesPopupAsync()
    {
        IsPendingInvitesPopupOpen = true;
        await LoadPendingInvitesAsync();
    }

    private void ClosePendingInvitesPopup()
    {
        IsPendingInvitesPopupOpen = false;
    }

    private async Task LoadPendingInvitesAsync()
    {
        IsLoadingPendingInvites = true;
        this.RaisePropertyChanged(nameof(HasNoPendingInvites));

        try
        {
            var result = await _apiClient.GetMyPendingInvitesAsync();
            if (result.Success && result.Data is not null)
            {
                PendingInvites.Clear();
                foreach (var invite in result.Data)
                    PendingInvites.Add(invite);

                this.RaisePropertyChanged(nameof(PendingInviteCount));
                this.RaisePropertyChanged(nameof(HasPendingInvites));
                this.RaisePropertyChanged(nameof(HasNoPendingInvites));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading pending invites: {ex.Message}");
        }
        finally
        {
            IsLoadingPendingInvites = false;
            this.RaisePropertyChanged(nameof(HasNoPendingInvites));
        }
    }

    private async Task AcceptInviteAsync(CommunityInviteResponse invite)
    {
        try
        {
            var result = await _apiClient.AcceptInviteAsync(invite.Id);
            if (result.Success)
            {
                // Remove from pending list
                PendingInvites.Remove(invite);
                this.RaisePropertyChanged(nameof(PendingInviteCount));
                this.RaisePropertyChanged(nameof(HasPendingInvites));
                this.RaisePropertyChanged(nameof(HasNoPendingInvites));

                // Reload communities to show the new one
                await LoadCommunitiesAsync();

                Console.WriteLine($"Accepted invite to {invite.CommunityName}");
            }
            else
            {
                Console.WriteLine($"Failed to accept invite: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error accepting invite: {ex.Message}");
        }
    }

    private async Task DeclineInviteAsync(CommunityInviteResponse invite)
    {
        try
        {
            var result = await _apiClient.DeclineInviteAsync(invite.Id);
            if (result.Success)
            {
                // Remove from pending list
                PendingInvites.Remove(invite);
                this.RaisePropertyChanged(nameof(PendingInviteCount));
                this.RaisePropertyChanged(nameof(HasPendingInvites));
                this.RaisePropertyChanged(nameof(HasNoPendingInvites));

                Console.WriteLine($"Declined invite to {invite.CommunityName}");
            }
            else
            {
                Console.WriteLine($"Failed to decline invite: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error declining invite: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles receiving a new community invite via SignalR.
    /// </summary>
    public void HandleCommunityInviteReceived(CommunityInviteReceivedEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            // Reload pending invites to include the new one
            await LoadPendingInvitesAsync();
        });
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
