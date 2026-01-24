using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Timers;
using Timer = System.Timers.Timer;

namespace Snacka.Client.Stores;

/// <summary>
/// Represents a user who is currently typing.
/// </summary>
public record TypingUserState(
    Guid UserId,
    string Username,
    Guid ChannelId,
    DateTime LastTypingAt
);

/// <summary>
/// Store managing typing indicator state for channels.
/// Handles automatic cleanup of expired typing indicators.
/// </summary>
public interface ITypingStore : IDisposable
{
    /// <summary>
    /// Observable list of users currently typing in the specified channel.
    /// </summary>
    IObservable<IReadOnlyCollection<TypingUserState>> GetTypingUsers(Guid channelId);

    /// <summary>
    /// Observable of whether anyone is typing in the specified channel.
    /// </summary>
    IObservable<bool> IsAnyoneTyping(Guid channelId);

    /// <summary>
    /// Observable of the typing indicator text for the specified channel.
    /// </summary>
    IObservable<string> GetTypingIndicatorText(Guid channelId);

    /// <summary>
    /// Gets typing users for a channel synchronously.
    /// </summary>
    IReadOnlyList<TypingUserState> GetTypingUsersSync(Guid channelId);

    // Actions
    void AddTypingUser(Guid channelId, Guid userId, string username);
    void RemoveTypingUser(Guid channelId, Guid userId);
    void ClearChannel(Guid channelId);
    void Clear();
}

public sealed class TypingStore : ITypingStore
{
    private readonly Dictionary<Guid, List<TypingUserState>> _typingByChannel = new();
    private readonly BehaviorSubject<int> _changeNotifier = new(0);
    private readonly Timer _cleanupTimer;
    private readonly object _lock = new();

    private const int TypingTimeoutMs = 5000; // Clear typing after 5 seconds of inactivity
    private const int CleanupIntervalMs = 1000; // Check for expired entries every second

    public TypingStore()
    {
        _cleanupTimer = new Timer(CleanupIntervalMs);
        _cleanupTimer.Elapsed += OnCleanupTimerElapsed;
        _cleanupTimer.AutoReset = true;
        _cleanupTimer.Start();
    }

    public IObservable<IReadOnlyCollection<TypingUserState>> GetTypingUsers(Guid channelId)
    {
        return _changeNotifier
            .Select(_ =>
            {
                lock (_lock)
                {
                    if (_typingByChannel.TryGetValue(channelId, out var users))
                        return users.ToList().AsReadOnly() as IReadOnlyCollection<TypingUserState>;
                    return Array.Empty<TypingUserState>();
                }
            })
            .DistinctUntilChanged(new TypingUsersComparer());
    }

    public IObservable<bool> IsAnyoneTyping(Guid channelId)
    {
        return GetTypingUsers(channelId).Select(users => users.Count > 0).DistinctUntilChanged();
    }

    public IObservable<string> GetTypingIndicatorText(Guid channelId)
    {
        return GetTypingUsers(channelId).Select(users =>
        {
            if (users.Count == 0) return string.Empty;
            if (users.Count == 1) return $"{users.First().Username} is typing...";
            if (users.Count == 2)
            {
                var userList = users.ToList();
                return $"{userList[0].Username} and {userList[1].Username} are typing...";
            }
            return $"{users.First().Username} and {users.Count - 1} others are typing...";
        }).DistinctUntilChanged();
    }

    public IReadOnlyList<TypingUserState> GetTypingUsersSync(Guid channelId)
    {
        lock (_lock)
        {
            if (_typingByChannel.TryGetValue(channelId, out var users))
                return users.ToList().AsReadOnly();
            return Array.Empty<TypingUserState>();
        }
    }

    public void AddTypingUser(Guid channelId, Guid userId, string username)
    {
        lock (_lock)
        {
            if (!_typingByChannel.TryGetValue(channelId, out var users))
            {
                users = new List<TypingUserState>();
                _typingByChannel[channelId] = users;
            }

            // Remove existing entry for this user (to update timestamp)
            users.RemoveAll(u => u.UserId == userId);

            // Add new entry
            users.Add(new TypingUserState(userId, username, channelId, DateTime.UtcNow));
        }

        NotifyChange();
    }

    public void RemoveTypingUser(Guid channelId, Guid userId)
    {
        bool removed;
        lock (_lock)
        {
            if (_typingByChannel.TryGetValue(channelId, out var users))
            {
                removed = users.RemoveAll(u => u.UserId == userId) > 0;
            }
            else
            {
                removed = false;
            }
        }

        if (removed)
            NotifyChange();
    }

    public void ClearChannel(Guid channelId)
    {
        bool hadUsers;
        lock (_lock)
        {
            hadUsers = _typingByChannel.Remove(channelId);
        }

        if (hadUsers)
            NotifyChange();
    }

    public void Clear()
    {
        bool hadAny;
        lock (_lock)
        {
            hadAny = _typingByChannel.Count > 0;
            _typingByChannel.Clear();
        }

        if (hadAny)
            NotifyChange();
    }

    private void OnCleanupTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.UtcNow;
        var anyRemoved = false;

        lock (_lock)
        {
            foreach (var channelUsers in _typingByChannel.Values)
            {
                var removed = channelUsers.RemoveAll(u =>
                    (now - u.LastTypingAt).TotalMilliseconds > TypingTimeoutMs);
                if (removed > 0)
                    anyRemoved = true;
            }
        }

        if (anyRemoved)
            NotifyChange();
    }

    private void NotifyChange()
    {
        _changeNotifier.OnNext(_changeNotifier.Value + 1);
    }

    public void Dispose()
    {
        _cleanupTimer.Stop();
        _cleanupTimer.Dispose();
        _changeNotifier.Dispose();
    }

    /// <summary>
    /// Comparer for detecting changes in typing users collection.
    /// </summary>
    private class TypingUsersComparer : IEqualityComparer<IReadOnlyCollection<TypingUserState>>
    {
        public bool Equals(IReadOnlyCollection<TypingUserState>? x, IReadOnlyCollection<TypingUserState>? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;
            if (x.Count != y.Count) return false;

            var xIds = x.Select(u => u.UserId).OrderBy(id => id).ToList();
            var yIds = y.Select(u => u.UserId).OrderBy(id => id).ToList();
            return xIds.SequenceEqual(yIds);
        }

        public int GetHashCode(IReadOnlyCollection<TypingUserState> obj)
        {
            return obj.Count;
        }
    }
}
