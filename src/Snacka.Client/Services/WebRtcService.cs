using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Encoders;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Snacka.Shared.Models;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Client.Services.WebRtc;

namespace Snacka.Client.Services;

public interface IWebRtcService : IAsyncDisposable
{
    Guid? CurrentChannelId { get; }
    Guid? CurrentUserId { get; }
    VoiceConnectionStatus ConnectionStatus { get; }
    int ConnectedPeerCount { get; }
    IReadOnlyDictionary<Guid, PeerConnectionState> PeerStates { get; }
    bool IsSpeaking { get; }
    bool IsCameraOn { get; }
    bool IsScreenSharing { get; }

    /// <summary>
    /// Sets the current user ID. Call this after login so the service knows which user is local.
    /// </summary>
    void SetCurrentUserId(Guid userId);

    Task JoinVoiceChannelAsync(Guid channelId, IEnumerable<VoiceParticipantResponse> existingParticipants);
    Task LeaveVoiceChannelAsync();

    Task HandleOfferAsync(Guid fromUserId, string sdp);
    Task HandleAnswerAsync(Guid fromUserId, string sdp);
    Task HandleIceCandidateAsync(Guid fromUserId, string candidate, string? sdpMid, int? sdpMLineIndex);

    void SetMuted(bool muted);
    void SetDeafened(bool deafened);
    Task SetCameraAsync(bool enabled);
    Task SetScreenSharingAsync(bool enabled, ScreenShareSettings? settings = null);

    /// <summary>
    /// Sets the volume for a specific user (0.0 - 2.0, where 1.0 is 100%).
    /// </summary>
    void SetUserVolume(Guid userId, float volume);

    /// <summary>
    /// Gets the volume for a specific user.
    /// </summary>
    float GetUserVolume(Guid userId);

    event Action<Guid>? PeerConnected;
    event Action<Guid>? PeerDisconnected;
    event Action<VoiceConnectionStatus>? ConnectionStatusChanged;
    event Action<bool>? SpeakingChanged;
    event Action<bool>? CameraStateChanged;
    event Action<bool>? ScreenSharingStateChanged;
    /// <summary>
    /// Fired when a video frame is received from a peer. Args: (userId, streamType, width, height, rgbData)
    /// </summary>
    event Action<Guid, VideoStreamType, int, int, byte[]>? VideoFrameReceived;
    /// <summary>
    /// Fired when an NV12 video frame is received (for GPU rendering). Args: (userId, streamType, width, height, nv12Data)
    /// </summary>
    event Action<Guid, VideoStreamType, int, int, byte[]>? Nv12VideoFrameReceived;
    /// <summary>
    /// Fired when a local video frame is captured (for self-preview). Args: (streamType, width, height, rgbData)
    /// </summary>
    event Action<VideoStreamType, int, int, byte[]>? LocalVideoFrameCaptured;

    /// <summary>
    /// Fired when a hardware video decoder is ready for a stream. Args: (userId, streamType, decoder)
    /// The UI should embed the decoder's native view for zero-copy GPU rendering.
    /// </summary>
    event Action<Guid, VideoStreamType, IHardwareVideoDecoder>? HardwareDecoderReady;

    /// <summary>
    /// Gets whether GPU video rendering is available.
    /// </summary>
    bool IsGpuRenderingAvailable { get; }

    /// <summary>
    /// Gets whether hardware video decoding is available.
    /// </summary>
    bool IsHardwareDecodingAvailable { get; }

    /// <summary>
    /// Adds a user to the list of screen shares we're watching.
    /// Call this when the user starts watching a screen share so video can be properly routed.
    /// </summary>
    void StartWatchingScreenShare(Guid userId);

    /// <summary>
    /// Removes a user from the list of screen shares we're watching.
    /// </summary>
    void StopWatchingScreenShare(Guid userId);
}

public enum VoiceConnectionStatus
{
    Disconnected,
    Connecting,
    Connected
}

public enum PeerConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}

public class WebRtcService : IWebRtcService
{
    private readonly ISignalRService _signalR;
    private readonly ISettingsStore? _settingsStore;

    // Extracted managers for audio handling
    private readonly AudioInputManager _audioInputManager;
    private readonly AudioOutputManager _audioOutputManager;

    // Extracted manager for camera handling
    private readonly CameraManager _cameraManager;

    // Extracted manager for video decoding
    private readonly VideoDecoderManager _videoDecoderManager;

    // Extracted manager for screen sharing
    private readonly ScreenShareManager _screenShareManager;

    // Extracted manager for SFU connection
    private readonly SfuConnectionManager _sfuConnectionManager;

    // Legacy P2P: keep for backward compatibility if needed
    private readonly ConcurrentDictionary<Guid, RTCPeerConnection> _peerConnections = new();
    private readonly ConcurrentDictionary<Guid, PeerConnectionState> _peerStates = new();

    // Legacy P2P: per-peer audio sinks
    private readonly ConcurrentDictionary<Guid, SDL2AudioEndPoint> _audioSinks = new();
    // Per-stream frame assemblers for camera and screen share video
    // These accumulate RTP payloads and assemble complete H264 frames
    private readonly H264FrameAssembler _cameraFrameAssembler = new();
    private readonly H264FrameAssembler _screenFrameAssembler = new();
    // SSRC to UserId mapping for incoming video
    private readonly ConcurrentDictionary<uint, Guid> _ssrcToUserMap = new();
    // Camera video SSRC to UserId mapping (received from server)
    private readonly ConcurrentDictionary<uint, Guid> _cameraVideoSsrcToUserMap = new();
    // SSRC to payload type mapping for detecting camera vs screen share
    private readonly ConcurrentDictionary<uint, int> _ssrcPayloadTypeMap = new();
    // UserIds of screen shares we're currently watching (can watch multiple)
    private readonly HashSet<Guid> _watchingScreenShareUserIds = new();

    // Video format for track negotiation (read from SFU manager)
    private const int VideoFps = 15;

    // Screen sharing state
    private ScreenShareSettings? _currentScreenShareSettings;

    private Guid? _currentChannelId;
    private Guid _localUserId;
    private VoiceConnectionStatus _connectionStatus = VoiceConnectionStatus.Disconnected;

    public Guid? CurrentChannelId => _currentChannelId;
    public Guid? CurrentUserId => _localUserId == Guid.Empty ? null : _localUserId;
    public VoiceConnectionStatus ConnectionStatus => _connectionStatus;
    public int ConnectedPeerCount => _peerStates.Count(p => p.Value == PeerConnectionState.Connected);
    public IReadOnlyDictionary<Guid, PeerConnectionState> PeerStates => _peerStates;
    public bool IsSpeaking => _audioInputManager.IsSpeaking;
    public bool IsCameraOn => _cameraManager.IsCameraOn;
    public bool IsScreenSharing => _screenShareManager.IsScreenSharing;

    public void SetCurrentUserId(Guid userId)
    {
        _localUserId = userId;
        Console.WriteLine($"WebRTC: Current user ID set to {userId}");
    }

    public event Action<Guid>? PeerConnected;
    public event Action<Guid>? PeerDisconnected;
    public event Action<VoiceConnectionStatus>? ConnectionStatusChanged;
    public event Action<bool>? SpeakingChanged;
    public event Action<bool>? CameraStateChanged;
    public event Action<bool>? ScreenSharingStateChanged;
    public event Action<Guid, VideoStreamType, int, int, byte[]>? VideoFrameReceived;
    public event Action<Guid, VideoStreamType, int, int, byte[]>? Nv12VideoFrameReceived;
    /// <summary>
    /// Fired when a hardware video decoder is ready for a user's stream.
    /// Args: (userId, streamType, hardwareDecoder)
    /// The hardwareDecoder can be used to get the native view handle for embedding.
    /// </summary>
    public event Action<Guid, VideoStreamType, IHardwareVideoDecoder>? HardwareDecoderReady;
    /// <summary>
    /// Fired when a local video frame is captured (for self-preview). Args: (streamType, width, height, rgbData)
    /// </summary>
    public event Action<VideoStreamType, int, int, byte[]>? LocalVideoFrameCaptured;

    /// <summary>
    /// Gets whether GPU video rendering is available on this platform.
    /// </summary>
    public bool IsGpuRenderingAvailable => _videoDecoderManager.IsGpuRenderingAvailable;

    /// <summary>
    /// Gets whether hardware video decoding is available on this platform.
    /// Hardware decoding provides zero-copy GPU pipeline: H264 → GPU Decode → GPU Render
    /// </summary>
    public bool IsHardwareDecodingAvailable => _videoDecoderManager.IsHardwareDecodingAvailable;

    public WebRtcService(ISignalRService signalR, ISettingsStore? settingsStore = null)
    {
        _signalR = signalR;
        _settingsStore = settingsStore;

        // Initialize audio managers
        _audioInputManager = new AudioInputManager(settingsStore);
        _audioOutputManager = new AudioOutputManager(settingsStore);

        // Initialize camera manager
        _cameraManager = new CameraManager(settingsStore);

        // Initialize video decoder manager
        _videoDecoderManager = new VideoDecoderManager();

        // Initialize screen share manager
        _screenShareManager = new ScreenShareManager();

        // Initialize SFU connection manager
        _sfuConnectionManager = new SfuConnectionManager(signalR);

        // Wire up speaking state changes from input manager
        _audioInputManager.SpeakingChanged += speaking => SpeakingChanged?.Invoke(speaking);

        // Wire up camera events
        _cameraManager.OnFrameEncoded += OnCameraVideoEncoded;
        _cameraManager.OnLocalFrameCaptured += (width, height, rgbData) =>
            LocalVideoFrameCaptured?.Invoke(VideoStreamType.Camera, width, height, rgbData);

        // Wire up video decoder events
        _videoDecoderManager.VideoFrameReceived += (userId, streamType, width, height, rgbData) =>
            VideoFrameReceived?.Invoke(userId, streamType, width, height, rgbData);
        _videoDecoderManager.Nv12VideoFrameReceived += (userId, streamType, width, height, nv12Data) =>
            Nv12VideoFrameReceived?.Invoke(userId, streamType, width, height, nv12Data);
        _videoDecoderManager.HardwareDecoderReady += (userId, streamType, decoder) =>
            HardwareDecoderReady?.Invoke(userId, streamType, decoder);

        // Wire up screen share manager events
        _screenShareManager.OnVideoFrameEncoded += OnScreenVideoEncoded;
        _screenShareManager.OnAudioEncoded += OnScreenAudioEncoded;
        _screenShareManager.OnLocalPreviewFrame += (width, height, rgbData) =>
            LocalVideoFrameCaptured?.Invoke(VideoStreamType.ScreenShare, width, height, rgbData);

        // Wire up SFU connection manager events
        _sfuConnectionManager.ConnectionStatusChanged += status => UpdateConnectionStatus(status);
        _sfuConnectionManager.AudioPacketReceived += OnSfuAudioPacketReceived;
        _sfuConnectionManager.VideoPacketReceived += (ssrc, payload, timestamp, marker, payloadType) =>
            OnSfuVideoPacketReceived(ssrc, payload, timestamp, marker, payloadType);

        // Subscribe to SFU signaling events (routed through SfuConnectionManager)
        _signalR.SfuOfferReceived += async e => await HandleSfuOfferAsync(e.ChannelId, e.Sdp);
        _signalR.SfuIceCandidateReceived += e => _sfuConnectionManager.HandleIceCandidate(e.Candidate, e.SdpMid, e.SdpMLineIndex);

        // Subscribe to SSRC mapping events for per-user volume control
        _signalR.UserAudioSsrcMapped += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                _audioOutputManager.AudioSsrcToUserMap[e.AudioSsrc] = e.UserId;
                Console.WriteLine($"WebRTC: Mapped mic audio SSRC {e.AudioSsrc} to user {e.UserId}");
            }
        };

        // Subscribe to screen audio SSRC mapping events
        _signalR.UserScreenAudioSsrcMapped += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                _audioOutputManager.ScreenAudioSsrcToUserMap[e.ScreenAudioSsrc] = e.UserId;
                Console.WriteLine($"WebRTC: Mapped screen audio SSRC {e.ScreenAudioSsrc} to user {e.UserId}");
            }
        };

        // Subscribe to camera video SSRC mapping events
        _signalR.UserCameraVideoSsrcMapped += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                _cameraVideoSsrcToUserMap[e.CameraVideoSsrc] = e.UserId;
                Console.WriteLine($"WebRTC: Mapped camera video SSRC {e.CameraVideoSsrc} to user {e.UserId}");
            }
        };

        _signalR.SsrcMappingsBatchReceived += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                foreach (var mapping in e.Mappings.Where(m => m.AudioSsrc.HasValue))
                {
                    _audioOutputManager.AudioSsrcToUserMap[mapping.AudioSsrc!.Value] = mapping.UserId;
                }
                foreach (var mapping in e.Mappings.Where(m => m.ScreenAudioSsrc.HasValue))
                {
                    _audioOutputManager.ScreenAudioSsrcToUserMap[mapping.ScreenAudioSsrc!.Value] = mapping.UserId;
                }
                Console.WriteLine($"WebRTC: Loaded {e.Mappings.Count(m => m.AudioSsrc.HasValue)} mic SSRC + {e.Mappings.Count(m => m.ScreenAudioSsrc.HasValue)} screen audio SSRC mappings");
            }
        };

        // Legacy P2P signaling events (kept for backward compatibility)
        _signalR.WebRtcOfferReceived += async e => await HandleOfferAsync(e.FromUserId, e.Sdp);
        _signalR.WebRtcAnswerReceived += async e => await HandleAnswerAsync(e.FromUserId, e.Sdp);
        _signalR.IceCandidateReceived += async e => await HandleIceCandidateAsync(e.FromUserId, e.Candidate, e.SdpMid, e.SdpMLineIndex);

        // Handle participant events - in SFU mode, we track participants for UI/video decoders
        // but don't create P2P connections
        _signalR.VoiceParticipantJoined += e =>
        {
            if (_currentChannelId == e.ChannelId && e.Participant.UserId != _localUserId)
            {
                // In SFU mode, ensure we have video decoders ready for this user
                // Always create camera decoder
                EnsureVideoDecoderForUser(e.Participant.UserId, VideoStreamType.Camera);
                // Create screen share decoder if they're screen sharing
                if (e.Participant.IsScreenSharing)
                {
                    EnsureVideoDecoderForUser(e.Participant.UserId, VideoStreamType.ScreenShare);
                }
            }
        };

        _signalR.VoiceParticipantLeft += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                // Clean up all video decoders for this user
                RemoveAllVideoDecodersForUser(e.UserId);
                // Legacy P2P cleanup
                ClosePeerConnection(e.UserId);
            }
        };

        // Handle voice state changes to manage screen share decoders
        _signalR.VoiceStateChanged += e =>
        {
            if (_currentChannelId == e.ChannelId && e.UserId != _localUserId)
            {
                // Create/remove screen share decoder based on screen sharing state
                if (e.State.IsScreenSharing.HasValue)
                {
                    if (e.State.IsScreenSharing.Value)
                    {
                        EnsureVideoDecoderForUser(e.UserId, VideoStreamType.ScreenShare);
                    }
                    else
                    {
                        RemoveVideoDecoderForUser(e.UserId, VideoStreamType.ScreenShare);
                    }
                }
            }
        };
    }

    public void SetLocalUserId(Guid userId)
    {
        _localUserId = userId;
    }

    private async Task InitializeAudioSourceAsync()
    {
        await _audioInputManager.InitializeAsync();
    }

    public async Task JoinVoiceChannelAsync(Guid channelId, IEnumerable<VoiceParticipantResponse> existingParticipants)
    {
        await LeaveVoiceChannelAsync();

        _currentChannelId = channelId;
        Console.WriteLine($"WebRTC: Joining voice channel {channelId} (SFU mode)");

        // Initialize microphone capture
        await InitializeAudioSourceAsync();

        // Initialize audio mixer for receiving audio from server (with per-user volume control)
        await InitializeAudioMixerAsync();

        // Prepare video decoders for existing participants
        foreach (var p in existingParticipants)
        {
            if (p.UserId != _localUserId)
            {
                // Always create camera decoder
                EnsureVideoDecoderForUser(p.UserId, VideoStreamType.Camera);
                // Create screen share decoder if they're screen sharing
                if (p.IsScreenSharing)
                {
                    EnsureVideoDecoderForUser(p.UserId, VideoStreamType.ScreenShare);
                }
            }
        }

        // In SFU mode, the server will send us an SDP offer after we join
        // Check if we already received a pending offer (timing issue where offer arrives before this method)
        if (_sfuConnectionManager.HasPendingOffer(channelId))
        {
            Console.WriteLine($"WebRTC: Processing pending SFU offer for channel {channelId}");
            var pending = _sfuConnectionManager.ConsumePendingOffer();
            if (pending.HasValue)
            {
                await _sfuConnectionManager.ProcessSfuOfferAsync(
                    pending.Value.ChannelId,
                    pending.Value.Sdp,
                    _audioInputManager.AudioSource);
            }
        }
        else
        {
            Console.WriteLine($"WebRTC: Waiting for SFU offer from server...");
            UpdateConnectionStatus(VoiceConnectionStatus.Connecting);
        }
    }

    public async Task LeaveVoiceChannelAsync()
    {
        if (_currentChannelId is null) return;

        Console.WriteLine($"WebRTC: Leaving voice channel {_currentChannelId}");

        // Stop video capture
        if (_cameraManager.IsCameraOn)
        {
            await _cameraManager.StopAsync();
            CameraStateChanged?.Invoke(false);
        }

        // Stop screen sharing
        if (_screenShareManager.IsScreenSharing)
        {
            await _screenShareManager.StopAsync();
            ScreenSharingStateChanged?.Invoke(false);
        }

        // Close SFU server connection
        _sfuConnectionManager.DisconnectAudioSource(_audioInputManager.AudioSource);
        _sfuConnectionManager.Close();

        // Close all legacy P2P peer connections
        foreach (var userId in _peerConnections.Keys.ToList())
        {
            ClosePeerConnection(userId);
        }

        // Clean up all video decoders
        _videoDecoderManager.ClearAll();
        _ssrcToUserMap.Clear();
        _ssrcPayloadTypeMap.Clear();
        _cameraVideoSsrcToUserMap.Clear();
        _watchingScreenShareUserIds.Clear();
        _cameraFrameAssembler.Reset();
        _screenFrameAssembler.Reset();

        // Stop and dispose audio managers
        await _audioInputManager.StopAsync();
        await _audioOutputManager.StopAsync();

        _currentChannelId = null;
        UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
    }

    // ==================== SFU Methods ====================

    private async Task InitializeAudioMixerAsync()
    {
        await _audioOutputManager.InitializeAsync();
    }

    /// <summary>
    /// Sets the volume for a specific user (0.0 - 2.0, where 1.0 is normal).
    /// </summary>
    public void SetUserVolume(Guid userId, float volume)
    {
        _audioOutputManager.SetUserVolume(userId, volume);
    }

    /// <summary>
    /// Gets the volume for a specific user.
    /// </summary>
    public float GetUserVolume(Guid userId)
    {
        return _audioOutputManager.GetUserVolume(userId);
    }

    private void EnsureVideoDecoderForUser(Guid userId, VideoStreamType streamType)
    {
        _videoDecoderManager.EnsureDecoderForUser(userId, _localUserId, streamType);
    }

    private void RemoveVideoDecoderForUser(Guid userId, VideoStreamType streamType)
    {
        _videoDecoderManager.RemoveDecoderForUser(userId, streamType);
    }

    private void RemoveAllVideoDecodersForUser(Guid userId)
    {
        _videoDecoderManager.RemoveAllDecodersForUser(userId);
    }

    /// <summary>
    /// Handles the SDP offer from the SFU server.
    /// Routes to SfuConnectionManager for processing.
    /// </summary>
    private async Task HandleSfuOfferAsync(Guid channelId, string sdp)
    {
        // If we don't have a current channel yet, cache the offer for later
        if (_currentChannelId == null)
        {
            Console.WriteLine($"WebRTC: Caching SFU offer for channel {channelId} (no current channel yet)");
            // Store the offer with SDP - will be processed when JoinVoiceChannelAsync is called
            _sfuConnectionManager.StorePendingOffer(channelId, sdp);
            return;
        }

        if (_currentChannelId != channelId)
        {
            Console.WriteLine($"WebRTC: Ignoring SFU offer for channel {channelId} (current: {_currentChannelId})");
            return;
        }

        await _sfuConnectionManager.ProcessSfuOfferAsync(channelId, sdp, _audioInputManager.AudioSource);
    }

    /// <summary>
    /// Handles audio RTP packets received from the SFU server.
    /// Routes to audio mixer for playback with per-user volume control.
    /// </summary>
    private void OnSfuAudioPacketReceived(uint ssrc, ushort seqNum, uint timestamp, int payloadType, bool marker, byte[] payload, bool isScreenAudio)
    {
        if (_audioOutputManager.IsDeafened || _audioOutputManager.AudioMixer == null)
            return;

        if (isScreenAudio)
        {
            // Screen audio (PT 112) - only play if watching any screen share
            Guid? watchedUserId = null;
            lock (_watchingScreenShareUserIds)
            {
                if (_watchingScreenShareUserIds.Count == 0)
                    return; // Not watching any screen share
                watchedUserId = _watchingScreenShareUserIds.First();
            }

            _screenAudioPacketCount++;
            if (_screenAudioPacketCount <= 5 || _screenAudioPacketCount % 500 == 0)
            {
                Console.WriteLine($"WebRTC: Playing screen audio packet #{_screenAudioPacketCount}, size={payload.Length}");
            }

            _audioOutputManager.AudioMixer.ProcessAudioPacket(
                ssrc, watchedUserId, seqNum, timestamp, payloadType, marker, payload);
        }
        else
        {
            // Microphone audio - always play (with per-user volume control)
            Guid? userId = _audioOutputManager.AudioSsrcToUserMap.TryGetValue(ssrc, out var uid) ? uid : null;

            _micAudioPacketCount++;
            if (_micAudioPacketCount <= 5 || _micAudioPacketCount % 500 == 0)
            {
                Console.WriteLine($"WebRTC: Mic audio packet #{_micAudioPacketCount}, PT={payloadType}, size={payload.Length}, ssrc={ssrc}, user={userId}");
            }

            _audioOutputManager.AudioMixer.ProcessAudioPacket(
                ssrc, userId, seqNum, timestamp, payloadType, marker, payload);
        }
    }

    /// <summary>
    /// Handles video RTP packets received from the SFU server.
    /// Routes to frame assemblers and video decoder manager.
    /// </summary>
    private void OnSfuVideoPacketReceived(uint ssrc, byte[] payload, uint timestamp, bool marker, int payloadType)
    {
        // Select the appropriate frame assembler based on payload type
        // PT 96 = camera, PT 97 = screen share
        var assembler = payloadType == 97 ? _screenFrameAssembler : _cameraFrameAssembler;
        var streamType = payloadType == 97 ? VideoStreamType.ScreenShare : VideoStreamType.Camera;

        // Process packet and check for complete frame
        var completeFrame = assembler.ProcessPacket(payload, timestamp, marker);
        if (completeFrame != null)
        {
            // For video, we need to determine the user ID from SSRC or watched users
            Guid userId = Guid.Empty;

            if (streamType == VideoStreamType.ScreenShare)
            {
                // For screen share, use first watched user
                lock (_watchingScreenShareUserIds)
                {
                    if (_watchingScreenShareUserIds.Count > 0)
                    {
                        userId = _watchingScreenShareUserIds.First();
                    }
                }
            }
            else if (streamType == VideoStreamType.Camera)
            {
                // For camera video, look up user ID from SSRC mapping
                if (_cameraVideoSsrcToUserMap.TryGetValue(ssrc, out var mappedUserId))
                {
                    userId = mappedUserId;
                }
            }

            // Process frame through video decoder manager
            _videoDecoderManager.ProcessFrame(userId, streamType, completeFrame);
        }
    }

    // ==================== Legacy P2P Methods ====================

    private async Task CreatePeerConnectionAsync(Guid remoteUserId, bool isInitiator)
    {
        if (_peerConnections.ContainsKey(remoteUserId))
        {
            Console.WriteLine($"WebRTC: Peer connection already exists for {remoteUserId}");
            return;
        }

        Console.WriteLine($"WebRTC: Creating peer connection to {remoteUserId} (initiator: {isInitiator})");

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        var pc = new RTCPeerConnection(config);
        _peerConnections[remoteUserId] = pc;
        _peerStates[remoteUserId] = PeerConnectionState.New;

        // Create audio sink (speaker) for this peer
        SDL2AudioEndPoint? audioSink = null;
        try
        {
            // Enable Opus for high-quality 48kHz audio
            var audioEncoder = new AudioEncoder(includeOpus: true);
            // Use selected audio output device from settings (empty string = default)
            var outputDevice = _settingsStore?.Settings.AudioOutputDevice ?? string.Empty;
            audioSink = new SDL2AudioEndPoint(outputDevice, audioEncoder);
            _audioSinks[remoteUserId] = audioSink;

            // Start playback
            await audioSink.StartAudioSink();

            Console.WriteLine($"WebRTC: Audio sink created for {remoteUserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to create audio sink: {ex.Message}");
        }

        // Add audio track if we have a source
        if (_audioInputManager.AudioSource != null)
        {
            var audioFormats = _audioInputManager.AudioSource.GetAudioSourceFormats();
            var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.SendRecv);
            pc.addTrack(audioTrack);

            // Wire up audio source to send to peer
            _audioInputManager.AudioSource.OnAudioSourceEncodedSample += pc.SendAudio;
        }

        // Create video decoder for this peer through the decoder manager
        EnsureVideoDecoderForUser(remoteUserId, VideoStreamType.Camera);

        // Add video track (H264)
        var videoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, VideoFps)
        };
        var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendRecv);
        pc.addTrack(videoTrack);

        // Handle audio format negotiation
        pc.OnAudioFormatsNegotiated += (formats) =>
        {
            Console.WriteLine($"WebRTC: Audio formats negotiated with {remoteUserId}: {string.Join(", ", formats.Select(f => f.FormatName))}");
            var format = formats.First();

            _audioInputManager.SetAudioFormat(format);
            audioSink?.SetAudioSinkFormat(format);
        };

        // Handle video format negotiation
        pc.OnVideoFormatsNegotiated += (formats) =>
        {
            Console.WriteLine($"WebRTC: Video formats negotiated with {remoteUserId}: {string.Join(", ", formats.Select(f => f.FormatName))}");
        };

        // Handle incoming audio RTP packets
        pc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                if (!_audioOutputManager.IsDeafened && audioSink != null)
                {
                    audioSink.GotAudioRtp(rep, rtpPkt.Header.SyncSource, rtpPkt.Header.SequenceNumber,
                        rtpPkt.Header.Timestamp, rtpPkt.Header.PayloadType,
                        rtpPkt.Header.MarkerBit == 1, rtpPkt.Payload);
                }
            }
        };

        // Handle incoming video frames - pass to video decoder manager
        var receivedFrameCount = 0;
        pc.OnVideoFrameReceived += (rep, timestamp, frame, format) =>
        {
            receivedFrameCount++;
            if (receivedFrameCount <= 5 || receivedFrameCount % 100 == 0)
            {
                Console.WriteLine($"WebRTC: Received video frame {receivedFrameCount} from {remoteUserId}, size={frame.Length}, format={format.FormatName}");
            }
            // Legacy P2P: all video is camera
            _videoDecoderManager.ProcessFrame(remoteUserId, VideoStreamType.Camera, frame);
        };

        // ICE candidate handling
        pc.onicecandidate += async candidate =>
        {
            if (candidate?.candidate is not null)
            {
                await _signalR.SendIceCandidateAsync(
                    remoteUserId,
                    candidate.candidate,
                    candidate.sdpMid,
                    (int?)candidate.sdpMLineIndex
                );
            }
        };

        // Connection state changes
        pc.onconnectionstatechange += state =>
        {
            Console.WriteLine($"WebRTC: Connection state with {remoteUserId}: {state}");
            _peerStates[remoteUserId] = state switch
            {
                RTCPeerConnectionState.connecting => PeerConnectionState.Connecting,
                RTCPeerConnectionState.connected => PeerConnectionState.Connected,
                RTCPeerConnectionState.disconnected => PeerConnectionState.Disconnected,
                RTCPeerConnectionState.failed => PeerConnectionState.Failed,
                RTCPeerConnectionState.closed => PeerConnectionState.Closed,
                _ => PeerConnectionState.New
            };

            if (state == RTCPeerConnectionState.connected)
            {
                PeerConnected?.Invoke(remoteUserId);
            }
            else if (state == RTCPeerConnectionState.disconnected || state == RTCPeerConnectionState.failed)
            {
                PeerDisconnected?.Invoke(remoteUserId);
            }

            // Update overall connection status
            RecalculateConnectionStatus();
        };

        if (isInitiator)
        {
            // Create and send offer
            var offer = pc.createOffer();
            await pc.setLocalDescription(offer);
            await _signalR.SendWebRtcOfferAsync(remoteUserId, offer.sdp);
            _peerStates[remoteUserId] = PeerConnectionState.Connecting;
        }
    }

    public async Task HandleOfferAsync(Guid fromUserId, string sdp)
    {
        Console.WriteLine($"WebRTC: Received offer from {fromUserId}");

        if (!_peerConnections.TryGetValue(fromUserId, out var pc))
        {
            // Create new peer connection for incoming offer
            await CreatePeerConnectionAsync(fromUserId, isInitiator: false);
            pc = _peerConnections[fromUserId];
        }

        var remoteDesc = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdp
        };

        pc.setRemoteDescription(remoteDesc);

        var answer = pc.createAnswer();
        await pc.setLocalDescription(answer);

        await _signalR.SendWebRtcAnswerAsync(fromUserId, answer.sdp);
        _peerStates[fromUserId] = PeerConnectionState.Connecting;
    }

    public async Task HandleAnswerAsync(Guid fromUserId, string sdp)
    {
        Console.WriteLine($"WebRTC: Received answer from {fromUserId}");

        if (_peerConnections.TryGetValue(fromUserId, out var pc))
        {
            var remoteDesc = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = sdp
            };

            pc.setRemoteDescription(remoteDesc);
        }

        await Task.CompletedTask;
    }

    public async Task HandleIceCandidateAsync(Guid fromUserId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_peerConnections.TryGetValue(fromUserId, out var pc))
        {
            var iceCandidate = new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = sdpMid ?? "0",
                sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
            };

            pc.addIceCandidate(iceCandidate);
        }

        await Task.CompletedTask;
    }

    private void ClosePeerConnection(Guid userId)
    {
        // Unsubscribe audio source from this peer
        if (_peerConnections.TryGetValue(userId, out var pc))
        {
            if (_audioInputManager.AudioSource != null)
            {
                _audioInputManager.AudioSource.OnAudioSourceEncodedSample -= pc.SendAudio;
            }
            // Note: Video encoder doesn't use events - we call SendVideo directly in the capture loop
        }

        if (_peerConnections.TryRemove(userId, out pc))
        {
            Console.WriteLine($"WebRTC: Closing peer connection to {userId}");
            pc.close();
            _peerStates.TryRemove(userId, out _);
            PeerDisconnected?.Invoke(userId);
        }

        if (_audioSinks.TryRemove(userId, out var audioSink))
        {
            try
            {
                audioSink.CloseAudioSink();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error closing audio sink: {ex.Message}");
            }
        }

        RemoveAllVideoDecodersForUser(userId);
    }

    public void StartWatchingScreenShare(Guid userId)
    {
        _watchingScreenShareUserIds.Add(userId);
        Console.WriteLine($"WebRTC: Started watching screen share from user {userId}");
    }

    public void StopWatchingScreenShare(Guid userId)
    {
        _watchingScreenShareUserIds.Remove(userId);
        Console.WriteLine($"WebRTC: Stopped watching screen share from user {userId}");
    }

    public void SetMuted(bool muted)
    {
        _audioInputManager.SetMuted(muted);
    }

    public void SetDeafened(bool deafened)
    {
        _audioOutputManager.SetDeafened(deafened);
    }

    public async Task SetCameraAsync(bool enabled)
    {
        if (_cameraManager.IsCameraOn == enabled) return;

        await _cameraManager.SetCameraAsync(enabled);
        CameraStateChanged?.Invoke(enabled);
    }

    public async Task SetScreenSharingAsync(bool enabled, ScreenShareSettings? settings = null)
    {
        if (_screenShareManager.IsScreenSharing == enabled) return;

        _currentScreenShareSettings = settings;
        Console.WriteLine($"WebRTC: Screen Sharing = {enabled}");

        await _screenShareManager.SetScreenSharingAsync(enabled, settings);
        ScreenSharingStateChanged?.Invoke(enabled);
    }

    private int _sentScreenFrameCount;
    private int _micAudioPacketCount;
    private int _screenAudioPacketCount;

    /// <summary>
    /// Handler for encoded camera video frames from CameraManager. Sends to camera video track.
    /// </summary>
    private void OnCameraVideoEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        // SFU mode: send to server connection via manager
        _sfuConnectionManager.SendCameraVideo(durationRtpUnits, encodedSample);

        // Legacy P2P mode: send to all peer connections
        foreach (var pc in _peerConnections.Values)
        {
            pc.SendVideo(durationRtpUnits, encodedSample);
        }
    }

    /// <summary>
    /// Handler for encoded screen share video frames. Sends to screen video track via manager.
    /// </summary>
    private void OnScreenVideoEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        _sentScreenFrameCount++;
        if (_sentScreenFrameCount <= 5 || _sentScreenFrameCount % 100 == 0)
        {
            Console.WriteLine($"WebRTC: Sending screen frame {_sentScreenFrameCount}, size={encodedSample.Length}");
        }

        // SFU mode: send to server connection via manager
        _sfuConnectionManager.SendScreenVideo(durationRtpUnits, encodedSample);

        // Legacy P2P mode: send to all peer connections (uses default track)
        foreach (var pc in _peerConnections.Values)
        {
            pc.SendVideo(durationRtpUnits, encodedSample);
        }
    }

    /// <summary>
    /// Handler for encoded screen share audio from ScreenShareManager.
    /// Sends via manager using dedicated screen audio track.
    /// </summary>
    private void OnScreenAudioEncoded(uint timestamp, byte[] opusData)
    {
        _sfuConnectionManager.SendScreenAudio(timestamp, opusData);
    }

    private void UpdateConnectionStatus(VoiceConnectionStatus newStatus)
    {
        if (_connectionStatus != newStatus)
        {
            _connectionStatus = newStatus;
            Console.WriteLine($"WebRTC: Connection status changed to {newStatus}");
            ConnectionStatusChanged?.Invoke(newStatus);
        }
    }

    private void RecalculateConnectionStatus()
    {
        if (_currentChannelId is null)
        {
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        // If any peer is connected, we're connected
        if (_peerStates.Any(p => p.Value == PeerConnectionState.Connected))
        {
            UpdateConnectionStatus(VoiceConnectionStatus.Connected);
            return;
        }

        // If any peer is connecting, we're connecting
        if (_peerStates.Any(p => p.Value == PeerConnectionState.Connecting || p.Value == PeerConnectionState.New))
        {
            UpdateConnectionStatus(VoiceConnectionStatus.Connecting);
            return;
        }

        // No peers or all failed/closed - still connected to channel but alone
        UpdateConnectionStatus(VoiceConnectionStatus.Connected);
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveVoiceChannelAsync();
    }
}
