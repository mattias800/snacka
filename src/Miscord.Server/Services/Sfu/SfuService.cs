using System.Collections.Concurrent;
using SIPSorcery.Net;

namespace Miscord.Server.Services.Sfu;

/// <summary>
/// Implementation of ISfuService that manages all SFU channel managers.
/// Registered as a singleton to maintain WebRTC sessions across requests.
/// </summary>
public class SfuService : ISfuService, IDisposable
{
    private readonly ILogger<SfuService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<Guid, SfuChannelManager> _channelManagers = new();
    private readonly object _lock = new();
    private bool _disposed;

    public event Action<Guid, Guid, RTCIceCandidate>? OnIceCandidateForClient;
    public event Action<Guid, Guid, RTCPeerConnectionState>? OnSessionStateChanged;

    public SfuService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SfuService>();
        _logger.LogInformation("SFU Service initialized");
    }

    public SfuSession GetOrCreateSession(Guid channelId, Guid userId)
    {
        var channelManager = GetOrCreateChannelManager(channelId);
        var existingSession = channelManager.GetSession(userId);

        if (existingSession != null)
        {
            _logger.LogDebug("Returning existing session for user {UserId} in channel {ChannelId}", userId, channelId);
            return existingSession;
        }

        var session = channelManager.CreateSession(userId);
        _logger.LogInformation("Created new SFU session for user {UserId} in channel {ChannelId}", userId, channelId);
        return session;
    }

    public SfuSession? GetSession(Guid channelId, Guid userId)
    {
        if (_channelManagers.TryGetValue(channelId, out var channelManager))
        {
            return channelManager.GetSession(userId);
        }
        return null;
    }

    public void RemoveSession(Guid channelId, Guid userId)
    {
        if (_channelManagers.TryGetValue(channelId, out var channelManager))
        {
            channelManager.RemoveSession(userId);

            // Clean up empty channel managers
            if (channelManager.IsEmpty)
            {
                lock (_lock)
                {
                    if (channelManager.IsEmpty && _channelManagers.TryRemove(channelId, out var removed))
                    {
                        removed.Dispose();
                        _logger.LogInformation("Removed empty channel manager for channel {ChannelId}", channelId);
                    }
                }
            }
        }
    }

    public SfuChannelManager? GetChannelManager(Guid channelId)
    {
        _channelManagers.TryGetValue(channelId, out var channelManager);
        return channelManager;
    }

    private SfuChannelManager GetOrCreateChannelManager(Guid channelId)
    {
        return _channelManagers.GetOrAdd(channelId, id =>
        {
            var manager = new SfuChannelManager(id, _loggerFactory);

            // Wire up events to bubble up to the service level
            manager.OnIceCandidateForClient += (userId, candidate) =>
            {
                OnIceCandidateForClient?.Invoke(userId, channelId, candidate);
            };

            manager.OnSessionStateChanged += (userId, state) =>
            {
                OnSessionStateChanged?.Invoke(userId, channelId, state);
            };

            _logger.LogInformation("Created channel manager for channel {ChannelId}", id);
            return manager;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var channelManager in _channelManagers.Values)
        {
            channelManager.Dispose();
        }
        _channelManagers.Clear();

        _logger.LogInformation("SFU Service disposed");
    }
}
