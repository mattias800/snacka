using DynamicData;

namespace Snacka.Client.Stores;

/// <summary>
/// Base interface for all reactive stores using DynamicData's SourceCache pattern.
/// Provides a consistent API for state management across the application.
/// </summary>
/// <typeparam name="TItem">The type of item stored in the cache</typeparam>
/// <typeparam name="TKey">The type of the unique key for each item</typeparam>
public interface IStore<TItem, TKey> where TItem : notnull where TKey : notnull
{
    /// <summary>
    /// Observable stream of changes to the store. Subscribe to receive real-time updates.
    /// </summary>
    IObservable<IChangeSet<TItem, TKey>> Connect();

    /// <summary>
    /// Observable of all current items in the store.
    /// </summary>
    IObservable<IReadOnlyCollection<TItem>> Items { get; }
}
