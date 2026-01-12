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

    // SFU mode: single connection to server
    private RTCPeerConnection? _serverConnection;
    private PeerConnectionState _serverConnectionState = PeerConnectionState.Closed;

    // Legacy P2P: keep for backward compatibility if needed
    private readonly ConcurrentDictionary<Guid, RTCPeerConnection> _peerConnections = new();
    private readonly ConcurrentDictionary<Guid, PeerConnectionState> _peerStates = new();

    // Shared audio source (microphone) for server connection
    private SDL2AudioSource? _audioSource;
    // Audio mixer with per-user volume control (replaces SDL2AudioEndPoint)
    private IUserAudioMixer? _audioMixer;
    // Microphone audio SSRC to UserId mapping for per-user volume control
    private readonly ConcurrentDictionary<uint, Guid> _audioSsrcToUserMap = new();
    // Screen audio SSRC to UserId mapping - used to filter screen audio when not watching
    private readonly ConcurrentDictionary<uint, Guid> _screenAudioSsrcToUserMap = new();
    // Legacy P2P: per-peer audio sinks
    private readonly ConcurrentDictionary<Guid, SDL2AudioEndPoint> _audioSinks = new();
    // Per-user video decoders (keyed by userId, server tells us which SSRC maps to which user)
    // Video decoders keyed by (userId, streamType) for camera and screen share
    private readonly ConcurrentDictionary<(Guid userId, VideoStreamType streamType), FfmpegProcessDecoder> _videoDecoders = new();
    // Hardware video decoders for zero-copy GPU pipeline (keyed by userId, streamType)
    private readonly ConcurrentDictionary<(Guid userId, VideoStreamType streamType), IHardwareVideoDecoder> _hardwareDecoders = new();
    // SPS/PPS storage for hardware decoder initialization (stored separately as they may arrive in different frames)
    private readonly ConcurrentDictionary<(Guid userId, VideoStreamType streamType), byte[]> _spsParams = new();
    private readonly ConcurrentDictionary<(Guid userId, VideoStreamType streamType), byte[]> _ppsParams = new();
    // Track streams where hardware decoder failed to avoid retrying every frame
    private readonly ConcurrentDictionary<(Guid userId, VideoStreamType streamType), bool> _hardwareDecoderFailed = new();
    // Per-stream frame assemblers for camera and screen share video
    // These accumulate RTP payloads and assemble complete H264 frames
    private readonly H264FrameAssembler _cameraFrameAssembler = new();
    private readonly H264FrameAssembler _screenFrameAssembler = new();
    // SSRC to UserId mapping for incoming video
    private readonly ConcurrentDictionary<uint, Guid> _ssrcToUserMap = new();
    // SSRC to payload type mapping for detecting camera vs screen share
    private readonly ConcurrentDictionary<uint, int> _ssrcPayloadTypeMap = new();
    // UserIds of screen shares we're currently watching (can watch multiple)
    private readonly HashSet<Guid> _watchingScreenShareUserIds = new();

    // Pending SFU offer (received before JoinVoiceChannelAsync was called)
    private (Guid ChannelId, string Sdp)? _pendingSfuOffer;

    // Video capture and encoding
    private VideoCapture? _videoCapture;
    private FfmpegProcessEncoder? _processEncoder;
    private CancellationTokenSource? _videoCts;
    private Task? _videoCaptureTask;
    private bool _isCameraOn;
    private VideoCodecsEnum _videoCodec = VideoCodecsEnum.H264;
    private const int VideoWidth = 640;
    private const int VideoHeight = 480;
    private const int VideoFps = 15;

    // Screen sharing
    private Process? _screenCaptureProcess;
    private FfmpegProcessEncoder? _screenEncoder;
    private CancellationTokenSource? _screenCts;
    private Task? _screenCaptureTask;
    private Task? _screenAudioTask;  // For reading audio from SnackaCapture
    private bool _isScreenSharing;
    private bool _isUsingSnackaCapture;  // True when using native capture with audio
    private ScreenShareSettings? _currentScreenShareSettings;
    // Default screen share settings (used as fallback)
    private const int DefaultScreenWidth = 1920;
    private const int DefaultScreenHeight = 1080;
    private const int DefaultScreenFps = 30;
    // SnackaCapture audio packet header
    private const uint SnackaCaptureAudioMagic = 0x4D434150;  // "MCAP" in little-endian
    // Screen audio encoding - Opus at 48kHz stereo
    private uint _screenAudioTimestamp;
    private AudioEncoder? _screenAudioEncoder;
    private AudioFormat? _screenAudioFormat;
    private const int ScreenAudioSampleRate = 48000;  // SnackaCapture outputs 48kHz
    private const int ScreenAudioChannels = 2;        // Stereo for screen share (music, videos, games)
    private const int AudioPacketDurationMs = 20;     // 20ms audio packets
    // Opus at 48kHz stereo: 48000 * 0.020 * 2 = 1920 samples per packet (960 per channel)
    private const int OpusSamplesPerPacket = ScreenAudioSampleRate * AudioPacketDurationMs / 1000 * ScreenAudioChannels;

    // Dual video tracks for simultaneous camera + screen share
    private MediaStreamTrack? _cameraVideoTrack;
    private MediaStreamTrack? _screenVideoTrack;
    private uint _cameraVideoSsrc;
    private uint _screenVideoSsrc;

    // Separate audio track for screen share audio (distinct from microphone audio)
    // The track's SSRC is detected by the server via payload type 112
    private MediaStreamTrack? _screenAudioTrack;
    private uint _screenAudioSsrc;
    private bool _screenAudioFirstPacket = true;  // Track first packet for RTP marker bit
    private int _screenAudioDiagCount;  // Diagnostic counter for logging
    private int _screenAudioEncodeCount;  // Diagnostic counter for encoding

    private Guid? _currentChannelId;
    private Guid _localUserId;
    private bool _isMuted;
    private bool _isDeafened;
    private VoiceConnectionStatus _connectionStatus = VoiceConnectionStatus.Disconnected;

    // Voice activity detection
    private bool _isSpeaking;
    private DateTime _lastAudioActivity = DateTime.MinValue;
    private Timer? _speakingTimer;
    private const int SpeakingTimeoutMs = 200; // Time before speaking state turns off
    private const int SpeakingCheckIntervalMs = 50;

    // Automatic Gain Control (AGC) for consistent microphone levels
    private float _agcGain = 1.0f;                    // Current AGC gain multiplier
    private const float AgcTargetRms = 6000f;         // Target RMS level (~-15dB from max)
    private const float AgcMinGain = 1.0f;            // Don't reduce below unity (let gate handle silence)
    private const float AgcMaxGain = 16.0f;           // Max 16x boost
    private const float AgcAttackCoeff = 0.1f;        // Fast attack (10% per frame) to catch loud sounds
    private const float AgcReleaseCoeff = 0.005f;     // Slow release (0.5% per frame) for natural sound
    private const float AgcSilenceThreshold = 200f;   // RMS below this is considered silence (don't adjust)
    private const float BaselineInputBoost = 4.0f;    // 4x baseline boost before AGC

    public Guid? CurrentChannelId => _currentChannelId;
    public Guid? CurrentUserId => _localUserId == Guid.Empty ? null : _localUserId;
    public VoiceConnectionStatus ConnectionStatus => _connectionStatus;
    public int ConnectedPeerCount => _peerStates.Count(p => p.Value == PeerConnectionState.Connected);
    public IReadOnlyDictionary<Guid, PeerConnectionState> PeerStates => _peerStates;
    public bool IsSpeaking => _isSpeaking;
    public bool IsCameraOn => _isCameraOn;
    public bool IsScreenSharing => _isScreenSharing;

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
    public bool IsGpuRenderingAvailable => Services.GpuVideo.GpuVideoRendererFactory.IsAvailable();

    /// <summary>
    /// Gets whether hardware video decoding is available on this platform.
    /// Hardware decoding provides zero-copy GPU pipeline: H264 → GPU Decode → GPU Render
    /// </summary>
    public bool IsHardwareDecodingAvailable => HardwareVideoDecoderFactory.IsAvailable();

    public WebRtcService(ISignalRService signalR, ISettingsStore? settingsStore = null)
    {
        _signalR = signalR;
        _settingsStore = settingsStore;

        // Subscribe to SFU signaling events
        _signalR.SfuOfferReceived += async e => await HandleSfuOfferAsync(e.ChannelId, e.Sdp);
        _signalR.SfuIceCandidateReceived += async e => await HandleSfuIceCandidateAsync(e.Candidate, e.SdpMid, e.SdpMLineIndex);

        // Subscribe to SSRC mapping events for per-user volume control
        _signalR.UserAudioSsrcMapped += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                _audioSsrcToUserMap[e.AudioSsrc] = e.UserId;
                Console.WriteLine($"WebRTC: Mapped mic audio SSRC {e.AudioSsrc} to user {e.UserId}");
            }
        };

        // Subscribe to screen audio SSRC mapping events
        _signalR.UserScreenAudioSsrcMapped += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                _screenAudioSsrcToUserMap[e.ScreenAudioSsrc] = e.UserId;
                Console.WriteLine($"WebRTC: Mapped screen audio SSRC {e.ScreenAudioSsrc} to user {e.UserId}");
            }
        };

        _signalR.SsrcMappingsBatchReceived += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                foreach (var mapping in e.Mappings.Where(m => m.AudioSsrc.HasValue))
                {
                    _audioSsrcToUserMap[mapping.AudioSsrc!.Value] = mapping.UserId;
                }
                foreach (var mapping in e.Mappings.Where(m => m.ScreenAudioSsrc.HasValue))
                {
                    _screenAudioSsrcToUserMap[mapping.ScreenAudioSsrc!.Value] = mapping.UserId;
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
        if (_audioSource != null) return;

        try
        {
            // Ensure SDL2 audio is initialized
            NativeLibraryInitializer.EnsureSdl2AudioInitialized();

            // Use selected audio input device from settings
            // Enable Opus for high-quality 48kHz audio
            var audioEncoder = new AudioEncoder(includeOpus: true);
            var inputDevice = _settingsStore?.Settings.AudioInputDevice ?? string.Empty;
            _audioSource = new SDL2AudioSource(inputDevice, audioEncoder);

            // Subscribe to raw audio samples for voice activity detection
            _audioSource.OnAudioSourceRawSample += OnAudioSourceRawSample;

            // Subscribe to error events
            _audioSource.OnAudioSourceError += (error) =>
            {
                Console.WriteLine($"WebRTC: Audio source error: {error}");
            };

            // Set audio format before starting - prefer Opus for high quality (48kHz)
            // Fall back to PCMU if Opus not available (shouldn't happen with SDL2)
            var formats = _audioSource.GetAudioSourceFormats();
            if (formats.Count > 0)
            {
                var selectedFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats[0];
                _audioSource.SetAudioSourceFormat(selectedFormat);
                Console.WriteLine($"WebRTC: Selected audio format: {selectedFormat.FormatName} ({selectedFormat.ClockRate}Hz)");
            }

            // Start the speaking check timer
            _speakingTimer = new Timer(CheckSpeakingTimeout, null, SpeakingCheckIntervalMs, SpeakingCheckIntervalMs);

            // Start capturing audio
            await _audioSource.StartAudio();

            if (_isMuted)
            {
                await _audioSource.PauseAudio();
            }

            Console.WriteLine("WebRTC: Audio source (microphone) initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to initialize audio source: {ex.Message}");
            _audioSource = null;
        }
    }

    private void OnAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        if (_isMuted || sample.Length == 0) return;

        // Get user settings
        var manualGain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Step 1: Calculate input RMS before any processing
        double sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += (double)sample[i] * sample[i];
        }
        float inputRms = (float)Math.Sqrt(sumOfSquares / sample.Length);

        // Step 2: Update AGC gain (only if not silence)
        if (inputRms > AgcSilenceThreshold)
        {
            // Calculate desired gain to reach target RMS
            float desiredGain = AgcTargetRms / inputRms;
            desiredGain = Math.Clamp(desiredGain, AgcMinGain, AgcMaxGain);

            // Smoothly adjust current gain toward desired gain
            // Use fast attack (loud sounds) and slow release (quiet sounds)
            if (desiredGain < _agcGain)
            {
                // Attack: getting quieter (loud input), adjust quickly
                _agcGain += (desiredGain - _agcGain) * AgcAttackCoeff;
            }
            else
            {
                // Release: getting louder (quiet input), adjust slowly
                _agcGain += (desiredGain - _agcGain) * AgcReleaseCoeff;
            }
        }

        // Step 3: Calculate total gain = baseline boost * AGC * manual adjustment
        // Manual gain is centered at 1.0 (100%), range 0-3 (0-300%)
        // Values < 1.0 reduce volume, > 1.0 increase volume
        float totalGain = BaselineInputBoost * _agcGain * manualGain;

        // Step 4: Apply total gain to samples
        for (int i = 0; i < sample.Length; i++)
        {
            float gainedSample = sample[i] * totalGain;
            // Soft clipping to prevent harsh distortion on peaks
            if (gainedSample > 30000f)
                gainedSample = 30000f + (gainedSample - 30000f) * 0.1f;
            else if (gainedSample < -30000f)
                gainedSample = -30000f + (gainedSample + 30000f) * 0.1f;
            // Final hard clamp
            sample[i] = (short)Math.Clamp(gainedSample, short.MinValue, short.MaxValue);
        }

        // Step 5: Calculate output RMS for voice activity detection
        sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += (double)sample[i] * sample[i];
        }
        double outputRms = Math.Sqrt(sumOfSquares / sample.Length);

        // Normalize RMS to 0-1 range for gate comparison
        double normalizedRms = Math.Min(1.0, outputRms / 10000.0);

        // Apply gate: only consider as voice activity if above threshold
        var effectiveThreshold = gateEnabled ? gateThreshold : 0.0;
        var isAboveGate = normalizedRms > effectiveThreshold;

        // If gate is enabled and audio is below threshold, zero out the samples
        if (gateEnabled && !isAboveGate)
        {
            Array.Clear(sample, 0, sample.Length);
        }

        if (isAboveGate)
        {
            _lastAudioActivity = DateTime.UtcNow;
            if (!_isSpeaking)
            {
                _isSpeaking = true;
                SpeakingChanged?.Invoke(true);
            }
        }
    }

    private void CheckSpeakingTimeout(object? state)
    {
        if (_isSpeaking && (DateTime.UtcNow - _lastAudioActivity).TotalMilliseconds > SpeakingTimeoutMs)
        {
            _isSpeaking = false;
            SpeakingChanged?.Invoke(false);
        }
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
        if (_pendingSfuOffer.HasValue && _pendingSfuOffer.Value.ChannelId == channelId)
        {
            Console.WriteLine($"WebRTC: Processing pending SFU offer for channel {channelId}");
            var pending = _pendingSfuOffer.Value;
            _pendingSfuOffer = null;
            await ProcessSfuOfferAsync(pending.ChannelId, pending.Sdp);
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

        // Stop speaking timer
        _speakingTimer?.Dispose();
        _speakingTimer = null;

        // Reset speaking state
        if (_isSpeaking)
        {
            _isSpeaking = false;
            SpeakingChanged?.Invoke(false);
        }

        // Stop video capture
        if (_isCameraOn)
        {
            await StopVideoCaptureAsync();
            _isCameraOn = false;
            CameraStateChanged?.Invoke(false);
        }

        // Stop screen sharing
        if (_isScreenSharing)
        {
            await StopScreenCaptureAsync();
            _isScreenSharing = false;
            ScreenSharingStateChanged?.Invoke(false);
        }

        // Close SFU server connection
        if (_serverConnection != null)
        {
            try
            {
                if (_audioSource != null)
                {
                    _audioSource.OnAudioSourceEncodedSample -= _serverConnection.SendAudio;
                }
                _serverConnection.close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error closing server connection: {ex.Message}");
            }
            _serverConnection = null;
            _serverConnectionState = PeerConnectionState.Closed;
            _cameraVideoTrack = null;
            _screenVideoTrack = null;
        }

        // Close all legacy P2P peer connections
        foreach (var userId in _peerConnections.Keys.ToList())
        {
            ClosePeerConnection(userId);
        }

        // Clean up all video decoders
        foreach (var key in _videoDecoders.Keys.ToList())
        {
            RemoveVideoDecoderForUser(key.userId, key.streamType);
        }
        _ssrcToUserMap.Clear();
        _ssrcPayloadTypeMap.Clear();
        _spsParams.Clear();
        _ppsParams.Clear();
        _hardwareDecoderFailed.Clear();
        _watchingScreenShareUserIds.Clear();
        _cameraFrameAssembler.Reset();
        _screenFrameAssembler.Reset();
        _pendingSfuOffer = null;

        // Stop and dispose audio source (microphone)
        if (_audioSource != null)
        {
            try
            {
                _audioSource.OnAudioSourceRawSample -= OnAudioSourceRawSample;
                await _audioSource.CloseAudio();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error closing audio source: {ex.Message}");
            }
            _audioSource = null;
        }

        // Stop and dispose audio mixer (speaker with per-user volume)
        if (_audioMixer != null)
        {
            try
            {
                await _audioMixer.StopAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error closing audio mixer: {ex.Message}");
            }
            _audioMixer = null;
        }

        // Clear audio SSRC mappings
        _audioSsrcToUserMap.Clear();
        _screenAudioSsrcToUserMap.Clear();

        _currentChannelId = null;
        UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
    }

    // ==================== SFU Methods ====================

    private async Task InitializeAudioMixerAsync()
    {
        if (_audioMixer != null) return;

        try
        {
            var outputDevice = _settingsStore?.Settings.AudioOutputDevice ?? string.Empty;
            _audioMixer = new UserAudioMixer();
            await _audioMixer.StartAsync(outputDevice);

            // Load saved per-user volumes
            LoadUserVolumes();

            Console.WriteLine("WebRTC: Audio mixer initialized with per-user volume control");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to initialize audio mixer: {ex.Message}");
            _audioMixer = null;
        }
    }

    /// <summary>
    /// Loads saved per-user volumes from settings.
    /// </summary>
    private void LoadUserVolumes()
    {
        if (_settingsStore?.Settings.UserVolumes == null || _audioMixer == null) return;

        foreach (var (userIdStr, volume) in _settingsStore.Settings.UserVolumes)
        {
            if (Guid.TryParse(userIdStr, out var userId))
            {
                _audioMixer.SetUserVolume(userId, volume);
            }
        }
        Console.WriteLine($"WebRTC: Loaded {_settingsStore.Settings.UserVolumes.Count} saved user volumes");
    }

    /// <summary>
    /// Sets the volume for a specific user (0.0 - 2.0, where 1.0 is normal).
    /// </summary>
    public void SetUserVolume(Guid userId, float volume)
    {
        _audioMixer?.SetUserVolume(userId, volume);

        // Save to settings
        if (_settingsStore != null)
        {
            _settingsStore.Settings.UserVolumes[userId.ToString()] = volume;
            _settingsStore.Save();
        }
    }

    /// <summary>
    /// Gets the volume for a specific user.
    /// </summary>
    public float GetUserVolume(Guid userId)
    {
        // Try settings first (for users not currently in channel)
        if (_settingsStore?.Settings.UserVolumes.TryGetValue(userId.ToString(), out var savedVolume) == true)
        {
            return savedVolume;
        }
        return _audioMixer?.GetUserVolume(userId) ?? 1.0f;
    }

    private void EnsureVideoDecoderForUser(Guid userId, VideoStreamType streamType)
    {
        // Skip creating decoder for our own streams - we don't need to decode what we're sending
        if (_localUserId != Guid.Empty && userId == _localUserId)
        {
            Console.WriteLine($"WebRTC: Skipping {streamType} decoder for self (user {userId})");
            return;
        }

        var key = (userId, streamType);
        if (_videoDecoders.ContainsKey(key)) return;

        try
        {
            // Use NV12 output format when GPU rendering is available (hardware-accelerated path)
            var useNv12 = IsGpuRenderingAvailable;
            var outputFormat = useNv12 ? DecoderOutputFormat.Nv12 : DecoderOutputFormat.Rgb24;

            // Use 1080p to support both camera and screen share
            var decoder = new FfmpegProcessDecoder(1920, 1080, VideoCodecsEnum.H264, outputFormat);
            decoder.OnDecodedFrame += (width, height, frameData) =>
            {
                if (useNv12)
                {
                    // Fire NV12 event for GPU rendering (fullscreen mode)
                    Nv12VideoFrameReceived?.Invoke(userId, streamType, width, height, frameData);

                    // Also convert NV12→RGB for bitmap display (tile view) if anyone is listening
                    if (VideoFrameReceived != null)
                    {
                        var rgbData = ConvertNv12ToRgb(frameData, width, height);
                        VideoFrameReceived.Invoke(userId, streamType, width, height, rgbData);
                    }
                }
                else
                {
                    // Software path - frame data is already RGB
                    VideoFrameReceived?.Invoke(userId, streamType, width, height, frameData);
                }
            };
            decoder.Start();
            _videoDecoders[key] = decoder;
            Console.WriteLine($"WebRTC: Created {streamType} video decoder for user {userId} (format={outputFormat})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to create {streamType} video decoder for user {userId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to process a complete H264 frame with the hardware decoder.
    /// Returns true if successfully processed, false to fall back to software decoder.
    /// </summary>
    private static bool _loggedHardwareAvailability;
    private bool TryProcessWithHardwareDecoder((Guid userId, VideoStreamType streamType) key, byte[] frame, VideoStreamType streamType)
    {
        // Skip if hardware decoding not available
        if (!IsHardwareDecodingAvailable)
        {
            if (!_loggedHardwareAvailability)
            {
                _loggedHardwareAvailability = true;
                Console.WriteLine($"WebRTC: Hardware decoding not available, using software decoder");
            }
            return false;
        }

        // Skip if hardware decoder already failed for this stream
        if (_hardwareDecoderFailed.ContainsKey(key))
        {
            return false;
        }

        // Parse NAL units from the Annex B frame
        var nalUnits = H264FrameAssembler.FindNalUnits(frame);
        if (nalUnits.Count == 0)
        {
            Console.WriteLine($"WebRTC: Hardware decode - no NAL units found in frame");
            return false;
        }

        // Check for SPS/PPS in this frame and store them separately
        // (they may arrive in different frames from the encoder)
        foreach (var nal in nalUnits)
        {
            if (nal.Length == 0) continue;
            var nalType = nal[0] & 0x1F;
            if (nalType == 7) // SPS
            {
                _spsParams[key] = nal;
                var hex = BitConverter.ToString(nal.Take(Math.Min(16, nal.Length)).ToArray());
                Console.WriteLine($"WebRTC: Stored SPS for {streamType} ({nal.Length} bytes): {hex}");
            }
            else if (nalType == 8) // PPS
            {
                _ppsParams[key] = nal;
                var hex = BitConverter.ToString(nal.Take(Math.Min(16, nal.Length)).ToArray());
                Console.WriteLine($"WebRTC: Stored PPS for {streamType} ({nal.Length} bytes): {hex}");
            }
        }

        // Check if we have a hardware decoder for this stream
        if (!_hardwareDecoders.TryGetValue(key, out var hwDecoder))
        {
            // Try to create one if we have both SPS and PPS
            if (_spsParams.TryGetValue(key, out var sps) && _ppsParams.TryGetValue(key, out var pps))
            {
                Console.WriteLine($"WebRTC: Creating hardware decoder for {streamType}...");
                hwDecoder = HardwareVideoDecoderFactory.Create();
                if (hwDecoder != null)
                {
                    // Initialize with SPS/PPS (assuming 1920x1080 for now, could parse from SPS)
                    Console.WriteLine($"WebRTC: Initializing hardware decoder with SPS/PPS...");
                    if (hwDecoder.Initialize(1920, 1080, sps, pps))
                    {
                        _hardwareDecoders[key] = hwDecoder;
                        Console.WriteLine($"WebRTC: Created hardware decoder for {streamType} (user {key.userId})");

                        // Notify listeners that hardware decoder is ready
                        HardwareDecoderReady?.Invoke(key.userId, streamType, hwDecoder);
                    }
                    else
                    {
                        hwDecoder.Dispose();
                        hwDecoder = null;
                        _hardwareDecoderFailed[key] = true;
                        Console.WriteLine($"WebRTC: Failed to initialize hardware decoder for {streamType}, will use software decoder");
                    }
                }
                else
                {
                    Console.WriteLine($"WebRTC: HardwareVideoDecoderFactory.Create() returned null");
                }
            }
            else
            {
                var hasSps = _spsParams.ContainsKey(key);
                var hasPps = _ppsParams.ContainsKey(key);
                Console.WriteLine($"WebRTC: Waiting for SPS/PPS for {streamType} (have SPS={hasSps}, have PPS={hasPps})");
            }
        }

        // If we have a hardware decoder, send NAL units to it
        if (hwDecoder != null)
        {
            var nalsSent = 0;
            foreach (var nal in nalUnits)
            {
                if (nal.Length == 0) continue;
                var nalType = nal[0] & 0x1F;

                // Only send VCL NAL units (coded slice data) to the decoder
                // Type 1 = Non-IDR slice (P/B frame)
                // Type 5 = IDR slice (keyframe)
                // Skip all other NAL types (SPS=7, PPS=8, SEI=6, AUD=9, etc.)
                if (nalType != 1 && nalType != 5)
                {
                    continue;
                }

                // Determine if this is a keyframe (IDR)
                var isKeyframe = nalType == 5;

                // Decode and render
                hwDecoder.DecodeAndRender(nal, isKeyframe);
                nalsSent++;
            }
            if (nalsSent > 0)
            {
                Console.WriteLine($"WebRTC: Hardware decoded {nalsSent} NAL units for {streamType}");
            }
            return true;
        }

        Console.WriteLine($"WebRTC: No hardware decoder available for {streamType}, falling back to software");
        return false;
    }

    /// <summary>
    /// Converts NV12 (YUV 4:2:0) to RGB24 for bitmap display.
    /// </summary>
    private static byte[] ConvertNv12ToRgb(byte[] nv12Data, int width, int height)
    {
        var rgbData = new byte[width * height * 3];
        var yPlaneSize = width * height;

        // Use parallel processing for speed
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                // Y value (full resolution)
                var yIndex = y * width + x;
                var yValue = nv12Data[yIndex];

                // UV values (half resolution, interleaved)
                var uvIndex = yPlaneSize + (y / 2) * width + (x / 2) * 2;
                var uValue = nv12Data[uvIndex];
                var vValue = nv12Data[uvIndex + 1];

                // YUV to RGB conversion (BT.601)
                var c = yValue - 16;
                var d = uValue - 128;
                var e = vValue - 128;

                var r = Clamp((298 * c + 409 * e + 128) >> 8);
                var g = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                var b = Clamp((298 * c + 516 * d + 128) >> 8);

                var rgbIndex = (y * width + x) * 3;
                rgbData[rgbIndex] = (byte)r;
                rgbData[rgbIndex + 1] = (byte)g;
                rgbData[rgbIndex + 2] = (byte)b;
            }
        });

        return rgbData;
    }

    private static int Clamp(int value) => value < 0 ? 0 : (value > 255 ? 255 : value);

    private void RemoveVideoDecoderForUser(Guid userId, VideoStreamType streamType)
    {
        var key = (userId, streamType);

        // Remove software decoder
        if (_videoDecoders.TryRemove(key, out var decoder))
        {
            try
            {
                decoder.Dispose();
                Console.WriteLine($"WebRTC: Removed {streamType} software decoder for user {userId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error disposing software decoder: {ex.Message}");
            }
        }

        // Remove hardware decoder
        if (_hardwareDecoders.TryRemove(key, out var hwDecoder))
        {
            try
            {
                hwDecoder.Dispose();
                Console.WriteLine($"WebRTC: Removed {streamType} hardware decoder for user {userId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error disposing hardware decoder: {ex.Message}");
            }
        }

        // Remove SPS/PPS storage and failure flag
        _spsParams.TryRemove(key, out _);
        _ppsParams.TryRemove(key, out _);
        _hardwareDecoderFailed.TryRemove(key, out _);
    }

    private void RemoveAllVideoDecodersForUser(Guid userId)
    {
        RemoveVideoDecoderForUser(userId, VideoStreamType.Camera);
        RemoveVideoDecoderForUser(userId, VideoStreamType.ScreenShare);
    }

    /// <summary>
    /// Handles the SDP offer from the SFU server.
    /// Creates a server connection and sends back an answer.
    /// </summary>
    private async Task HandleSfuOfferAsync(Guid channelId, string sdp)
    {
        // If we don't have a current channel yet, cache the offer for later
        if (_currentChannelId == null)
        {
            Console.WriteLine($"WebRTC: Caching SFU offer for channel {channelId} (no current channel yet)");
            _pendingSfuOffer = (channelId, sdp);
            return;
        }

        if (_currentChannelId != channelId)
        {
            Console.WriteLine($"WebRTC: Ignoring SFU offer for channel {channelId} (current: {_currentChannelId})");
            return;
        }

        await ProcessSfuOfferAsync(channelId, sdp);
    }

    /// <summary>
    /// Actually processes the SFU offer - separated out so it can be called from HandleSfuOfferAsync or JoinVoiceChannelAsync
    /// </summary>
    private async Task ProcessSfuOfferAsync(Guid channelId, string sdp)
    {
        // Validate SDP parameter
        if (string.IsNullOrEmpty(sdp))
        {
            Console.WriteLine($"WebRTC: Error - SFU offer has null or empty SDP for channel {channelId}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        Console.WriteLine($"WebRTC: Received SFU offer for channel {channelId}");

        // Close existing server connection if any
        if (_serverConnection != null)
        {
            try
            {
                _serverConnection.close();
            }
            catch { }
            _serverConnection = null;
        }

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        _serverConnection = new RTCPeerConnection(config);
        _serverConnectionState = PeerConnectionState.New;

        // Add audio track for sending our microphone audio to server
        if (_audioSource != null)
        {
            var audioFormats = _audioSource.GetAudioSourceFormats();
            var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.SendRecv);
            _serverConnection.addTrack(audioTrack);
            _audioSource.OnAudioSourceEncodedSample += _serverConnection.SendAudio;
        }

        // Add TWO video tracks: one for camera, one for screen share
        // This allows simultaneous camera + screen sharing

        // Camera video track (first track - will be used by default SendVideo)
        var cameraVideoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, VideoFps)
        };
        _cameraVideoTrack = new MediaStreamTrack(cameraVideoFormats, MediaStreamStatusEnum.SendRecv);
        _serverConnection.addTrack(_cameraVideoTrack);

        // Screen share video track (second track)
        var screenVideoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, DefaultScreenFps)
        };
        _screenVideoTrack = new MediaStreamTrack(screenVideoFormats, MediaStreamStatusEnum.SendRecv);
        _serverConnection.addTrack(_screenVideoTrack);

        // Screen share audio track (separate from microphone audio for clean separation)
        // This allows viewers to control screen audio independently
        if (_audioSource != null)
        {
            var screenAudioFormats = _audioSource.GetAudioSourceFormats();
            _screenAudioTrack = new MediaStreamTrack(screenAudioFormats, MediaStreamStatusEnum.SendRecv);
            _serverConnection.addTrack(_screenAudioTrack);
            Console.WriteLine("WebRTC SFU: Added separate screen audio track");
        }

        // Handle audio format negotiation - log only, mixer handles all formats
        _serverConnection.OnAudioFormatsNegotiated += formats =>
        {
            Console.WriteLine($"WebRTC SFU: Audio formats negotiated: {string.Join(", ", formats.Select(f => f.FormatName))}");
            // UserAudioMixer handles both PCMU and PCMA based on payload type
        };

        // Handle video format negotiation
        _serverConnection.OnVideoFormatsNegotiated += formats =>
        {
            Console.WriteLine($"WebRTC SFU: Video formats negotiated: {string.Join(", ", formats.Select(f => f.FormatName))}");
            if (formats != null && formats.Any())
            {
                _videoCodec = formats.First().Codec;
            }
        };

        // Handle incoming RTP packets (audio and video from server)
        // Track video RTP for stream type detection (PT 96 = camera, PT 97 = screen)
        int screenAudioPacketCount = 0;
        _serverConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio && !_isDeafened && _audioMixer != null)
            {
                var ssrc = rtpPkt.Header.SyncSource;
                var payloadType = rtpPkt.Header.PayloadType;

                // Check if this is screen audio (PT 112) - only play if watching any screen share
                // Note: We can't use SSRC to identify the sender because the server's forwarding
                // changes the SSRC. Instead, we play PT 112 audio if we're watching any screen share.
                if (payloadType == 112)
                {
                    screenAudioPacketCount++;

                    // Check if we're watching any screen share
                    Guid? watchedUserId = null;
                    lock (_watchingScreenShareUserIds)
                    {
                        if (_watchingScreenShareUserIds.Count == 0)
                        {
                            // Not watching any screen share, skip screen audio
                            return;
                        }
                        // Use the first watched user for volume control
                        // (if watching multiple, they all get the same volume for now)
                        watchedUserId = _watchingScreenShareUserIds.First();
                    }

                    if (screenAudioPacketCount <= 5 || screenAudioPacketCount % 500 == 0)
                    {
                        Console.WriteLine($"WebRTC: Playing screen audio packet #{screenAudioPacketCount}, size={rtpPkt.Payload.Length}");
                    }

                    _audioMixer.ProcessAudioPacket(
                        ssrc,
                        watchedUserId,
                        rtpPkt.Header.SequenceNumber,
                        rtpPkt.Header.Timestamp,
                        payloadType,
                        rtpPkt.Header.MarkerBit == 1,
                        rtpPkt.Payload);
                    return;
                }

                // Microphone audio - always play (with per-user volume control)
                Guid? userId = _audioSsrcToUserMap.TryGetValue(ssrc, out var uid) ? uid : null;

                // Diagnostic logging for first few mic audio packets
                _micAudioPacketCount++;
                if (_micAudioPacketCount <= 5 || _micAudioPacketCount % 500 == 0)
                {
                    Console.WriteLine($"WebRTC: Mic audio packet #{_micAudioPacketCount}, PT={payloadType}, size={rtpPkt.Payload.Length}, ssrc={ssrc}, user={userId}");
                }

                _audioMixer.ProcessAudioPacket(
                    ssrc,
                    userId,
                    rtpPkt.Header.SequenceNumber,
                    rtpPkt.Header.Timestamp,
                    payloadType,
                    rtpPkt.Header.MarkerBit == 1,
                    rtpPkt.Payload);
            }
            else if (media == SDPMediaTypesEnum.video)
            {
                // Route video RTP directly to our own frame assemblers based on payload type
                // This bypasses SIPSorcery's internal video processing for reliable stream separation
                var ssrc = rtpPkt.Header.SyncSource;
                var payloadType = rtpPkt.Header.PayloadType;
                var timestamp = rtpPkt.Header.Timestamp;
                var markerBit = rtpPkt.Header.MarkerBit == 1;
                _ssrcPayloadTypeMap[ssrc] = payloadType;

                // Select the appropriate frame assembler based on payload type
                var assembler = payloadType == 97 ? _screenFrameAssembler : _cameraFrameAssembler;
                var streamType = payloadType == 97 ? VideoStreamType.ScreenShare : VideoStreamType.Camera;

                // Process packet and check for complete frame
                var completeFrame = assembler.ProcessPacket(rtpPkt.Payload, timestamp, markerBit);
                if (completeFrame != null)
                {
                    // Get userId from SSRC, or try to match with watched screen shares
                    Guid userId;
                    if (_ssrcToUserMap.TryGetValue(ssrc, out var uid))
                    {
                        userId = uid;
                    }
                    else if (streamType == VideoStreamType.ScreenShare && _watchingScreenShareUserIds.Count > 0)
                    {
                        // For screen share with unknown SSRC, try to assign to a watched user
                        // Find a watched user who doesn't yet have an SSRC assigned
                        var unassignedUser = _watchingScreenShareUserIds
                            .FirstOrDefault(watchedUserId => !_ssrcToUserMap.Values.Contains(watchedUserId));

                        if (unassignedUser != Guid.Empty)
                        {
                            userId = unassignedUser;
                            _ssrcToUserMap[ssrc] = userId;
                            Console.WriteLine($"WebRTC: Mapped screen share SSRC {ssrc} to user {userId}");
                        }
                        else
                        {
                            // All watched users already have SSRCs, use first one as fallback
                            userId = _watchingScreenShareUserIds.First();
                        }
                    }
                    else
                    {
                        userId = Guid.Empty;
                    }
                    var decoderKey = (userId, streamType);

                    // Try to use hardware decoder first (zero-copy GPU pipeline)
                    var handledByHardware = TryProcessWithHardwareDecoder(decoderKey, completeFrame, streamType);

                    if (!handledByHardware)
                    {
                        // Fall back to software decoder (ffmpeg)
                        var decoder = _videoDecoders
                            .Where(kvp => kvp.Key.streamType == streamType)
                            .Select(kvp => kvp.Value)
                            .FirstOrDefault();

                        if (decoder != null)
                        {
                            try
                            {
                                decoder.DecodeFrame(completeFrame);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"WebRTC: Decode error for {streamType}: {ex.Message}");
                            }
                        }
                    }
                }
            }
        };

        // NOTE: We no longer use SIPSorcery's OnVideoFrameReceived callback for video routing.
        // Instead, we handle video RTP directly in OnRtpPacketReceived above, routing to our
        // own H264FrameAssembler instances per stream type for reliable camera/screen separation.

        // ICE candidate handling
        _serverConnection.onicecandidate += async candidate =>
        {
            if (candidate?.candidate != null && _currentChannelId.HasValue)
            {
                await _signalR.SendSfuIceCandidateAsync(
                    _currentChannelId.Value,
                    candidate.candidate,
                    candidate.sdpMid,
                    (int?)candidate.sdpMLineIndex
                );
            }
        };

        // Connection state changes
        _serverConnection.onconnectionstatechange += state =>
        {
            Console.WriteLine($"WebRTC SFU: Connection state: {state}");
            _serverConnectionState = state switch
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
                UpdateConnectionStatus(VoiceConnectionStatus.Connected);
                // Capture SSRCs for dual video tracks after connection is established
                CaptureVideoTrackSsrcs();
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            }
        };

        // Set remote description (server's offer)
        var remoteDesc = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdp
        };

        try
        {
            _serverConnection.setRemoteDescription(remoteDesc);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC SFU: Failed to set remote description: {ex.Message}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        // Create and send answer
        RTCSessionDescriptionInit? answer;
        try
        {
            answer = _serverConnection.createAnswer();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC SFU: Failed to create answer: {ex.Message}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        if (answer == null)
        {
            Console.WriteLine("WebRTC SFU: Failed to create answer - createAnswer() returned null");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        if (string.IsNullOrEmpty(answer.sdp))
        {
            Console.WriteLine("WebRTC SFU: Failed to create answer - answer.sdp is null or empty");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        try
        {
            await _serverConnection.setLocalDescription(answer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC SFU: Failed to set local description: {ex.Message}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        Console.WriteLine($"WebRTC SFU: Sending answer to server");
        await _signalR.SendSfuAnswerAsync(channelId, answer.sdp);
    }

    /// <summary>
    /// Captures the SSRCs assigned to our video and audio tracks after connection is established.
    /// This is needed for dual-track sending - camera/mic use default Send methods,
    /// screen share video/audio use SendRtpRaw with specific SSRCs.
    /// </summary>
    private void CaptureVideoTrackSsrcs()
    {
        try
        {
            // Get video tracks from the peer connection
            var videoTracks = _serverConnection?.VideoStreamList;
            if (videoTracks == null || videoTracks.Count == 0)
            {
                Console.WriteLine("WebRTC: No video tracks found to capture SSRCs");
            }
            else
            {
                // Camera track is first, screen track is second
                if (videoTracks.Count >= 1 && videoTracks[0].LocalTrack != null)
                {
                    _cameraVideoSsrc = videoTracks[0].LocalTrack.Ssrc;
                    Console.WriteLine($"WebRTC: Camera video track SSRC = {_cameraVideoSsrc}");
                }

                if (videoTracks.Count >= 2 && videoTracks[1].LocalTrack != null)
                {
                    _screenVideoSsrc = videoTracks[1].LocalTrack.Ssrc;
                    Console.WriteLine($"WebRTC: Screen video track SSRC = {_screenVideoSsrc}");
                }
                else
                {
                    // If we only have one video stream but two tracks were added,
                    // generate a unique SSRC for the screen track
                    _screenVideoSsrc = (uint)new Random().Next(100000, int.MaxValue);
                    Console.WriteLine($"WebRTC: Generated screen video track SSRC = {_screenVideoSsrc}");
                }
            }

            // Get audio tracks - mic audio track is first, screen audio track is second
            var audioTracks = _serverConnection?.AudioStreamList;
            if (audioTracks != null && audioTracks.Count >= 2 && audioTracks[1].LocalTrack != null)
            {
                _screenAudioSsrc = audioTracks[1].LocalTrack.Ssrc;
                Console.WriteLine($"WebRTC: Screen audio track SSRC = {_screenAudioSsrc}");
            }
            else
            {
                // Generate a unique SSRC for screen audio
                _screenAudioSsrc = (uint)new Random().Next(100000, int.MaxValue);
                Console.WriteLine($"WebRTC: Generated screen audio track SSRC = {_screenAudioSsrc}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Error capturing track SSRCs: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles ICE candidates from the SFU server.
    /// </summary>
    private async Task HandleSfuIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_serverConnection == null)
        {
            Console.WriteLine("WebRTC SFU: Ignoring ICE candidate - no server connection");
            return;
        }

        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid ?? "0",
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };

        _serverConnection.addIceCandidate(iceCandidate);
        await Task.CompletedTask;
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
        if (_audioSource != null)
        {
            var audioFormats = _audioSource.GetAudioSourceFormats();
            var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.SendRecv);
            pc.addTrack(audioTrack);

            // Wire up audio source to send to peer
            _audioSource.OnAudioSourceEncodedSample += pc.SendAudio;
        }

        // Create video decoder for this peer (using FFmpeg subprocess)
        // We'll create it lazily when we know the video dimensions, or use a default
        FfmpegProcessDecoder? videoDecoder = null;
        try
        {
            // Use 1080p to support both camera (upscaled) and screen share (native)
            videoDecoder = new FfmpegProcessDecoder(1920, 1080, VideoCodecsEnum.H264);
            videoDecoder.OnDecodedFrame += (width, height, rgbData) =>
            {
                // Legacy P2P: all video is camera
                VideoFrameReceived?.Invoke(remoteUserId, VideoStreamType.Camera, width, height, rgbData);
            };
            videoDecoder.Start();
            _videoDecoders[(remoteUserId, VideoStreamType.Camera)] = videoDecoder;

            Console.WriteLine($"WebRTC: Video decoder created for {remoteUserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to create video decoder: {ex.Message}");
        }

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

            _audioSource?.SetAudioSourceFormat(format);
            audioSink?.SetAudioSinkFormat(format);
        };

        // Handle video format negotiation
        pc.OnVideoFormatsNegotiated += (formats) =>
        {
            Console.WriteLine($"WebRTC: Video formats negotiated with {remoteUserId}: {string.Join(", ", formats.Select(f => f.FormatName))}");
            var format = formats.First();
            _videoCodec = format.Codec;
        };

        // Handle incoming audio RTP packets
        pc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                if (!_isDeafened && audioSink != null)
                {
                    audioSink.GotAudioRtp(rep, rtpPkt.Header.SyncSource, rtpPkt.Header.SequenceNumber,
                        rtpPkt.Header.Timestamp, rtpPkt.Header.PayloadType,
                        rtpPkt.Header.MarkerBit == 1, rtpPkt.Payload);
                }
            }
        };

        // Handle incoming video frames - pass to FFmpeg decoder
        var receivedFrameCount = 0;
        pc.OnVideoFrameReceived += (rep, timestamp, frame, format) =>
        {
            receivedFrameCount++;
            if (receivedFrameCount <= 5 || receivedFrameCount % 100 == 0)
            {
                Console.WriteLine($"WebRTC: Received video frame {receivedFrameCount} from {remoteUserId}, size={frame.Length}, format={format.FormatName}");
            }
            try
            {
                videoDecoder?.DecodeFrame(frame);
            }
            catch (Exception ex)
            {
                if (receivedFrameCount <= 5)
                {
                    Console.WriteLine($"WebRTC: DecodeFrame error: {ex.Message}");
                }
            }
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
            if (_audioSource != null)
            {
                _audioSource.OnAudioSourceEncodedSample -= pc.SendAudio;
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
        _isMuted = muted;
        Console.WriteLine($"WebRTC: Muted = {muted}");

        if (_audioSource != null)
        {
            try
            {
                if (muted)
                {
                    _ = _audioSource.PauseAudio();
                }
                else
                {
                    _ = _audioSource.ResumeAudio();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error setting mute state: {ex.Message}");
            }
        }
    }

    public void SetDeafened(bool deafened)
    {
        _isDeafened = deafened;
        Console.WriteLine($"WebRTC: Deafened = {deafened}");

        // When deafened, we still receive audio but don't pass it to the sinks
        // This is handled in the OnRtpPacketReceived callback
    }

    public async Task SetCameraAsync(bool enabled)
    {
        if (_isCameraOn == enabled) return;

        Console.WriteLine($"WebRTC: Camera = {enabled}");

        if (enabled)
        {
            // Start capture BEFORE setting state - if it throws, state remains false
            await StartVideoCaptureAsync();
            _isCameraOn = true;
        }
        else
        {
            _isCameraOn = false;
            await StopVideoCaptureAsync();
        }

        CameraStateChanged?.Invoke(enabled);
    }

    public async Task SetScreenSharingAsync(bool enabled, ScreenShareSettings? settings = null)
    {
        if (_isScreenSharing == enabled) return;

        _currentScreenShareSettings = settings;
        Console.WriteLine($"WebRTC: Screen Sharing = {enabled}");

        if (enabled)
        {
            // Phase 2: Allow both camera and screen sharing simultaneously
            // Each goes to a separate video track
            // Start capture BEFORE setting state - if it throws, state remains false
            await StartScreenCaptureAsync(settings);
            _isScreenSharing = true;
        }
        else
        {
            _isScreenSharing = false;
            await StopScreenCaptureAsync();
        }

        ScreenSharingStateChanged?.Invoke(enabled);
    }

    private async Task StartScreenCaptureAsync(ScreenShareSettings? settings = null)
    {
        if (_screenCaptureProcess != null) return;

        // Use settings or defaults
        var screenWidth = settings?.Resolution.Width ?? DefaultScreenWidth;
        var screenHeight = settings?.Resolution.Height ?? DefaultScreenHeight;
        var screenFps = settings?.Framerate.Fps ?? DefaultScreenFps;
        var source = settings?.Source;

        try
        {
            Console.WriteLine($"WebRTC: Starting screen capture... (source: {source?.Name ?? "default"}, {screenWidth}x{screenHeight} @ {screenFps}fps)");

            // Check if we should use native capture (macOS SnackaCapture or Windows SnackaCaptureWindows)
            string? nativeCapturePath = null;
            string nativeCaptureArgs = "";
            var captureAudio = settings?.IncludeAudio ?? false;

            if (ShouldUseSnackaCapture())
            {
                nativeCapturePath = GetSnackaCapturePath();
                if (nativeCapturePath != null)
                {
                    nativeCaptureArgs = GetSnackaCaptureArgs(source, screenWidth, screenHeight, screenFps, captureAudio);
                }
            }
            else if (ShouldUseSnackaCaptureWindows())
            {
                nativeCapturePath = GetSnackaCaptureWindowsPath();
                if (nativeCapturePath != null)
                {
                    nativeCaptureArgs = GetSnackaCaptureWindowsArgs(source, screenWidth, screenHeight, screenFps, captureAudio);
                }
            }

            _isUsingSnackaCapture = nativeCapturePath != null;

            // Create encoder with appropriate pixel format:
            // - NV12 for native capture (hardware-accelerated)
            // - BGR24 for ffmpeg fallback (software path)
            var inputPixelFormat = _isUsingSnackaCapture ? "nv12" : "bgr24";
            _screenEncoder = new FfmpegProcessEncoder(screenWidth, screenHeight, screenFps, VideoCodecsEnum.H264, inputPixelFormat);
            _screenEncoder.OnEncodedFrame += OnScreenVideoEncoded;
            _screenEncoder.Start();

            if (_isUsingSnackaCapture)
            {
                // Use native capture with audio support
                var args = nativeCaptureArgs;

                // Initialize Opus encoder for screen audio if audio capture is enabled
                if (captureAudio)
                {
                    _screenAudioEncoder = new AudioEncoder(includeOpus: true);
                    // Get Opus format from encoder's supported formats
                    var opusFormat = _screenAudioEncoder.SupportedFormats.FirstOrDefault(f => f.FormatName == "OPUS");
                    _screenAudioTimestamp = 0;
                    _screenAudioFirstPacket = true;  // Reset for new stream
                    if (!string.IsNullOrEmpty(opusFormat.FormatName))
                    {
                        // Create stereo version of Opus format for screen audio (music, videos, games)
                        // Microphone uses mono, but screen share benefits from stereo
                        var stereoOpusFormat = new AudioFormat(
                            opusFormat.Codec,
                            opusFormat.FormatID,
                            opusFormat.ClockRate,
                            ScreenAudioChannels,  // 2 channels for stereo
                            opusFormat.Parameters);
                        _screenAudioFormat = stereoOpusFormat;
                        Console.WriteLine($"WebRTC: Screen audio encoder initialized ({stereoOpusFormat.FormatName} {stereoOpusFormat.ClockRate}Hz, {stereoOpusFormat.ChannelCount} ch stereo)");

                        // Verify we're encoding at 48kHz (standard for Opus)
                        if (stereoOpusFormat.ClockRate != 48000)
                        {
                            Console.WriteLine($"WebRTC: WARNING - Screen audio encoder is {stereoOpusFormat.ClockRate}Hz, expected 48000Hz!");
                        }
                    }
                    else
                    {
                        Console.WriteLine("WebRTC: Warning - Opus format not available for screen audio");
                    }
                }

                Console.WriteLine($"WebRTC: Using native capture: {nativeCapturePath} {args}");

                _screenCaptureProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = nativeCapturePath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,  // Audio comes on stderr
                        CreateNoWindow = true
                    }
                };

                _screenCaptureProcess.Start();

                // Start reading screen frames and audio
                _screenCts = new CancellationTokenSource();
                _screenCaptureTask = Task.Run(() => ScreenCaptureLoop(_screenCts.Token, screenWidth, screenHeight, screenFps));
                _screenAudioTask = Task.Run(() => ScreenAudioLoop(_screenCts.Token));

                Console.WriteLine($"WebRTC: Screen capture started with native capture (audio: {captureAudio})");
            }
            else
            {
                // Fall back to ffmpeg
                var ffmpegPath = "ffmpeg";
                var (captureDevice, inputDevice, extraArgs) = GetScreenCaptureArgs(source);

                // Build platform-specific capture args
                string args;
                if (OperatingSystem.IsMacOS())
                {
                    // macOS avfoundation: capture at native rate then use fps filter to get desired framerate
                    // This avoids frame duplication issues when capture rate doesn't match requested rate
                    args = $"-f avfoundation -capture_cursor 1 -pixel_format uyvy422 -i \"{inputDevice}\" " +
                           $"-vf \"fps={screenFps},scale={screenWidth}:{screenHeight}:force_original_aspect_ratio=decrease,pad={screenWidth}:{screenHeight}:(ow-iw)/2:(oh-ih)/2,format=bgr24\" " +
                           $"-f rawvideo -pix_fmt bgr24 pipe:1";
                }
                else
                {
                    args = $"-f {captureDevice} {extraArgs}-framerate {screenFps} -i \"{inputDevice}\" " +
                           $"-vf \"scale={screenWidth}:{screenHeight}:force_original_aspect_ratio=decrease,pad={screenWidth}:{screenHeight}:(ow-iw)/2:(oh-ih)/2,format=bgr24\" " +
                           $"-f rawvideo -pix_fmt bgr24 pipe:1";
                }

                Console.WriteLine($"WebRTC: Screen capture command: {ffmpegPath} {args}");

                _screenCaptureProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                _screenCaptureProcess.Start();

                // Start reading screen frames
                _screenCts = new CancellationTokenSource();
                _screenCaptureTask = Task.Run(() => ScreenCaptureLoop(_screenCts.Token, screenWidth, screenHeight, screenFps));

                Console.WriteLine("WebRTC: Screen capture started with ffmpeg (no audio)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to start screen capture: {ex.Message}");
            await StopScreenCaptureAsync();
            _isScreenSharing = false;
            ScreenSharingStateChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// Gets FFmpeg capture arguments based on platform and source.
    /// Returns (captureDevice, inputDevice, extraArgs).
    /// </summary>
    private (string captureDevice, string inputDevice, string extraArgs) GetScreenCaptureArgs(ScreenCaptureSource? source)
    {
        if (OperatingSystem.IsMacOS())
        {
            // macOS: avfoundation with "Capture screen N"
            var displayIndex = source?.Id ?? "0";
            return ("avfoundation", $"Capture screen {displayIndex}", "");
        }

        if (OperatingSystem.IsWindows())
        {
            if (source == null || source.Type == ScreenCaptureSourceType.Display)
            {
                // Windows display capture via gdigrab
                // For multi-monitor, we'd need to specify offset, but "desktop" captures primary
                return ("gdigrab", "desktop", "");
            }
            else
            {
                // Window capture via gdigrab with window title
                // The source.Id contains the window title for gdigrab
                return ("gdigrab", $"title={source.Id}", "");
            }
        }

        if (OperatingSystem.IsLinux())
        {
            if (source == null || source.Type == ScreenCaptureSourceType.Display)
            {
                // Linux display capture via x11grab
                // source.Id contains the :0.0+x,y format for multi-monitor
                var displayId = source?.Id ?? ":0.0";
                return ("x11grab", displayId, "");
            }
            else
            {
                // Window capture via x11grab with window ID
                // Need to get window geometry first - for now, use root window
                // TODO: Implement proper window capture with xwininfo
                Console.WriteLine("WebRTC: Linux window capture not fully implemented, capturing root");
                return ("x11grab", ":0.0", "");
            }
        }

        // Fallback
        return ("x11grab", ":0.0", "");
    }

    /// <summary>
    /// Checks if SnackaCapture (native ScreenCaptureKit) should be used.
    /// Returns true on macOS 13+ where we have the native capture tool.
    /// </summary>
    private bool ShouldUseSnackaCapture()
    {
        if (!OperatingSystem.IsMacOS()) return false;

        // Environment.OSVersion.Version returns Darwin kernel version on macOS
        // Darwin 22.x = macOS 13 Ventura, Darwin 23.x = macOS 14, Darwin 24.x = macOS 15
        // We need macOS 13+ for ScreenCaptureKit audio support
        var darwinVersion = Environment.OSVersion.Version.Major;
        if (darwinVersion < 22)
        {
            Console.WriteLine($"WebRTC: macOS Darwin version {darwinVersion} < 22, SnackaCapture requires macOS 13+");
            return false;
        }

        // Check if the binary exists
        var snackaCapturePath = GetSnackaCapturePath();
        if (snackaCapturePath != null && File.Exists(snackaCapturePath))
        {
            Console.WriteLine($"WebRTC: SnackaCapture available at {snackaCapturePath}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if SnackaCaptureWindows (native Desktop Duplication + WASAPI) should be used.
    /// Returns true on Windows 10+ where we have the native capture tool.
    /// </summary>
    private bool ShouldUseSnackaCaptureWindows()
    {
        if (!OperatingSystem.IsWindows()) return false;

        // Windows 10 build 1803+ required for Desktop Duplication API
        // Environment.OSVersion.Version.Build >= 17134 for Win10 1803
        var build = Environment.OSVersion.Version.Build;
        if (build < 17134)
        {
            Console.WriteLine($"WebRTC: Windows build {build} < 17134, SnackaCaptureWindows requires Windows 10 1803+");
            return false;
        }

        // Check if the binary exists
        var capturePath = GetSnackaCaptureWindowsPath();
        if (capturePath != null && File.Exists(capturePath))
        {
            Console.WriteLine($"WebRTC: SnackaCaptureWindows available at {capturePath}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the path to the SnackaCapture binary.
    /// </summary>
    private string? GetSnackaCapturePath()
    {
        // Look for SnackaCapture in several locations:
        // 1. Same directory as the app (bundled)
        // 2. Swift 6+ architecture-specific build paths (arm64-apple-macosx)
        // 3. Legacy Swift build paths (fallback)

        var appDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            // Bundled with app
            Path.Combine(appDir, "SnackaCapture"),
            // Swift 6+ architecture-specific build paths (arm64-apple-macosx)
            Path.Combine(appDir, "..", "SnackaCapture", ".build", "arm64-apple-macosx", "release", "SnackaCapture"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCapture", ".build", "arm64-apple-macosx", "release", "SnackaCapture"),
            Path.Combine(appDir, "..", "SnackaCapture", ".build", "arm64-apple-macosx", "debug", "SnackaCapture"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCapture", ".build", "arm64-apple-macosx", "debug", "SnackaCapture"),
            // Legacy Swift build paths (fallback)
            Path.Combine(appDir, "..", "SnackaCapture", ".build", "release", "SnackaCapture"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCapture", ".build", "release", "SnackaCapture"),
            Path.Combine(appDir, "..", "SnackaCapture", ".build", "debug", "SnackaCapture"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCapture", ".build", "debug", "SnackaCapture"),
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"WebRTC: Found SnackaCapture at {fullPath}");
                return fullPath;
            }
        }

        Console.WriteLine("WebRTC: SnackaCapture not found, will use ffmpeg");
        return null;
    }

    /// <summary>
    /// Gets the path to the SnackaCaptureWindows binary.
    /// </summary>
    private string? GetSnackaCaptureWindowsPath()
    {
        // Look for SnackaCaptureWindows in several locations:
        // 1. Same directory as the app (bundled)
        // 2. CMake build paths (Release/Debug)

        var appDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            // Bundled with app
            Path.Combine(appDir, "SnackaCaptureWindows.exe"),
            // CMake build paths
            Path.Combine(appDir, "..", "SnackaCaptureWindows", "build", "bin", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureWindows", "build", "bin", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "SnackaCaptureWindows", "build", "bin", "Release", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureWindows", "build", "bin", "Release", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "SnackaCaptureWindows", "build", "bin", "Debug", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureWindows", "build", "bin", "Debug", "SnackaCaptureWindows.exe"),
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"WebRTC: Found SnackaCaptureWindows at {fullPath}");
                return fullPath;
            }
        }

        Console.WriteLine("WebRTC: SnackaCaptureWindows not found, will use ffmpeg");
        return null;
    }

    /// <summary>
    /// Builds SnackaCaptureWindows command arguments based on source and settings.
    /// </summary>
    private string GetSnackaCaptureWindowsArgs(ScreenCaptureSource? source, int width, int height, int fps, bool captureAudio)
    {
        var args = new List<string>();

        // Source type - Windows version doesn't have a "capture" subcommand
        if (source == null || source.Type == ScreenCaptureSourceType.Display)
        {
            var displayIndex = source?.Id ?? "0";
            args.Add($"--display {displayIndex}");
        }
        else if (source.Type == ScreenCaptureSourceType.Window)
        {
            // Windows uses HWND (window handle) for window capture
            args.Add($"--window {source.Id}");
        }
        // Note: Application capture not supported on Windows yet

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Audio - WASAPI loopback captures all system audio
        if (captureAudio)
        {
            args.Add("--audio");
        }

        return string.Join(" ", args);
    }

    /// <summary>
    /// Builds SnackaCapture command arguments based on source and settings.
    /// </summary>
    private string GetSnackaCaptureArgs(ScreenCaptureSource? source, int width, int height, int fps, bool captureAudio)
    {
        var args = new List<string> { "capture" };

        // Source type
        if (source == null || source.Type == ScreenCaptureSourceType.Display)
        {
            var displayIndex = source?.Id ?? "0";
            args.Add($"--display {displayIndex}");
        }
        else if (source.Type == ScreenCaptureSourceType.Window)
        {
            args.Add($"--window {source.Id}");
        }
        else if (source.Type == ScreenCaptureSourceType.Application)
        {
            // Use bundleId for application capture (captures all windows of the app)
            var bundleId = source.BundleId ?? source.Id;
            args.Add($"--app {bundleId}");
        }

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Audio
        if (captureAudio)
        {
            args.Add("--audio");
            args.Add("--exclude-self");  // Don't capture SnackaCapture's own audio

            // Exclude Snacka.Client's audio to prevent capturing other users' voices
            // Try to get the bundle ID - for .NET apps this may be the process name or a custom ID
            var bundleId = GetAppBundleId();
            if (!string.IsNullOrEmpty(bundleId))
            {
                // Quote the bundle ID in case it contains spaces
                args.Add($"--exclude-app \"{bundleId}\"");
                Console.WriteLine($"WebRTC: Will exclude audio from app: {bundleId}");
            }
        }

        return string.Join(" ", args);
    }

    /// <summary>
    /// Gets the bundle identifier for the current app.
    /// On macOS, this is used to exclude our app's audio from screen capture.
    /// </summary>
    private string? GetAppBundleId()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        try
        {
            // For .NET apps, use the process name as a fallback
            // ScreenCaptureKit can find apps by bundle ID or we match by process name
            var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            // If running from IDE/dotnet run, process name is typically "dotnet"
            // In that case, we can't reliably exclude ourselves
            if (processName == "dotnet")
            {
                Console.WriteLine("WebRTC: Running via 'dotnet' - cannot determine bundle ID to exclude");
                return null;
            }

            // Use the process name - ScreenCaptureKit might match by app name
            // For a published app, this would be "Snacka.Client" or similar
            return processName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads audio packets from SnackaCapture's stderr.
    /// Audio format: 16-byte header (magic + sampleCount + timestamp) followed by PCM data.
    /// Note: stderr may also contain log messages, so we need to scan for the magic number.
    /// </summary>
    private void ScreenAudioLoop(CancellationToken token)
    {
        Console.WriteLine("WebRTC: Screen audio loop starting");
        var audioPacketCount = 0;
        var skippedBytes = 0;

        try
        {
            var stream = _screenCaptureProcess?.StandardError.BaseStream;
            if (stream == null) return;

            // Ring buffer for scanning - we need at least 4 bytes to check for magic
            var scanBuffer = new byte[4];
            var scanIndex = 0;
            var headerBuffer = new byte[24];  // Version 2 header: 4+1+1+1+1+4+4+8 = 24 bytes

            while (!token.IsCancellationRequested && _screenCaptureProcess != null && !_screenCaptureProcess.HasExited)
            {
                // Scan for magic number byte-by-byte
                // This handles log messages mixed with audio data on stderr
                bool foundMagic = false;
                while (!foundMagic && !token.IsCancellationRequested)
                {
                    // Read one byte at a time into scan buffer
                    var b = stream.ReadByte();
                    if (b < 0) break; // EOF

                    // Shift buffer and add new byte
                    scanBuffer[0] = scanBuffer[1];
                    scanBuffer[1] = scanBuffer[2];
                    scanBuffer[2] = scanBuffer[3];
                    scanBuffer[3] = (byte)b;
                    scanIndex++;

                    // Need at least 4 bytes before checking
                    if (scanIndex < 4) continue;

                    // Check for magic number (little-endian: 0x4D434150 = "MCAP")
                    var magic = BitConverter.ToUInt32(scanBuffer, 0);
                    if (magic == SnackaCaptureAudioMagic)
                    {
                        foundMagic = true;
                        // Copy magic to header buffer
                        Array.Copy(scanBuffer, 0, headerBuffer, 0, 4);
                    }
                    else if (scanIndex > 4)
                    {
                        skippedBytes++;
                    }
                }

                if (!foundMagic) break;

                // Read bytes 4-7 to determine header version
                // Header v1 (16 bytes): magic(4) + sampleCount(4) + timestamp(8)
                // Header v2 (24 bytes): magic(4) + version(1) + bitsPerSample(1) + channels(1) + isFloat(1) + sampleCount(4) + sampleRate(4) + timestamp(8)
                var peekBytes = new byte[4];
                var peekRead = 0;
                while (peekRead < 4)
                {
                    var r = stream.Read(peekBytes, peekRead, 4 - peekRead);
                    if (r == 0) break;
                    peekRead += r;
                }
                if (peekRead < 4) break;
                Array.Copy(peekBytes, 0, headerBuffer, 4, 4);

                // V2 header has: version=2, bitsPerSample=16 or 32, channels=1-8, isFloat=0 or 1
                // V1 header has: sampleCount at bytes 4-7 (typically 960-2000 for 20ms audio)
                var byte4 = peekBytes[0];
                var byte5 = peekBytes[1];
                var byte6 = peekBytes[2];
                var byte7 = peekBytes[3];

                // Detect v2: version==2, reasonable bitsPerSample (16 or 32), reasonable channels (1-8)
                bool isV2Header = byte4 == 2 && (byte5 == 16 || byte5 == 32) && byte6 >= 1 && byte6 <= 8;

                if (audioPacketCount == 0)
                {
                    Console.WriteLine($"WebRTC: Audio header bytes 4-7: [{byte4}, {byte5}, {byte6}, {byte7}], detected as {(isV2Header ? "v2" : "v1")}");
                }
                int headerSize = isV2Header ? 24 : 16;

                // We already have bytes 0-7 (magic + first 4 bytes), read the rest
                var headerRead = 8;
                while (headerRead < headerSize && !token.IsCancellationRequested)
                {
                    var read = stream.Read(headerBuffer, headerRead, headerSize - headerRead);
                    if (read == 0) break;
                    headerRead += read;
                }

                if (headerRead < headerSize) break;

                uint sampleCount;
                uint sampleRate;
                byte bitsPerSample;
                byte channels;
                bool isFloat;
                ulong timestamp;

                if (isV2Header)
                {
                    // V2 header with format info
                    bitsPerSample = headerBuffer[5];
                    channels = headerBuffer[6];
                    isFloat = headerBuffer[7] != 0;
                    sampleCount = BitConverter.ToUInt32(headerBuffer, 8);
                    sampleRate = BitConverter.ToUInt32(headerBuffer, 12);
                    timestamp = BitConverter.ToUInt64(headerBuffer, 16);
                }
                else
                {
                    // V1 header - assume 16-bit stereo at 48kHz (old format)
                    sampleCount = BitConverter.ToUInt32(headerBuffer, 4);
                    timestamp = BitConverter.ToUInt64(headerBuffer, 8);
                    bitsPerSample = 16;
                    channels = 2;
                    isFloat = false;
                    sampleRate = 48000;

                    if (audioPacketCount == 0)
                    {
                        Console.WriteLine("WebRTC: Detected v1 audio header format - assuming 16-bit stereo 48kHz. Rebuild SnackaCapture for proper format detection.");
                    }
                }

                // Sanity check - audio buffers shouldn't be huge
                if (sampleCount > 48000 * 10) // More than 10 seconds of audio
                {
                    Console.WriteLine($"WebRTC: Invalid audio sample count {sampleCount}, skipping");
                    scanIndex = 0; // Reset scan
                    continue;
                }

                // Calculate bytes per frame from header format info
                var bytesPerSample = bitsPerSample / 8;
                var bytesPerFrame = bytesPerSample * channels;
                var audioSize = (int)(sampleCount * bytesPerFrame);
                var audioBuffer = new byte[audioSize];

                var audioRead = 0;
                while (audioRead < audioSize && !token.IsCancellationRequested)
                {
                    var read = stream.Read(audioBuffer, audioRead, audioSize - audioRead);
                    if (read == 0) break;
                    audioRead += read;
                }

                if (audioRead < audioSize) break;

                audioPacketCount++;
                if (audioPacketCount <= 5 || audioPacketCount % 100 == 0)
                {
                    Console.WriteLine($"WebRTC: Screen audio packet {audioPacketCount}, samples={sampleCount}, {sampleRate}Hz, {bitsPerSample}-bit, {channels}ch, {(isFloat ? "float" : "int")}, skipped={skippedBytes} bytes");
                }

                // Reset for next packet
                scanIndex = 0;

                // Process and send screen audio (resample, encode, transmit)
                ProcessScreenShareAudio(audioBuffer, sampleCount, sampleRate, bitsPerSample, channels, isFloat);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"WebRTC: Screen audio loop error: {ex.Message}");
            }
        }

        Console.WriteLine($"WebRTC: Screen audio loop ended after {audioPacketCount} packets, skipped {skippedBytes} bytes");
    }

    /// <summary>
    /// Processes screen share audio: encodes audio to Opus and sends via dedicated screen audio track.
    /// Uses SendRtpRaw with screen audio SSRC to keep it separate from microphone audio.
    /// SnackaCapture normalizes all audio to 48kHz 16-bit stereo before sending.
    /// </summary>
    private void ProcessScreenShareAudio(byte[] pcmData, uint sampleCount, uint sampleRate, byte bitsPerSample, byte channelCount, bool isFloat)
    {
        if (_serverConnection == null || _screenAudioEncoder == null || !_screenAudioFormat.HasValue || pcmData.Length == 0) return;
        if (_screenAudioSsrc == 0)
        {
            Console.WriteLine("WebRTC: Screen audio SSRC not initialized, skipping audio packet");
            return;
        }

        var format = _screenAudioFormat.Value;

        // Input: 48kHz 16-bit stereo from SnackaCapture (normalized at capture stage)
        // Output: Opus at 48kHz STEREO for high-quality screen share audio (music, videos, games)

        // Read stereo samples directly (interleaved L,R,L,R,...)
        int frameCount = (int)sampleCount;  // Number of stereo frames
        var stereoSamples = new short[frameCount * 2];  // 2 samples per frame (L,R)

        for (int i = 0; i < frameCount && (i * 4 + 3) < pcmData.Length; i++)
        {
            // Read stereo pair (little-endian Int16)
            short left = (short)(pcmData[i * 4] | (pcmData[i * 4 + 1] << 8));
            short right = (short)(pcmData[i * 4 + 2] | (pcmData[i * 4 + 3] << 8));
            // Keep as stereo (interleaved)
            stereoSamples[i * 2] = left;
            stereoSamples[i * 2 + 1] = right;
        }

        // Diagnostic: Log sample values and peak level for first few packets
        _screenAudioDiagCount++;
        if (_screenAudioDiagCount <= 3)
        {
            // Find peak sample value
            short peak = 0;
            for (int i = 0; i < Math.Min(200, stereoSamples.Length); i++)
            {
                if (Math.Abs(stereoSamples[i]) > Math.Abs(peak)) peak = stereoSamples[i];
            }
            Console.WriteLine($"WebRTC: Screen audio diag #{_screenAudioDiagCount}: {frameCount} stereo frames @ {sampleRate}Hz {bitsPerSample}-bit -> {stereoSamples.Length} samples stereo, peak={peak}");
        }

        // Process in 20ms chunks
        // For stereo at 48kHz: 960 frames * 2 channels = 1920 samples per packet
        const int framesPerPacket = ScreenAudioSampleRate * AudioPacketDurationMs / 1000;  // 960 frames
        const int samplesPerPacket = framesPerPacket * ScreenAudioChannels;  // 1920 samples (stereo)

        // Screen audio uses payload type 112 to distinguish from microphone audio (PT 111)
        // Both use Opus codec but server routes them differently
        const int ScreenAudioPayloadType = 112;

        int offset = 0;
        while (offset + samplesPerPacket <= stereoSamples.Length)
        {
            // Extract one packet's worth of stereo samples
            var packetSamples = new short[samplesPerPacket];
            Array.Copy(stereoSamples, offset, packetSamples, 0, samplesPerPacket);

            try
            {
                // Encode to Opus (stereo)
                var opusData = _screenAudioEncoder.EncodeAudio(packetSamples, format);

                if (opusData != null && opusData.Length > 0)
                {
                    // Diagnostic: Log encoded size for first few packets
                    _screenAudioEncodeCount++;
                    if (_screenAudioEncodeCount <= 5)
                    {
                        Console.WriteLine($"WebRTC: Screen audio encode #{_screenAudioEncodeCount}: {samplesPerPacket} stereo samples -> {opusData.Length} bytes Opus");
                    }

                    // Send screen audio with payload type 112 to distinguish from mic audio (PT 111)
                    // The server will detect PT 112 and track it as screen audio SSRC
                    // Marker bit: 1 only for first packet (start of talk spurt), 0 for subsequent
                    var markerBit = _screenAudioFirstPacket ? 1 : 0;
                    _screenAudioFirstPacket = false;

                    _serverConnection.SendRtpRaw(
                        SDPMediaTypesEnum.audio,
                        opusData,
                        _screenAudioTimestamp,
                        markerBit,
                        ScreenAudioPayloadType);

                    // RTP timestamp increments by frames (not samples) at 48kHz
                    // For 20ms at 48kHz: 960 frames
                    _screenAudioTimestamp += (uint)framesPerPacket;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error encoding/sending screen audio: {ex.Message}");
            }

            offset += samplesPerPacket;
        }
    }
    private void ScreenCaptureLoop(CancellationToken token, int width, int height, int fps)
    {
        // NV12: 1.5 bytes per pixel (Y plane + UV plane at half resolution)
        // BGR24: 3 bytes per pixel
        var isNv12 = _isUsingSnackaCapture;
        var frameSize = isNv12 ? (width * height * 3 / 2) : (width * height * 3);
        var buffer = new byte[frameSize];
        var frameCount = 0;

        // Calculate preview skip rate based on fps (show ~15fps preview max)
        var previewSkip = Math.Max(1, fps / 15);

        Console.WriteLine($"WebRTC: Screen capture loop starting - {width}x{height} @ {fps}fps (format: {(isNv12 ? "NV12" : "BGR24")})");

        try
        {
            var stream = _screenCaptureProcess?.StandardOutput.BaseStream;
            if (stream == null) return;

            while (!token.IsCancellationRequested && _screenCaptureProcess != null && !_screenCaptureProcess.HasExited)
            {
                var bytesRead = 0;
                while (bytesRead < frameSize && !token.IsCancellationRequested)
                {
                    var read = stream.Read(buffer, bytesRead, frameSize - bytesRead);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead < frameSize) break;

                frameCount++;

                // Send frame to encoder (NV12 or BGR24 depending on capture mode)
                _screenEncoder?.EncodeFrame(buffer);

                // Generate preview (skip frames to reduce overhead, targeting ~15fps preview)
                if (frameCount % previewSkip == 0)
                {
                    byte[] rgbData;
                    if (isNv12)
                    {
                        // Convert NV12 to RGB for preview
                        rgbData = ConvertNv12ToRgb(buffer, width, height);
                    }
                    else
                    {
                        // Convert BGR to RGB for preview
                        var rgbSize = width * height * 3;
                        rgbData = new byte[rgbSize];
                        for (var i = 0; i < rgbSize; i += 3)
                        {
                            rgbData[i] = buffer[i + 2];     // R
                            rgbData[i + 1] = buffer[i + 1]; // G
                            rgbData[i + 2] = buffer[i];     // B
                        }
                    }
                    LocalVideoFrameCaptured?.Invoke(VideoStreamType.ScreenShare, width, height, rgbData);
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"WebRTC: Screen capture loop error: {ex.Message}");
            }
        }

        Console.WriteLine($"WebRTC: Screen capture loop ended after {frameCount} frames");
    }

    private async Task StopScreenCaptureAsync()
    {
        Console.WriteLine("WebRTC: Stopping screen capture...");

        _screenCts?.Cancel();

        // Wait for video capture task
        if (_screenCaptureTask != null)
        {
            try
            {
                await _screenCaptureTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("WebRTC: Screen capture task did not stop in time");
            }
            catch (OperationCanceledException) { }
            _screenCaptureTask = null;
        }

        // Wait for audio task (if using SnackaCapture)
        if (_screenAudioTask != null)
        {
            try
            {
                await _screenAudioTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("WebRTC: Screen audio task did not stop in time");
            }
            catch (OperationCanceledException) { }
            _screenAudioTask = null;
        }

        _screenCts?.Dispose();
        _screenCts = null;
        _isUsingSnackaCapture = false;

        if (_screenCaptureProcess != null)
        {
            try
            {
                if (!_screenCaptureProcess.HasExited)
                {
                    _screenCaptureProcess.Kill();
                    await _screenCaptureProcess.WaitForExitAsync();
                }
            }
            catch { }
            _screenCaptureProcess.Dispose();
            _screenCaptureProcess = null;
        }

        if (_screenEncoder != null)
        {
            _screenEncoder.OnEncodedFrame -= OnScreenVideoEncoded;
            _screenEncoder.Dispose();
            _screenEncoder = null;
        }

        // Clean up screen audio encoder
        _screenAudioEncoder = null;
        _screenAudioFormat = null;

        Console.WriteLine("WebRTC: Screen capture stopped");
    }

    private async Task StartVideoCaptureAsync()
    {
        if (_videoCapture != null) return;

        try
        {
            // Get video device from settings
            var deviceIndex = 0;
            var devicePath = _settingsStore?.Settings.VideoDevice;
            if (!string.IsNullOrEmpty(devicePath) && int.TryParse(devicePath, out var parsed))
            {
                deviceIndex = parsed;
            }

            // Use AVFoundation on macOS, V4L2 on Linux for correct device mapping
            var backend = VideoCapture.API.Any;
            if (OperatingSystem.IsMacOS())
            {
                backend = VideoCapture.API.AVFoundation;
            }
            else if (OperatingSystem.IsLinux())
            {
                backend = VideoCapture.API.V4L2;
            }

            Console.WriteLine($"WebRTC: Starting video capture on device {deviceIndex} with backend {backend}");
            _videoCapture = new VideoCapture(deviceIndex, backend);

            if (!_videoCapture.IsOpened)
            {
                throw new InvalidOperationException($"Failed to open camera {deviceIndex}");
            }

            // Set capture properties
            _videoCapture.Set(CapProp.FrameWidth, VideoWidth);
            _videoCapture.Set(CapProp.FrameHeight, VideoHeight);
            _videoCapture.Set(CapProp.Fps, VideoFps);

            var actualWidth = (int)_videoCapture.Get(CapProp.FrameWidth);
            var actualHeight = (int)_videoCapture.Get(CapProp.FrameHeight);

            Console.WriteLine($"WebRTC: Video capture opened - {actualWidth}x{actualHeight}");

            // Create FFmpeg process encoder for H264 (hardware accelerated on most platforms)
            _processEncoder = new FfmpegProcessEncoder(actualWidth, actualHeight, VideoFps, VideoCodecsEnum.H264);
            _processEncoder.OnEncodedFrame += OnCameraVideoEncoded;
            _processEncoder.Start();
            _videoCodec = VideoCodecsEnum.H264;
            Console.WriteLine($"WebRTC: Video encoder created for {_videoCodec}");

            // Start capture loop
            _videoCts = new CancellationTokenSource();
            _videoCaptureTask = Task.Run(() => VideoCaptureLoop(actualWidth, actualHeight, _videoCts.Token));

            Console.WriteLine("WebRTC: Video capture started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to start video capture - {ex.Message}");
            await StopVideoCaptureAsync();
            throw;
        }
    }

    private int _sentCameraFrameCount;
    private int _sentScreenFrameCount;
    private int _micAudioPacketCount;

    /// <summary>
    /// Handler for encoded camera video frames. Sends to camera video track.
    /// </summary>
    private void OnCameraVideoEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        _sentCameraFrameCount++;
        if (_sentCameraFrameCount <= 5 || _sentCameraFrameCount % 100 == 0)
        {
            Console.WriteLine($"WebRTC: Sending camera frame {_sentCameraFrameCount}, size={encodedSample.Length}");
        }

        // SFU mode: send to server connection (camera track is first, so SendVideo goes there)
        if (_serverConnection != null && _serverConnectionState == PeerConnectionState.Connected)
        {
            _serverConnection.SendVideo(durationRtpUnits, encodedSample);
        }

        // Legacy P2P mode: send to all peer connections
        foreach (var pc in _peerConnections.Values)
        {
            pc.SendVideo(durationRtpUnits, encodedSample);
        }
    }

    // RTP timestamp tracking for screen video
    private uint _screenRtpTimestamp;
    private ushort _screenRtpSeqNum;

    /// <summary>
    /// Handler for encoded screen share video frames. Sends to screen video track
    /// using SendRtpRaw with the screen track's SSRC for proper dual-track support.
    /// </summary>
    private void OnScreenVideoEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        _sentScreenFrameCount++;
        if (_sentScreenFrameCount <= 5 || _sentScreenFrameCount % 100 == 0)
        {
            Console.WriteLine($"WebRTC: Sending screen frame {_sentScreenFrameCount}, size={encodedSample.Length}, SSRC={_screenVideoSsrc}");
        }

        // SFU mode: send to server connection using payload type 97 for screen share
        // This allows the server to distinguish screen share from camera video
        if (_serverConnection != null && _serverConnectionState == PeerConnectionState.Connected)
        {
            try
            {
                SendH264ForScreenShare(encodedSample, durationRtpUnits, ref _screenRtpTimestamp, ref _screenRtpSeqNum);
            }
            catch (Exception ex)
            {
                if (_sentScreenFrameCount <= 5)
                {
                    Console.WriteLine($"WebRTC: Error sending screen frame: {ex.Message}");
                }
            }
        }

        // Legacy P2P mode: send to all peer connections (uses default track)
        foreach (var pc in _peerConnections.Values)
        {
            pc.SendVideo(durationRtpUnits, encodedSample);
        }
    }

    /// <summary>
    /// Sends H264 encoded data for screen share using payload type 97.
    /// Handles NAL unit packetization (single NAL or FU-A fragmentation).
    /// The different payload type (97 vs 96 for camera) allows server to
    /// distinguish screen share from camera video.
    /// </summary>
    private void SendH264ForScreenShare(byte[] encodedSample, uint durationRtpUnits, ref uint rtpTimestamp, ref ushort seqNum)
    {
        const int MaxRtpPayloadSize = 1400; // Leave room for headers
        const byte H264PayloadType = 97; // Screen share uses payload type 97 (camera uses 96)

        // Find NAL units in the encoded sample (separated by 00 00 00 01 or 00 00 01)
        var nalUnits = H264FrameAssembler.FindNalUnits(encodedSample);

        // Update timestamp
        rtpTimestamp += durationRtpUnits;

        for (int i = 0; i < nalUnits.Count; i++)
        {
            var nalUnit = nalUnits[i];
            bool isLastNalUnit = (i == nalUnits.Count - 1);

            if (nalUnit.Length <= MaxRtpPayloadSize)
            {
                // Single NAL unit packet - send as-is
                seqNum++;
                _serverConnection?.SendRtpRaw(
                    SDPMediaTypesEnum.video,
                    nalUnit,
                    rtpTimestamp,
                    isLastNalUnit ? 1 : 0, // Marker bit on last NAL of frame
                    H264PayloadType);
            }
            else
            {
                // Large NAL unit - fragment using FU-A
                SendFuAFragments(nalUnit, rtpTimestamp, isLastNalUnit, H264PayloadType, ref seqNum, MaxRtpPayloadSize);
            }
        }
    }

    /// <summary>
    /// Sends a large NAL unit using FU-A fragmentation (RFC 6184).
    /// </summary>
    private void SendFuAFragments(byte[] nalUnit, uint rtpTimestamp, bool isLastNalUnit,
        byte payloadType, ref ushort seqNum, int maxPayloadSize)
    {
        if (nalUnit.Length == 0) return;

        byte nalHeader = nalUnit[0];
        byte nalType = (byte)(nalHeader & 0x1F);
        byte nri = (byte)(nalHeader & 0x60);

        // FU indicator: same NRI as original, type = 28 (FU-A)
        byte fuIndicator = (byte)(nri | 28);

        int offset = 1; // Skip NAL header
        bool isFirst = true;

        // Fragment payload size (account for FU indicator + FU header)
        int fragmentPayloadSize = maxPayloadSize - 2;

        while (offset < nalUnit.Length)
        {
            int remaining = nalUnit.Length - offset;
            int fragmentSize = Math.Min(remaining, fragmentPayloadSize);
            bool isLast = (offset + fragmentSize >= nalUnit.Length);

            // FU header: S=start, E=end, R=0, Type=original NAL type
            byte fuHeader = nalType;
            if (isFirst) fuHeader |= 0x80; // S bit
            if (isLast) fuHeader |= 0x40;  // E bit

            // Build FU-A packet: FU indicator + FU header + payload
            var fuPacket = new byte[2 + fragmentSize];
            fuPacket[0] = fuIndicator;
            fuPacket[1] = fuHeader;
            Array.Copy(nalUnit, offset, fuPacket, 2, fragmentSize);

            // Set marker bit only on last fragment of last NAL unit in frame
            int marker = (isLast && isLastNalUnit) ? 1 : 0;

            seqNum++;
            _serverConnection?.SendRtpRaw(
                SDPMediaTypesEnum.video,
                fuPacket,
                rtpTimestamp,
                marker,
                payloadType);

            offset += fragmentSize;
            isFirst = false;
        }
    }

    private void VideoCaptureLoop(int width, int height, CancellationToken token)
    {
        using var frame = new Mat();
        var frameIntervalMs = 1000 / VideoFps;
        var frameCount = 0;

        Console.WriteLine($"WebRTC: Video capture loop starting - target {width}x{height} @ {VideoFps}fps");

        while (!token.IsCancellationRequested && _videoCapture != null)
        {
            try
            {
                if (!_videoCapture.Read(frame) || frame.IsEmpty)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Get frame dimensions and raw BGR bytes (OpenCV captures in BGR format)
                var frameWidth = frame.Width;
                var frameHeight = frame.Height;
                var dataSize = frameWidth * frameHeight * 3;

                frameCount++;
                if (frameCount == 1 || frameCount % 100 == 0)
                {
                    Console.WriteLine($"WebRTC: Captured frame {frameCount} - {frameWidth}x{frameHeight}, peers: {_peerConnections.Count}");
                }

                // Get BGR data from OpenCV frame
                var bgrData = new byte[dataSize];
                System.Runtime.InteropServices.Marshal.Copy(frame.DataPointer, bgrData, 0, dataSize);

                // Send frame to encoder (encoding happens asynchronously in FfmpegProcessEncoder)
                // SFU mode: check _serverConnection, P2P mode: check _peerConnections
                var hasServerConnection = _serverConnection != null && _serverConnectionState == PeerConnectionState.Connected;
                var hasPeerConnections = _peerConnections.Count > 0;
                if (_processEncoder != null && (hasServerConnection || hasPeerConnections))
                {
                    try
                    {
                        // Send to FFmpeg process for encoding
                        _processEncoder.EncodeFrame(bgrData);
                    }
                    catch (Exception encodeEx)
                    {
                        if (frameCount <= 5 || frameCount % 100 == 0)
                        {
                            Console.WriteLine($"WebRTC: Encoding error on frame {frameCount}: {encodeEx.Message}");
                        }
                    }
                }

                // Fire local preview event (convert BGR to RGB)
                if (LocalVideoFrameCaptured != null && frameCount % 2 == 0) // Every other frame for performance
                {
                    var rgbData = ColorSpaceConverter.BgrToRgb(bgrData, frameWidth, frameHeight);
                    LocalVideoFrameCaptured.Invoke(VideoStreamType.Camera, frameWidth, frameHeight, rgbData);
                }

                Thread.Sleep(frameIntervalMs);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Video capture error - {ex.Message}");
                Console.WriteLine($"WebRTC: Stack trace: {ex.StackTrace}");
                Thread.Sleep(100);
            }
        }

        Console.WriteLine($"WebRTC: Video capture loop ended after {frameCount} frames");
    }

    private async Task StopVideoCaptureAsync()
    {
        Console.WriteLine("WebRTC: Stopping video capture...");

        _videoCts?.Cancel();

        if (_videoCaptureTask != null)
        {
            try
            {
                await _videoCaptureTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("WebRTC: Video capture task did not stop in time");
            }
            catch (OperationCanceledException) { }
            _videoCaptureTask = null;
        }

        _videoCts?.Dispose();
        _videoCts = null;

        // Dispose video encoder
        if (_processEncoder != null)
        {
            _processEncoder.OnEncodedFrame -= OnCameraVideoEncoded;
            _processEncoder.Dispose();
            _processEncoder = null;
        }

        if (_videoCapture != null)
        {
            _videoCapture.Dispose();
            _videoCapture = null;
        }

        Console.WriteLine("WebRTC: Video capture stopped");
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
