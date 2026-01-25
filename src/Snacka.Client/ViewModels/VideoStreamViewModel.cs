using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Shared.Models;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

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
    private bool _hasAudio;
    private float _volume = 1.0f;
    private Action<Guid, VideoStreamType, float>? _onVolumeChanged;

    // Gaming station properties
    private bool _isGamingStation;
    private string? _gamingStationMachineId;
    private Action<string>? _onShareScreenCommand;
    private Action<string>? _onStopShareScreenCommand;

    public VideoStreamViewModel(Guid userId, string username, VideoStreamType streamType, Guid localUserId)
        : this(userId, username, streamType, localUserId, null)
    {
    }

    public VideoStreamViewModel(Guid userId, string username, VideoStreamType streamType, Guid localUserId,
        Action<Guid, VideoStreamType, float>? onVolumeChanged)
    {
        UserId = userId;
        Username = username;
        StreamType = streamType;
        _localUserId = localUserId;
        _onVolumeChanged = onVolumeChanged;

        // Local user's streams are always "watching" (they see their own preview)
        // Camera streams from remote users are auto-watched (no opt-in needed)
        // Remote screen shares require explicit opt-in
        _isWatching = !IsRemoteScreenShare;
    }

    public Guid UserId { get; }
    public string Username { get; }
    public VideoStreamType StreamType { get; }

    /// <summary>
    /// Whether this participant is a gaming station that can be remotely controlled.
    /// </summary>
    public bool IsGamingStation
    {
        get => _isGamingStation;
        set
        {
            this.RaiseAndSetIfChanged(ref _isGamingStation, value);
            this.RaisePropertyChanged(nameof(ShowGamingStationControls));
        }
    }

    /// <summary>
    /// The machine ID of the gaming station (for sending remote commands).
    /// </summary>
    public string? GamingStationMachineId
    {
        get => _gamingStationMachineId;
        set => this.RaiseAndSetIfChanged(ref _gamingStationMachineId, value);
    }

    /// <summary>
    /// Whether to show gaming station remote control buttons.
    /// Only shown for gaming station camera tiles that belong to the local user.
    /// </summary>
    public bool ShowGamingStationControls =>
        IsGamingStation &&
        StreamType == VideoStreamType.Camera &&
        UserId == _localUserId;

    /// <summary>
    /// Callback for when the "Share Screen" button is clicked on a gaming station tile.
    /// </summary>
    public Action<string>? OnShareScreenCommand
    {
        get => _onShareScreenCommand;
        set => this.RaiseAndSetIfChanged(ref _onShareScreenCommand, value);
    }

    /// <summary>
    /// Callback for when the "Stop Share" button is clicked on a gaming station tile.
    /// </summary>
    public Action<string>? OnStopShareScreenCommand
    {
        get => _onStopShareScreenCommand;
        set => this.RaiseAndSetIfChanged(ref _onStopShareScreenCommand, value);
    }

    /// <summary>
    /// Invokes the share screen command for this gaming station.
    /// </summary>
    public void ShareScreen()
    {
        if (!string.IsNullOrEmpty(GamingStationMachineId))
        {
            OnShareScreenCommand?.Invoke(GamingStationMachineId);
        }
    }

    /// <summary>
    /// Invokes the stop share screen command for this gaming station.
    /// </summary>
    public void StopShareScreen()
    {
        if (!string.IsNullOrEmpty(GamingStationMachineId))
        {
            OnStopShareScreenCommand?.Invoke(GamingStationMachineId);
        }
    }

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
    /// Whether to show video content (bitmap or placeholder).
    /// True for local streams (always), or remote streams when watching.
    /// </summary>
    public bool ShowVideoContent => !IsRemoteScreenShare || IsWatching;

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

    /// <summary>
    /// Whether this stream has audio that can be heard by viewers.
    /// True for screen shares with audio enabled.
    /// </summary>
    public bool HasAudio
    {
        get => _hasAudio;
        set
        {
            this.RaiseAndSetIfChanged(ref _hasAudio, value);
            this.RaisePropertyChanged(nameof(ShowVolumeSlider));
        }
    }

    /// <summary>
    /// Whether to show the volume slider. Only for remote streams with audio.
    /// Local user doesn't need volume control on their own streams.
    /// </summary>
    public bool ShowVolumeSlider => HasAudio && UserId != _localUserId;

    /// <summary>
    /// Volume level for this stream's audio (0.0 to 2.0, where 1.0 = 100%).
    /// Only relevant when HasAudio is true.
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
                this.RaisePropertyChanged(nameof(VolumePercent));
                _onVolumeChanged?.Invoke(UserId, StreamType, clamped);
            }
        }
    }

    /// <summary>
    /// Volume as a percentage (0-200%) for UI binding.
    /// </summary>
    public int VolumePercent
    {
        get => (int)(_volume * 100);
        set => Volume = value / 100f;
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
    public bool ScreenShareHasAudio { get; set; }
    public bool IsSpeaking { get; set; }
    public bool IsGamingStation { get; set; }
    public string? GamingStationMachineId { get; set; }
}
