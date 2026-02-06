using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Coordinators;
using Snacka.Client.Services;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

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
    private readonly IPortForwardCoordinator? _portForwardCoordinator;
    private readonly Guid _localUserId;
    private Guid? _currentChannelId;
    private ObservableCollection<VideoStreamViewModel> _videoStreams = new();
    private IReadOnlyList<SharedPortState> _sharedPorts = Array.Empty<SharedPortState>();
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
        Guid localUserId,
        IPortForwardCoordinator? portForwardCoordinator = null)
    {
        _webRtc = webRtc;
        _voiceStore = voiceStore;
        _channelStore = channelStore;
        _signalR = signalR;
        _localUserId = localUserId;
        _portForwardCoordinator = portForwardCoordinator;

        OpenSharedPortCommand = ReactiveCommand.CreateFromTask<string>(async tunnelId =>
        {
            if (_portForwardCoordinator is not null)
                await _portForwardCoordinator.OpenSharedPortInBrowserAsync(tunnelId);
        });

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

        // Subscribe to shared ports changes
        _voiceStore.SharedPorts
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(ports =>
            {
                _sharedPorts = ports;
                this.RaisePropertyChanged(nameof(SharedPorts));
                this.RaisePropertyChanged(nameof(HasSharedPorts));
            })
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

    /// <summary>
    /// Shared ports in the current voice channel.
    /// </summary>
    public IReadOnlyList<SharedPortState> SharedPorts => _sharedPorts;

    /// <summary>
    /// Whether there are any shared ports in the channel.
    /// </summary>
    public bool HasSharedPorts => _sharedPorts.Count > 0;

    /// <summary>
    /// Command to open a shared port in the browser.
    /// </summary>
    public ICommand OpenSharedPortCommand { get; }

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
