using System.Collections.ObjectModel;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// Type of item in the quick switcher.
/// </summary>
public enum QuickSwitcherItemType
{
    TextChannel,
    VoiceChannel,
    User
}

/// <summary>
/// Represents an item in the quick switcher search results.
/// </summary>
public class QuickSwitcherItem
{
    public QuickSwitcherItemType Type { get; }
    public Guid Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public int Score { get; set; }

    /// <summary>
    /// Icon prefix for display (# for text channels, speaker for voice, @ for users).
    /// </summary>
    public string Icon => Type switch
    {
        QuickSwitcherItemType.TextChannel => "#",
        QuickSwitcherItemType.VoiceChannel => "🔊",
        QuickSwitcherItemType.User => "@",
        _ => ""
    };

    public QuickSwitcherItem(QuickSwitcherItemType type, Guid id, string name, string? description = null)
    {
        Type = type;
        Id = id;
        Name = name;
        Description = description;
    }
}

/// <summary>
/// Record for persisting recent quick switcher items.
/// </summary>
public record RecentQuickSwitcherItem(
    QuickSwitcherItemType Type,
    Guid Id,
    string Name,
    DateTime LastUsed);

/// <summary>
/// ViewModel for the quick switcher modal.
/// </summary>
public class QuickSwitcherViewModel : ViewModelBase
{
    private readonly ObservableCollection<ChannelResponse> _textChannels;
    private readonly ObservableCollection<VoiceChannelViewModel> _voiceChannels;
    private readonly ObservableCollection<CommunityMemberResponse> _members;
    private readonly Guid _currentUserId;
    private readonly Action<QuickSwitcherItem> _onItemSelected;
    private readonly Action _onClose;
    private readonly ISettingsStore _settingsStore;

    private string _searchQuery = string.Empty;
    private int _selectedIndex = -1; // -1 means no selection
    private ObservableCollection<QuickSwitcherItem> _filteredItems = new();

    public QuickSwitcherViewModel(
        ObservableCollection<ChannelResponse> textChannels,
        ObservableCollection<VoiceChannelViewModel> voiceChannels,
        ObservableCollection<CommunityMemberResponse> members,
        Guid currentUserId,
        ISettingsStore settingsStore,
        Action<QuickSwitcherItem> onItemSelected,
        Action onClose)
    {
        _textChannels = textChannels;
        _voiceChannels = voiceChannels;
        _members = members;
        _currentUserId = currentUserId;
        _settingsStore = settingsStore;
        _onItemSelected = onItemSelected;
        _onClose = onClose;

        // Show recent items initially
        LoadRecentItems();
    }

    /// <summary>
    /// The search query entered by the user.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            UpdateFilteredItems();
        }
    }

    /// <summary>
    /// The index of the currently selected item. -1 means no selection.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Math.Clamp(value, -1, Math.Max(0, FilteredItems.Count - 1));
            this.RaiseAndSetIfChanged(ref _selectedIndex, clamped);
        }
    }

    /// <summary>
    /// The filtered list of items to display.
    /// </summary>
    public ObservableCollection<QuickSwitcherItem> FilteredItems
    {
        get => _filteredItems;
        private set => this.RaiseAndSetIfChanged(ref _filteredItems, value);
    }

    /// <summary>
    /// Whether there are any items to display.
    /// </summary>
    public bool HasItems => FilteredItems.Count > 0;

    /// <summary>
    /// The header text to show above items.
    /// </summary>
    public string HeaderText => string.IsNullOrEmpty(SearchQuery) ? "RECENT" : "RESULTS";

    /// <summary>
    /// Move selection up.
    /// </summary>
    public void MoveUp()
    {
        if (SelectedIndex > 0)
            SelectedIndex--;
        else if (SelectedIndex == -1 && FilteredItems.Count > 0)
            SelectedIndex = 0; // First up press selects first item
    }

    /// <summary>
    /// Move selection down.
    /// </summary>
    public void MoveDown()
    {
        if (SelectedIndex == -1 && FilteredItems.Count > 0)
            SelectedIndex = 0; // First down press selects first item
        else if (SelectedIndex < FilteredItems.Count - 1)
            SelectedIndex++;
    }

    /// <summary>
    /// Select the currently highlighted item.
    /// </summary>
    public void SelectCurrent()
    {
        if (FilteredItems.Count > 0)
        {
            // If no selection, select first item
            var index = SelectedIndex == -1 ? 0 : SelectedIndex;
            if (index < FilteredItems.Count)
            {
                var item = FilteredItems[index];
                AddToRecent(item);
                _onItemSelected(item);
            }
        }
    }

    /// <summary>
    /// Select a specific item.
    /// </summary>
    public void SelectItem(QuickSwitcherItem item)
    {
        AddToRecent(item);
        _onItemSelected(item);
    }

    /// <summary>
    /// Close the quick switcher.
    /// </summary>
    public void Close() => _onClose();

    private void LoadRecentItems()
    {
        var recentItems = _settingsStore.Settings.RecentQuickSwitcherItems ?? new List<RecentQuickSwitcherItem>();

        FilteredItems.Clear();
        foreach (var recent in recentItems.OrderByDescending(r => r.LastUsed).Take(10))
        {
            // Verify the item still exists
            if (ItemExists(recent.Type, recent.Id))
            {
                FilteredItems.Add(new QuickSwitcherItem(recent.Type, recent.Id, recent.Name));
            }
        }

        SelectedIndex = -1; // No selection until user navigates
        this.RaisePropertyChanged(nameof(HasItems));
        this.RaisePropertyChanged(nameof(HeaderText));
    }

    private bool ItemExists(QuickSwitcherItemType type, Guid id)
    {
        return type switch
        {
            QuickSwitcherItemType.TextChannel => _textChannels.Any(c => c.Id == id),
            QuickSwitcherItemType.VoiceChannel => _voiceChannels.Any(c => c.Id == id),
            QuickSwitcherItemType.User => _members.Any(m => m.UserId == id),
            _ => false
        };
    }

    private void UpdateFilteredItems()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            LoadRecentItems();
            return;
        }

        var query = SearchQuery.Trim();
        var results = new List<QuickSwitcherItem>();

        // Check for prefix filter
        var filterChannelsOnly = query.StartsWith('#');
        var filterUsersOnly = query.StartsWith('@');

        if (filterChannelsOnly || filterUsersOnly)
        {
            query = query.Substring(1); // Remove prefix
        }

        // Search text channels
        if (!filterUsersOnly)
        {
            foreach (var channel in _textChannels)
            {
                var (isMatch, score) = FuzzyMatcher.Match(query, channel.Name);
                if (isMatch)
                {
                    results.Add(new QuickSwitcherItem(
                        QuickSwitcherItemType.TextChannel,
                        channel.Id,
                        channel.Name,
                        channel.Topic) { Score = score });
                }
            }
        }

        // Search voice channels
        if (!filterUsersOnly)
        {
            foreach (var channel in _voiceChannels)
            {
                var (isMatch, score) = FuzzyMatcher.Match(query, channel.Name);
                if (isMatch)
                {
                    results.Add(new QuickSwitcherItem(
                        QuickSwitcherItemType.VoiceChannel,
                        channel.Id,
                        channel.Name) { Score = score });
                }
            }
        }

        // Search users (exclude current user)
        if (!filterChannelsOnly)
        {
            foreach (var member in _members.Where(m => m.UserId != _currentUserId))
            {
                var displayName = member.EffectiveDisplayName;
                var (isMatch, score) = FuzzyMatcher.Match(query, displayName);

                // Also try matching username if display name didn't match well
                if (!isMatch && displayName != member.Username)
                {
                    (isMatch, score) = FuzzyMatcher.Match(query, member.Username);
                }

                if (isMatch)
                {
                    results.Add(new QuickSwitcherItem(
                        QuickSwitcherItemType.User,
                        member.UserId,
                        displayName,
                        displayName != member.Username ? $"({member.Username})" : null) { Score = score });
                }
            }
        }

        // Sort by score (highest first) and take top 15
        var sortedResults = results
            .OrderByDescending(r => r.Score)
            .Take(15)
            .ToList();

        FilteredItems.Clear();
        foreach (var item in sortedResults)
        {
            FilteredItems.Add(item);
        }

        SelectedIndex = -1; // No selection until user navigates
        this.RaisePropertyChanged(nameof(HasItems));
        this.RaisePropertyChanged(nameof(HeaderText));
    }

    private void AddToRecent(QuickSwitcherItem item)
    {
        var recentItems = _settingsStore.Settings.RecentQuickSwitcherItems ?? new List<RecentQuickSwitcherItem>();

        // Remove existing entry for this item
        recentItems.RemoveAll(r => r.Type == item.Type && r.Id == item.Id);

        // Add new entry at the beginning
        recentItems.Insert(0, new RecentQuickSwitcherItem(item.Type, item.Id, item.Name, DateTime.UtcNow));

        // Keep only the last 10 items
        if (recentItems.Count > 10)
        {
            recentItems = recentItems.Take(10).ToList();
        }

        _settingsStore.Settings.RecentQuickSwitcherItems = recentItems;
        _settingsStore.Save();
    }
}
