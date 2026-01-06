using System.Collections.Concurrent;
using SIPSorcery.Net;

namespace Miscord.Server.Services.Sfu;

/// <summary>
/// Manages all SFU sessions for a single voice channel.
/// Handles participant join/leave and coordinates media forwarding.
/// </summary>
public class SfuChannelManager : IDisposable
{
    private readonly ILogger<SfuChannelManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<Guid, SfuSession> _sessions = new();
    private bool _disposed;

    public Guid ChannelId { get; }

    /// <summary>
    /// Fired when a session needs to send an ICE candidate to the client.
    /// Args: (userId, candidate)
    /// </summary>
    public event Action<Guid, RTCIceCandidate>? OnIceCandidateForClient;

    /// <summary>
    /// Fired when a session's connection state changes.
    /// Args: (userId, state)
    /// </summary>
    public event Action<Guid, RTCPeerConnectionState>? OnSessionStateChanged;

    public SfuChannelManager(Guid channelId, ILoggerFactory loggerFactory)
    {
        ChannelId = channelId;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SfuChannelManager>();

        _logger.LogInformation("SFU channel manager created for channel {ChannelId}", channelId);
    }

    /// <summary>
    /// Creates a new session for a user joining the voice channel.
    /// </summary>
    public SfuSession CreateSession(Guid userId)
    {
        if (_sessions.TryGetValue(userId, out var existingSession))
        {
            _logger.LogWarning("Session already exists for user {UserId} in channel {ChannelId}, disposing old session",
                userId, ChannelId);
            RemoveSession(userId);
        }

        var session = new SfuSession(
            userId,
            ChannelId,
            _loggerFactory.CreateLogger<SfuSession>());

        // Wire up events
        session.OnIceCandidate += candidate =>
        {
            OnIceCandidateForClient?.Invoke(userId, candidate);
        };

        session.OnConnectionStateChanged += state =>
        {
            OnSessionStateChanged?.Invoke(userId, state);

            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                _logger.LogInformation("Session {UserId} connection failed/closed, removing from channel", userId);
                // Don't call RemoveSession here - let the hub handle cleanup
            }
        };

        // Wire up raw RTP forwarding for minimal latency
        session.OnAudioRtpReceived += OnAudioRtpReceivedFromSession;
        session.OnVideoRtpReceived += OnVideoRtpReceivedFromSession;

        if (!_sessions.TryAdd(userId, session))
        {
            session.Dispose();
            throw new InvalidOperationException($"Failed to add session for user {userId}");
        }

        _logger.LogInformation("Created SFU session for user {UserId} in channel {ChannelId}. Total sessions: {Count}",
            userId, ChannelId, _sessions.Count);

        return session;
    }

    /// <summary>
    /// Gets an existing session for a user.
    /// </summary>
    public SfuSession? GetSession(Guid userId)
    {
        _sessions.TryGetValue(userId, out var session);
        return session;
    }

    /// <summary>
    /// Removes and disposes a session when a user leaves the channel.
    /// </summary>
    public void RemoveSession(Guid userId)
    {
        if (_sessions.TryRemove(userId, out var session))
        {
            session.OnAudioRtpReceived -= OnAudioRtpReceivedFromSession;
            session.OnVideoRtpReceived -= OnVideoRtpReceivedFromSession;
            session.Dispose();

            _logger.LogInformation("Removed SFU session for user {UserId} from channel {ChannelId}. Remaining sessions: {Count}",
                userId, ChannelId, _sessions.Count);
        }
    }

    /// <summary>
    /// Gets all active sessions in this channel.
    /// </summary>
    public IEnumerable<SfuSession> GetAllSessions()
    {
        return _sessions.Values;
    }

    /// <summary>
    /// Gets the number of active sessions in this channel.
    /// </summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Checks if the channel has any active sessions.
    /// </summary>
    public bool IsEmpty => _sessions.IsEmpty;

    private int _audioPacketCount;
    private int _videoPacketCount;

    private void OnAudioRtpReceivedFromSession(SfuSession sender, RTPPacket packet)
    {
        _audioPacketCount++;
        if (_audioPacketCount <= 5 || _audioPacketCount % 1000 == 0)
        {
            _logger.LogInformation("Audio RTP {Count} from {UserId}, size={Size}, forwarding to {OtherCount} sessions",
                _audioPacketCount, sender.UserId, packet.Payload.Length, _sessions.Count - 1);
        }

        // Forward raw RTP to all OTHER sessions (not back to sender)
        foreach (var session in _sessions.Values)
        {
            if (session.UserId != sender.UserId &&
                session.ConnectionState == RTCPeerConnectionState.connected)
            {
                try
                {
                    session.ForwardAudioRtpRaw(packet);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to forward audio to session {UserId}", session.UserId);
                }
            }
        }
    }

    private void OnVideoRtpReceivedFromSession(SfuSession sender, RTPPacket packet)
    {
        _videoPacketCount++;
        if (_videoPacketCount <= 5 || _videoPacketCount % 500 == 0)
        {
            _logger.LogInformation("Video RTP {Count} from {UserId}, size={Size}, marker={Marker}, forwarding to {OtherCount} sessions",
                _videoPacketCount, sender.UserId, packet.Payload.Length, packet.Header.MarkerBit, _sessions.Count - 1);
        }

        // Forward raw RTP to all OTHER sessions (not back to sender)
        foreach (var session in _sessions.Values)
        {
            if (session.UserId != sender.UserId &&
                session.ConnectionState == RTCPeerConnectionState.connected)
            {
                try
                {
                    session.ForwardVideoRtpRaw(packet);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to forward video to session {UserId}", session.UserId);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var session in _sessions.Values)
        {
            session.OnAudioRtpReceived -= OnAudioRtpReceivedFromSession;
            session.OnVideoRtpReceived -= OnVideoRtpReceivedFromSession;
            session.Dispose();
        }
        _sessions.Clear();

        _logger.LogInformation("SFU channel manager disposed for channel {ChannelId}", ChannelId);
    }
}
