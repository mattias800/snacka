using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Miscord.Client.Services;
using Miscord.Shared.Models;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

public class MainAppViewModel : ViewModelBase, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly IWebRtcService _webRtc;
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
    private string? _errorMessage;
    private ChannelResponse? _editingChannel;
    private string _editingChannelName = string.Empty;
    private MessageResponse? _editingMessage;
    private string _editingMessageContent = string.Empty;
    private Guid? _previousChannelId;

    // Voice channel state
    private ChannelResponse? _currentVoiceChannel;
    private bool _isMuted;
    private bool _isDeafened;
    private VoiceConnectionStatus _voiceConnectionStatus = VoiceConnectionStatus.Disconnected;
    private ObservableCollection<VoiceParticipantResponse> _voiceParticipants = new();

    // Voice channels with participant tracking (robust reactive approach)
    private ObservableCollection<VoiceChannelViewModel> _voiceChannelViewModels = new();

    // Permission state
    private UserRole? _currentUserRole;

    public MainAppViewModel(IApiClient apiClient, ISignalRService signalR, IWebRtcService webRtc, string baseUrl, AuthResponse auth, Action onLogout, Action? onSwitchServer = null, Action? onOpenDMs = null, Action<Guid?, string?>? onOpenDMsWithUser = null, Action? onOpenSettings = null)
    {
        _apiClient = apiClient;
        _signalR = signalR;
        _webRtc = webRtc;
        _baseUrl = baseUrl;
        _auth = auth;
        _onLogout = onLogout;
        _onSwitchServer = onSwitchServer;
        _onOpenDMs = onOpenDMs;
        _onOpenDMsWithUser = onOpenDMsWithUser;
        _onOpenSettings = onOpenSettings;

        // Set local user ID for WebRTC
        if (_webRtc is WebRtcService webRtcService)
        {
            webRtcService.SetLocalUserId(auth.UserId);
        }

        // Subscribe to WebRTC connection status changes
        _webRtc.ConnectionStatusChanged += status =>
        {
            Dispatcher.UIThread.Post(() => VoiceConnectionStatus = status);
        };

        Communities = new ObservableCollection<CommunityResponse>();
        Channels = new ObservableCollection<ChannelResponse>();
        Messages = new ObservableCollection<MessageResponse>();
        Members = new ObservableCollection<CommunityMemberResponse>();

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
        SelectChannelCommand = ReactiveCommand.Create<ChannelResponse>(channel => SelectedChannel = channel);

        // Channel creation canExecute - prevents rapid clicks
        var canCreateChannel = this.WhenAnyValue(
            x => x.SelectedCommunity,
            x => x.IsLoading,
            (community, isLoading) => community is not null && !isLoading);

        CreateChannelCommand = ReactiveCommand.CreateFromTask(CreateChannelAsync, canCreateChannel);
        StartEditChannelCommand = ReactiveCommand.Create<ChannelResponse>(StartEditChannel);
        SaveChannelNameCommand = ReactiveCommand.CreateFromTask(SaveChannelNameAsync);
        CancelEditChannelCommand = ReactiveCommand.Create(CancelEditChannel);

        // Message commands
        StartEditMessageCommand = ReactiveCommand.Create<MessageResponse>(StartEditMessage);
        SaveMessageEditCommand = ReactiveCommand.CreateFromTask(SaveMessageEditAsync);
        CancelEditMessageCommand = ReactiveCommand.Create(CancelEditMessage);
        DeleteMessageCommand = ReactiveCommand.CreateFromTask<MessageResponse>(DeleteMessageAsync);

        // Member commands
        StartDMCommand = ReactiveCommand.Create<CommunityMemberResponse>(StartDMWithMember);
        PromoteToAdminCommand = ReactiveCommand.CreateFromTask<CommunityMemberResponse>(PromoteToAdminAsync);
        DemoteToMemberCommand = ReactiveCommand.CreateFromTask<CommunityMemberResponse>(DemoteToMemberAsync);
        TransferOwnershipCommand = ReactiveCommand.CreateFromTask<CommunityMemberResponse>(TransferOwnershipAsync);

        // Voice commands
        CreateVoiceChannelCommand = ReactiveCommand.CreateFromTask(CreateVoiceChannelAsync, canCreateChannel);
        JoinVoiceChannelCommand = ReactiveCommand.CreateFromTask<ChannelResponse>(JoinVoiceChannelAsync);
        LeaveVoiceChannelCommand = ReactiveCommand.CreateFromTask(LeaveVoiceChannelAsync);
        ToggleMuteCommand = ReactiveCommand.CreateFromTask(ToggleMuteAsync);
        ToggleDeafenCommand = ReactiveCommand.CreateFromTask(ToggleDeafenAsync);

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
                    var vm = new VoiceChannelViewModel(channel);
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

        _signalR.MessageReceived += message => Dispatcher.UIThread.Post(() =>
        {
            if (SelectedChannel is not null && message.ChannelId == SelectedChannel.Id)
            {
                // Don't add if it's our own message (we already added it optimistically)
                if (!Messages.Any(m => m.Id == message.Id))
                    Messages.Add(message);
            }
        });

        _signalR.MessageEdited += message => Dispatcher.UIThread.Post(() =>
        {
            var index = Messages.ToList().FindIndex(m => m.Id == message.Id);
            if (index >= 0)
                Messages[index] = message;
        });

        _signalR.MessageDeleted += e => Dispatcher.UIThread.Post(() =>
        {
            var message = Messages.FirstOrDefault(m => m.Id == e.MessageId);
            if (message is not null)
                Messages.Remove(message);
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
                    Members.Remove(member);
            }
        });

        // Voice channel events - update VoiceChannelViewModels and current VoiceParticipants
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
            }
        });

        // Speaking state from other users
        _signalR.SpeakingStateChanged += e => Dispatcher.UIThread.Post(() =>
        {
            var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == e.ChannelId);
            voiceChannel?.UpdateSpeakingState(e.UserId, e.IsSpeaking);
        });

        // Local speaking detection - broadcast to others and update own state
        _webRtc.SpeakingChanged += isSpeaking => Dispatcher.UIThread.Post(async () =>
        {
            // Capture channel reference to avoid race condition during leave
            var currentChannel = CurrentVoiceChannel;
            if (currentChannel is not null)
            {
                // Broadcast to others
                await _signalR.UpdateSpeakingStateAsync(currentChannel.Id, isSpeaking);

                // Update our own speaking state in the ViewModel
                var voiceChannel = VoiceChannelViewModels.FirstOrDefault(v => v.Id == currentChannel.Id);
                voiceChannel?.UpdateSpeakingState(_auth.UserId, isSpeaking);
            }
        });
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

        if (SelectedChannel is not null)
        {
            _previousChannelId = SelectedChannel.Id;
            await _signalR.JoinChannelAsync(SelectedChannel.Id);
        }

        await LoadMessagesAsync();
    }

    public string Username => _auth.Username;
    public string Email => _auth.Email;
    public Guid UserId => _auth.UserId;

    public ObservableCollection<CommunityResponse> Communities { get; }
    public ObservableCollection<ChannelResponse> Channels { get; }
    public ObservableCollection<MessageResponse> Messages { get; }
    public ObservableCollection<CommunityMemberResponse> Members { get; }

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
        set => this.RaiseAndSetIfChanged(ref _messageInput, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

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

    // Voice channel properties
    public ChannelResponse? CurrentVoiceChannel
    {
        get => _currentVoiceChannel;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentVoiceChannel, value);
            this.RaisePropertyChanged(nameof(IsInVoiceChannel));
        }
    }

    public bool IsInVoiceChannel => CurrentVoiceChannel is not null;

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

    public VoiceConnectionStatus VoiceConnectionStatus
    {
        get => _voiceConnectionStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _voiceConnectionStatus, value);
            this.RaisePropertyChanged(nameof(VoiceConnectionStatusText));
        }
    }

    public string VoiceConnectionStatusText => VoiceConnectionStatus switch
    {
        VoiceConnectionStatus.Connected => "Voice Connected",
        VoiceConnectionStatus.Connecting => "Connecting...",
        _ => ""
    };

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
        }
    }

    public bool CanManageChannels => CurrentUserRole is UserRole.Owner or UserRole.Admin;
    public bool CanManageMembers => CurrentUserRole is UserRole.Owner;

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
    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<MessageResponse, Unit> StartEditMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveMessageEditCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelEditMessageCommand { get; }
    public ReactiveCommand<MessageResponse, Unit> DeleteMessageCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> StartDMCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> PromoteToAdminCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> DemoteToMemberCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> TransferOwnershipCommand { get; }

    // Voice commands
    public ReactiveCommand<Unit, Unit> CreateVoiceChannelCommand { get; }
    public ReactiveCommand<ChannelResponse, Unit> JoinVoiceChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> LeaveVoiceChannelCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDeafenCommand { get; }

    public bool CanSwitchServer => _onSwitchServer is not null;

    private void StartDMWithMember(CommunityMemberResponse member)
    {
        // Don't DM yourself
        if (member.UserId == _auth.UserId) return;

        _onOpenDMsWithUser?.Invoke(member.UserId, member.Username);
    }

    private async Task PromoteToAdminAsync(CommunityMemberResponse member)
    {
        if (SelectedCommunity is null || !CanManageMembers) return;

        // Can't change owner or self
        if (member.Role == UserRole.Owner || member.UserId == _auth.UserId) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateMemberRoleAsync(SelectedCommunity.Id, member.UserId, UserRole.Admin);
            if (result.Success && result.Data is not null)
            {
                // Update the member in the list
                var index = Members.ToList().FindIndex(m => m.UserId == member.UserId);
                if (index >= 0)
                    Members[index] = result.Data;
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

    private async Task DemoteToMemberAsync(CommunityMemberResponse member)
    {
        if (SelectedCommunity is null || !CanManageMembers) return;

        // Can't change owner or self
        if (member.Role == UserRole.Owner || member.UserId == _auth.UserId) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateMemberRoleAsync(SelectedCommunity.Id, member.UserId, UserRole.Member);
            if (result.Success && result.Data is not null)
            {
                // Update the member in the list
                var index = Members.ToList().FindIndex(m => m.UserId == member.UserId);
                if (index >= 0)
                    Members[index] = result.Data;
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

    private async Task TransferOwnershipAsync(CommunityMemberResponse member)
    {
        if (SelectedCommunity is null || !CanManageMembers) return;

        // Can't transfer to yourself or to the current owner
        if (member.UserId == _auth.UserId || member.Role == UserRole.Owner) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.TransferOwnershipAsync(SelectedCommunity.Id, member.UserId);
            if (result.Success)
            {
                // Reload members to get updated roles
                var membersResult = await _apiClient.GetMembersAsync(SelectedCommunity.Id);
                if (membersResult.Success && membersResult.Data is not null)
                {
                    Members.Clear();
                    foreach (var m in membersResult.Data)
                        Members.Add(m);

                    // Update current user's role
                    var currentMember = membersResult.Data.FirstOrDefault(m => m.UserId == _auth.UserId);
                    CurrentUserRole = currentMember?.Role;
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
                    var vm = new VoiceChannelViewModel(voiceChannel);

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

                // Set current user's role
                var currentMember = membersResult.Data.FirstOrDefault(m => m.UserId == _auth.UserId);
                CurrentUserRole = currentMember?.Role;
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

        IsLoading = true;
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
            IsLoading = false;
        }
    }

    private async Task SendMessageAsync()
    {
        if (SelectedChannel is null || string.IsNullOrWhiteSpace(MessageInput)) return;

        var content = MessageInput;
        MessageInput = string.Empty;

        var result = await _apiClient.SendMessageAsync(SelectedChannel.Id, content);
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

        IsLoading = true;
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
            IsLoading = false;
        }
    }

    private async Task DeleteMessageAsync(MessageResponse message)
    {
        if (SelectedChannel is null) return;

        IsLoading = true;
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
                    var vm = new VoiceChannelViewModel(result.Data);
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

        var participant = await _signalR.JoinVoiceChannelAsync(channel.Id);
        if (participant is not null)
        {
            CurrentVoiceChannel = channel;
            IsMuted = participant.IsMuted;
            IsDeafened = participant.IsDeafened;

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

            // Start WebRTC connections to all existing participants
            await _webRtc.JoinVoiceChannelAsync(channel.Id, participants);

            Console.WriteLine($"Joined voice channel: {channel.Name}");
        }
    }

    private async Task LeaveVoiceChannelAsync()
    {
        if (CurrentVoiceChannel is null) return;

        var channelId = CurrentVoiceChannel.Id;
        var channelName = CurrentVoiceChannel.Name;

        // Leave WebRTC connections first
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
        IsMuted = false;
        IsDeafened = false;

        Console.WriteLine("Left voice channel");
    }

    private async Task ToggleMuteAsync()
    {
        if (CurrentVoiceChannel is null) return;

        IsMuted = !IsMuted;
        _webRtc.SetMuted(IsMuted);
        await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsMuted: IsMuted));
    }

    private async Task ToggleDeafenAsync()
    {
        if (CurrentVoiceChannel is null) return;

        IsDeafened = !IsDeafened;
        // If deafening, also mute
        if (IsDeafened && !IsMuted)
        {
            IsMuted = true;
            _webRtc.SetMuted(IsMuted);
        }
        _webRtc.SetDeafened(IsDeafened);
        await _signalR.UpdateVoiceStateAsync(CurrentVoiceChannel.Id, new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened));
    }

    public void Dispose()
    {
        _ = _signalR.DisposeAsync();
    }
}
