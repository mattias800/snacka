using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Miscord.Client.Services;
using Miscord.Shared.Models;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// ViewModel for a single video stream in the voice channel grid view.
/// Each user can have multiple streams (camera + screen share), each represented by one VideoStreamViewModel.
/// </summary>
public class VideoStreamViewModel : ReactiveObject
{
    private WriteableBitmap? _videoBitmap;
    private bool _isSpeaking;

    public VideoStreamViewModel(Guid userId, string username, VideoStreamType streamType)
    {
        UserId = userId;
        Username = username;
        StreamType = streamType;
    }

    public Guid UserId { get; }
    public string Username { get; }
    public VideoStreamType StreamType { get; }

    /// <summary>
    /// Display label shown on the tile. Empty for camera, "Screen" for screen share.
    /// </summary>
    public string StreamLabel => StreamType == VideoStreamType.ScreenShare ? "Screen" : "";

    /// <summary>
    /// Whether to show the speaking indicator. Only shows for camera streams.
    /// </summary>
    public bool ShowSpeakingIndicator => StreamType == VideoStreamType.Camera && IsSpeaking;

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSpeaking, value);
            this.RaisePropertyChanged(nameof(ShowSpeakingIndicator));
        }
    }

    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        set => this.RaiseAndSetIfChanged(ref _videoBitmap, value);
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
                Console.WriteLine($"VideoStreamVM: Error updating video frame: {ex.Message}");
            }
        });
    }
}

/// <summary>
/// Tracks participant info for managing video streams.
/// </summary>
public class ParticipantInfo
{
    public Guid UserId { get; init; }
    public string Username { get; set; } = "";
    public bool IsMuted { get; set; }
    public bool IsDeafened { get; set; }
    public bool IsCameraOn { get; set; }
    public bool IsScreenSharing { get; set; }
    public bool IsSpeaking { get; set; }
}

/// <summary>
/// ViewModel for the voice channel content view.
/// Displays a grid of video streams (one tile per camera or screen share).
/// </summary>
public class VoiceChannelContentViewModel : ReactiveObject, IDisposable
{
    private readonly IWebRtcService _webRtc;
    private readonly Guid _localUserId;
    private ChannelResponse? _channel;
    private ObservableCollection<VideoStreamViewModel> _videoStreams = new();
    private readonly Dictionary<Guid, ParticipantInfo> _participants = new();

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

    /// <summary>
    /// Collection of video streams to display. Each user may have 0, 1, or 2 streams
    /// (camera and/or screen share).
    /// </summary>
    public ObservableCollection<VideoStreamViewModel> VideoStreams
    {
        get => _videoStreams;
        set => this.RaiseAndSetIfChanged(ref _videoStreams, value);
    }

    public void SetParticipants(IEnumerable<VoiceParticipantResponse> participants)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _participants.Clear();
            VideoStreams.Clear();

            foreach (var p in participants)
            {
                var info = new ParticipantInfo
                {
                    UserId = p.UserId,
                    Username = p.Username,
                    IsMuted = p.IsMuted,
                    IsDeafened = p.IsDeafened,
                    IsCameraOn = p.IsCameraOn,
                    IsScreenSharing = p.IsScreenSharing
                };
                _participants[p.UserId] = info;

                // Create streams for active video sources
                if (p.IsCameraOn)
                {
                    VideoStreams.Add(new VideoStreamViewModel(p.UserId, p.Username, VideoStreamType.Camera));
                }
                if (p.IsScreenSharing)
                {
                    VideoStreams.Add(new VideoStreamViewModel(p.UserId, p.Username, VideoStreamType.ScreenShare));
                }
            }
        });
    }

    public void AddParticipant(VoiceParticipantResponse participant)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_participants.ContainsKey(participant.UserId))
                return;

            var info = new ParticipantInfo
            {
                UserId = participant.UserId,
                Username = participant.Username,
                IsMuted = participant.IsMuted,
                IsDeafened = participant.IsDeafened,
                IsCameraOn = participant.IsCameraOn,
                IsScreenSharing = participant.IsScreenSharing
            };
            _participants[participant.UserId] = info;

            // Create streams for active video sources
            if (participant.IsCameraOn)
            {
                VideoStreams.Add(new VideoStreamViewModel(participant.UserId, participant.Username, VideoStreamType.Camera));
            }
            if (participant.IsScreenSharing)
            {
                VideoStreams.Add(new VideoStreamViewModel(participant.UserId, participant.Username, VideoStreamType.ScreenShare));
            }
        });
    }

    public void RemoveParticipant(Guid userId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _participants.Remove(userId);

            // Remove all streams for this user
            var toRemove = VideoStreams.Where(s => s.UserId == userId).ToList();
            foreach (var stream in toRemove)
            {
                VideoStreams.Remove(stream);
            }
        });
    }

    public void UpdateParticipantState(Guid userId, VoiceStateUpdate state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_participants.TryGetValue(userId, out var info))
                return;

            var username = info.Username;

            // Handle camera state change
            if (state.IsCameraOn.HasValue)
            {
                var wasCameraOn = info.IsCameraOn;
                info.IsCameraOn = state.IsCameraOn.Value;

                if (state.IsCameraOn.Value && !wasCameraOn)
                {
                    // Camera turned on - add stream
                    if (!VideoStreams.Any(s => s.UserId == userId && s.StreamType == VideoStreamType.Camera))
                    {
                        VideoStreams.Add(new VideoStreamViewModel(userId, username, VideoStreamType.Camera));
                    }
                }
                else if (!state.IsCameraOn.Value && wasCameraOn)
                {
                    // Camera turned off - remove stream
                    var stream = VideoStreams.FirstOrDefault(s => s.UserId == userId && s.StreamType == VideoStreamType.Camera);
                    if (stream != null)
                    {
                        VideoStreams.Remove(stream);
                    }
                }
            }

            // Handle screen share state change
            if (state.IsScreenSharing.HasValue)
            {
                var wasScreenSharing = info.IsScreenSharing;
                info.IsScreenSharing = state.IsScreenSharing.Value;

                if (state.IsScreenSharing.Value && !wasScreenSharing)
                {
                    // Screen share turned on - add stream
                    if (!VideoStreams.Any(s => s.UserId == userId && s.StreamType == VideoStreamType.ScreenShare))
                    {
                        VideoStreams.Add(new VideoStreamViewModel(userId, username, VideoStreamType.ScreenShare));
                    }
                }
                else if (!state.IsScreenSharing.Value && wasScreenSharing)
                {
                    // Screen share turned off - remove stream
                    var stream = VideoStreams.FirstOrDefault(s => s.UserId == userId && s.StreamType == VideoStreamType.ScreenShare);
                    if (stream != null)
                    {
                        VideoStreams.Remove(stream);
                    }
                }
            }

            // Update other state
            if (state.IsMuted.HasValue)
                info.IsMuted = state.IsMuted.Value;
            if (state.IsDeafened.HasValue)
                info.IsDeafened = state.IsDeafened.Value;
        });
    }

    public void UpdateSpeakingState(Guid userId, bool isSpeaking)
    {
        // Only update speaking state on camera streams
        var cameraStream = VideoStreams.FirstOrDefault(s => s.UserId == userId && s.StreamType == VideoStreamType.Camera);
        if (cameraStream != null)
        {
            cameraStream.IsSpeaking = isSpeaking;
        }

        if (_participants.TryGetValue(userId, out var info))
        {
            info.IsSpeaking = isSpeaking;
        }
    }

    private void OnVideoFrameReceived(Guid userId, VideoStreamType streamType, int width, int height, byte[] rgbData)
    {
        var stream = VideoStreams.FirstOrDefault(s => s.UserId == userId && s.StreamType == streamType);
        stream?.UpdateVideoFrame(rgbData, width, height);
    }

    private void OnLocalVideoFrameCaptured(VideoStreamType streamType, int width, int height, byte[] rgbData)
    {
        // Update local user's video preview for the appropriate stream
        var stream = VideoStreams.FirstOrDefault(s => s.UserId == _localUserId && s.StreamType == streamType);
        stream?.UpdateVideoFrame(rgbData, width, height);
    }

    public void Dispose()
    {
        _webRtc.VideoFrameReceived -= OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured -= OnLocalVideoFrameCaptured;
    }
}
