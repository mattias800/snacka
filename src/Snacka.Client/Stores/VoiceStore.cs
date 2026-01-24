using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;
using Snacka.Client.Services;

namespace Snacka.Client.Stores;

// Note: Uses Snacka.Client.Services.VoiceConnectionStatus

/// <summary>
/// Immutable state representing a voice participant.
/// </summary>
public record VoiceParticipantState(
    Guid Id,
    Guid UserId,
    string Username,
    Guid ChannelId,
    bool IsMuted,
    bool IsDeafened,
    bool IsServerMuted,
    bool IsServerDeafened,
    bool IsSpeaking,
    bool IsScreenSharing,
    bool ScreenShareHasAudio,
    bool IsCameraOn,
    DateTime JoinedAt,
    bool IsGamingStation = false,
    string? GamingStationMachineId = null
);

/// <summary>
/// Store managing voice channel state including participants and local user state.
/// </summary>
public interface IVoiceStore : IStore<VoiceParticipantState, Guid>
{
    /// <summary>
    /// Current voice channel ID the local user is connected to.
    /// </summary>
    IObservable<Guid?> CurrentChannelId { get; }

    /// <summary>
    /// Voice connection status.
    /// </summary>
    IObservable<VoiceConnectionStatus> ConnectionStatus { get; }

    /// <summary>
    /// Participants in the current voice channel.
    /// </summary>
    IObservable<IReadOnlyCollection<VoiceParticipantState>> CurrentChannelParticipants { get; }

    /// <summary>
    /// Local user's muted state.
    /// </summary>
    IObservable<bool> IsMuted { get; }

    /// <summary>
    /// Local user's deafened state.
    /// </summary>
    IObservable<bool> IsDeafened { get; }

    /// <summary>
    /// Local user's camera state.
    /// </summary>
    IObservable<bool> IsCameraOn { get; }

    /// <summary>
    /// Local user's screen sharing state.
    /// </summary>
    IObservable<bool> IsScreenSharing { get; }

    /// <summary>
    /// Local user's speaking state.
    /// </summary>
    IObservable<bool> IsSpeaking { get; }

    /// <summary>
    /// Multi-device: voice session active on another device.
    /// </summary>
    IObservable<(Guid? ChannelId, string? ChannelName)> VoiceOnOtherDevice { get; }

    /// <summary>
    /// Gets participants for a specific channel.
    /// </summary>
    IReadOnlyList<VoiceParticipantState> GetParticipantsForChannel(Guid channelId);

    /// <summary>
    /// Gets the local user's participant state if connected.
    /// </summary>
    VoiceParticipantState? GetLocalParticipant(Guid localUserId);

    // Actions
    void SetCurrentChannel(Guid? channelId);
    void SetConnectionStatus(VoiceConnectionStatus status);
    void SetParticipants(Guid channelId, IEnumerable<VoiceParticipantResponse> participants);
    void AddParticipant(VoiceParticipantResponse participant);
    void RemoveParticipant(Guid channelId, Guid participantId);
    void UpdateVoiceState(Guid channelId, Guid userId, VoiceStateUpdate update);
    void UpdateSpeakingState(Guid channelId, Guid userId, bool isSpeaking);
    void UpdateServerVoiceState(Guid channelId, Guid userId, bool? isServerMuted, bool? isServerDeafened);
    void SetLocalMuted(bool muted);
    void SetLocalDeafened(bool deafened);
    void SetLocalCameraOn(bool cameraOn);
    void SetLocalScreenSharing(bool sharing);
    void SetLocalSpeaking(bool speaking);
    void SetVoiceOnOtherDevice(Guid? channelId, string? channelName);
    void ClearChannel(Guid channelId);
    void Clear();
}

public sealed class VoiceStore : IVoiceStore, IDisposable
{
    private readonly SourceCache<VoiceParticipantState, Guid> _participantCache;
    private readonly BehaviorSubject<Guid?> _currentChannelId;
    private readonly BehaviorSubject<VoiceConnectionStatus> _connectionStatus;
    private readonly BehaviorSubject<bool> _isMuted;
    private readonly BehaviorSubject<bool> _isDeafened;
    private readonly BehaviorSubject<bool> _isCameraOn;
    private readonly BehaviorSubject<bool> _isScreenSharing;
    private readonly BehaviorSubject<bool> _isSpeaking;
    private readonly BehaviorSubject<(Guid? ChannelId, string? ChannelName)> _voiceOnOtherDevice;
    private readonly IDisposable _cleanUp;

    public VoiceStore()
    {
        _participantCache = new SourceCache<VoiceParticipantState, Guid>(p => p.Id);
        _currentChannelId = new BehaviorSubject<Guid?>(null);
        _connectionStatus = new BehaviorSubject<VoiceConnectionStatus>(VoiceConnectionStatus.Disconnected);
        _isMuted = new BehaviorSubject<bool>(false);
        _isDeafened = new BehaviorSubject<bool>(false);
        _isCameraOn = new BehaviorSubject<bool>(false);
        _isScreenSharing = new BehaviorSubject<bool>(false);
        _isSpeaking = new BehaviorSubject<bool>(false);
        _voiceOnOtherDevice = new BehaviorSubject<(Guid?, string?)>((null, null));

        _cleanUp = _participantCache.Connect().Subscribe();
    }

    public IObservable<IChangeSet<VoiceParticipantState, Guid>> Connect() => _participantCache.Connect();

    public IObservable<IReadOnlyCollection<VoiceParticipantState>> Items =>
        _participantCache.Connect()
            .QueryWhenChanged(cache => cache.Items.ToList().AsReadOnly() as IReadOnlyCollection<VoiceParticipantState>);

    public IObservable<Guid?> CurrentChannelId => _currentChannelId.AsObservable();

    public IObservable<VoiceConnectionStatus> ConnectionStatus => _connectionStatus.AsObservable();

    public IObservable<IReadOnlyCollection<VoiceParticipantState>> CurrentChannelParticipants =>
        _currentChannelId
            .CombineLatest(
                _participantCache.Connect().QueryWhenChanged(),
                (channelId, cache) =>
                {
                    if (channelId is null)
                        return Array.Empty<VoiceParticipantState>() as IReadOnlyCollection<VoiceParticipantState>;

                    return cache.Items
                        .Where(p => p.ChannelId == channelId.Value)
                        .OrderBy(p => p.JoinedAt)
                        .ToList()
                        .AsReadOnly() as IReadOnlyCollection<VoiceParticipantState>;
                });

    public IObservable<bool> IsMuted => _isMuted.AsObservable();
    public IObservable<bool> IsDeafened => _isDeafened.AsObservable();
    public IObservable<bool> IsCameraOn => _isCameraOn.AsObservable();
    public IObservable<bool> IsScreenSharing => _isScreenSharing.AsObservable();
    public IObservable<bool> IsSpeaking => _isSpeaking.AsObservable();
    public IObservable<(Guid? ChannelId, string? ChannelName)> VoiceOnOtherDevice => _voiceOnOtherDevice.AsObservable();

    public IReadOnlyList<VoiceParticipantState> GetParticipantsForChannel(Guid channelId)
    {
        return _participantCache.Items
            .Where(p => p.ChannelId == channelId)
            .OrderBy(p => p.JoinedAt)
            .ToList()
            .AsReadOnly();
    }

    public VoiceParticipantState? GetLocalParticipant(Guid localUserId)
    {
        var channelId = _currentChannelId.Value;
        if (channelId is null) return null;

        return _participantCache.Items.FirstOrDefault(p => p.ChannelId == channelId.Value && p.UserId == localUserId);
    }

    public void SetCurrentChannel(Guid? channelId)
    {
        _currentChannelId.OnNext(channelId);

        if (channelId is null)
        {
            _connectionStatus.OnNext(VoiceConnectionStatus.Disconnected);
        }
    }

    public void SetConnectionStatus(VoiceConnectionStatus status)
    {
        _connectionStatus.OnNext(status);
    }

    public void SetParticipants(Guid channelId, IEnumerable<VoiceParticipantResponse> participants)
    {
        _participantCache.Edit(cache =>
        {
            // Remove existing participants for this channel
            var toRemove = cache.Items.Where(p => p.ChannelId == channelId).Select(p => p.Id).ToList();
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }

            // Add new participants
            foreach (var participant in participants)
            {
                cache.AddOrUpdate(MapToState(participant));
            }
        });
    }

    public void AddParticipant(VoiceParticipantResponse participant)
    {
        _participantCache.AddOrUpdate(MapToState(participant));
    }

    public void RemoveParticipant(Guid channelId, Guid participantId)
    {
        // Find by channel and user ID
        var participant = _participantCache.Items.FirstOrDefault(p => p.ChannelId == channelId && p.UserId == participantId);
        if (participant is not null)
        {
            _participantCache.Remove(participant.Id);
        }
    }

    public void UpdateVoiceState(Guid channelId, Guid userId, VoiceStateUpdate update)
    {
        var existing = _participantCache.Items.FirstOrDefault(p => p.ChannelId == channelId && p.UserId == userId);
        if (existing is not null)
        {
            var updated = existing with
            {
                IsMuted = update.IsMuted ?? existing.IsMuted,
                IsDeafened = update.IsDeafened ?? existing.IsDeafened,
                IsScreenSharing = update.IsScreenSharing ?? existing.IsScreenSharing,
                ScreenShareHasAudio = update.ScreenShareHasAudio ?? existing.ScreenShareHasAudio,
                IsCameraOn = update.IsCameraOn ?? existing.IsCameraOn
            };
            _participantCache.AddOrUpdate(updated);
        }
    }

    public void UpdateSpeakingState(Guid channelId, Guid userId, bool isSpeaking)
    {
        var existing = _participantCache.Items.FirstOrDefault(p => p.ChannelId == channelId && p.UserId == userId);
        if (existing is not null)
        {
            _participantCache.AddOrUpdate(existing with { IsSpeaking = isSpeaking });
        }
    }

    public void UpdateServerVoiceState(Guid channelId, Guid userId, bool? isServerMuted, bool? isServerDeafened)
    {
        var existing = _participantCache.Items.FirstOrDefault(p => p.ChannelId == channelId && p.UserId == userId);
        if (existing is not null)
        {
            _participantCache.AddOrUpdate(existing with
            {
                IsServerMuted = isServerMuted ?? existing.IsServerMuted,
                IsServerDeafened = isServerDeafened ?? existing.IsServerDeafened
            });
        }
    }

    public void SetLocalMuted(bool muted)
    {
        _isMuted.OnNext(muted);
    }

    public void SetLocalDeafened(bool deafened)
    {
        _isDeafened.OnNext(deafened);
    }

    public void SetLocalCameraOn(bool cameraOn)
    {
        _isCameraOn.OnNext(cameraOn);
    }

    public void SetLocalScreenSharing(bool sharing)
    {
        _isScreenSharing.OnNext(sharing);
    }

    public void SetLocalSpeaking(bool speaking)
    {
        _isSpeaking.OnNext(speaking);
    }

    public void SetVoiceOnOtherDevice(Guid? channelId, string? channelName)
    {
        _voiceOnOtherDevice.OnNext((channelId, channelName));
    }

    public void ClearChannel(Guid channelId)
    {
        var toRemove = _participantCache.Items.Where(p => p.ChannelId == channelId).Select(p => p.Id).ToList();
        _participantCache.Edit(cache =>
        {
            foreach (var id in toRemove)
            {
                cache.Remove(id);
            }
        });
    }

    public void Clear()
    {
        _participantCache.Clear();
        _currentChannelId.OnNext(null);
        _connectionStatus.OnNext(VoiceConnectionStatus.Disconnected);
        _isMuted.OnNext(false);
        _isDeafened.OnNext(false);
        _isCameraOn.OnNext(false);
        _isScreenSharing.OnNext(false);
        _isSpeaking.OnNext(false);
        _voiceOnOtherDevice.OnNext((null, null));
    }

    private static VoiceParticipantState MapToState(VoiceParticipantResponse response) =>
        new VoiceParticipantState(
            Id: response.Id,
            UserId: response.UserId,
            Username: response.Username,
            ChannelId: response.ChannelId,
            IsMuted: response.IsMuted,
            IsDeafened: response.IsDeafened,
            IsServerMuted: response.IsServerMuted,
            IsServerDeafened: response.IsServerDeafened,
            IsSpeaking: false,
            IsScreenSharing: response.IsScreenSharing,
            ScreenShareHasAudio: response.ScreenShareHasAudio,
            IsCameraOn: response.IsCameraOn,
            JoinedAt: response.JoinedAt,
            IsGamingStation: response.IsGamingStation,
            GamingStationMachineId: response.GamingStationMachineId
        );

    public void Dispose()
    {
        _cleanUp.Dispose();
        _participantCache.Dispose();
        _currentChannelId.Dispose();
        _connectionStatus.Dispose();
        _isMuted.Dispose();
        _isDeafened.Dispose();
        _isCameraOn.Dispose();
        _isScreenSharing.Dispose();
        _isSpeaking.Dispose();
        _voiceOnOtherDevice.Dispose();
    }
}
