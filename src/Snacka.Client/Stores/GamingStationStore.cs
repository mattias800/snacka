using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Snacka.Client.Models;

namespace Snacka.Client.Stores;

/// <summary>
/// Store managing gaming station state for the current user's machines.
/// </summary>
public interface IGamingStationStore : IStore<MyGamingStationInfo, string>
{
    /// <summary>
    /// All gaming stations owned by the current user.
    /// </summary>
    IObservable<IReadOnlyCollection<MyGamingStationInfo>> MyStations { get; }

    /// <summary>
    /// The current machine's gaming station info (if this machine is a gaming station).
    /// </summary>
    IObservable<MyGamingStationInfo?> CurrentMachine { get; }

    /// <summary>
    /// The current machine's unique identifier.
    /// </summary>
    string CurrentMachineId { get; }

    /// <summary>
    /// Gets a station by machine ID synchronously.
    /// </summary>
    MyGamingStationInfo? GetStation(string machineId);

    /// <summary>
    /// Gets available stations (not current machine, available for use).
    /// </summary>
    IReadOnlyList<MyGamingStationInfo> GetAvailableStations();

    // Actions
    void SetCurrentMachineId(string machineId);
    void SetStations(IEnumerable<MyGamingStationInfo> stations);
    void AddOrUpdateStation(MyGamingStationInfo station);
    void UpdateStationStatus(string machineId, bool isAvailable, bool isInVoice, Guid? currentChannelId, bool isScreenSharing);
    void RemoveStation(string machineId);
    void Clear();
}

public sealed class GamingStationStore : IGamingStationStore, IDisposable
{
    private readonly SourceCache<MyGamingStationInfo, string> _stationCache;
    private readonly BehaviorSubject<string> _currentMachineId;
    private readonly IDisposable _cleanUp;

    public GamingStationStore()
    {
        _stationCache = new SourceCache<MyGamingStationInfo, string>(s => s.MachineId);
        _currentMachineId = new BehaviorSubject<string>(string.Empty);

        _cleanUp = _stationCache.Connect().Subscribe();
    }

    public IObservable<IChangeSet<MyGamingStationInfo, string>> Connect() => _stationCache.Connect();

    public IObservable<IReadOnlyCollection<MyGamingStationInfo>> Items =>
        _stationCache.Connect()
            .QueryWhenChanged(cache => cache.Items.ToList().AsReadOnly() as IReadOnlyCollection<MyGamingStationInfo>);

    public IObservable<IReadOnlyCollection<MyGamingStationInfo>> MyStations =>
        _stationCache.Connect()
            .QueryWhenChanged(cache =>
                cache.Items
                    .OrderBy(s => s.DisplayName)
                    .ToList()
                    .AsReadOnly() as IReadOnlyCollection<MyGamingStationInfo>);

    public IObservable<MyGamingStationInfo?> CurrentMachine =>
        _currentMachineId
            .CombineLatest(
                _stationCache.Connect().QueryWhenChanged(),
                (machineId, cache) =>
                {
                    if (string.IsNullOrEmpty(machineId)) return null;
                    var lookup = cache.Lookup(machineId);
                    return lookup.HasValue ? lookup.Value : null;
                })
            .DistinctUntilChanged();

    public string CurrentMachineId => _currentMachineId.Value;

    public MyGamingStationInfo? GetStation(string machineId)
    {
        var lookup = _stationCache.Lookup(machineId);
        return lookup.HasValue ? lookup.Value : null;
    }

    public IReadOnlyList<MyGamingStationInfo> GetAvailableStations()
    {
        var currentId = _currentMachineId.Value;
        return _stationCache.Items
            .Where(s => s.MachineId != currentId && s.IsAvailable)
            .OrderBy(s => s.DisplayName)
            .ToList()
            .AsReadOnly();
    }

    public void SetCurrentMachineId(string machineId)
    {
        _currentMachineId.OnNext(machineId);
    }

    public void SetStations(IEnumerable<MyGamingStationInfo> stations)
    {
        var currentId = _currentMachineId.Value;

        _stationCache.Edit(cache =>
        {
            cache.Clear();
            foreach (var station in stations)
            {
                // Mark current machine
                var stationWithCurrentFlag = station.IsCurrentMachine
                    ? station
                    : station with { IsCurrentMachine = station.MachineId == currentId };
                cache.AddOrUpdate(stationWithCurrentFlag);
            }
        });
    }

    public void AddOrUpdateStation(MyGamingStationInfo station)
    {
        var currentId = _currentMachineId.Value;
        var stationWithCurrentFlag = station with { IsCurrentMachine = station.MachineId == currentId };
        _stationCache.AddOrUpdate(stationWithCurrentFlag);
    }

    public void UpdateStationStatus(string machineId, bool isAvailable, bool isInVoice, Guid? currentChannelId, bool isScreenSharing)
    {
        var existing = _stationCache.Lookup(machineId);
        if (existing.HasValue)
        {
            _stationCache.AddOrUpdate(existing.Value with
            {
                IsAvailable = isAvailable,
                IsInVoiceChannel = isInVoice,
                CurrentChannelId = currentChannelId,
                IsScreenSharing = isScreenSharing
            });
        }
    }

    public void RemoveStation(string machineId)
    {
        _stationCache.Remove(machineId);
    }

    public void Clear()
    {
        _stationCache.Clear();
    }

    public void Dispose()
    {
        _cleanUp.Dispose();
        _stationCache.Dispose();
        _currentMachineId.Dispose();
    }
}
