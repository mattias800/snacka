using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using Snacka.Client.Services;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

public class AdminPanelViewModel : ViewModelBase, IDisposable
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService? _signalRService;

    private bool _isLoadingInvites;
    private bool _isLoadingUsers;
    private bool _isCreatingInvite;
    private string? _invitesError;
    private string? _usersError;
    private string? _createInviteError;
    private int _newInviteMaxUses;
    private string _selectedTab = "Invites";

    // Server feature flags
    private readonly bool _gifsEnabled;

    public AdminPanelViewModel(IApiClient apiClient, ISignalRService? signalRService = null, bool gifsEnabled = false)
    {
        _apiClient = apiClient;
        _signalRService = signalRService;
        _gifsEnabled = gifsEnabled;

        Invites = new ObservableCollection<InviteViewModel>();
        Users = new ObservableCollection<UserViewModel>();

        var canCreate = this.WhenAnyValue(x => x.IsCreatingInvite, isCreating => !isCreating);
        CreateInviteCommand = ReactiveCommand.CreateFromTask(CreateInviteAsync, canCreate);
        RefreshInvitesCommand = ReactiveCommand.CreateFromTask(LoadInvitesAsync);
        RefreshUsersCommand = ReactiveCommand.CreateFromTask(LoadUsersAsync);
        SelectTabCommand = ReactiveCommand.Create<string>(tab => SelectedTab = tab);

        // Subscribe to real-time user registration events
        if (_signalRService != null)
        {
            _signalRService.UserRegistered += OnUserRegistered;
        }

        // Load data
        _ = LoadInvitesAsync();
        _ = LoadUsersAsync();
    }

    private void OnUserRegistered(AdminUserResponse newUser)
    {
        // Add new user to the list on the UI thread
        Dispatcher.UIThread.Post(() =>
        {
            // Check if user already exists (avoid duplicates)
            if (Users.All(u => u.Id != newUser.Id))
            {
                Users.Add(new UserViewModel(newUser, _apiClient, async () => await LoadUsersAsync()));
            }
        });
    }

    public void Dispose()
    {
        if (_signalRService != null)
        {
            _signalRService.UserRegistered -= OnUserRegistered;
        }
    }

    public ObservableCollection<InviteViewModel> Invites { get; }
    public ObservableCollection<UserViewModel> Users { get; }

    // Server feature flags
    public bool IsGifsEnabled => _gifsEnabled;
    public bool HasMissingConfiguration => !_gifsEnabled;

    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            this.RaisePropertyChanged(nameof(IsInvitesTabSelected));
            this.RaisePropertyChanged(nameof(IsUsersTabSelected));
            this.RaisePropertyChanged(nameof(IsConfigTabSelected));
        }
    }

    public bool IsInvitesTabSelected => SelectedTab == "Invites";
    public bool IsUsersTabSelected => SelectedTab == "Users";
    public bool IsConfigTabSelected => SelectedTab == "Config";

    public bool IsLoadingInvites
    {
        get => _isLoadingInvites;
        set => this.RaiseAndSetIfChanged(ref _isLoadingInvites, value);
    }

    public bool IsLoadingUsers
    {
        get => _isLoadingUsers;
        set => this.RaiseAndSetIfChanged(ref _isLoadingUsers, value);
    }

    public bool IsCreatingInvite
    {
        get => _isCreatingInvite;
        set => this.RaiseAndSetIfChanged(ref _isCreatingInvite, value);
    }

    public string? InvitesError
    {
        get => _invitesError;
        set => this.RaiseAndSetIfChanged(ref _invitesError, value);
    }

    public string? UsersError
    {
        get => _usersError;
        set => this.RaiseAndSetIfChanged(ref _usersError, value);
    }

    public string? CreateInviteError
    {
        get => _createInviteError;
        set => this.RaiseAndSetIfChanged(ref _createInviteError, value);
    }

    public int NewInviteMaxUses
    {
        get => _newInviteMaxUses;
        set => this.RaiseAndSetIfChanged(ref _newInviteMaxUses, value);
    }

    public ReactiveCommand<Unit, Unit> CreateInviteCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshInvitesCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshUsersCommand { get; }
    public ReactiveCommand<string, Unit> SelectTabCommand { get; }

    private async Task LoadInvitesAsync()
    {
        IsLoadingInvites = true;
        InvitesError = null;

        try
        {
            var result = await _apiClient.GetInvitesAsync();
            if (result.Success && result.Data is not null)
            {
                Invites.Clear();
                foreach (var invite in result.Data)
                {
                    Invites.Add(new InviteViewModel(invite, _apiClient, async () => await LoadInvitesAsync()));
                }
            }
            else
            {
                InvitesError = result.Error ?? "Failed to load invites";
            }
        }
        finally
        {
            IsLoadingInvites = false;
        }
    }

    private async Task LoadUsersAsync()
    {
        IsLoadingUsers = true;
        UsersError = null;

        try
        {
            var result = await _apiClient.GetAllUsersAsync();
            if (result.Success && result.Data is not null)
            {
                Users.Clear();
                foreach (var user in result.Data)
                {
                    Users.Add(new UserViewModel(user, _apiClient, async () => await LoadUsersAsync()));
                }
            }
            else
            {
                UsersError = result.Error ?? "Failed to load users";
            }
        }
        finally
        {
            IsLoadingUsers = false;
        }
    }

    private async Task CreateInviteAsync()
    {
        IsCreatingInvite = true;
        CreateInviteError = null;

        try
        {
            var result = await _apiClient.CreateInviteAsync(NewInviteMaxUses);
            if (result.Success)
            {
                NewInviteMaxUses = 0;
                await LoadInvitesAsync();
            }
            else
            {
                CreateInviteError = result.Error ?? "Failed to create invite";
            }
        }
        finally
        {
            IsCreatingInvite = false;
        }
    }
}

public class InviteViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly Func<Task> _onRefresh;
    private readonly ServerInviteResponse _invite;
    private bool _isRevoking;
    private string? _error;

    public InviteViewModel(ServerInviteResponse invite, IApiClient apiClient, Func<Task> onRefresh)
    {
        _invite = invite;
        _apiClient = apiClient;
        _onRefresh = onRefresh;

        var canRevoke = this.WhenAnyValue(x => x.IsRevoking, isRevoking => !isRevoking);
        RevokeCommand = ReactiveCommand.CreateFromTask(RevokeAsync, canRevoke);
        CopyCodeCommand = ReactiveCommand.Create(CopyCode);
        CopyInviteLinkCommand = ReactiveCommand.Create(CopyInviteLink);
    }

    public Guid Id => _invite.Id;
    public string Code => _invite.Code;
    public int MaxUses => _invite.MaxUses;
    public int CurrentUses => _invite.CurrentUses;
    public DateTime? ExpiresAt => _invite.ExpiresAt;
    public bool IsRevoked => _invite.IsRevoked;
    public string? CreatedByUsername => _invite.CreatedByUsername;
    public DateTime CreatedAt => _invite.CreatedAt;

    public string UsesDisplay => MaxUses == 0 ? $"{CurrentUses} / Unlimited" : $"{CurrentUses} / {MaxUses}";
    public string ExpiresDisplay => ExpiresAt?.ToString("g") ?? "Never";
    public string StatusDisplay => IsRevoked ? "Revoked" : "Active";

    public bool IsRevoking
    {
        get => _isRevoking;
        set => this.RaiseAndSetIfChanged(ref _isRevoking, value);
    }

    public string? Error
    {
        get => _error;
        set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    public ReactiveCommand<Unit, Unit> RevokeCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCodeCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyInviteLinkCommand { get; }

    /// <summary>
    /// Gets the full invite link (server URL + invite code) for sharing.
    /// Format: https://server.com#invite=CODE
    /// </summary>
    public string InviteLink => $"{_apiClient.BaseUrl}#invite={Code}";

    private async Task RevokeAsync()
    {
        IsRevoking = true;
        Error = null;

        try
        {
            var result = await _apiClient.RevokeInviteAsync(Id);
            if (result.Success)
            {
                await _onRefresh();
            }
            else
            {
                Error = result.Error ?? "Failed to revoke invite";
            }
        }
        finally
        {
            IsRevoking = false;
        }
    }

    private void CopyCode()
    {
        CopyToClipboard(Code);
    }

    private void CopyInviteLink()
    {
        CopyToClipboard(InviteLink);
    }

    private static void CopyToClipboard(string text)
    {
        // Copy to clipboard using Avalonia's clipboard API
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Clipboard?.SetTextAsync(text);
        }
    }
}

public class UserViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly Func<Task> _onRefresh;
    private readonly AdminUserResponse _user;
    private bool _isUpdating;
    private bool _isDeleting;
    private bool _showDeleteConfirmation;
    private string? _error;

    public UserViewModel(AdminUserResponse user, IApiClient apiClient, Func<Task> onRefresh)
    {
        _user = user;
        _apiClient = apiClient;
        _onRefresh = onRefresh;

        var canToggle = this.WhenAnyValue(x => x.IsUpdating, isUpdating => !isUpdating);
        ToggleAdminCommand = ReactiveCommand.CreateFromTask(ToggleAdminAsync, canToggle);

        var canDelete = this.WhenAnyValue(x => x.IsDeleting, isDeleting => !isDeleting);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync, canDelete);

        ShowDeleteConfirmationCommand = ReactiveCommand.Create(() => { ShowDeleteConfirmation = true; });
        CancelDeleteCommand = ReactiveCommand.Create(() => { ShowDeleteConfirmation = false; });
    }

    public Guid Id => _user.Id;
    public string Username => _user.Username;
    public string Email => _user.Email;
    public bool IsServerAdmin => _user.IsServerAdmin;
    public bool IsOnline => _user.IsOnline;
    public DateTime CreatedAt => _user.CreatedAt;
    public string? InvitedByUsername => _user.InvitedByUsername;

    public string AdminStatusDisplay => IsServerAdmin ? "Admin" : "User";
    public string OnlineStatusDisplay => IsOnline ? "Online" : "Offline";
    public string InvitedByDisplay => InvitedByUsername ?? "(First user)";
    public string ToggleAdminButtonText => IsServerAdmin ? "Remove Admin" : "Make Admin";

    public bool IsUpdating
    {
        get => _isUpdating;
        set => this.RaiseAndSetIfChanged(ref _isUpdating, value);
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        set => this.RaiseAndSetIfChanged(ref _isDeleting, value);
    }

    public bool ShowDeleteConfirmation
    {
        get => _showDeleteConfirmation;
        set => this.RaiseAndSetIfChanged(ref _showDeleteConfirmation, value);
    }

    public string? Error
    {
        get => _error;
        set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleAdminCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowDeleteConfirmationCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }

    private async Task ToggleAdminAsync()
    {
        IsUpdating = true;
        Error = null;

        try
        {
            var result = await _apiClient.SetUserAdminStatusAsync(Id, !IsServerAdmin);
            if (result.Success)
            {
                await _onRefresh();
            }
            else
            {
                Error = result.Error ?? "Failed to update admin status";
            }
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private async Task DeleteAsync()
    {
        IsDeleting = true;
        Error = null;

        try
        {
            var result = await _apiClient.DeleteUserAsync(Id);
            if (result.Success)
            {
                await _onRefresh();
            }
            else
            {
                Error = result.Error ?? "Failed to delete user";
            }
        }
        finally
        {
            IsDeleting = false;
            ShowDeleteConfirmation = false;
        }
    }
}
