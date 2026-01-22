using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Controls;

/// <summary>
/// Modal for creating a group DM conversation.
/// </summary>
public partial class CreateGroupDMModal : UserControl
{
    private IApiClient? _apiClient;
    private DMContentViewModel? _dmViewModel;
    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer _searchDebounceTimer;

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<CreateGroupDMModal, bool>(nameof(IsOpen));

    public static readonly StyledProperty<string?> GroupNameProperty =
        AvaloniaProperty.Register<CreateGroupDMModal, string?>(nameof(GroupName));

    public static readonly StyledProperty<string?> SearchQueryProperty =
        AvaloniaProperty.Register<CreateGroupDMModal, string?>(nameof(SearchQuery));

    public static readonly StyledProperty<bool> IsCreatingProperty =
        AvaloniaProperty.Register<CreateGroupDMModal, bool>(nameof(IsCreating));

    public static readonly StyledProperty<bool> CanCreateProperty =
        AvaloniaProperty.Register<CreateGroupDMModal, bool>(nameof(CanCreate));

    public CreateGroupDMModal()
    {
        InitializeComponent();

        SelectedUsers = new ObservableCollection<UserSearchResult>();
        SearchResults = new ObservableCollection<UserSearchResult>();

        CloseCommand = ReactiveCommand.Create(Close);
        CreateCommand = ReactiveCommand.CreateFromTask(CreateGroupAsync);
        AddUserCommand = ReactiveCommand.Create<UserSearchResult>(AddUser);
        RemoveUserCommand = ReactiveCommand.Create<UserSearchResult>(RemoveUser);

        // Setup search debounce
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += async (_, _) =>
        {
            _searchDebounceTimer.Stop();
            await SearchUsersAsync();
        };

        // Watch for search query changes
        this.GetObservable(SearchQueryProperty).Subscribe(_ =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        });

        // Watch for selected users changes
        SelectedUsers.CollectionChanged += (_, _) =>
        {
            CanCreate = SelectedUsers.Count >= 1; // Need at least 1 other user for a group
        };
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string? GroupName
    {
        get => GetValue(GroupNameProperty);
        set => SetValue(GroupNameProperty, value);
    }

    public string? SearchQuery
    {
        get => GetValue(SearchQueryProperty);
        set => SetValue(SearchQueryProperty, value);
    }

    public bool IsCreating
    {
        get => GetValue(IsCreatingProperty);
        set => SetValue(IsCreatingProperty, value);
    }

    public bool CanCreate
    {
        get => GetValue(CanCreateProperty);
        set => SetValue(CanCreateProperty, value);
    }

    public ObservableCollection<UserSearchResult> SelectedUsers { get; }
    public ObservableCollection<UserSearchResult> SearchResults { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateCommand { get; }
    public ReactiveCommand<UserSearchResult, Unit> AddUserCommand { get; }
    public ReactiveCommand<UserSearchResult, Unit> RemoveUserCommand { get; }

    /// <summary>
    /// Opens the modal for creating a group DM.
    /// </summary>
    public void Open(IApiClient apiClient, DMContentViewModel dmViewModel)
    {
        _apiClient = apiClient;
        _dmViewModel = dmViewModel;
        GroupName = null;
        SearchQuery = null;
        SelectedUsers.Clear();
        SearchResults.Clear();
        IsCreating = false;
        CanCreate = false;
        IsOpen = true;
    }

    private void Close()
    {
        IsOpen = false;
        _searchCts?.Cancel();
    }

    private void AddUser(UserSearchResult user)
    {
        if (!SelectedUsers.Any(u => u.Id == user.Id))
        {
            SelectedUsers.Add(user);
            // Remove from search results to avoid duplicates
            var found = SearchResults.FirstOrDefault(u => u.Id == user.Id);
            if (found != null)
            {
                SearchResults.Remove(found);
            }
        }
    }

    private void RemoveUser(UserSearchResult user)
    {
        SelectedUsers.Remove(user);
    }

    private async Task SearchUsersAsync()
    {
        if (_apiClient == null || string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchResults.Clear();
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        try
        {
            var result = await _apiClient.SearchUsersAsync(SearchQuery);
            SearchResults.Clear();

            if (result.Success && result.Data != null)
            {
                foreach (var user in result.Data.Where(u => !SelectedUsers.Any(s => s.Id == u.Id)))
                {
                    SearchResults.Add(user);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled, ignore
        }
    }

    private async Task CreateGroupAsync()
    {
        if (_dmViewModel == null || SelectedUsers.Count < 1)
            return;

        IsCreating = true;
        try
        {
            var participantIds = SelectedUsers.Select(u => u.Id).ToList();
            var name = string.IsNullOrWhiteSpace(GroupName) ? null : GroupName.Trim();

            var success = await _dmViewModel.CreateGroupConversationAsync(participantIds, name);
            if (success)
            {
                Close();
            }
        }
        finally
        {
            IsCreating = false;
        }
    }
}
