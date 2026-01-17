using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Snacka.Client.Services;

namespace Snacka.Client.ViewModels;

/// <summary>
/// Type of activity/notification event.
/// </summary>
public enum ActivityType
{
    /// <summary>User joined the server (visible to admins/owners).</summary>
    UserJoinedServer,

    /// <summary>User joined a community (visible to all community members).</summary>
    UserJoinedCommunity,

    /// <summary>User left the server or community.</summary>
    UserLeft,

    /// <summary>User was mentioned in a channel or DM.</summary>
    Mention,

    /// <summary>New direct message received.</summary>
    DirectMessage,

    /// <summary>New message in a channel with unread messages.</summary>
    ChannelMessage,

    /// <summary>User was invited to a community.</summary>
    CommunityInvite,

    /// <summary>Thread reply received.</summary>
    ThreadReply
}

/// <summary>
/// Represents a single activity/notification item in the activity feed.
/// </summary>
public record ActivityItem(
    Guid Id,
    ActivityType Type,
    DateTime Timestamp,
    string Title,
    string Description,
    Guid? UserId = null,
    string? Username = null,
    Guid? CommunityId = null,
    string? CommunityName = null,
    Guid? ChannelId = null,
    string? ChannelName = null,
    Guid? MessageId = null,
    Guid? InviteId = null,
    bool IsRead = false
)
{
    /// <summary>
    /// Returns the appropriate icon for this activity type.
    /// </summary>
    public string Icon => Type switch
    {
        ActivityType.UserJoinedServer => "PersonAdd",
        ActivityType.UserJoinedCommunity => "People",
        ActivityType.UserLeft => "PersonDelete",
        ActivityType.Mention => "Mention",
        ActivityType.DirectMessage => "Chat",
        ActivityType.ChannelMessage => "Comment",
        ActivityType.CommunityInvite => "Mail",
        ActivityType.ThreadReply => "CommentMultiple",
        _ => "Info"
    };

    /// <summary>
    /// Returns a relative time string (e.g., "2m ago", "1h ago").
    /// </summary>
    public string RelativeTime
    {
        get
        {
            var diff = DateTime.UtcNow - Timestamp;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return Timestamp.ToString("MMM d");
        }
    }
}

/// <summary>
/// ViewModel for the activity/notifications feed.
/// </summary>
public class ActivityFeedViewModel : ViewModelBase
{
    private readonly ISignalRService _signalR;
    private readonly IApiClient _apiClient;
    private readonly Guid _currentUserId;
    private readonly Func<Guid?> _getCurrentCommunityId;
    private readonly Func<bool> _canManageServer;
    private readonly Func<Task> _onCommunitiesChanged;
    private readonly Action<Guid, string>? _onOpenDm;

    private ObservableCollection<ActivityItem> _activities = new();
    private bool _isLoading;

    public ActivityFeedViewModel(
        ISignalRService signalR,
        IApiClient apiClient,
        Guid currentUserId,
        Func<Guid?> getCurrentCommunityId,
        Func<bool> canManageServer,
        Func<Task> onCommunitiesChanged,
        Action<Guid, string>? onOpenDm = null)
    {
        _signalR = signalR;
        _apiClient = apiClient;
        _currentUserId = currentUserId;
        _getCurrentCommunityId = getCurrentCommunityId;
        _canManageServer = canManageServer;
        _onCommunitiesChanged = onCommunitiesChanged;
        _onOpenDm = onOpenDm;

        // Commands
        MarkAllAsReadCommand = ReactiveCommand.Create(MarkAllAsRead);
        ClearAllCommand = ReactiveCommand.Create(ClearAll);
        AcceptInviteCommand = ReactiveCommand.CreateFromTask<ActivityItem>(AcceptInviteAsync);
        DeclineInviteCommand = ReactiveCommand.CreateFromTask<ActivityItem>(DeclineInviteAsync);
        ActivityClickedCommand = ReactiveCommand.Create<ActivityItem>(OnActivityClicked);

        SetupSignalRHandlers();

        // Load pending invites on startup
        _ = LoadPendingInvitesAsync();
    }

    // Commands
    public ReactiveCommand<Unit, Unit> MarkAllAsReadCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAllCommand { get; }
    public ReactiveCommand<ActivityItem, Unit> AcceptInviteCommand { get; }
    public ReactiveCommand<ActivityItem, Unit> DeclineInviteCommand { get; }
    public ReactiveCommand<ActivityItem, Unit> ActivityClickedCommand { get; }

    public ObservableCollection<ActivityItem> Activities => _activities;

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public int UnreadCount => _activities.Count(a => !a.IsRead);

    public bool HasUnread => UnreadCount > 0;

    private void SetupSignalRHandlers()
    {
        // User joined community
        _signalR.CommunityMemberAdded += e => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var currentCommunityId = _getCurrentCommunityId();
            if (currentCommunityId == e.CommunityId)
            {
                AddActivity(new ActivityItem(
                    Guid.NewGuid(),
                    ActivityType.UserJoinedCommunity,
                    DateTime.UtcNow,
                    "New member joined",
                    "Welcome to the community!",
                    UserId: e.UserId,
                    CommunityId: e.CommunityId
                ));
            }
        });

        // Direct message received
        _signalR.DirectMessageReceived += message => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Only show notification if it's from someone else
            if (message.SenderId != _currentUserId)
            {
                AddActivity(new ActivityItem(
                    Guid.NewGuid(),
                    ActivityType.DirectMessage,
                    message.CreatedAt,
                    message.SenderEffectiveDisplayName,
                    message.Content.Length > 50 ? message.Content[..50] + "..." : message.Content,
                    UserId: message.SenderId,
                    Username: message.SenderUsername,
                    MessageId: message.Id
                ));
            }
        });

        // Message received (for mentions)
        _signalR.MessageReceived += message => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Check if current user is mentioned (simple text-based check)
            // A more robust implementation would parse mentions server-side
            if (message.Content.Contains($"@") && message.AuthorId != _currentUserId)
            {
                AddActivity(new ActivityItem(
                    Guid.NewGuid(),
                    ActivityType.Mention,
                    message.CreatedAt,
                    $"Mentioned by {message.AuthorEffectiveDisplayName}",
                    message.Content.Length > 50 ? message.Content[..50] + "..." : message.Content,
                    UserId: message.AuthorId,
                    Username: message.AuthorUsername,
                    ChannelId: message.ChannelId,
                    MessageId: message.Id
                ));
            }
        });

        // Thread reply
        _signalR.ThreadReplyReceived += e => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Only notify if someone else replied
            if (e.Reply.AuthorId != _currentUserId)
            {
                AddActivity(new ActivityItem(
                    Guid.NewGuid(),
                    ActivityType.ThreadReply,
                    e.Reply.CreatedAt,
                    $"Reply from {e.Reply.AuthorEffectiveDisplayName}",
                    e.Reply.Content.Length > 50 ? e.Reply.Content[..50] + "..." : e.Reply.Content,
                    UserId: e.Reply.AuthorId,
                    Username: e.Reply.AuthorUsername,
                    MessageId: e.Reply.Id
                ));
            }
        });

        // Community invite received
        _signalR.CommunityInviteReceived += e => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AddActivity(new ActivityItem(
                Guid.NewGuid(),
                ActivityType.CommunityInvite,
                e.CreatedAt,
                $"Invite to {e.CommunityName}",
                $"From {e.InvitedByEffectiveDisplayName}",
                UserId: e.InvitedById,
                Username: e.InvitedByUsername,
                CommunityId: e.CommunityId,
                CommunityName: e.CommunityName,
                InviteId: e.InviteId
            ));
        });
    }

    /// <summary>
    /// Loads pending invites from the API and adds them to the activity feed.
    /// </summary>
    private async Task LoadPendingInvitesAsync()
    {
        try
        {
            var result = await _apiClient.GetMyPendingInvitesAsync();
            if (result.Success && result.Data is not null)
            {
                foreach (var invite in result.Data)
                {
                    // Check if this invite is already in the feed
                    if (!_activities.Any(a => a.InviteId == invite.Id))
                    {
                        AddActivity(new ActivityItem(
                            Guid.NewGuid(),
                            ActivityType.CommunityInvite,
                            invite.CreatedAt,
                            $"Invite to {invite.CommunityName}",
                            $"From {invite.InvitedByEffectiveDisplayName}",
                            UserId: invite.InvitedById,
                            Username: invite.InvitedByUsername,
                            CommunityId: invite.CommunityId,
                            CommunityName: invite.CommunityName,
                            InviteId: invite.Id
                        ));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading pending invites: {ex.Message}");
        }
    }

    /// <summary>
    /// Accepts a community invite.
    /// </summary>
    private async Task AcceptInviteAsync(ActivityItem activity)
    {
        if (activity.InviteId is null) return;

        try
        {
            var result = await _apiClient.AcceptInviteAsync(activity.InviteId.Value);
            if (result.Success)
            {
                // Remove the invite from the activity feed
                _activities.Remove(activity);
                this.RaisePropertyChanged(nameof(UnreadCount));
                this.RaisePropertyChanged(nameof(HasUnread));

                // Notify to reload communities
                await _onCommunitiesChanged();

                Console.WriteLine($"Accepted invite to {activity.CommunityName}");
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

    /// <summary>
    /// Declines a community invite.
    /// </summary>
    private async Task DeclineInviteAsync(ActivityItem activity)
    {
        if (activity.InviteId is null) return;

        try
        {
            var result = await _apiClient.DeclineInviteAsync(activity.InviteId.Value);
            if (result.Success)
            {
                // Remove the invite from the activity feed
                _activities.Remove(activity);
                this.RaisePropertyChanged(nameof(UnreadCount));
                this.RaisePropertyChanged(nameof(HasUnread));

                Console.WriteLine($"Declined invite to {activity.CommunityName}");
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

    private void AddActivity(ActivityItem activity)
    {
        // Insert at the beginning (most recent first)
        _activities.Insert(0, activity);

        // Limit to 50 items
        while (_activities.Count > 50)
            _activities.RemoveAt(_activities.Count - 1);

        this.RaisePropertyChanged(nameof(UnreadCount));
        this.RaisePropertyChanged(nameof(HasUnread));
    }

    /// <summary>
    /// Marks all activities as read.
    /// </summary>
    public void MarkAllAsRead()
    {
        for (int i = 0; i < _activities.Count; i++)
        {
            if (!_activities[i].IsRead)
            {
                _activities[i] = _activities[i] with { IsRead = true };
            }
        }
        this.RaisePropertyChanged(nameof(UnreadCount));
        this.RaisePropertyChanged(nameof(HasUnread));
    }

    /// <summary>
    /// Clears all activities.
    /// </summary>
    public void ClearAll()
    {
        _activities.Clear();
        this.RaisePropertyChanged(nameof(UnreadCount));
        this.RaisePropertyChanged(nameof(HasUnread));
    }

    /// <summary>
    /// Handles clicking on an activity item.
    /// </summary>
    private void OnActivityClicked(ActivityItem activity)
    {
        // Mark as read
        MarkActivityAsRead(activity);

        // Navigate based on activity type
        switch (activity.Type)
        {
            case ActivityType.DirectMessage:
                if (activity.UserId.HasValue && activity.Username is not null)
                {
                    _onOpenDm?.Invoke(activity.UserId.Value, activity.Title);
                }
                break;
            // TODO: Add navigation for other activity types (mentions, thread replies, etc.)
        }
    }

    /// <summary>
    /// Marks a specific activity as read.
    /// </summary>
    public void MarkActivityAsRead(ActivityItem activity)
    {
        var index = _activities.IndexOf(activity);
        if (index >= 0 && !activity.IsRead)
        {
            _activities[index] = activity with { IsRead = true };
            this.RaisePropertyChanged(nameof(UnreadCount));
            this.RaisePropertyChanged(nameof(HasUnread));
        }
    }
}
