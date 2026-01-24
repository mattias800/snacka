using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using Snacka.Client.Models;
using Snacka.Client.Services;
using Snacka.Shared.Models;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the members list component.
/// Handles member role management, nickname editing, and DM initiation.
/// </summary>
public class MembersListViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly Guid _currentUserId;
    private readonly Func<Guid> _getSelectedCommunityId;
    private readonly Action<string?> _onError;
    private readonly Action<CommunityMemberResponse> _onStartDM;
    private readonly Func<Guid, int> _getDmUnreadCount;
    private readonly Func<Task<bool>> _isInVoiceChannel;
    private readonly Func<string, Task> _onAddStationToChannel;

    // Shared members collection (store-backed, read-only)
    private readonly ReadOnlyObservableCollection<CommunityMemberResponse> _members;

    // Store mutation delegates
    private readonly Action<Guid, Guid, string?> _updateMemberNickname;
    private readonly Action<Guid, Guid, UserRole> _updateMemberRole;
    private readonly Action<Guid, IEnumerable<CommunityMemberResponse>> _setMembers;

    // Gaming stations collection (owned by MainAppViewModel)
    private readonly ObservableCollection<MyGamingStationInfo> _myGamingStations;

    private bool _isLoading;
    private UserRole? _currentUserRole;

    // Nickname editing state
    private bool _isEditingNickname;
    private CommunityMemberResponse? _editingNicknameMember;
    private string _editingNickname = string.Empty;

    public MembersListViewModel(
        IApiClient apiClient,
        Guid currentUserId,
        ReadOnlyObservableCollection<CommunityMemberResponse> members,
        ObservableCollection<MyGamingStationInfo> myGamingStations,
        Func<Guid> getSelectedCommunityId,
        Action<CommunityMemberResponse> onStartDM,
        Func<Guid, int> getDmUnreadCount,
        Func<Task<bool>> isInVoiceChannel,
        Func<string, Task> onAddStationToChannel,
        Action<Guid, Guid, string?> updateMemberNickname,
        Action<Guid, Guid, UserRole> updateMemberRole,
        Action<Guid, IEnumerable<CommunityMemberResponse>> setMembers,
        Action<string?> onError)
    {
        _apiClient = apiClient;
        _currentUserId = currentUserId;
        _members = members;
        _myGamingStations = myGamingStations;
        _getSelectedCommunityId = getSelectedCommunityId;
        _onStartDM = onStartDM;
        _getDmUnreadCount = getDmUnreadCount;
        _isInVoiceChannel = isInVoiceChannel;
        _onAddStationToChannel = onAddStationToChannel;
        _updateMemberNickname = updateMemberNickname;
        _updateMemberRole = updateMemberRole;
        _setMembers = setMembers;
        _onError = onError;

        // Nickname commands
        ChangeMyNicknameCommand = ReactiveCommand.Create(StartEditMyNickname);
        ChangeMemberNicknameCommand = ReactiveCommand.Create<CommunityMemberResponse>(StartEditMemberNickname);
        SaveNicknameCommand = ReactiveCommand.CreateFromTask(SaveNicknameAsync);
        CancelNicknameEditCommand = ReactiveCommand.Create(CancelNicknameEdit);

        // Role management commands
        PromoteToAdminCommand = ReactiveCommand.CreateFromTask<CommunityMemberResponse>(PromoteToAdminAsync);
        DemoteToMemberCommand = ReactiveCommand.CreateFromTask<CommunityMemberResponse>(DemoteToMemberAsync);
        TransferOwnershipCommand = ReactiveCommand.CreateFromTask<CommunityMemberResponse>(TransferOwnershipAsync);

        // Start DM command - delegates to parent
        StartDMCommand = ReactiveCommand.Create<CommunityMemberResponse>(member => _onStartDM(member));

        // Gaming station command
        AddStationToChannelCommand = ReactiveCommand.CreateFromTask<MyGamingStationInfo>(AddStationToChannelAsync);

        // Subscribe to collection changes to notify SortedMembers (auto-update from store)
        ((INotifyCollectionChanged)_members).CollectionChanged += OnMembersCollectionChanged;
    }

    private void OnMembersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(SortedMembers));
    }

    // Properties
    public Guid CurrentUserId => _currentUserId;

    public IEnumerable<CommunityMemberResponse> SortedMembers =>
        _members.OrderByDescending(m => m.UserId == _currentUserId).ThenBy(m => m.Username);

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public UserRole? CurrentUserRole
    {
        get => _currentUserRole;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentUserRole, value);
            this.RaisePropertyChanged(nameof(CanManageMembers));
        }
    }

    public bool CanManageMembers => CurrentUserRole is UserRole.Owner;

    // Nickname editing properties
    public bool IsEditingNickname
    {
        get => _isEditingNickname;
        set => this.RaiseAndSetIfChanged(ref _isEditingNickname, value);
    }

    public CommunityMemberResponse? EditingNicknameMember
    {
        get => _editingNicknameMember;
        set => this.RaiseAndSetIfChanged(ref _editingNicknameMember, value);
    }

    public string EditingNickname
    {
        get => _editingNickname;
        set => this.RaiseAndSetIfChanged(ref _editingNickname, value);
    }

    public bool IsEditingMyNickname => IsEditingNickname && EditingNicknameMember?.UserId == _currentUserId;

    // Gaming stations
    public ObservableCollection<MyGamingStationInfo> MyGamingStations => _myGamingStations;
    public bool HasGamingStations => _myGamingStations.Count > 0;

    // Commands
    public ReactiveCommand<Unit, Unit> ChangeMyNicknameCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> ChangeMemberNicknameCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveNicknameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelNicknameEditCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> PromoteToAdminCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> DemoteToMemberCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> TransferOwnershipCommand { get; }
    public ReactiveCommand<CommunityMemberResponse, Unit> StartDMCommand { get; }
    public ReactiveCommand<MyGamingStationInfo, Unit> AddStationToChannelCommand { get; }

    /// <summary>
    /// Updates the current user's role. Called when community selection changes.
    /// </summary>
    public void UpdateCurrentUserRole(UserRole? role)
    {
        CurrentUserRole = role;
    }

    /// <summary>
    /// Notifies the view that the members collection has changed.
    /// </summary>
    public void NotifyMembersChanged()
    {
        this.RaisePropertyChanged(nameof(SortedMembers));
    }

    /// <summary>
    /// Gets the unread DM count for a specific user.
    /// </summary>
    public int GetDmUnreadCount(Guid userId) => _getDmUnreadCount(userId);

    // Nickname methods
    private void StartEditMyNickname()
    {
        var myMember = _members.FirstOrDefault(m => m.UserId == _currentUserId);
        if (myMember is null) return;

        EditingNicknameMember = myMember;
        EditingNickname = myMember.DisplayNameOverride ?? string.Empty;
        IsEditingNickname = true;
    }

    private void StartEditMemberNickname(CommunityMemberResponse member)
    {
        if (!CanManageMembers) return;

        EditingNicknameMember = member;
        EditingNickname = member.DisplayNameOverride ?? string.Empty;
        IsEditingNickname = true;
    }

    private void CancelNicknameEdit()
    {
        IsEditingNickname = false;
        EditingNicknameMember = null;
        EditingNickname = string.Empty;
    }

    private async Task SaveNicknameAsync()
    {
        var communityId = _getSelectedCommunityId();
        if (communityId == Guid.Empty || EditingNicknameMember is null) return;

        IsLoading = true;
        try
        {
            var nickname = string.IsNullOrWhiteSpace(EditingNickname) ? null : EditingNickname.Trim();

            ApiResult<CommunityMemberResponse> result;
            if (EditingNicknameMember.UserId == _currentUserId)
            {
                result = await _apiClient.UpdateMyNicknameAsync(communityId, nickname);
            }
            else
            {
                result = await _apiClient.UpdateMemberNicknameAsync(communityId, EditingNicknameMember.UserId, nickname);
            }

            if (result.Success && result.Data is not null)
            {
                // Update store - collection will auto-update via DynamicData binding
                _updateMemberNickname(communityId, EditingNicknameMember.UserId, nickname);
                CancelNicknameEdit();
            }
            else
            {
                _onError(result.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Role management methods
    private async Task PromoteToAdminAsync(CommunityMemberResponse member)
    {
        var communityId = _getSelectedCommunityId();
        if (communityId == Guid.Empty || !CanManageMembers) return;

        // Can't change owner or self
        if (member.Role == UserRole.Owner || member.UserId == _currentUserId) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateMemberRoleAsync(communityId, member.UserId, UserRole.Admin);
            if (result.Success && result.Data is not null)
            {
                // Update store - collection will auto-update via DynamicData binding
                _updateMemberRole(communityId, member.UserId, UserRole.Admin);
            }
            else
            {
                _onError(result.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DemoteToMemberAsync(CommunityMemberResponse member)
    {
        var communityId = _getSelectedCommunityId();
        if (communityId == Guid.Empty || !CanManageMembers) return;

        // Can't change owner or self
        if (member.Role == UserRole.Owner || member.UserId == _currentUserId) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.UpdateMemberRoleAsync(communityId, member.UserId, UserRole.Member);
            if (result.Success && result.Data is not null)
            {
                // Update store - collection will auto-update via DynamicData binding
                _updateMemberRole(communityId, member.UserId, UserRole.Member);
            }
            else
            {
                _onError(result.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task TransferOwnershipAsync(CommunityMemberResponse member)
    {
        var communityId = _getSelectedCommunityId();
        if (communityId == Guid.Empty || !CanManageMembers) return;

        // Can't transfer to owner or self
        if (member.Role == UserRole.Owner || member.UserId == _currentUserId) return;

        IsLoading = true;
        try
        {
            var result = await _apiClient.TransferOwnershipAsync(communityId, member.UserId);
            if (result.Success)
            {
                // Reload members to get updated roles
                var membersResult = await _apiClient.GetMembersAsync(communityId);
                if (membersResult.Success && membersResult.Data is not null)
                {
                    // Update store - collection will auto-update via DynamicData binding
                    _setMembers(communityId, membersResult.Data);

                    // Update current user's role
                    var currentMember = _members.FirstOrDefault(m => m.UserId == _currentUserId);
                    CurrentUserRole = currentMember?.Role;
                }
            }
            else
            {
                _onError(result.Error);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Gaming station methods
    private async Task AddStationToChannelAsync(MyGamingStationInfo station)
    {
        // Check if we're in a voice channel
        if (!await _isInVoiceChannel())
        {
            _onError("You must be in a voice channel to add a gaming station");
            return;
        }

        // Can't add station that's the current machine
        if (station.IsCurrentMachine)
        {
            _onError("You cannot add your current device as a gaming station");
            return;
        }

        // Can't add station that's already in a voice channel
        if (station.IsInVoiceChannel)
        {
            _onError("This gaming station is already in a voice channel");
            return;
        }

        await _onAddStationToChannel(station.MachineId);
    }

    /// <summary>
    /// Notifies the view that the gaming stations collection has changed.
    /// </summary>
    public void NotifyGamingStationsChanged()
    {
        this.RaisePropertyChanged(nameof(MyGamingStations));
        this.RaisePropertyChanged(nameof(HasGamingStations));
    }
}
