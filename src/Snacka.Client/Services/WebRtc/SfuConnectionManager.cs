using System.Collections.Concurrent;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Snacka.Shared.Models;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages the WebRTC connection to the SFU server.
/// Handles SDP offer/answer negotiation, ICE candidates, track management,
/// and RTP packet sending/receiving.
/// Extracted from WebRtcService for single responsibility.
/// </summary>
public class SfuConnectionManager : IAsyncDisposable
{
    private readonly ISignalRService _signalR;

    // Server connection
    private RTCPeerConnection? _serverConnection;
    private PeerConnectionState _serverConnectionState = PeerConnectionState.Closed;

    // Pending offer (received before JoinVoiceChannelAsync was called)
    private (Guid ChannelId, string Sdp)? _pendingSfuOffer;

    // Video tracks for simultaneous camera + screen share
    private MediaStreamTrack? _cameraVideoTrack;
    private MediaStreamTrack? _screenVideoTrack;
    private uint _cameraVideoSsrc;
    private uint _screenVideoSsrc;

    // Screen audio track (separate from microphone)
    private MediaStreamTrack? _screenAudioTrack;
    private uint _screenAudioSsrc;
    private bool _screenAudioFirstPacket = true;

    // RTP state for screen share video
    private uint _screenRtpTimestamp;
    private ushort _screenRtpSeqNum;

    // Track video format negotiated
    private VideoCodecsEnum _videoCodec = VideoCodecsEnum.H264;

    // Constants
    private const int VideoFps = 15;
    private const int ScreenFps = 30;
    private const int MaxRtpPayloadSize = 1400;
    private const byte CameraPayloadType = 96;
    private const byte ScreenVideoPayloadType = 97;
    private const byte ScreenAudioPayloadType = 112;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public PeerConnectionState State => _serverConnectionState;

    /// <summary>
    /// Gets whether the connection is established.
    /// </summary>
    public bool IsConnected => _serverConnectionState == PeerConnectionState.Connected;

    /// <summary>
    /// Gets the negotiated video codec.
    /// </summary>
    public VideoCodecsEnum VideoCodec => _videoCodec;

    /// <summary>
    /// Fired when connection status changes.
    /// </summary>
    public event Action<VoiceConnectionStatus>? ConnectionStatusChanged;

    /// <summary>
    /// Fired when audio RTP packet is received. Args: (ssrc, seqNum, timestamp, payloadType, marker, payload, isScreenAudio)
    /// </summary>
    public event Action<uint, ushort, uint, int, bool, byte[], bool>? AudioPacketReceived;

    /// <summary>
    /// Fired when video RTP packet is received. Args: (payload, timestamp, marker, payloadType)
    /// </summary>
    public event Action<byte[], uint, bool, int>? VideoPacketReceived;

    /// <summary>
    /// Fired when audio formats are negotiated.
    /// </summary>
    public event Action<List<AudioFormat>>? AudioFormatsNegotiated;

    /// <summary>
    /// Fired when video formats are negotiated.
    /// </summary>
    public event Action<List<VideoFormat>>? VideoFormatsNegotiated;

    public SfuConnectionManager(ISignalRService signalR)
    {
        _signalR = signalR;
    }

    /// <summary>
    /// Checks if there's a pending SFU offer for the given channel.
    /// </summary>
    public bool HasPendingOffer(Guid channelId)
    {
        return _pendingSfuOffer.HasValue && _pendingSfuOffer.Value.ChannelId == channelId;
    }

    /// <summary>
    /// Gets and clears the pending SFU offer.
    /// </summary>
    public (Guid ChannelId, string Sdp)? ConsumePendingOffer()
    {
        var offer = _pendingSfuOffer;
        _pendingSfuOffer = null;
        return offer;
    }

    /// <summary>
    /// Clears any pending offer.
    /// </summary>
    public void ClearPendingOffer()
    {
        _pendingSfuOffer = null;
    }

    /// <summary>
    /// Stores a pending SFU offer for later processing when the channel is joined.
    /// </summary>
    public void StorePendingOffer(Guid channelId, string sdp)
    {
        Console.WriteLine($"SfuConnectionManager: Storing pending SFU offer for channel {channelId}");
        _pendingSfuOffer = (channelId, sdp);
    }

    /// <summary>
    /// Processes an SFU offer - creates connection and sends answer.
    /// </summary>
    public async Task ProcessSfuOfferAsync(Guid channelId, string sdp, IAudioSource? audioSource)
    {
        if (string.IsNullOrEmpty(sdp))
        {
            Console.WriteLine($"SfuConnectionManager: Error - SFU offer has null or empty SDP for channel {channelId}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        Console.WriteLine($"SfuConnectionManager: Received SFU offer for channel {channelId}");

        // Close existing connection if any
        if (_serverConnection != null)
        {
            try
            {
                if (audioSource != null)
                {
                    audioSource.OnAudioSourceEncodedSample -= _serverConnection.SendAudio;
                }
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

        // Add audio track for sending microphone audio
        if (audioSource != null)
        {
            var audioFormats = audioSource.GetAudioSourceFormats();
            var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.SendRecv);
            _serverConnection.addTrack(audioTrack);
            audioSource.OnAudioSourceEncodedSample += _serverConnection.SendAudio;
        }

        // Camera video track (first track - uses default SendVideo)
        var cameraVideoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, VideoFps)
        };
        _cameraVideoTrack = new MediaStreamTrack(cameraVideoFormats, MediaStreamStatusEnum.SendRecv);
        _serverConnection.addTrack(_cameraVideoTrack);

        // Screen share video track (second track)
        var screenVideoFormats = new List<VideoFormat>
        {
            new VideoFormat(VideoCodecsEnum.H264, ScreenFps)
        };
        _screenVideoTrack = new MediaStreamTrack(screenVideoFormats, MediaStreamStatusEnum.SendRecv);
        _serverConnection.addTrack(_screenVideoTrack);

        // Screen audio track (separate from microphone)
        if (audioSource != null)
        {
            var screenAudioFormats = audioSource.GetAudioSourceFormats();
            _screenAudioTrack = new MediaStreamTrack(screenAudioFormats, MediaStreamStatusEnum.SendRecv);
            _serverConnection.addTrack(_screenAudioTrack);
            Console.WriteLine("SfuConnectionManager: Added separate screen audio track");
        }

        // Audio format negotiation
        _serverConnection.OnAudioFormatsNegotiated += formats =>
        {
            Console.WriteLine($"SfuConnectionManager: Audio formats negotiated: {string.Join(", ", formats.Select(f => f.FormatName))}");
            AudioFormatsNegotiated?.Invoke(formats);
        };

        // Video format negotiation
        _serverConnection.OnVideoFormatsNegotiated += formats =>
        {
            Console.WriteLine($"SfuConnectionManager: Video formats negotiated: {string.Join(", ", formats.Select(f => f.FormatName))}");
            if (formats != null && formats.Any())
            {
                _videoCodec = formats.First().Codec;
                VideoFormatsNegotiated?.Invoke(formats);
            }
        };

        // Handle incoming RTP packets
        _serverConnection.OnRtpPacketReceived += (rep, media, rtpPkt) =>
        {
            var ssrc = rtpPkt.Header.SyncSource;
            var payloadType = rtpPkt.Header.PayloadType;
            var seqNum = rtpPkt.Header.SequenceNumber;
            var timestamp = rtpPkt.Header.Timestamp;
            var marker = rtpPkt.Header.MarkerBit == 1;

            if (media == SDPMediaTypesEnum.audio)
            {
                // Check if screen audio (PT 112) vs mic audio
                var isScreenAudio = payloadType == ScreenAudioPayloadType;
                AudioPacketReceived?.Invoke(ssrc, seqNum, timestamp, payloadType, marker, rtpPkt.Payload, isScreenAudio);
            }
            else if (media == SDPMediaTypesEnum.video)
            {
                VideoPacketReceived?.Invoke(rtpPkt.Payload, timestamp, marker, payloadType);
            }
        };

        // ICE candidate handling
        _serverConnection.onicecandidate += async candidate =>
        {
            if (candidate?.candidate != null)
            {
                await _signalR.SendSfuIceCandidateAsync(
                    channelId,
                    candidate.candidate,
                    candidate.sdpMid,
                    (int?)candidate.sdpMLineIndex
                );
            }
        };

        // Connection state changes
        _serverConnection.onconnectionstatechange += state =>
        {
            Console.WriteLine($"SfuConnectionManager: Connection state: {state}");
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
            Console.WriteLine($"SfuConnectionManager: Failed to set remote description: {ex.Message}");
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
            Console.WriteLine($"SfuConnectionManager: Failed to create answer: {ex.Message}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        if (answer == null || string.IsNullOrEmpty(answer.sdp))
        {
            Console.WriteLine("SfuConnectionManager: Failed to create answer - null or empty SDP");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        try
        {
            await _serverConnection.setLocalDescription(answer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SfuConnectionManager: Failed to set local description: {ex.Message}");
            UpdateConnectionStatus(VoiceConnectionStatus.Disconnected);
            return;
        }

        Console.WriteLine($"SfuConnectionManager: Sending answer to server");
        await _signalR.SendSfuAnswerAsync(channelId, answer.sdp);
    }

    /// <summary>
    /// Captures SSRCs for video and audio tracks after connection is established.
    /// </summary>
    private void CaptureVideoTrackSsrcs()
    {
        try
        {
            var videoTracks = _serverConnection?.VideoStreamList;
            if (videoTracks == null || videoTracks.Count == 0)
            {
                Console.WriteLine("SfuConnectionManager: No video tracks found to capture SSRCs");
            }
            else
            {
                // Camera track is first, screen track is second
                if (videoTracks.Count >= 1 && videoTracks[0].LocalTrack != null)
                {
                    _cameraVideoSsrc = videoTracks[0].LocalTrack.Ssrc;
                    Console.WriteLine($"SfuConnectionManager: Camera video track SSRC = {_cameraVideoSsrc}");
                }

                if (videoTracks.Count >= 2 && videoTracks[1].LocalTrack != null)
                {
                    _screenVideoSsrc = videoTracks[1].LocalTrack.Ssrc;
                    Console.WriteLine($"SfuConnectionManager: Screen video track SSRC = {_screenVideoSsrc}");
                }
                else
                {
                    _screenVideoSsrc = (uint)new Random().Next(100000, int.MaxValue);
                    Console.WriteLine($"SfuConnectionManager: Generated screen video track SSRC = {_screenVideoSsrc}");
                }
            }

            // Get audio tracks - mic is first, screen audio is second
            var audioTracks = _serverConnection?.AudioStreamList;
            if (audioTracks != null && audioTracks.Count >= 2 && audioTracks[1].LocalTrack != null)
            {
                _screenAudioSsrc = audioTracks[1].LocalTrack.Ssrc;
                Console.WriteLine($"SfuConnectionManager: Screen audio track SSRC = {_screenAudioSsrc}");
            }
            else
            {
                _screenAudioSsrc = (uint)new Random().Next(100000, int.MaxValue);
                Console.WriteLine($"SfuConnectionManager: Generated screen audio track SSRC = {_screenAudioSsrc}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SfuConnectionManager: Error capturing track SSRCs: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles ICE candidate from SFU server.
    /// </summary>
    public void HandleIceCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        if (_serverConnection == null)
        {
            Console.WriteLine("SfuConnectionManager: Ignoring ICE candidate - no server connection");
            return;
        }

        var iceCandidate = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid ?? "0",
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0)
        };

        _serverConnection.addIceCandidate(iceCandidate);
    }

    /// <summary>
    /// Sends camera video to the server.
    /// </summary>
    public void SendCameraVideo(uint durationRtpUnits, byte[] encodedSample)
    {
        if (_serverConnection != null && _serverConnectionState == PeerConnectionState.Connected)
        {
            _serverConnection.SendVideo(durationRtpUnits, encodedSample);
        }
    }

    /// <summary>
    /// Sends screen share video to the server using payload type 97.
    /// </summary>
    public void SendScreenVideo(uint durationRtpUnits, byte[] encodedSample)
    {
        if (_serverConnection == null || _serverConnectionState != PeerConnectionState.Connected)
            return;

        try
        {
            SendH264ForScreenShare(encodedSample, durationRtpUnits);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SfuConnectionManager: Error sending screen frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends screen share audio to the server using payload type 112.
    /// </summary>
    public void SendScreenAudio(uint timestamp, byte[] opusData)
    {
        if (_serverConnection == null || _serverConnectionState != PeerConnectionState.Connected)
            return;

        if (_screenAudioSsrc == 0)
        {
            Console.WriteLine("SfuConnectionManager: Screen audio SSRC not initialized, skipping audio packet");
            return;
        }

        var markerBit = _screenAudioFirstPacket ? 1 : 0;
        _screenAudioFirstPacket = false;

        try
        {
            _serverConnection.SendRtpRaw(
                SDPMediaTypesEnum.audio,
                opusData,
                timestamp,
                markerBit,
                ScreenAudioPayloadType);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SfuConnectionManager: Error sending screen audio: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends H264 encoded data for screen share using payload type 97.
    /// </summary>
    private void SendH264ForScreenShare(byte[] encodedSample, uint durationRtpUnits)
    {
        var nalUnits = H264FrameAssembler.FindNalUnits(encodedSample);
        _screenRtpTimestamp += durationRtpUnits;

        for (int i = 0; i < nalUnits.Count; i++)
        {
            var nalUnit = nalUnits[i];
            bool isLastNalUnit = (i == nalUnits.Count - 1);

            if (nalUnit.Length <= MaxRtpPayloadSize)
            {
                // Single NAL unit packet
                _screenRtpSeqNum++;
                _serverConnection?.SendRtpRaw(
                    SDPMediaTypesEnum.video,
                    nalUnit,
                    _screenRtpTimestamp,
                    isLastNalUnit ? 1 : 0,
                    ScreenVideoPayloadType);
            }
            else
            {
                // Large NAL unit - fragment using FU-A
                SendFuAFragments(nalUnit, isLastNalUnit);
            }
        }
    }

    /// <summary>
    /// Sends a large NAL unit using FU-A fragmentation (RFC 6184).
    /// </summary>
    private void SendFuAFragments(byte[] nalUnit, bool isLastNalUnit)
    {
        if (nalUnit.Length == 0) return;

        byte nalHeader = nalUnit[0];
        byte nalType = (byte)(nalHeader & 0x1F);
        byte nri = (byte)(nalHeader & 0x60);
        byte fuIndicator = (byte)(nri | 28); // FU-A type

        int offset = 1;
        bool isFirst = true;
        int fragmentPayloadSize = MaxRtpPayloadSize - 2;

        while (offset < nalUnit.Length)
        {
            int remaining = nalUnit.Length - offset;
            int fragmentSize = Math.Min(remaining, fragmentPayloadSize);
            bool isLast = (offset + fragmentSize >= nalUnit.Length);

            byte fuHeader = nalType;
            if (isFirst) fuHeader |= 0x80; // S bit
            if (isLast) fuHeader |= 0x40;  // E bit

            var fuPacket = new byte[2 + fragmentSize];
            fuPacket[0] = fuIndicator;
            fuPacket[1] = fuHeader;
            Array.Copy(nalUnit, offset, fuPacket, 2, fragmentSize);

            int marker = (isLast && isLastNalUnit) ? 1 : 0;

            _screenRtpSeqNum++;
            _serverConnection?.SendRtpRaw(
                SDPMediaTypesEnum.video,
                fuPacket,
                _screenRtpTimestamp,
                marker,
                ScreenVideoPayloadType);

            offset += fragmentSize;
            isFirst = false;
        }
    }

    /// <summary>
    /// Disconnects audio source from the connection.
    /// </summary>
    public void DisconnectAudioSource(IAudioSource? audioSource)
    {
        if (audioSource != null && _serverConnection != null)
        {
            audioSource.OnAudioSourceEncodedSample -= _serverConnection.SendAudio;
        }
    }

    /// <summary>
    /// Closes the server connection.
    /// </summary>
    public void Close()
    {
        if (_serverConnection != null)
        {
            try
            {
                _serverConnection.close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SfuConnectionManager: Error closing connection: {ex.Message}");
            }
            _serverConnection = null;
        }

        _serverConnectionState = PeerConnectionState.Closed;
        _cameraVideoTrack = null;
        _screenVideoTrack = null;
        _screenAudioTrack = null;
        _screenAudioFirstPacket = true;
        _screenRtpTimestamp = 0;
        _screenRtpSeqNum = 0;
        _pendingSfuOffer = null;
    }

    private void UpdateConnectionStatus(VoiceConnectionStatus status)
    {
        Console.WriteLine($"SfuConnectionManager: Connection status changed to {status}");
        ConnectionStatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await Task.CompletedTask;
    }
}
