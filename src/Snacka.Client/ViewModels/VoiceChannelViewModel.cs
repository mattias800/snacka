using System.Collections.ObjectModel;
using Snacka.Client.Services;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

/// <summary>
/// Wrapper for VoiceParticipantResponse that adds reactive IsSpeaking state and per-user volume control.
/// </summary>
public class VoiceParticipantViewModel : ReactiveObject
{
    private bool _isSpeaking;
    private float _volume = 1.0f;
    private readonly Guid _currentUserId;
    private readonly Action<Guid, float>? _onVolumeChanged;

    public VoiceParticipantViewModel(VoiceParticipantResponse participant, Guid currentUserId, float initialVolume = 1.0f, Action<Guid, float>? onVolumeChanged = null)
    {
        Participant = participant;
        _currentUserId = currentUserId;
        _volume = Math.Clamp(initialVolume, 0f, 2f);
        _onVolumeChanged = onVolumeChanged;
    }

    public VoiceParticipantResponse Participant { get; private set; }

    public Guid UserId => Participant.UserId;
    public string Username => Participant.Username;
    public bool IsMuted => Participant.IsMuted;
    public bool IsDeafened => Participant.IsDeafened;
    public bool IsServerMuted => Participant.IsServerMuted;
    public bool IsServerDeafened => Participant.IsServerDeafened;
    public bool IsCameraOn => Participant.IsCameraOn;
    public bool IsScreenSharing => Participant.IsScreenSharing;

    /// <summary>
    /// Whether the user is effectively muted (self-muted OR server-muted).
    /// </summary>
    public bool IsEffectivelyMuted => IsMuted || IsServerMuted;

    /// <summary>
    /// Whether the user is effectively deafened (self-deafened OR server-deafened).
    /// </summary>
    public bool IsEffectivelyDeafened => IsDeafened || IsServerDeafened;

    /// <summary>
    /// Whether this participant is the current user.
    /// </summary>
    public bool IsCurrentUser => UserId == _currentUserId;

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set => this.RaiseAndSetIfChanged(ref _isSpeaking, value);
    }

    /// <summary>
    /// Volume level for this user (0.0 to 2.0, where 1.0 is 100%).
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0f, 2f);
            if (Math.Abs(_volume - clamped) > 0.001f)
            {
                this.RaiseAndSetIfChanged(ref _volume, clamped);
                _onVolumeChanged?.Invoke(UserId, clamped);
                this.RaisePropertyChanged(nameof(VolumePercent));
            }
        }
    }

    /// <summary>
    /// Volume as a percentage (0 to 200).
    /// </summary>
    public int VolumePercent
    {
        get => (int)(_volume * 100);
        set => Volume = value / 100f;
    }

    public void UpdateState(VoiceStateUpdate state)
    {
        Participant = Participant with
        {
            IsMuted = state.IsMuted ?? Participant.IsMuted,
            IsDeafened = state.IsDeafened ?? Participant.IsDeafened,
            IsScreenSharing = state.IsScreenSharing ?? Participant.IsScreenSharing,
            IsCameraOn = state.IsCameraOn ?? Participant.IsCameraOn
        };
        this.RaisePropertyChanged(nameof(IsMuted));
        this.RaisePropertyChanged(nameof(IsDeafened));
        this.RaisePropertyChanged(nameof(IsCameraOn));
        this.RaisePropertyChanged(nameof(IsScreenSharing));
        this.RaisePropertyChanged(nameof(IsEffectivelyMuted));
        this.RaisePropertyChanged(nameof(IsEffectivelyDeafened));
    }

    /// <summary>
    /// Updates the server-imposed mute/deafen state (admin action).
    /// </summary>
    public void UpdateServerState(bool? isServerMuted, bool? isServerDeafened)
    {
        Participant = Participant with
        {
            IsServerMuted = isServerMuted ?? Participant.IsServerMuted,
            IsServerDeafened = isServerDeafened ?? Participant.IsServerDeafened
        };
        this.RaisePropertyChanged(nameof(IsServerMuted));
        this.RaisePropertyChanged(nameof(IsServerDeafened));
        this.RaisePropertyChanged(nameof(IsEffectivelyMuted));
        this.RaisePropertyChanged(nameof(IsEffectivelyDeafened));
    }
}

/// <summary>
/// Wrapper ViewModel for voice channels that provides reactive participant tracking.
/// This ensures proper UI updates when participants join/leave.
/// </summary>
public class VoiceChannelViewModel : ReactiveObject
{
    private readonly ChannelResponse _channel;
    private readonly Guid _currentUserId;
    private readonly Action<Guid, float>? _onVolumeChanged;
    private readonly Func<Guid, float>? _getInitialVolume;
    private int _position;
    private bool _showGapAbove;
    private bool _showGapBelow;
    private bool _isDragSource;

    public VoiceChannelViewModel(ChannelResponse channel, Guid currentUserId = default, Action<Guid, float>? onVolumeChanged = null, Func<Guid, float>? getInitialVolume = null)
    {
        _channel = channel;
        _currentUserId = currentUserId;
        _onVolumeChanged = onVolumeChanged;
        _getInitialVolume = getInitialVolume;
        _position = channel.Position;
        Participants = new ObservableCollection<VoiceParticipantViewModel>();
    }

    public Guid Id => _channel.Id;
    public string Name => _channel.Name;
    public ChannelResponse Channel => _channel;

    public int Position
    {
        get => _position;
        set => this.RaiseAndSetIfChanged(ref _position, value);
    }

    // Drag preview state
    public bool ShowGapAbove
    {
        get => _showGapAbove;
        set => this.RaiseAndSetIfChanged(ref _showGapAbove, value);
    }

    public bool ShowGapBelow
    {
        get => _showGapBelow;
        set => this.RaiseAndSetIfChanged(ref _showGapBelow, value);
    }

    public bool IsDragSource
    {
        get => _isDragSource;
        set => this.RaiseAndSetIfChanged(ref _isDragSource, value);
    }

    public ObservableCollection<VoiceParticipantViewModel> Participants { get; }

    public void AddParticipant(VoiceParticipantResponse participant)
    {
        if (!Participants.Any(p => p.UserId == participant.UserId))
        {
            Console.WriteLine($"VoiceChannelVM [{Name}]: Adding participant {participant.Username}");
            var initialVolume = _getInitialVolume?.Invoke(participant.UserId) ?? 1.0f;
            Participants.Add(new VoiceParticipantViewModel(participant, _currentUserId, initialVolume, _onVolumeChanged));
        }
        else
        {
            Console.WriteLine($"VoiceChannelVM [{Name}]: Participant {participant.Username} already exists");
        }
    }

    public void RemoveParticipant(Guid userId)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant is not null)
        {
            Console.WriteLine($"VoiceChannelVM [{Name}]: Removing participant {participant.Username}");
            Participants.Remove(participant);
        }
        else
        {
            Console.WriteLine($"VoiceChannelVM [{Name}]: Participant with ID {userId} not found");
        }
    }

    public void UpdateParticipantState(Guid userId, VoiceStateUpdate state)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant is not null)
        {
            participant.UpdateState(state);
            Console.WriteLine($"VoiceChannelVM [{Name}]: Updated state for {participant.Username}");
        }
    }

    public void UpdateSpeakingState(Guid userId, bool isSpeaking)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant is not null)
        {
            participant.IsSpeaking = isSpeaking;
        }
    }

    public void UpdateServerState(Guid userId, bool? isServerMuted, bool? isServerDeafened)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant is not null)
        {
            participant.UpdateServerState(isServerMuted, isServerDeafened);
            Console.WriteLine($"VoiceChannelVM [{Name}]: Updated server state for {participant.Username} - serverMuted={isServerMuted}, serverDeafened={isServerDeafened}");
        }
    }

    public void SetParticipants(IEnumerable<VoiceParticipantResponse> participants)
    {
        Console.WriteLine($"VoiceChannelVM [{Name}]: Setting {participants.Count()} participants");
        Participants.Clear();
        foreach (var p in participants)
        {
            var initialVolume = _getInitialVolume?.Invoke(p.UserId) ?? 1.0f;
            Participants.Add(new VoiceParticipantViewModel(p, _currentUserId, initialVolume, _onVolumeChanged));
        }
    }
}
