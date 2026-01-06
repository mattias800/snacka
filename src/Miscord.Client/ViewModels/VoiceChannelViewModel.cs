using System.Collections.ObjectModel;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// Wrapper for VoiceParticipantResponse that adds reactive IsSpeaking state.
/// </summary>
public class VoiceParticipantViewModel : ReactiveObject
{
    private bool _isSpeaking;

    public VoiceParticipantViewModel(VoiceParticipantResponse participant)
    {
        Participant = participant;
    }

    public VoiceParticipantResponse Participant { get; private set; }

    public Guid UserId => Participant.UserId;
    public string Username => Participant.Username;
    public bool IsMuted => Participant.IsMuted;
    public bool IsDeafened => Participant.IsDeafened;
    public bool IsCameraOn => Participant.IsCameraOn;

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set => this.RaiseAndSetIfChanged(ref _isSpeaking, value);
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
    }
}

/// <summary>
/// Wrapper ViewModel for voice channels that provides reactive participant tracking.
/// This ensures proper UI updates when participants join/leave.
/// </summary>
public class VoiceChannelViewModel : ReactiveObject
{
    private readonly ChannelResponse _channel;

    public VoiceChannelViewModel(ChannelResponse channel)
    {
        _channel = channel;
        Participants = new ObservableCollection<VoiceParticipantViewModel>();
    }

    public Guid Id => _channel.Id;
    public string Name => _channel.Name;
    public ChannelResponse Channel => _channel;

    public ObservableCollection<VoiceParticipantViewModel> Participants { get; }

    public void AddParticipant(VoiceParticipantResponse participant)
    {
        if (!Participants.Any(p => p.UserId == participant.UserId))
        {
            Console.WriteLine($"VoiceChannelVM [{Name}]: Adding participant {participant.Username}");
            Participants.Add(new VoiceParticipantViewModel(participant));
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

    public void SetParticipants(IEnumerable<VoiceParticipantResponse> participants)
    {
        Console.WriteLine($"VoiceChannelVM [{Name}]: Setting {participants.Count()} participants");
        Participants.Clear();
        foreach (var p in participants)
        {
            Participants.Add(new VoiceParticipantViewModel(p));
        }
    }
}
