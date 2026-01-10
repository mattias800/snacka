using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Miscord.Client.Services;
using Miscord.Client.Services.HardwareVideo;
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
    private bool _isWatching;
    private IHardwareVideoDecoder? _hardwareDecoder;
    private readonly Guid _localUserId;

    public VideoStreamViewModel(Guid userId, string username, VideoStreamType streamType, Guid localUserId)
    {
        UserId = userId;
        Username = username;
        StreamType = streamType;
        _localUserId = localUserId;

        // Local user's streams are always "watching" (they see their own preview)
        // Camera streams from remote users are auto-watched (no opt-in needed)
        // Remote screen shares require explicit opt-in
        _isWatching = !IsRemoteScreenShare;
    }

    public Guid UserId { get; }
    public string Username { get; }
    public VideoStreamType StreamType { get; }

    /// <summary>
    /// Whether this is a screen share from a remote user (not the local user).
    /// Remote screen shares require opt-in to view.
    /// </summary>
    public bool IsRemoteScreenShare => StreamType == VideoStreamType.ScreenShare && UserId != _localUserId;

    /// <summary>
    /// Whether the local user has opted in to watch this stream.
    /// Always true for local streams and remote camera streams.
    /// For remote screen shares, this is false until the user clicks "Watch".
    /// </summary>
    public bool IsWatching
    {
        get => _isWatching;
        set
        {
            this.RaiseAndSetIfChanged(ref _isWatching, value);
            this.RaisePropertyChanged(nameof(ShowWatchButton));
            this.RaisePropertyChanged(nameof(ShowVideoContent));
            this.RaisePropertyChanged(nameof(IsLoadingStream));
        }
    }

    /// <summary>
    /// Whether to show the "Watch" button. Only for remote screen shares that aren't being watched.
    /// </summary>
    public bool ShowWatchButton => IsRemoteScreenShare && !IsWatching;

    /// <summary>
    /// Whether to show video content (bitmap or placeholder). True when watching or for non-screen shares.
    /// </summary>
    public bool ShowVideoContent => IsWatching;

    /// <summary>
    /// Whether to show loading indicator. True when watching a screen share but no frames received yet.
    /// </summary>
    public bool IsLoadingStream => IsRemoteScreenShare && IsWatching && VideoBitmap == null && !IsUsingHardwareDecoder;

    /// <summary>
    /// The hardware video decoder for zero-copy GPU rendering.
    /// When set, the UI should embed the native view instead of showing the bitmap.
    /// </summary>
    public IHardwareVideoDecoder? HardwareDecoder
    {
        get => _hardwareDecoder;
        set
        {
            this.RaiseAndSetIfChanged(ref _hardwareDecoder, value);
            this.RaisePropertyChanged(nameof(IsUsingHardwareDecoder));
            this.RaisePropertyChanged(nameof(IsLoadingStream));
            this.RaisePropertyChanged(nameof(ShowAvatarPlaceholder));
        }
    }

    /// <summary>
    /// Whether this stream is using hardware decoding with native view rendering.
    /// </summary>
    public bool IsUsingHardwareDecoder => _hardwareDecoder != null;

    /// <summary>
    /// Whether to show the avatar placeholder (no video available from any source).
    /// </summary>
    public bool ShowAvatarPlaceholder => !IsUsingHardwareDecoder && VideoBitmap == null;

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
        set
        {
            this.RaiseAndSetIfChanged(ref _videoBitmap, value);
            this.RaisePropertyChanged(nameof(IsLoadingStream));
            this.RaisePropertyChanged(nameof(ShowAvatarPlaceholder));
        }
    }

    private int _frameCount;

    public void UpdateVideoFrame(byte[] rgbData, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgbData.Length < width * height * 3)
            return;

        _frameCount++;
        var pixelCount = width * height;

        // Convert RGB to BGRA using parallel processing for speed
        var bgraData = new byte[pixelCount * 4];
        Parallel.For(0, pixelCount, i =>
        {
            var srcIndex = i * 3;
            var destIndex = i * 4;
            bgraData[destIndex] = rgbData[srcIndex + 2];     // B
            bgraData[destIndex + 1] = rgbData[srcIndex + 1]; // G
            bgraData[destIndex + 2] = rgbData[srcIndex];     // R
            bgraData[destIndex + 3] = 255;                   // A
        });

        var w = width;
        var h = height;

        // Post to UI thread - use Send priority for immediate update
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var bitmap = new WriteableBitmap(
                    new PixelSize(w, h),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);

                using (var buffer = bitmap.Lock())
                {
                    System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, buffer.Address, bgraData.Length);
                }

                // Use property setter for proper change notification
                VideoBitmap = bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VideoStreamVM: Error updating video frame: {ex.Message}");
            }
        }, DispatcherPriority.Render);
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
    private readonly ISignalRService _signalR;
    private readonly Guid _localUserId;
    private ChannelResponse? _channel;
    private ObservableCollection<VideoStreamViewModel> _videoStreams = new();
    private readonly Dictionary<Guid, ParticipantInfo> _participants = new();

    public VoiceChannelContentViewModel(IWebRtcService webRtc, ISignalRService signalR, Guid localUserId)
    {
        _webRtc = webRtc;
        _signalR = signalR;
        _localUserId = localUserId;

        // Subscribe to video frames from WebRTC
        _webRtc.VideoFrameReceived += OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured += OnLocalVideoFrameCaptured;
        _webRtc.HardwareDecoderReady += OnHardwareDecoderReady;
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

                // Every participant always has a camera stream (shows avatar when camera off, video when on)
                VideoStreams.Add(new VideoStreamViewModel(p.UserId, p.Username, VideoStreamType.Camera, _localUserId));

                // Screen share adds a second stream
                if (p.IsScreenSharing)
                {
                    VideoStreams.Add(new VideoStreamViewModel(p.UserId, p.Username, VideoStreamType.ScreenShare, _localUserId));
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

            // Every participant always has a camera stream (shows avatar when camera off, video when on)
            VideoStreams.Add(new VideoStreamViewModel(participant.UserId, participant.Username, VideoStreamType.Camera, _localUserId));

            // Screen share adds a second stream
            if (participant.IsScreenSharing)
            {
                VideoStreams.Add(new VideoStreamViewModel(participant.UserId, participant.Username, VideoStreamType.ScreenShare, _localUserId));
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

            // Handle camera state change - just update the state, stream is always present
            if (state.IsCameraOn.HasValue)
            {
                info.IsCameraOn = state.IsCameraOn.Value;

                // If camera turned off, clear the video bitmap to show avatar
                if (!state.IsCameraOn.Value)
                {
                    var cameraStream = VideoStreams.FirstOrDefault(s => s.UserId == userId && s.StreamType == VideoStreamType.Camera);
                    if (cameraStream != null)
                    {
                        cameraStream.VideoBitmap = null;
                    }
                }
            }

            // Handle screen share state change - add/remove the screen share stream
            if (state.IsScreenSharing.HasValue)
            {
                var wasScreenSharing = info.IsScreenSharing;
                info.IsScreenSharing = state.IsScreenSharing.Value;

                if (state.IsScreenSharing.Value && !wasScreenSharing)
                {
                    // Screen share turned on - add stream
                    if (!VideoStreams.Any(s => s.UserId == userId && s.StreamType == VideoStreamType.ScreenShare))
                    {
                        Console.WriteLine($"VoiceChannelContent: Adding ScreenShare stream for user {userId} ({username})");
                        VideoStreams.Add(new VideoStreamViewModel(userId, username, VideoStreamType.ScreenShare, _localUserId));
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

    private void OnHardwareDecoderReady(Guid userId, VideoStreamType streamType, IHardwareVideoDecoder decoder)
    {
        // Route hardware decoder to the appropriate video stream for native view embedding
        Dispatcher.UIThread.Post(() =>
        {
            var stream = VideoStreams.FirstOrDefault(s => s.UserId == userId && s.StreamType == streamType);
            if (stream != null)
            {
                stream.HardwareDecoder = decoder;
                Console.WriteLine($"VoiceChannelContent: Hardware decoder ready for {stream.Username} ({streamType})");
            }
        });
    }

    /// <summary>
    /// Start watching a remote user's screen share.
    /// </summary>
    public async Task WatchScreenShareAsync(VideoStreamViewModel stream)
    {
        if (Channel == null || !stream.IsRemoteScreenShare || stream.IsWatching)
            return;

        await _signalR.WatchScreenShareAsync(Channel.Id, stream.UserId);
        _webRtc.StartWatchingScreenShare(stream.UserId);
        stream.IsWatching = true;
        Console.WriteLine($"VoiceChannelContent: Started watching screen share from {stream.Username}");
    }

    /// <summary>
    /// Stop watching a remote user's screen share.
    /// </summary>
    public async Task StopWatchingScreenShareAsync(VideoStreamViewModel stream)
    {
        if (Channel == null || !stream.IsRemoteScreenShare || !stream.IsWatching)
            return;

        await _signalR.StopWatchingScreenShareAsync(Channel.Id, stream.UserId);
        _webRtc.StopWatchingScreenShare(stream.UserId);
        stream.IsWatching = false;
        stream.VideoBitmap = null; // Clear the video frame
        stream.HardwareDecoder = null; // Clear hardware decoder reference
        Console.WriteLine($"VoiceChannelContent: Stopped watching screen share from {stream.Username}");
    }

    /// <summary>
    /// Stop watching all active screen shares. Called when leaving voice channel.
    /// </summary>
    public async Task StopWatchingAllScreenSharesAsync()
    {
        if (Channel == null) return;

        foreach (var stream in VideoStreams.Where(s => s.IsRemoteScreenShare && s.IsWatching).ToList())
        {
            try
            {
                await _signalR.StopWatchingScreenShareAsync(Channel.Id, stream.UserId);
                stream.IsWatching = false;
                stream.VideoBitmap = null;
                Console.WriteLine($"VoiceChannelContent: Stopped watching screen share from {stream.Username} (cleanup)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VoiceChannelContent: Error stopping watch for {stream.Username}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // Stop watching all screen shares (fire and forget since Dispose is sync)
        _ = StopWatchingAllScreenSharesAsync();

        _webRtc.VideoFrameReceived -= OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured -= OnLocalVideoFrameCaptured;
        _webRtc.HardwareDecoderReady -= OnHardwareDecoderReady;
    }
}
