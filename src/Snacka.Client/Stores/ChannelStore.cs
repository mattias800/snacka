using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.Stores;

/// <summary>
/// Immutable state representing a channel.
/// </summary>
public record ChannelState(
    Guid Id,
    string Name,
    string? Topic,
    ChannelType Type,
    Guid CommunityId,
    int Position,
    int UnreadCount,
    DateTime CreatedAt
);

/// <summary>
/// Grouping of text channels for display.
/// </summary>
public record CategoryWithChannels(
    string? CategoryName,
    IReadOnlyList<ChannelState> Channels
);

/// <summary>
/// Store managing channel state including selection, unread counts, and organization.
/// </summary>
public interface IChannelStore : IStore<ChannelState, Guid>
{
    /// <summary>
    /// Currently selected channel ID.
    /// </summary>
    IObservable<Guid?> SelectedChannelId { get; }

    /// <summary>
    /// Currently selected channel state.
    /// </summary>
    IObservable<ChannelState?> SelectedChannel { get; }

    /// <summary>
    /// All text channels sorted by position.
    /// </summary>
    IObservable<IReadOnlyCollection<ChannelState>> TextChannels { get; }

    /// <summary>
    /// All voice channels sorted by position.
    /// </summary>
    IObservable<IReadOnlyCollection<ChannelState>> VoiceChannels { get; }

    /// <summary>
    /// Total unread count across all channels.
    /// </summary>
    IObservable<int> TotalUnreadCount { get; }

    /// <summary>
    /// Gets a channel by ID synchronously.
    /// </summary>
    ChannelState? GetChannel(Guid channelId);

    // Actions
    void SetChannels(IEnumerable<ChannelResponse> channels);
    void SelectChannel(Guid? channelId);
    void AddChannel(ChannelResponse channel);
    void UpdateChannel(ChannelResponse channel);
    void RemoveChannel(Guid channelId);
    void UpdateUnreadCount(Guid channelId, int count);
    void IncrementUnreadCount(Guid channelId);
    void ReorderChannels(IEnumerable<ChannelResponse> channels);
    void ClearForCommunity(Guid communityId);
    void Clear();
}

public sealed class ChannelStore : IChannelStore, IDisposable
{
    private readonly SourceCache<ChannelState, Guid> _channelCache;
    private readonly BehaviorSubject<Guid?> _selectedChannelId;
    private readonly IDisposable _cleanUp;

    public ChannelStore()
    {
        _channelCache = new SourceCache<ChannelState, Guid>(c => c.Id);
        _selectedChannelId = new BehaviorSubject<Guid?>(null);

        _cleanUp = _channelCache.Connect().Subscribe();
    }

    public IObservable<IChangeSet<ChannelState, Guid>> Connect() => _channelCache.Connect();

    public IObservable<IReadOnlyCollection<ChannelState>> Items =>
        _channelCache.Connect()
            .QueryWhenChanged(cache => cache.Items.ToList().AsReadOnly() as IReadOnlyCollection<ChannelState>);

    public IObservable<Guid?> SelectedChannelId => _selectedChannelId.AsObservable();

    public IObservable<ChannelState?> SelectedChannel =>
        _selectedChannelId
            .CombineLatest(
                _channelCache.Connect().QueryWhenChanged(),
                (selectedId, cache) =>
                {
                    if (selectedId is null) return null;
                    var lookup = cache.Lookup(selectedId.Value);
                    return lookup.HasValue ? lookup.Value : null;
                })
            .DistinctUntilChanged();

    public IObservable<IReadOnlyCollection<ChannelState>> TextChannels =>
        _channelCache.Connect()
            .QueryWhenChanged(cache =>
                cache.Items
                    .Where(c => c.Type == ChannelType.Text)
                    .OrderBy(c => c.Position)
                    .ToList()
                    .AsReadOnly() as IReadOnlyCollection<ChannelState>);

    public IObservable<IReadOnlyCollection<ChannelState>> VoiceChannels =>
        _channelCache.Connect()
            .QueryWhenChanged(cache =>
                cache.Items
                    .Where(c => c.Type == ChannelType.Voice)
                    .OrderBy(c => c.Position)
                    .ToList()
                    .AsReadOnly() as IReadOnlyCollection<ChannelState>);

    public IObservable<int> TotalUnreadCount =>
        _channelCache.Connect()
            .QueryWhenChanged(cache => cache.Items.Sum(c => c.UnreadCount))
            .DistinctUntilChanged();

    public ChannelState? GetChannel(Guid channelId)
    {
        var lookup = _channelCache.Lookup(channelId);
        return lookup.HasValue ? lookup.Value : null;
    }

    public void SetChannels(IEnumerable<ChannelResponse> channels)
    {
        _channelCache.Edit(cache =>
        {
            cache.Clear();
            foreach (var channel in channels)
            {
                cache.AddOrUpdate(MapToState(channel));
            }
        });
    }

    public void SelectChannel(Guid? channelId)
    {
        _selectedChannelId.OnNext(channelId);
    }

    public void AddChannel(ChannelResponse channel)
    {
        _channelCache.AddOrUpdate(MapToState(channel));
    }

    public void UpdateChannel(ChannelResponse channel)
    {
        var existing = _channelCache.Lookup(channel.Id);
        if (existing.HasValue)
        {
            // Preserve unread count when updating
            var updated = MapToState(channel) with { UnreadCount = existing.Value.UnreadCount };
            _channelCache.AddOrUpdate(updated);
        }
        else
        {
            _channelCache.AddOrUpdate(MapToState(channel));
        }
    }

    public void RemoveChannel(Guid channelId)
    {
        _channelCache.Remove(channelId);

        // Clear selection if removed channel was selected
        if (_selectedChannelId.Value == channelId)
        {
            _selectedChannelId.OnNext(null);
        }
    }

    public void UpdateUnreadCount(Guid channelId, int count)
    {
        var existing = _channelCache.Lookup(channelId);
        if (existing.HasValue)
        {
            _channelCache.AddOrUpdate(existing.Value with { UnreadCount = count });
        }
    }

    public void IncrementUnreadCount(Guid channelId)
    {
        var existing = _channelCache.Lookup(channelId);
        if (existing.HasValue)
        {
            _channelCache.AddOrUpdate(existing.Value with { UnreadCount = existing.Value.UnreadCount + 1 });
        }
    }

    public void ReorderChannels(IEnumerable<ChannelResponse> channels)
    {
        _channelCache.Edit(cache =>
        {
            foreach (var channel in channels)
            {
                var existing = cache.Lookup(channel.Id);
                if (existing.HasValue)
                {
                    cache.AddOrUpdate(existing.Value with { Position = channel.Position });
                }
            }
        });
    }

    public void ClearForCommunity(Guid communityId)
    {
        var toRemove = _channelCache.Items
            .Where(c => c.CommunityId == communityId)
            .Select(c => c.Id)
            .ToList();

        _channelCache.Edit(cache =>
        {
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }
        });
    }

    public void Clear()
    {
        _channelCache.Clear();
        _selectedChannelId.OnNext(null);
    }

    private static ChannelState MapToState(ChannelResponse response) =>
        new ChannelState(
            Id: response.Id,
            Name: response.Name,
            Topic: response.Topic,
            Type: response.Type,
            CommunityId: response.CommunityId,
            Position: response.Position,
            UnreadCount: response.UnreadCount,
            CreatedAt: response.CreatedAt
        );

    public void Dispose()
    {
        _cleanUp.Dispose();
        _channelCache.Dispose();
        _selectedChannelId.Dispose();
    }
}
