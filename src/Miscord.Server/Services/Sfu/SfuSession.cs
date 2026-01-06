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

    public Guid UserId { get; }
    public Guid ChannelId { get; }
    public RTCPeerConnection PeerConnection => _peerConnection;

    /// <summary>
    /// Fired when audio RTP is received from this client.
    /// Args: (session, rtpPacket)
    /// </summary>
    public event Action<SfuSession, RTPPacket>? OnAudioRtpReceived;

    /// <summary>
    /// Fired when video RTP is received from this client.
    /// Args: (session, rtpPacket)
    /// </summary>
    public event Action<SfuSession, RTPPacket>? OnVideoRtpReceived;

    /// <summary>
    /// Fired when an ICE candidate is gathered by the server.
    /// Args: (candidate)
    /// </summary>
    public event Action<RTCIceCandidate>? OnIceCandidate;

    /// <summary>
    /// Fired when the connection state changes.
    /// </summary>
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

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
                OnAudioRtpReceived?.Invoke(this, rtpPkt);
            }
            else if (media == SDPMediaTypesEnum.video)
            {
                OnVideoRtpReceived?.Invoke(this, rtpPkt);
            }
        };
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

        // Video track - bidirectional (receive from client, send to client)
        var videoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, 96)
        };
        var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.SendRecv);
        _peerConnection.addTrack(videoTrack);

        _logger.LogDebug("SFU session {UserId}: Added audio and video tracks", UserId);
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
    /// </summary>
    public void ForwardVideoRtpRaw(RTPPacket packet)
    {
        // Use SendRtpRaw to forward with minimal latency - no re-packetization
        _peerConnection.SendRtpRaw(
            SDPMediaTypesEnum.video,
            packet.Payload,
            packet.Header.Timestamp,
            packet.Header.MarkerBit,
            packet.Header.PayloadType);
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
