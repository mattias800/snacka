using System.Collections.Concurrent;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;

namespace Miscord.Client.Services;

public interface IWebRtcService : IAsyncDisposable
{
    Guid? CurrentChannelId { get; }
    VoiceConnectionStatus ConnectionStatus { get; }
    int ConnectedPeerCount { get; }
    IReadOnlyDictionary<Guid, PeerConnectionState> PeerStates { get; }
    bool IsSpeaking { get; }

    Task JoinVoiceChannelAsync(Guid channelId, IEnumerable<VoiceParticipantResponse> existingParticipants);
    Task LeaveVoiceChannelAsync();

    Task HandleOfferAsync(Guid fromUserId, string sdp);
    Task HandleAnswerAsync(Guid fromUserId, string sdp);
    Task HandleIceCandidateAsync(Guid fromUserId, string candidate, string? sdpMid, int? sdpMLineIndex);

    void SetMuted(bool muted);
    void SetDeafened(bool deafened);

    event Action<Guid>? PeerConnected;
    event Action<Guid>? PeerDisconnected;
    event Action<VoiceConnectionStatus>? ConnectionStatusChanged;
    event Action<bool>? SpeakingChanged;
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
    private readonly ConcurrentDictionary<Guid, RTCPeerConnection> _peerConnections = new();
    private readonly ConcurrentDictionary<Guid, PeerConnectionState> _peerStates = new();

    // Shared audio source (microphone) for all peer connections
    private SDL2AudioSource? _audioSource;
    // Per-peer audio sinks (speakers) for receiving audio
    private readonly ConcurrentDictionary<Guid, SDL2AudioEndPoint> _audioSinks = new();

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

    public event Action<Guid>? PeerConnected;
    public event Action<Guid>? PeerDisconnected;
    public event Action<VoiceConnectionStatus>? ConnectionStatusChanged;
    public event Action<bool>? SpeakingChanged;

    public WebRtcService(ISignalRService signalR, ISettingsStore? settingsStore = null)
    {
        _signalR = signalR;
        _settingsStore = settingsStore;

        // Subscribe to WebRTC signaling events
        _signalR.WebRtcOfferReceived += async e => await HandleOfferAsync(e.FromUserId, e.Sdp);
        _signalR.WebRtcAnswerReceived += async e => await HandleAnswerAsync(e.FromUserId, e.Sdp);
        _signalR.IceCandidateReceived += async e => await HandleIceCandidateAsync(e.FromUserId, e.Candidate, e.SdpMid, e.SdpMLineIndex);

        // Handle participant events
        _signalR.VoiceParticipantJoined += async e =>
        {
            if (_currentChannelId == e.ChannelId && e.Participant.UserId != _localUserId)
            {
                await CreatePeerConnectionAsync(e.Participant.UserId, isInitiator: true);
            }
        };

        _signalR.VoiceParticipantLeft += e =>
        {
            if (_currentChannelId == e.ChannelId)
            {
                ClosePeerConnection(e.UserId);
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
            // Use selected audio input device from settings (empty string = default)
            var audioEncoder = new AudioEncoder();
            var inputDevice = _settingsStore?.Settings.AudioInputDevice ?? string.Empty;
            _audioSource = new SDL2AudioSource(inputDevice, audioEncoder);

            // Subscribe to raw audio samples for voice activity detection
            _audioSource.OnAudioSourceRawSample += OnAudioSourceRawSample;

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

        // Calculate RMS (Root Mean Square) for voice activity detection
        double sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += sample[i] * sample[i];
        }
        double rms = Math.Sqrt(sumOfSquares / sample.Length);

        // Threshold for voice activity (adjust as needed)
        const double voiceThreshold = 500;

        if (rms > voiceThreshold)
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
        Console.WriteLine($"WebRTC: Joining voice channel {channelId}");

        // Initialize microphone capture
        await InitializeAudioSourceAsync();

        var otherParticipants = existingParticipants.Count(p => p.UserId != _localUserId);

        // Don't create peer connections here as initiator.
        // Existing participants will receive VoiceParticipantJoined event and will initiate.
        // This prevents both sides from sending offers simultaneously.
        Console.WriteLine($"WebRTC: Waiting for {otherParticipants} existing participants to initiate connections");

        // Set status to Connecting if we're waiting for peers, otherwise Connected (solo in channel)
        UpdateConnectionStatus(otherParticipants > 0 ? VoiceConnectionStatus.Connecting : VoiceConnectionStatus.Connected);
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

        // Close all peer connections and audio sinks
        foreach (var userId in _peerConnections.Keys.ToList())
        {
            ClosePeerConnection(userId);
        }

        // Stop and dispose audio source
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

        _currentChannelId = null;
        UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
    }

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

        // Handle audio format negotiation
        pc.OnAudioFormatsNegotiated += (formats) =>
        {
            Console.WriteLine($"WebRTC: Audio formats negotiated with {remoteUserId}: {string.Join(", ", formats.Select(f => f.FormatName))}");
            var format = formats.First();

            _audioSource?.SetAudioSourceFormat(format);
            audioSink?.SetAudioSinkFormat(format);
        };

        // Handle incoming audio
        pc.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (!_isDeafened && media == SDPMediaTypesEnum.audio && audioSink != null)
            {
                audioSink.GotAudioRtp(rep, rtpPkt.Header.SyncSource, rtpPkt.Header.SequenceNumber,
                    rtpPkt.Header.Timestamp, rtpPkt.Header.PayloadType,
                    rtpPkt.Header.MarkerBit == 1, rtpPkt.Payload);
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
        if (_peerConnections.TryGetValue(userId, out var pc) && _audioSource != null)
        {
            _audioSource.OnAudioSourceEncodedSample -= pc.SendAudio;
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
