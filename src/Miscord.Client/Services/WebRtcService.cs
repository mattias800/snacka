using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Encoders;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace Miscord.Client.Services;

public interface IWebRtcService : IAsyncDisposable
{
    Guid? CurrentChannelId { get; }
    VoiceConnectionStatus ConnectionStatus { get; }
    int ConnectedPeerCount { get; }
    IReadOnlyDictionary<Guid, PeerConnectionState> PeerStates { get; }
    bool IsSpeaking { get; }
    bool IsCameraOn { get; }

    Task JoinVoiceChannelAsync(Guid channelId, IEnumerable<VoiceParticipantResponse> existingParticipants);
    Task LeaveVoiceChannelAsync();

    Task HandleOfferAsync(Guid fromUserId, string sdp);
    Task HandleAnswerAsync(Guid fromUserId, string sdp);
    Task HandleIceCandidateAsync(Guid fromUserId, string candidate, string? sdpMid, int? sdpMLineIndex);

    void SetMuted(bool muted);
    void SetDeafened(bool deafened);
    Task SetCameraAsync(bool enabled);

    event Action<Guid>? PeerConnected;
    event Action<Guid>? PeerDisconnected;
    event Action<VoiceConnectionStatus>? ConnectionStatusChanged;
    event Action<bool>? SpeakingChanged;
    event Action<bool>? CameraStateChanged;
    /// <summary>
    /// Fired when a video frame is received from a peer. Args: (userId, width, height, rgbData)
    /// </summary>
    event Action<Guid, int, int, byte[]>? VideoFrameReceived;
    /// <summary>
    /// Fired when a local video frame is captured (for self-preview). Args: (width, height, rgbData)
    /// </summary>
    event Action<int, int, byte[]>? LocalVideoFrameCaptured;
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
    private static bool _ffmpegInitialized;
    private static readonly object _ffmpegInitLock = new();
    private static bool _vpxInitialized;
    private static readonly object _vpxInitLock = new();

    // VPX library paths for macOS
    private static readonly string[] VpxPaths =
    {
        "/opt/homebrew/lib/libvpx.dylib",      // Apple Silicon Homebrew
        "/opt/homebrew/opt/libvpx/lib/libvpx.dylib",
        "/usr/local/lib/libvpx.dylib",         // Intel Homebrew
        "/usr/lib/libvpx.dylib",               // System
        "libvpx.dylib",                        // Current directory / PATH
        "libvpx"                               // Let system find it
    };

    private static IntPtr ResolveVpx(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Handle vpxmd (Windows name) -> libvpx (macOS/Linux name)
        if (libraryName == "vpxmd" || libraryName == "libvpx" || libraryName == "vpx")
        {
            foreach (var path in VpxPaths)
            {
                if (NativeLibrary.TryLoad(path, out var handle))
                {
                    Console.WriteLine($"WebRTC: Loaded VPX from {path}");
                    return handle;
                }
            }
        }
        return IntPtr.Zero;
    }

    private static void EnsureVpxInitialized()
    {
        if (_vpxInitialized) return;

        lock (_vpxInitLock)
        {
            if (_vpxInitialized) return;

            try
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                {
                    // Register DllImportResolver for the VPX encoder assembly
                    var encoderAssembly = typeof(VideoEncoderEndPoint).Assembly;
                    NativeLibrary.SetDllImportResolver(encoderAssembly, ResolveVpx);
                    Console.WriteLine($"WebRTC: Registered VPX DllImportResolver for {encoderAssembly.GetName().Name}");
                }

                _vpxInitialized = true;
                Console.WriteLine("WebRTC: VPX initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Failed to initialize VPX - {ex.Message}");
                _vpxInitialized = true; // Mark as attempted
            }
        }
    }

    private static void EnsureFfmpegInitialized()
    {
        if (_ffmpegInitialized) return;

        lock (_ffmpegInitLock)
        {
            if (_ffmpegInitialized) return;

            try
            {
                // Set FFmpeg library path for macOS
                // Use FFmpeg 6.x which is compatible with FFmpeg.AutoGen 8.0.0
                if (OperatingSystem.IsMacOS())
                {
                    // Try versioned FFmpeg 6 first (compatible with FFmpeg.AutoGen 8.0.0)
                    // Then fall back to default paths
                    var paths = new[]
                    {
                        "/opt/homebrew/opt/ffmpeg@6/lib",  // Apple Silicon Homebrew FFmpeg 6
                        "/usr/local/opt/ffmpeg@6/lib",     // Intel Homebrew FFmpeg 6
                        "/opt/homebrew/lib",               // Apple Silicon Homebrew (default)
                        "/usr/local/lib",                  // Intel Homebrew (default)
                        "/usr/lib"                         // System
                    };

                    foreach (var path in paths)
                    {
                        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "libavcodec.dylib")))
                        {
                            FFmpeg.AutoGen.ffmpeg.RootPath = path;
                            Console.WriteLine($"WebRTC: FFmpeg path set to {path}");
                            break;
                        }
                    }
                }
                else if (OperatingSystem.IsLinux())
                {
                    var paths = new[]
                    {
                        "/usr/lib/x86_64-linux-gnu",
                        "/usr/lib",
                        "/usr/local/lib"
                    };

                    foreach (var path in paths)
                    {
                        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "libavcodec.so")))
                        {
                            FFmpeg.AutoGen.ffmpeg.RootPath = path;
                            Console.WriteLine($"WebRTC: FFmpeg path set to {path}");
                            break;
                        }
                    }
                }

                _ffmpegInitialized = true;
                Console.WriteLine("WebRTC: FFmpeg initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Failed to initialize FFmpeg - {ex.Message}");
                _ffmpegInitialized = true; // Mark as attempted
            }
        }
    }

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
    // Single audio sink for receiving audio from server
    private SDL2AudioEndPoint? _audioSink;
    // Legacy P2P: per-peer audio sinks
    private readonly ConcurrentDictionary<Guid, SDL2AudioEndPoint> _audioSinks = new();
    // Per-user video decoders (keyed by userId, server tells us which SSRC maps to which user)
    private readonly ConcurrentDictionary<Guid, FfmpegProcessDecoder> _videoDecoders = new();
    // SSRC to UserId mapping for incoming video
    private readonly ConcurrentDictionary<uint, Guid> _ssrcToUserMap = new();

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

    public Guid? CurrentChannelId => _currentChannelId;
    public VoiceConnectionStatus ConnectionStatus => _connectionStatus;
    public int ConnectedPeerCount => _peerStates.Count(p => p.Value == PeerConnectionState.Connected);
    public IReadOnlyDictionary<Guid, PeerConnectionState> PeerStates => _peerStates;
    public bool IsSpeaking => _isSpeaking;
    public bool IsCameraOn => _isCameraOn;

    public event Action<Guid>? PeerConnected;
    public event Action<Guid>? PeerDisconnected;
    public event Action<VoiceConnectionStatus>? ConnectionStatusChanged;
    public event Action<bool>? SpeakingChanged;
    public event Action<bool>? CameraStateChanged;
    public event Action<Guid, int, int, byte[]>? VideoFrameReceived;
    /// <summary>
    /// Fired when a local video frame is captured (for self-preview). Args: (width, height, rgbData)
    /// </summary>
    public event Action<int, int, byte[]>? LocalVideoFrameCaptured;

    public WebRtcService(ISignalRService signalR, ISettingsStore? settingsStore = null)
    {
        _signalR = signalR;
        _settingsStore = settingsStore;

        // Subscribe to SFU signaling events
        _signalR.SfuOfferReceived += async e => await HandleSfuOfferAsync(e.ChannelId, e.Sdp);
        _signalR.SfuIceCandidateReceived += async e => await HandleSfuIceCandidateAsync(e.Candidate, e.SdpMid, e.SdpMLineIndex);

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
                // In SFU mode, just ensure we have a video decoder ready for this user
                EnsureVideoDecoderForUser(e.Participant.UserId);
            }
        };

        _signalR.VoiceParticipantLeft += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                // Clean up video decoder for this user
                RemoveVideoDecoderForUser(e.UserId);
                // Legacy P2P cleanup
                ClosePeerConnection(e.UserId);
            }
        };
    }

    public void SetLocalUserId(Guid userId)
    {
        _localUserId = userId;
    }

    // SDL2 P/Invoke for audio init
    [DllImport("SDL2")]
    private static extern int SDL_Init(uint flags);
    private const uint SDL_INIT_AUDIO = 0x00000010;

    private static bool _sdl2AudioInitialized;
    private static readonly object _sdl2InitLock = new();

    private static void EnsureSdl2AudioInitialized()
    {
        if (_sdl2AudioInitialized) return;
        lock (_sdl2InitLock)
        {
            if (_sdl2AudioInitialized) return;
            SDL_Init(SDL_INIT_AUDIO);
            _sdl2AudioInitialized = true;
        }
    }

    private async Task InitializeAudioSourceAsync()
    {
        if (_audioSource != null) return;

        try
        {
            // Ensure SDL2 audio is initialized
            EnsureSdl2AudioInitialized();

            // Use selected audio input device from settings
            var audioEncoder = new AudioEncoder();
            var inputDevice = _settingsStore?.Settings.AudioInputDevice ?? string.Empty;
            _audioSource = new SDL2AudioSource(inputDevice, audioEncoder);

            // Subscribe to raw audio samples for voice activity detection
            _audioSource.OnAudioSourceRawSample += OnAudioSourceRawSample;

            // Subscribe to error events
            _audioSource.OnAudioSourceError += (error) =>
            {
                Console.WriteLine($"WebRTC: Audio source error: {error}");
            };

            // Set audio format before starting - required for SDL2AudioSource to work
            var formats = _audioSource.GetAudioSourceFormats();
            if (formats.Count > 0)
            {
                var selectedFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats[0];
                _audioSource.SetAudioSourceFormat(selectedFormat);
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

        // Get gain and gate settings
        var gain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Calculate RMS (Root Mean Square) for voice activity detection
        // Apply gain during calculation and modify samples in-place
        double sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            // Apply gain to sample
            var gainedSample = sample[i] * gain;
            // Clamp to short range to prevent overflow
            gainedSample = Math.Clamp(gainedSample, short.MinValue, short.MaxValue);
            sample[i] = (short)gainedSample;
            sumOfSquares += gainedSample * gainedSample;
        }
        double rms = Math.Sqrt(sumOfSquares / sample.Length);

        // Normalize RMS to 0-1 range (short.MaxValue = 32767)
        double normalizedRms = Math.Min(1.0, rms / 10000.0);

        // Apply gate: only consider as voice activity if above threshold
        // Gate threshold is in 0-0.5 range, normalized RMS is 0-1
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

        // Initialize audio sink for receiving audio from server
        await InitializeAudioSinkAsync();

        // Prepare video decoders for existing participants
        foreach (var p in existingParticipants)
        {
            if (p.UserId != _localUserId)
            {
                EnsureVideoDecoderForUser(p.UserId);
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
        }

        // Close all legacy P2P peer connections
        foreach (var userId in _peerConnections.Keys.ToList())
        {
            ClosePeerConnection(userId);
        }

        // Clean up all video decoders
        foreach (var userId in _videoDecoders.Keys.ToList())
        {
            RemoveVideoDecoderForUser(userId);
        }
        _ssrcToUserMap.Clear();
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

        // Stop and dispose audio sink (speaker)
        if (_audioSink != null)
        {
            try
            {
                await _audioSink.CloseAudioSink();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error closing audio sink: {ex.Message}");
            }
            _audioSink = null;
        }

        _currentChannelId = null;
        UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
    }

    // ==================== SFU Methods ====================

    private async Task InitializeAudioSinkAsync()
    {
        if (_audioSink != null) return;

        try
        {
            var audioEncoder = new AudioEncoder();
            var outputDevice = _settingsStore?.Settings.AudioOutputDevice ?? string.Empty;
            _audioSink = new SDL2AudioEndPoint(outputDevice, audioEncoder);
            await _audioSink.StartAudioSink();
            Console.WriteLine("WebRTC: Audio sink initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to initialize audio sink: {ex.Message}");
            _audioSink = null;
        }
    }

    private void EnsureVideoDecoderForUser(Guid userId)
    {
        if (_videoDecoders.ContainsKey(userId)) return;

        try
        {
            // Use 720p 16:9 for incoming video (will scale with aspect ratio preservation)
            var decoder = new FfmpegProcessDecoder(1280, 720, VideoCodecsEnum.H264);
            decoder.OnDecodedFrame += (width, height, rgbData) =>
            {
                VideoFrameReceived?.Invoke(userId, width, height, rgbData);
            };
            decoder.Start();
            _videoDecoders[userId] = decoder;
            Console.WriteLine($"WebRTC: Created video decoder for user {userId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebRTC: Failed to create video decoder for user {userId}: {ex.Message}");
        }
    }

    private void RemoveVideoDecoderForUser(Guid userId)
    {
        if (_videoDecoders.TryRemove(userId, out var decoder))
        {
            try
            {
                decoder.Dispose();
                Console.WriteLine($"WebRTC: Removed video decoder for user {userId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error disposing video decoder: {ex.Message}");
            }
        }
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

        // Add video track for sending our camera video to server
        var videoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, VideoFps)
        };
        var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendRecv);
        _serverConnection.addTrack(videoTrack);

        // Handle audio format negotiation - only set sink format, not source (source is already running)
        _serverConnection.OnAudioFormatsNegotiated += formats =>
        {
            Console.WriteLine($"WebRTC SFU: Audio formats negotiated: {string.Join(", ", formats.Select(f => f.FormatName))}");
            _audioSink?.SetAudioSinkFormat(formats.First());
        };

        // Handle video format negotiation
        _serverConnection.OnVideoFormatsNegotiated += formats =>
        {
            Console.WriteLine($"WebRTC SFU: Video formats negotiated: {string.Join(", ", formats.Select(f => f.FormatName))}");
            _videoCodec = formats.First().Codec;
        };

        // Handle incoming RTP packets (audio from server)
        _serverConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio && !_isDeafened && _audioSink != null)
            {
                _audioSink.GotAudioRtp(rep, rtpPkt.Header.SyncSource, rtpPkt.Header.SequenceNumber,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.PayloadType,
                    rtpPkt.Header.MarkerBit == 1, rtpPkt.Payload);
            }
        };

        // Handle incoming video frames from server
        // In SFU mode, server forwards video from all participants
        // TODO: Need SSRC mapping to know which user each video stream is from
        // For now, we'll use a simple approach: route to first non-local video decoder
        var receivedFrameCount = 0;
        _serverConnection.OnVideoFrameReceived += (rep, timestamp, frame, format) =>
        {
            receivedFrameCount++;
            if (receivedFrameCount <= 5 || receivedFrameCount % 100 == 0)
            {
                Console.WriteLine($"WebRTC SFU: Received video frame {receivedFrameCount}, size={frame.Length}");
            }

            // Route video to appropriate decoder
            // For now, route to first available decoder (will improve with SSRC mapping)
            var decoder = _videoDecoders.Values.FirstOrDefault();
            if (decoder != null)
            {
                try
                {
                    decoder.DecodeFrame(frame);
                }
                catch (Exception ex)
                {
                    if (receivedFrameCount <= 5)
                    {
                        Console.WriteLine($"WebRTC SFU: DecodeFrame error: {ex.Message}");
                    }
                }
            }
        };

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
        _serverConnection.setRemoteDescription(remoteDesc);

        // Create and send answer
        var answer = _serverConnection.createAnswer();
        await _serverConnection.setLocalDescription(answer);

        Console.WriteLine($"WebRTC SFU: Sending answer to server");
        await _signalR.SendSfuAnswerAsync(channelId, answer.sdp);
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
            var audioEncoder = new AudioEncoder();
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
            // Use 720p 16:9 for incoming video (will scale with aspect ratio preservation)
            videoDecoder = new FfmpegProcessDecoder(1280, 720, VideoCodecsEnum.H264);
            videoDecoder.OnDecodedFrame += (width, height, rgbData) =>
            {
                VideoFrameReceived?.Invoke(remoteUserId, width, height, rgbData);
            };
            videoDecoder.Start();
            _videoDecoders[remoteUserId] = videoDecoder;

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

        if (_videoDecoders.TryRemove(userId, out var videoDecoder))
        {
            try
            {
                videoDecoder.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC: Error disposing video decoder: {ex.Message}");
            }
        }
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

        _isCameraOn = enabled;
        Console.WriteLine($"WebRTC: Camera = {enabled}");

        if (enabled)
        {
            await StartVideoCaptureAsync();
        }
        else
        {
            await StopVideoCaptureAsync();
        }

        CameraStateChanged?.Invoke(enabled);
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
            _processEncoder.OnEncodedFrame += OnLocalVideoEncoded;
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

    private int _sentVideoFrameCount;
    private void OnLocalVideoEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        _sentVideoFrameCount++;
        if (_sentVideoFrameCount <= 5 || _sentVideoFrameCount % 100 == 0)
        {
            Console.WriteLine($"WebRTC: Sending video frame {_sentVideoFrameCount}, size={encodedSample.Length}, server={_serverConnection != null}, peers={_peerConnections.Count}");
        }

        // SFU mode: send to server connection
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
                    var rgbData = BgrToRgb(bgrData, frameWidth, frameHeight);
                    LocalVideoFrameCaptured.Invoke(frameWidth, frameHeight, rgbData);
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

    private static byte[] RgbToI420(byte[] rgb, int width, int height)
    {
        // I420 format: Y plane (width*height), U plane (width/2 * height/2), V plane (width/2 * height/2)
        var ySize = width * height;
        var uvSize = (width / 2) * (height / 2);
        var i420 = new byte[ySize + uvSize * 2];

        var yPlane = i420.AsSpan(0, ySize);
        var uPlane = i420.AsSpan(ySize, uvSize);
        var vPlane = i420.AsSpan(ySize + uvSize, uvSize);

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                var rgbIndex = (j * width + i) * 3;
                var r = rgb[rgbIndex];
                var g = rgb[rgbIndex + 1];
                var b = rgb[rgbIndex + 2];

                // RGB to Y
                var y = (byte)Math.Clamp((66 * r + 129 * g + 25 * b + 128) / 256 + 16, 0, 255);
                yPlane[j * width + i] = y;

                // Subsample U and V (every 2x2 block)
                if (j % 2 == 0 && i % 2 == 0)
                {
                    var uvIndex = (j / 2) * (width / 2) + (i / 2);
                    uPlane[uvIndex] = (byte)Math.Clamp((-38 * r - 74 * g + 112 * b + 128) / 256 + 128, 0, 255);
                    vPlane[uvIndex] = (byte)Math.Clamp((112 * r - 94 * g - 18 * b + 128) / 256 + 128, 0, 255);
                }
            }
        }

        return i420;
    }

    private static byte[] BgrToI420(byte[] bgr, int width, int height)
    {
        // I420 format: Y plane (width*height), U plane (width/2 * height/2), V plane (width/2 * height/2)
        var ySize = width * height;
        var uvSize = (width / 2) * (height / 2);
        var i420 = new byte[ySize + uvSize * 2];

        var yPlane = i420.AsSpan(0, ySize);
        var uPlane = i420.AsSpan(ySize, uvSize);
        var vPlane = i420.AsSpan(ySize + uvSize, uvSize);

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                var bgrIndex = (j * width + i) * 3;
                var b = bgr[bgrIndex];
                var g = bgr[bgrIndex + 1];
                var r = bgr[bgrIndex + 2];

                // RGB to Y
                var y = (byte)Math.Clamp((66 * r + 129 * g + 25 * b + 128) / 256 + 16, 0, 255);
                yPlane[j * width + i] = y;

                // Subsample U and V (every 2x2 block)
                if (j % 2 == 0 && i % 2 == 0)
                {
                    var uvIndex = (j / 2) * (width / 2) + (i / 2);
                    uPlane[uvIndex] = (byte)Math.Clamp((-38 * r - 74 * g + 112 * b + 128) / 256 + 128, 0, 255);
                    vPlane[uvIndex] = (byte)Math.Clamp((112 * r - 94 * g - 18 * b + 128) / 256 + 128, 0, 255);
                }
            }
        }

        return i420;
    }

    private static byte[] I420ToRgb(byte[] i420, int width, int height)
    {
        // I420 format: Y plane (width*height), U plane (width/2 * height/2), V plane (width/2 * height/2)
        var ySize = width * height;
        var uvSize = (width / 2) * (height / 2);
        var rgb = new byte[width * height * 3];

        var yPlane = i420.AsSpan(0, ySize);
        var uPlane = i420.AsSpan(ySize, uvSize);
        var vPlane = i420.AsSpan(ySize + uvSize, uvSize);

        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                var yIndex = j * width + i;
                var uvIndex = (j / 2) * (width / 2) + (i / 2);

                var y = yPlane[yIndex] - 16;
                var u = uPlane[uvIndex] - 128;
                var v = vPlane[uvIndex] - 128;

                // YUV to RGB conversion
                var r = (298 * y + 409 * v + 128) >> 8;
                var g = (298 * y - 100 * u - 208 * v + 128) >> 8;
                var b = (298 * y + 516 * u + 128) >> 8;

                var rgbIndex = (j * width + i) * 3;
                rgb[rgbIndex] = (byte)Math.Clamp(r, 0, 255);
                rgb[rgbIndex + 1] = (byte)Math.Clamp(g, 0, 255);
                rgb[rgbIndex + 2] = (byte)Math.Clamp(b, 0, 255);
            }
        }

        return rgb;
    }

    private static byte[] BgrToRgb(byte[] bgr, int width, int height)
    {
        var rgb = new byte[bgr.Length];
        for (int i = 0; i < bgr.Length; i += 3)
        {
            rgb[i] = bgr[i + 2];     // R = B
            rgb[i + 1] = bgr[i + 1]; // G = G
            rgb[i + 2] = bgr[i];     // B = R
        }
        return rgb;
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
            _processEncoder.OnEncodedFrame -= OnLocalVideoEncoded;
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
