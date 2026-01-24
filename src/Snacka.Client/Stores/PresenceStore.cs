using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Snacka.Client.Services;

namespace Snacka.Client.Stores;

/// <summary>
/// State representing a user's online presence.
/// </summary>
public record UserPresenceState(
    Guid UserId,
    bool IsOnline
);

/// <summary>
/// Store managing user presence (online/offline status) and SignalR connection state.
/// </summary>
public interface IPresenceStore : IStore<UserPresenceState, Guid>
{
    /// <summary>
    /// Observable set of currently online user IDs.
    /// </summary>
    IObservable<IReadOnlyCollection<Guid>> OnlineUserIds { get; }

    /// <summary>
    /// Current SignalR connection status.
    /// </summary>
    IObservable<ConnectionState> ConnectionStatus { get; }

    /// <summary>
    /// Reconnection countdown seconds remaining.
    /// </summary>
    IObservable<int> ReconnectSecondsRemaining { get; }

    /// <summary>
    /// Checks if a specific user is online.
    /// </summary>
    bool IsUserOnline(Guid userId);

    // Actions
    void SetOnlineUsers(IEnumerable<Guid> userIds);
    void SetUserOnline(Guid userId);
    void SetUserOffline(Guid userId);
    void SetConnectionStatus(ConnectionState status);
    void SetReconnectCountdown(int seconds);
    void Clear();
}

public sealed class PresenceStore : IPresenceStore, IDisposable
{
    private readonly SourceCache<UserPresenceState, Guid> _presenceCache;
    private readonly BehaviorSubject<ConnectionState> _connectionStatus;
    private readonly BehaviorSubject<int> _reconnectSeconds;
    private readonly IDisposable _cleanUp;

    public PresenceStore()
    {
        _presenceCache = new SourceCache<UserPresenceState, Guid>(p => p.UserId);
        _connectionStatus = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
        _reconnectSeconds = new BehaviorSubject<int>(0);

        _cleanUp = _presenceCache.Connect().Subscribe();
    }

    public IObservable<IChangeSet<UserPresenceState, Guid>> Connect() => _presenceCache.Connect();

    public IObservable<IReadOnlyCollection<UserPresenceState>> Items =>
        _presenceCache.Connect()
            .QueryWhenChanged(cache => cache.Items.ToList().AsReadOnly() as IReadOnlyCollection<UserPresenceState>);

    public IObservable<IReadOnlyCollection<Guid>> OnlineUserIds =>
        _presenceCache.Connect()
            .QueryWhenChanged(cache =>
                cache.Items
                    .Where(p => p.IsOnline)
                    .Select(p => p.UserId)
                    .ToList()
                    .AsReadOnly() as IReadOnlyCollection<Guid>)
            .DistinctUntilChanged();

    public IObservable<ConnectionState> ConnectionStatus => _connectionStatus.AsObservable();

    public IObservable<int> ReconnectSecondsRemaining => _reconnectSeconds.AsObservable();

    public bool IsUserOnline(Guid userId)
    {
        var lookup = _presenceCache.Lookup(userId);
        return lookup.HasValue && lookup.Value.IsOnline;
    }

    public void SetOnlineUsers(IEnumerable<Guid> userIds)
    {
        var onlineSet = userIds.ToHashSet();

        _presenceCache.Edit(cache =>
        {
            // Mark all existing as offline first
            var toUpdate = cache.Items
                .Where(p => p.IsOnline && !onlineSet.Contains(p.UserId))
                .Select(p => p with { IsOnline = false })
                .ToList();

            foreach (var item in toUpdate)
            {
                cache.AddOrUpdate(item);
            }

            // Add/update online users
            foreach (var userId in onlineSet)
            {
                cache.AddOrUpdate(new UserPresenceState(userId, true));
            }
        });
    }

    public void SetUserOnline(Guid userId)
    {
        _presenceCache.AddOrUpdate(new UserPresenceState(userId, true));
    }

    public void SetUserOffline(Guid userId)
    {
        var existing = _presenceCache.Lookup(userId);
        if (existing.HasValue)
        {
            _presenceCache.AddOrUpdate(existing.Value with { IsOnline = false });
        }
    }

    public void SetConnectionStatus(ConnectionState status)
    {
        _connectionStatus.OnNext(status);
    }

    public void SetReconnectCountdown(int seconds)
    {
        _reconnectSeconds.OnNext(seconds);
    }

    public void Clear()
    {
        _presenceCache.Clear();
        _connectionStatus.OnNext(ConnectionState.Disconnected);
        _reconnectSeconds.OnNext(0);
    }

    public void Dispose()
    {
        _cleanUp.Dispose();
        _presenceCache.Dispose();
        _connectionStatus.Dispose();
        _reconnectSeconds.Dispose();
    }
}
