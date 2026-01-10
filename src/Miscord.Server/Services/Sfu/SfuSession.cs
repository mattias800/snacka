using System.Text.RegularExpressions;
using Miscord.Shared.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Miscord.Server.Services.Sfu;

/// <summary>
/// Represents a single client's WebRTC connection to the SFU server.
/// Handles the peer connection lifecycle and media routing.
/// </summary>
public class SfuSession : IDisposable
{
    private readonly ILogger<SfuSession> _logger;
    private readonly RTCPeerConnection _peerConnection;
    private bool _disposed;

    // SSRC tracking for audio and dual video streams
    private uint? _audioSsrc;           // Microphone audio
    private uint? _screenAudioSsrc;      // Screen share audio (separate from mic)
    private uint? _cameraVideoSsrc;
    private uint? _screenVideoSsrc;

    public Guid UserId { get; }
    public Guid ChannelId { get; }
    public RTCPeerConnection PeerConnection => _peerConnection;

    /// <summary>
    /// The SSRC used by this client for microphone audio.
    /// </summary>
    public uint? AudioSsrc => _audioSsrc;

    /// <summary>
    /// The SSRC used by this client for screen share audio.
    /// Screen audio is sent with payload type 112 to distinguish from mic audio (PT 111).
    /// </summary>
    public uint? ScreenAudioSsrc => _screenAudioSsrc;

    /// <summary>
    /// The SSRC used by this client for camera video.
    /// </summary>
    public uint? CameraVideoSsrc => _cameraVideoSsrc;

    /// <summary>
    /// The SSRC used by this client for screen share video.
    /// </summary>
    public uint? ScreenVideoSsrc => _screenVideoSsrc;

    /// <summary>
    /// Fired when audio RTP is received from this client.
    /// Args: (session, rtpPacket)
    /// </summary>
    public event Action<SfuSession, RTPPacket>? OnAudioRtpReceived;

    /// <summary>
    /// Fired when video RTP is received from this client (any stream type).
    /// Args: (session, rtpPacket)
    /// </summary>
    public event Action<SfuSession, RTPPacket>? OnVideoRtpReceived;

    /// <summary>
    /// Fired when camera video RTP is received from this client.
    /// Args: (session, rtpPacket)
    /// </summary>
    public event Action<SfuSession, RTPPacket>? OnCameraVideoRtpReceived;

    /// <summary>
    /// Fired when screen share video RTP is received from this client.
    /// Args: (session, rtpPacket)
    /// </summary>
    public event Action<SfuSession, RTPPacket>? OnScreenVideoRtpReceived;

    /// <summary>
    /// Fired when an ICE candidate is gathered by the server.
    /// Args: (candidate)
    /// </summary>
    public event Action<RTCIceCandidate>? OnIceCandidate;

    /// <summary>
    /// Fired when the connection state changes.
    /// </summary>
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

    /// <summary>
    /// Fired when the microphone audio SSRC is discovered for this client.
    /// Used for per-user volume control on clients.
    /// Args: (session, audioSsrc)
    /// </summary>
    public event Action<SfuSession, uint>? OnAudioSsrcDiscovered;

    /// <summary>
    /// Fired when the screen share audio SSRC is discovered for this client.
    /// Screen audio should only be played when watching the user's screen share.
    /// Args: (session, screenAudioSsrc)
    /// </summary>
    public event Action<SfuSession, uint>? OnScreenAudioSsrcDiscovered;

    public SfuSession(
        Guid userId,
        Guid channelId,
        ILogger<SfuSession> logger)
    {
        UserId = userId;
        ChannelId = channelId;
        _logger = logger;

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun1.l.google.com:19302" }
            }
        };

        _peerConnection = new RTCPeerConnection(config);
        SetupEventHandlers();
    }

    private void SetupEventHandlers()
    {
        _peerConnection.onicecandidate += candidate =>
        {
            if (candidate?.candidate != null)
            {
                _logger.LogDebug("SFU session {UserId}: ICE candidate gathered", UserId);
                OnIceCandidate?.Invoke(candidate);
            }
        };

        _peerConnection.onconnectionstatechange += state =>
        {
            _logger.LogInformation("SFU session {UserId}: Connection state changed to {State}", UserId, state);
            OnConnectionStateChanged?.Invoke(state);
        };

        // Handle incoming RTP packets - forward raw for minimal latency
        _peerConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                var ssrc = rtpPkt.Header.SyncSource;
                var payloadType = rtpPkt.Header.PayloadType;

                // Distinguish microphone audio (PT 111) from screen share audio (PT 112)
                // Both use Opus codec but are sent on different payload types
                if (payloadType == 112)
                {
                    // Screen share audio - track separately
                    if (_screenAudioSsrc != ssrc)
                    {
                        _screenAudioSsrc = ssrc;
                        _logger.LogInformation("SFU session {UserId}: Detected screen audio SSRC={Ssrc} (PT={PayloadType})",
                            UserId, ssrc, payloadType);
                        OnScreenAudioSsrcDiscovered?.Invoke(this, ssrc);
                    }
                }
                else
                {
                    // Microphone audio - track for per-user volume control
                    if (_audioSsrc != ssrc)
                    {
                        _audioSsrc = ssrc;
                        _logger.LogInformation("SFU session {UserId}: Detected mic audio SSRC={Ssrc} (PT={PayloadType})",
                            UserId, ssrc, payloadType);
                        OnAudioSsrcDiscovered?.Invoke(this, ssrc);
                    }
                }

                // Forward all audio (both mic and screen) - client will filter based on watching
                OnAudioRtpReceived?.Invoke(this, rtpPkt);
            }
            else if (media == SDPMediaTypesEnum.video)
            {
                var payloadType = rtpPkt.Header.PayloadType;
                var ssrc = rtpPkt.Header.SyncSource;

                // Route to stream-specific event based on payload type
                // Payload type 96 = camera, 97 = screen share
                var streamType = GetStreamTypeForPayloadType(payloadType, ssrc);

                // Always fire the general video event for backward compatibility
                OnVideoRtpReceived?.Invoke(this, rtpPkt);

                // Fire stream-specific events
                if (streamType == VideoStreamType.Camera)
                {
                    OnCameraVideoRtpReceived?.Invoke(this, rtpPkt);
                }
                else if (streamType == VideoStreamType.ScreenShare)
                {
                    OnScreenVideoRtpReceived?.Invoke(this, rtpPkt);
                }
            }
        };
    }

    /// <summary>
    /// Determines the stream type based on payload type.
    /// Primary: Payload type 96 = camera, 97 = screen share.
    /// Fallback: Uses SSRC heuristic if payload types aren't distinct.
    /// </summary>
    private VideoStreamType GetStreamTypeForPayloadType(int payloadType, uint ssrc)
    {
        // Primary detection: by payload type
        // Camera uses payload type 96, screen share uses 97
        if (payloadType == 97)
        {
            // Track screen SSRC for logging
            if (_screenVideoSsrc != ssrc)
            {
                _screenVideoSsrc = ssrc;
                _logger.LogInformation("SFU session {UserId}: Detected screen share video (PT={PayloadType}, SSRC={Ssrc})",
                    UserId, payloadType, ssrc);
            }
            return VideoStreamType.ScreenShare;
        }

        // Payload type 96 or other = camera
        if (_cameraVideoSsrc != ssrc)
        {
            _cameraVideoSsrc = ssrc;
            _logger.LogInformation("SFU session {UserId}: Detected camera video (PT={PayloadType}, SSRC={Ssrc})",
                UserId, payloadType, ssrc);
        }
        return VideoStreamType.Camera;
    }

    /// <summary>
    /// Adds media tracks for sending/receiving audio and video.
    /// Must be called before creating the offer.
    /// </summary>
    public void AddMediaTracks()
    {
        // Audio track - bidirectional (receive from client, send to client)
        var audioFormats = new List<AudioFormat>
        {
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA)
        };
        var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.SendRecv);
        _peerConnection.addTrack(audioTrack);

        // Video track 1: Camera - bidirectional (receive from client, send to client)
        var cameraVideoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, 96)
        };
        var cameraVideoTrack = new MediaStreamTrack(cameraVideoFormats, MediaStreamStatusEnum.SendRecv);
        _peerConnection.addTrack(cameraVideoTrack);

        // Video track 2: Screen share - bidirectional (receive from client, send to client)
        var screenVideoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, 97) // Different payload type for screen
        };
        var screenVideoTrack = new MediaStreamTrack(screenVideoFormats, MediaStreamStatusEnum.SendRecv);
        _peerConnection.addTrack(screenVideoTrack);

        _logger.LogDebug("SFU session {UserId}: Added audio and dual video tracks (camera + screen)", UserId);
    }

    /// <summary>
    /// Creates an SDP offer for the client.
    /// </summary>
    public async Task<string> CreateOfferAsync()
    {
        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);
        _logger.LogDebug("SFU session {UserId}: Created offer", UserId);
        return offer.sdp;
    }

    /// <summary>
    /// Sets the remote SDP answer from the client.
    /// </summary>
    public void SetRemoteAnswer(string sdp)
    {
        var answer = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdp
        };
        _peerConnection.setRemoteDescription(answer);
        _logger.LogDebug("SFU session {UserId}: Set remote answer", UserId);
    }

    /// <summary>
    /// Adds an ICE candidate from the client.
    /// </summary>
    public void AddIceCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid ?? "0",
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };
        _peerConnection.addIceCandidate(iceCandidate);
        _logger.LogDebug("SFU session {UserId}: Added ICE candidate", UserId);
    }

    /// <summary>
    /// Forwards a raw audio RTP packet to this client with minimal processing.
    /// </summary>
    public void ForwardAudioRtpRaw(RTPPacket packet)
    {
        // Use SendRtpRaw to forward with minimal latency - no re-packetization
        _peerConnection.SendRtpRaw(
            SDPMediaTypesEnum.audio,
            packet.Payload,
            packet.Header.Timestamp,
            packet.Header.MarkerBit,
            packet.Header.PayloadType);
    }

    /// <summary>
    /// Forwards a raw video RTP packet to this client with minimal processing.
    /// Uses the first (camera) video track.
    /// </summary>
    public void ForwardVideoRtpRaw(RTPPacket packet)
    {
        // Use SendRtpRaw to forward with minimal latency - no re-packetization
        // This goes to the first video track (camera)
        _peerConnection.SendRtpRaw(
            SDPMediaTypesEnum.video,
            packet.Payload,
            packet.Header.Timestamp,
            packet.Header.MarkerBit,
            packet.Header.PayloadType);
    }

    /// <summary>
    /// Forwards camera video RTP to this client's camera video track (first video track).
    /// </summary>
    public void ForwardCameraVideoRtpRaw(RTPPacket packet)
    {
        // Camera uses the first video track (payload type 96)
        _peerConnection.SendRtpRaw(
            SDPMediaTypesEnum.video,
            packet.Payload,
            packet.Header.Timestamp,
            packet.Header.MarkerBit,
            96); // H264 camera payload type
    }

    /// <summary>
    /// Forwards screen share video RTP to this client's screen video track (second video track).
    /// </summary>
    public void ForwardScreenVideoRtpRaw(RTPPacket packet)
    {
        // Screen share uses the second video track (payload type 97)
        // Note: SendRtpRaw with different payload type will route to the track
        // that negotiated that payload type
        _peerConnection.SendRtpRaw(
            SDPMediaTypesEnum.video,
            packet.Payload,
            packet.Header.Timestamp,
            packet.Header.MarkerBit,
            97); // H264 screen payload type
    }

    public RTCPeerConnectionState ConnectionState => _peerConnection.connectionState;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _peerConnection.close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing peer connection for session {UserId}", UserId);
        }

        _logger.LogInformation("SFU session {UserId}: Disposed", UserId);
    }
}
