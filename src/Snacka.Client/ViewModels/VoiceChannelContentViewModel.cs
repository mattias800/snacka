using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Snacka.Client.Services;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Client.Stores;
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

/// <summary>
/// ViewModel for the voice channel content view.
/// Displays a grid of video streams (one tile per camera or screen share).
/// Subscribes to VoiceStore for reactive state management (Redux-style).
/// </summary>
public class VoiceChannelContentViewModel : ReactiveObject, IDisposable
{
    private readonly IWebRtcService _webRtc;
    private readonly IVoiceStore _voiceStore;
    private readonly IChannelStore _channelStore;
    private readonly Guid _localUserId;
    private Guid? _currentChannelId;
    private ObservableCollection<VideoStreamViewModel> _videoStreams = new();
    private readonly Dictionary<Guid, ParticipantInfo> _participants = new();
    private readonly CompositeDisposable _subscriptions = new();

    /// <summary>
    /// Callback for when a gaming station's "Share Screen" button is clicked.
    /// The parameter is the gaming station's machine ID.
    /// </summary>
    public Action<string>? OnGamingStationShareScreen { get; set; }

    /// <summary>
    /// Callback for when a gaming station's "Stop Share" button is clicked.
    /// The parameter is the gaming station's machine ID.
    /// </summary>
    public Action<string>? OnGamingStationStopShareScreen { get; set; }

    /// <summary>
    /// Event raised when a participant leaves the channel.
    /// Parameters are (channelId, userId).
    /// </summary>
    public event Action<Guid, Guid>? ParticipantLeft;

    private readonly ISignalRService _signalR;

    public VoiceChannelContentViewModel(
        IWebRtcService webRtc,
        IVoiceStore voiceStore,
        IChannelStore channelStore,
        ISignalRService signalR,
        Guid localUserId)
    {
        _webRtc = webRtc;
        _voiceStore = voiceStore;
        _channelStore = channelStore;
        _signalR = signalR;
        _localUserId = localUserId;

        // Subscribe to video frames from WebRTC
        _webRtc.VideoFrameReceived += OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured += OnLocalVideoFrameCaptured;
        _webRtc.HardwareDecoderReady += OnHardwareDecoderReady;
        _webRtc.LocalHardwarePreviewReady += OnLocalHardwarePreviewReady;

        // Subscribe to VoiceStore for reactive updates (like useSelector in Redux)
        SubscribeToStore();
    }

    /// <summary>
    /// Subscribe to VoiceStore observables for reactive state management.
    /// This replaces the prop drilling from MainAppViewModel.
    /// </summary>
    private void SubscribeToStore()
    {
        // Subscribe to current channel changes
        _voiceStore.CurrentChannelId
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(channelId =>
            {
                var previousChannelId = _currentChannelId;
                _currentChannelId = channelId;

                this.RaisePropertyChanged(nameof(HasChannel));
                this.RaisePropertyChanged(nameof(ChannelName));

                // Clear video streams when leaving a channel
                if (channelId is null && previousChannelId is not null)
                {
                    ClearAllStreams();
                }
            })
            .DisposeWith(_subscriptions);

        // Subscribe to participant changes in the current channel
        _voiceStore.CurrentChannelParticipants
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(participants => SyncParticipants(participants))
            .DisposeWith(_subscriptions);
    }

    /// <summary>
    /// Syncs VideoStreamViewModels with the current participant state from the store.
    /// Handles additions, removals, and updates.
    /// </summary>
    private void SyncParticipants(IReadOnlyCollection<VoiceParticipantState> storeParticipants)
    {
        var storeUserIds = storeParticipants.Select(p => p.UserId).ToHashSet();
        var localUserIds = _participants.Keys.ToHashSet();

        // Find participants to remove (in local but not in store)
        var toRemove = localUserIds.Except(storeUserIds).ToList();
        foreach (var userId in toRemove)
        {
            RemoveParticipantInternal(userId);
            if (_currentChannelId.HasValue)
            {
                ParticipantLeft?.Invoke(_currentChannelId.Value, userId);
            }
        }

        // Find participants to add (in store but not in local)
        var toAdd = storeParticipants.Where(p => !localUserIds.Contains(p.UserId)).ToList();
        foreach (var participant in toAdd)
        {
            AddParticipantFromState(participant);
        }

        // Update existing participants
        foreach (var participant in storeParticipants.Where(p => localUserIds.Contains(p.UserId)))
        {
            UpdateParticipantFromState(participant);
        }
    }

    private void AddParticipantFromState(VoiceParticipantState participant)
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
            IsScreenSharing = participant.IsScreenSharing,
            ScreenShareHasAudio = participant.ScreenShareHasAudio,
            IsGamingStation = participant.IsGamingStation,
            GamingStationMachineId = participant.GamingStationMachineId
        };
        _participants[participant.UserId] = info;

        // Every participant always has a camera stream
        VideoStreams.Add(CreateVideoStream(
            participant.UserId,
            participant.Username,
            VideoStreamType.Camera,
            participant.IsGamingStation,
            participant.GamingStationMachineId));

        // Screen share adds a second stream
        if (participant.IsScreenSharing)
        {
            var screenStream = CreateVideoStream(
                participant.UserId,
                participant.Username,
                VideoStreamType.ScreenShare,
                participant.IsGamingStation,
                participant.GamingStationMachineId);
            screenStream.HasAudio = participant.ScreenShareHasAudio;
            VideoStreams.Add(screenStream);
        }
    }

    private void UpdateParticipantFromState(VoiceParticipantState participant)
    {
        if (!_participants.TryGetValue(participant.UserId, out var info))
            return;

        // Update speaking state
        var cameraStream = VideoStreams.FirstOrDefault(s => s.UserId == participant.UserId && s.StreamType == VideoStreamType.Camera);
        if (cameraStream != null)
        {
            cameraStream.IsSpeaking = participant.IsSpeaking;
        }

        // Handle screen sharing state change
        var wasScreenSharing = info.IsScreenSharing;
        info.IsScreenSharing = participant.IsScreenSharing;
        info.ScreenShareHasAudio = participant.ScreenShareHasAudio;
        info.IsSpeaking = participant.IsSpeaking;

        if (participant.IsScreenSharing && !wasScreenSharing)
        {
            // Screen share started - add stream
            if (!VideoStreams.Any(s => s.UserId == participant.UserId && s.StreamType == VideoStreamType.ScreenShare))
            {
                var screenStream = CreateVideoStream(participant.UserId, participant.Username, VideoStreamType.ScreenShare);
                screenStream.HasAudio = participant.ScreenShareHasAudio;
                VideoStreams.Add(screenStream);
            }
        }
        else if (!participant.IsScreenSharing && wasScreenSharing)
        {
            // Screen share stopped - remove stream
            var stream = VideoStreams.FirstOrDefault(s => s.UserId == participant.UserId && s.StreamType == VideoStreamType.ScreenShare);
            if (stream != null)
            {
                VideoStreams.Remove(stream);
            }
        }

        // Update HasAudio on existing screen share stream
        var existingScreenStream = VideoStreams.FirstOrDefault(s => s.UserId == participant.UserId && s.StreamType == VideoStreamType.ScreenShare);
        if (existingScreenStream != null)
        {
            existingScreenStream.HasAudio = participant.ScreenShareHasAudio;
        }

        // Handle camera state - clear bitmap when camera turns off
        if (!participant.IsCameraOn && cameraStream != null)
        {
            cameraStream.VideoBitmap = null;
        }
    }

    private void RemoveParticipantInternal(Guid userId)
    {
        _participants.Remove(userId);

        // Remove all streams for this user
        var toRemove = VideoStreams.Where(s => s.UserId == userId).ToList();
        foreach (var stream in toRemove)
        {
            VideoStreams.Remove(stream);
        }
    }

    private void ClearAllStreams()
    {
        _participants.Clear();
        VideoStreams.Clear();
    }

    /// <summary>
    /// Callback for when a video stream's volume is changed by the user.
    /// </summary>
    private void OnStreamVolumeChanged(Guid userId, VideoStreamType streamType, float volume)
    {
        // All audio from a user goes through the same mixer, so we just set the user volume
        // (both mic and screen share audio use the same per-user volume setting)
        _webRtc.SetUserVolume(userId, volume);
        Console.WriteLine($"VoiceChannelContent: Set volume for user {userId} ({streamType}) to {volume:P0}");
    }

    /// <summary>
    /// Creates a VideoStreamViewModel with the volume callback wired up.
    /// </summary>
    private VideoStreamViewModel CreateVideoStream(
        Guid userId,
        string username,
        VideoStreamType streamType,
        bool isGamingStation = false,
        string? gamingStationMachineId = null)
    {
        var stream = new VideoStreamViewModel(userId, username, streamType, _localUserId, OnStreamVolumeChanged);

        // Set initial volume from saved settings
        stream.Volume = _webRtc.GetUserVolume(userId);

        // Set gaming station properties if applicable
        if (isGamingStation && !string.IsNullOrEmpty(gamingStationMachineId))
        {
            stream.IsGamingStation = true;
            stream.GamingStationMachineId = gamingStationMachineId;
            stream.OnShareScreenCommand = OnGamingStationShareScreen;
            stream.OnStopShareScreenCommand = OnGamingStationStopShareScreen;
        }

        return stream;
    }

    /// <summary>
    /// Gets the channel name from the store. Returns empty string if not in a voice channel.
    /// </summary>
    public string ChannelName
    {
        get
        {
            if (_currentChannelId is null) return "";
            var channel = _channelStore.GetChannel(_currentChannelId.Value);
            return channel?.Name ?? "";
        }
    }

    /// <summary>
    /// Whether currently in a voice channel. Derived from VoiceStore.
    /// </summary>
    public bool HasChannel => _currentChannelId != null;

    /// <summary>
    /// Collection of video streams to display. Each user may have 0, 1, or 2 streams
    /// (camera and/or screen share).
    /// </summary>
    public ObservableCollection<VideoStreamViewModel> VideoStreams
    {
        get => _videoStreams;
        set => this.RaiseAndSetIfChanged(ref _videoStreams, value);
    }

    // Note: SetParticipants, AddParticipant, RemoveParticipant, UpdateParticipantState, UpdateSpeakingState
    // are no longer needed - VoiceStore subscriptions handle all participant state changes reactively.

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

    private void OnLocalHardwarePreviewReady(VideoStreamType streamType, IHardwareVideoDecoder decoder)
    {
        // Route hardware decoder to local user's video stream for self-preview
        Dispatcher.UIThread.Post(() =>
        {
            var stream = VideoStreams.FirstOrDefault(s => s.UserId == _localUserId && s.StreamType == streamType);
            if (stream != null)
            {
                stream.HardwareDecoder = decoder;
                Console.WriteLine($"VoiceChannelContent: Local hardware preview decoder ready ({streamType})");
            }
            else
            {
                Console.WriteLine($"VoiceChannelContent: No local stream found for hardware preview ({streamType})");
            }
        });
    }

    /// <summary>
    /// Start watching a remote user's screen share.
    /// </summary>
    public async Task WatchScreenShareAsync(VideoStreamViewModel stream)
    {
        if (_currentChannelId is null || !stream.IsRemoteScreenShare || stream.IsWatching)
            return;

        await _signalR.WatchScreenShareAsync(_currentChannelId.Value, stream.UserId);
        _webRtc.StartWatchingScreenShare(stream.UserId);
        stream.IsWatching = true;
        Console.WriteLine($"VoiceChannelContent: Started watching screen share from {stream.Username}");
    }

    /// <summary>
    /// Stop watching a remote user's screen share.
    /// </summary>
    public async Task StopWatchingScreenShareAsync(VideoStreamViewModel stream)
    {
        if (_currentChannelId is null || !stream.IsRemoteScreenShare || !stream.IsWatching)
            return;

        await _signalR.StopWatchingScreenShareAsync(_currentChannelId.Value, stream.UserId);
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
        if (_currentChannelId is null) return;

        foreach (var stream in VideoStreams.Where(s => s.IsRemoteScreenShare && s.IsWatching).ToList())
        {
            try
            {
                await _signalR.StopWatchingScreenShareAsync(_currentChannelId.Value, stream.UserId);
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
        // Dispose store subscriptions
        _subscriptions.Dispose();

        // Stop watching all screen shares (fire and forget since Dispose is sync)
        _ = StopWatchingAllScreenSharesAsync();

        _webRtc.VideoFrameReceived -= OnVideoFrameReceived;
        _webRtc.LocalVideoFrameCaptured -= OnLocalVideoFrameCaptured;
        _webRtc.HardwareDecoderReady -= OnHardwareDecoderReady;
        _webRtc.LocalHardwarePreviewReady -= OnLocalHardwarePreviewReady;
    }
}
