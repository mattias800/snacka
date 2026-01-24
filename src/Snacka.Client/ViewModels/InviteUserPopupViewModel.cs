using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the invite user popup.
/// Handles searching for users and sending community invites.
/// Reads current community from CommunityStore (Redux-style).
/// </summary>
public class InviteUserPopupViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly ICommunityStore _communityStore;

    private bool _isOpen;
    private string _searchQuery = string.Empty;
    private bool _isSearching;
    private ObservableCollection<UserSearchResult> _searchResults = new();
    private bool _hasNoResults;
    private string? _statusMessage;
    private bool _isStatusError;

    public InviteUserPopupViewModel(IApiClient apiClient, ICommunityStore communityStore)
    {
        _apiClient = apiClient;
        _communityStore = communityStore;

        OpenCommand = ReactiveCommand.Create(Open);
        CloseCommand = ReactiveCommand.Create(Close);
        InviteUserCommand = ReactiveCommand.CreateFromTask<UserSearchResult>(InviteUserAsync);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    public ObservableCollection<UserSearchResult> SearchResults
    {
        get => _searchResults;
        set => this.RaiseAndSetIfChanged(ref _searchResults, value);
    }

    public bool HasNoResults
    {
        get => _hasNoResults;
        set => this.RaiseAndSetIfChanged(ref _hasNoResults, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool IsStatusError
    {
        get => _isStatusError;
        set => this.RaiseAndSetIfChanged(ref _isStatusError, value);
    }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<UserSearchResult, Unit> InviteUserCommand { get; }

    private void Open()
    {
        var communityId = _communityStore.GetSelectedCommunityId();
        if (communityId == null) return;

        SearchQuery = string.Empty;
        SearchResults.Clear();
        HasNoResults = false;
        StatusMessage = null;
        IsStatusError = false;

        IsOpen = true;
    }

    private void Close()
    {
        IsOpen = false;
    }

    public async Task SearchUsersAsync(string query)
    {
        var communityId = _communityStore.GetSelectedCommunityId();
        if (communityId == null) return;

        IsSearching = true;
        HasNoResults = false;
        StatusMessage = null;

        try
        {
            var result = await _apiClient.SearchUsersToInviteAsync(communityId.Value, query);
            if (result.Success && result.Data is not null)
            {
                SearchResults.Clear();
                foreach (var user in result.Data)
                    SearchResults.Add(user);

                HasNoResults = SearchResults.Count == 0;
            }
            else
            {
                StatusMessage = result.Error ?? "Failed to search users";
                IsStatusError = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error searching users: {ex.Message}");
            StatusMessage = "Failed to search users";
            IsStatusError = true;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task InviteUserAsync(UserSearchResult user)
    {
        var communityId = _communityStore.GetSelectedCommunityId();
        if (communityId == null) return;

        StatusMessage = null;

        try
        {
            var result = await _apiClient.CreateCommunityInviteAsync(communityId.Value, user.Id);
            if (result.Success)
            {
                StatusMessage = $"Invite sent to {user.EffectiveDisplayName}";
                IsStatusError = false;

                // Remove user from search results
                var existingUser = SearchResults.FirstOrDefault(u => u.Id == user.Id);
                if (existingUser != null)
                {
                    SearchResults.Remove(existingUser);
                }
            }
            else
            {
                StatusMessage = result.Error ?? "Failed to send invite";
                IsStatusError = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inviting user: {ex.Message}");
            StatusMessage = "Failed to send invite";
            IsStatusError = true;
        }
    }
}
