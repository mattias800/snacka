using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// ViewModel for a participant in the voice channel grid view.
/// Holds video frame data if camera is enabled.
/// </summary>
public class VideoParticipantViewModel : ReactiveObject
{
    private WriteableBitmap? _videoBitmap;
    private bool _isSpeaking;

    public VideoParticipantViewModel(VoiceParticipantResponse participant)
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

    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        set => this.RaiseAndSetIfChanged(ref _videoBitmap, value);
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

    public void UpdateVideoFrame(byte[] rgbData, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgbData.Length < width * height * 3)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Create new bitmap for each frame (Avalonia needs new object to detect changes)
                var bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);

                using (var buffer = bitmap.Lock())
                {
                    // Convert RGB to BGRA
                    var pixelCount = width * height;
                    var bgraData = new byte[pixelCount * 4];

                    for (int i = 0; i < pixelCount; i++)
                    {
                        var srcIndex = i * 3;
                        var destIndex = i * 4;
                        bgraData[destIndex] = rgbData[srcIndex + 2];     // B
                        bgraData[destIndex + 1] = rgbData[srcIndex + 1]; // G
                        bgraData[destIndex + 2] = rgbData[srcIndex];     // R
                        bgraData[destIndex + 3] = 255;                   // A
                    }

                    System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, buffer.Address, bgraData.Length);
                }

                VideoBitmap = bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VideoParticipantVM: Error updating video frame: {ex.Message}");
            }
        });
    }
}

/// <summary>
/// ViewModel for the voice channel content view.
/// Displays a grid of participants with video streams.
/// </summary>
public class VoiceChannelContentViewModel : ReactiveObject, IDisposable
{
    private readonly IWebRtcService _webRtc;
    private readonly Guid _localUserId;
    private ChannelResponse? _channel;
    private ObservableCollection<VideoParticipantViewModel> _participants = new();

    public VoiceChannelContentViewModel(IWebRtcService webRtc, Guid localUserId)
    {
        _webRtc = webRtc;
        _localUserId = localUserId;

        // Subscribe to video frames from WebRTC
        _webRtc.VideoFrameReceived += OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured += OnLocalVideoFrameCaptured;
    }

    public ChannelResponse? Channel
    {
        get => _channel;
        set
        {
            this.RaiseAndSetIfChanged(ref _channel, value);
            this.RaisePropertyChanged(nameof(ChannelName));
            this.RaisePropertyChanged(nameof(HasChannel));
        }
    }

    public string ChannelName => Channel?.Name ?? "";
    public bool HasChannel => Channel != null;

    public ObservableCollection<VideoParticipantViewModel> Participants
    {
        get => _participants;
        set => this.RaiseAndSetIfChanged(ref _participants, value);
    }

    public void SetParticipants(IEnumerable<VoiceParticipantResponse> participants)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Participants.Clear();
            foreach (var p in participants)
            {
                Participants.Add(new VideoParticipantViewModel(p));
            }
        });
    }

    public void AddParticipant(VoiceParticipantResponse participant)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!Participants.Any(p => p.UserId == participant.UserId))
            {
                Participants.Add(new VideoParticipantViewModel(participant));
            }
        });
    }

    public void RemoveParticipant(Guid userId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var participant = Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant != null)
            {
                Participants.Remove(participant);
            }
        });
    }

    public void UpdateParticipantState(Guid userId, VoiceStateUpdate state)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        participant?.UpdateState(state);
    }

    public void UpdateSpeakingState(Guid userId, bool isSpeaking)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant != null)
        {
            participant.IsSpeaking = isSpeaking;
        }
    }

    private void OnVideoFrameReceived(Guid userId, int width, int height, byte[] rgbData)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        participant?.UpdateVideoFrame(rgbData, width, height);
    }

    private void OnLocalVideoFrameCaptured(int width, int height, byte[] rgbData)
    {
        // Update local user's video preview
        var participant = Participants.FirstOrDefault(p => p.UserId == _localUserId);
        participant?.UpdateVideoFrame(rgbData, width, height);
    }

    public void Dispose()
    {
        _webRtc.VideoFrameReceived -= OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured -= OnLocalVideoFrameCaptured;
    }
}
