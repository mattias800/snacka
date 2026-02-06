using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Snacka.Server.DTOs;

namespace Snacka.Server.Services;

public interface ITunnelService
{
    TunnelInfo CreateTunnel(Guid ownerId, string ownerUsername, Guid channelId, int localPort, string? label);
    void RemoveTunnel(string tunnelId);
    List<TunnelInfo> RemoveAllTunnelsForUser(Guid userId);
    TunnelInfo? GetTunnel(string tunnelId);
    IReadOnlyList<TunnelInfo> GetTunnelsForChannel(Guid channelId);
    IReadOnlyList<TunnelInfo> GetTunnelsForUser(Guid userId);
    int GetTunnelCountForUser(Guid userId);

    // Access tokens
    string GenerateAccessToken(string tunnelId, Guid userId);
    TunnelAccessClaim? ValidateAccessToken(string token);

    // Session cookies
    string GenerateSessionCookie(string tunnelId, Guid userId);
    TunnelAccessClaim? ValidateSessionCookie(string cookie);

    // Connection management (data plane)
    void RegisterControlConnection(string tunnelId, WebSocket controlWs);
    void UnregisterControlConnection(string tunnelId);
    bool HasControlConnection(string tunnelId);
    WebSocket? GetControlConnection(string tunnelId);
    Task<WebSocket?> WaitForDataConnection(string tunnelId, string connectionId, TimeSpan timeout);
    void RegisterDataConnection(string tunnelId, string connectionId, WebSocket dataWs);
}

public record TunnelInfo(
    string TunnelId,
    Guid OwnerId,
    string OwnerUsername,
    Guid ChannelId,
    int LocalPort,
    string? Label,
    DateTime CreatedAt
);

public record TunnelAccessClaim(string TunnelId, Guid UserId, DateTime ExpiresAt);

public class TunnelService : ITunnelService
{
    private const int MaxTunnelsPerUser = 5;
    private const int TunnelIdLength = 8;
    private const int AccessTokenExpiryMinutes = 5;
    private const int SessionCookieExpiryMinutes = 30;

    private readonly ConcurrentDictionary<string, TunnelInfo> _tunnels = new();
    private readonly ConcurrentDictionary<string, WebSocket> _controlConnections = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocket>> _pendingDataConnections = new();
    private readonly byte[] _signingKey;
    private readonly ILogger<TunnelService> _logger;

    public TunnelService(IOptions<JwtSettings> jwtSettings, ILogger<TunnelService> logger)
    {
        _logger = logger;
        // Derive a separate key for tunnel tokens from the JWT secret
        _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes("tunnel:" + jwtSettings.Value.SecretKey));
    }

    public TunnelInfo CreateTunnel(Guid ownerId, string ownerUsername, Guid channelId, int localPort, string? label)
    {
        if (GetTunnelCountForUser(ownerId) >= MaxTunnelsPerUser)
            throw new InvalidOperationException($"Maximum of {MaxTunnelsPerUser} tunnels per user reached");

        var tunnelId = GenerateTunnelId();
        var tunnel = new TunnelInfo(tunnelId, ownerId, ownerUsername, channelId, localPort, label, DateTime.UtcNow);

        if (!_tunnels.TryAdd(tunnelId, tunnel))
            throw new InvalidOperationException("Failed to create tunnel (ID collision)");

        _logger.LogInformation("Tunnel {TunnelId} created by {Username} for port {Port} in channel {ChannelId}",
            tunnelId, ownerUsername, localPort, channelId);

        return tunnel;
    }

    public void RemoveTunnel(string tunnelId)
    {
        if (_tunnels.TryRemove(tunnelId, out var tunnel))
        {
            // Close control connection if any
            if (_controlConnections.TryRemove(tunnelId, out var ws))
            {
                try { ws.Abort(); } catch { /* ignore */ }
            }

            // Cancel any pending data connections
            CancelPendingConnections(tunnelId);

            _logger.LogInformation("Tunnel {TunnelId} removed (was port {Port} by {Username})",
                tunnelId, tunnel.LocalPort, tunnel.OwnerUsername);
        }
    }

    public List<TunnelInfo> RemoveAllTunnelsForUser(Guid userId)
    {
        var removed = new List<TunnelInfo>();
        var userTunnels = _tunnels.Values.Where(t => t.OwnerId == userId).ToList();

        foreach (var tunnel in userTunnels)
        {
            if (_tunnels.TryRemove(tunnel.TunnelId, out var t))
            {
                removed.Add(t);
                if (_controlConnections.TryRemove(tunnel.TunnelId, out var ws))
                {
                    try { ws.Abort(); } catch { /* ignore */ }
                }
                CancelPendingConnections(tunnel.TunnelId);
            }
        }

        if (removed.Count > 0)
            _logger.LogInformation("Removed {Count} tunnels for user {UserId}", removed.Count, userId);

        return removed;
    }

    public TunnelInfo? GetTunnel(string tunnelId)
    {
        return _tunnels.GetValueOrDefault(tunnelId);
    }

    public IReadOnlyList<TunnelInfo> GetTunnelsForChannel(Guid channelId)
    {
        return _tunnels.Values.Where(t => t.ChannelId == channelId).ToList();
    }

    public IReadOnlyList<TunnelInfo> GetTunnelsForUser(Guid userId)
    {
        return _tunnels.Values.Where(t => t.OwnerId == userId).ToList();
    }

    public int GetTunnelCountForUser(Guid userId)
    {
        return _tunnels.Values.Count(t => t.OwnerId == userId);
    }

    // ========================================================================
    // Access tokens & session cookies
    // ========================================================================

    public string GenerateAccessToken(string tunnelId, Guid userId)
    {
        return GenerateSignedToken(tunnelId, userId, TimeSpan.FromMinutes(AccessTokenExpiryMinutes));
    }

    public TunnelAccessClaim? ValidateAccessToken(string token)
    {
        return ValidateSignedToken(token);
    }

    public string GenerateSessionCookie(string tunnelId, Guid userId)
    {
        return GenerateSignedToken(tunnelId, userId, TimeSpan.FromMinutes(SessionCookieExpiryMinutes));
    }

    public TunnelAccessClaim? ValidateSessionCookie(string cookie)
    {
        return ValidateSignedToken(cookie);
    }

    // ========================================================================
    // Connection management
    // ========================================================================

    public void RegisterControlConnection(string tunnelId, WebSocket controlWs)
    {
        _controlConnections[tunnelId] = controlWs;
        _logger.LogInformation("Control connection registered for tunnel {TunnelId}", tunnelId);
    }

    public void UnregisterControlConnection(string tunnelId)
    {
        _controlConnections.TryRemove(tunnelId, out _);
        CancelPendingConnections(tunnelId);
        _logger.LogInformation("Control connection unregistered for tunnel {TunnelId}", tunnelId);
    }

    public bool HasControlConnection(string tunnelId)
    {
        return _controlConnections.TryGetValue(tunnelId, out var ws) &&
               ws.State == WebSocketState.Open;
    }

    public WebSocket? GetControlConnection(string tunnelId)
    {
        if (_controlConnections.TryGetValue(tunnelId, out var ws) && ws.State == WebSocketState.Open)
            return ws;
        return null;
    }

    public Task<WebSocket?> WaitForDataConnection(string tunnelId, string connectionId, TimeSpan timeout)
    {
        var key = $"{tunnelId}:{connectionId}";
        var tcs = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingDataConnections[key] = tcs;

        // Set timeout
        var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() =>
        {
            if (tcs.TrySetResult(null!))
            {
                _pendingDataConnections.TryRemove(key, out _);
            }
            cts.Dispose();
        });

        return tcs.Task;
    }

    public void RegisterDataConnection(string tunnelId, string connectionId, WebSocket dataWs)
    {
        var key = $"{tunnelId}:{connectionId}";
        if (_pendingDataConnections.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(dataWs);
        }
        else
        {
            _logger.LogWarning("No pending data connection for tunnel {TunnelId} connection {ConnectionId}", tunnelId, connectionId);
            try { dataWs.Abort(); } catch { /* ignore */ }
        }
    }

    // ========================================================================
    // Private helpers
    // ========================================================================

    private static string GenerateTunnelId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(TunnelIdLength);
        var result = new char[TunnelIdLength];
        for (int i = 0; i < TunnelIdLength; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }
        return new string(result);
    }

    private string GenerateSignedToken(string tunnelId, Guid userId, TimeSpan expiry)
    {
        var expiresAt = DateTime.UtcNow.Add(expiry);
        var payload = $"{tunnelId}|{userId}|{expiresAt:O}";
        var signature = ComputeSignature(payload);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{signature}"));
        return token;
    }

    private TunnelAccessClaim? ValidateSignedToken(string token)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var parts = decoded.Split('|');
            if (parts.Length != 4) return null;

            var tunnelId = parts[0];
            if (!Guid.TryParse(parts[1], out var userId)) return null;
            if (!DateTime.TryParse(parts[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt)) return null;
            var signature = parts[3];

            // Verify signature
            var payload = $"{tunnelId}|{userId}|{expiresAt:O}";
            var expectedSignature = ComputeSignature(payload);
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSignature)))
            {
                return null;
            }

            // Check expiry
            if (DateTime.UtcNow > expiresAt) return null;

            // Check tunnel still exists
            if (!_tunnels.ContainsKey(tunnelId)) return null;

            return new TunnelAccessClaim(tunnelId, userId, expiresAt);
        }
        catch
        {
            return null;
        }
    }

    private string ComputeSignature(string payload)
    {
        var hash = HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private void CancelPendingConnections(string tunnelId)
    {
        var keysToRemove = _pendingDataConnections.Keys
            .Where(k => k.StartsWith(tunnelId + ":"))
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_pendingDataConnections.TryRemove(key, out var tcs))
            {
                tcs.TrySetResult(null!);
            }
        }
    }
}
