using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Snacka.Server.Services;

/// <summary>LiveKit server connection settings (section "LiveKit").</summary>
public sealed class LiveKitSettings
{
    public const string SectionName = "LiveKit";

    /// <summary>WebSocket URL clients connect to, e.g. wss://voice.example.com or ws://localhost:7880.</summary>
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public int TokenLifetimeMinutes { get; set; } = 360;

    /// <summary>
    /// Clients reach LiveKit through this server (<see cref="BundledLiveKitOptions.ProxyPath"/>) rather
    /// than at <see cref="Url"/>: set when the server runs a bundled LiveKit.
    /// </summary>
    public bool ViaServer { get; set; }

    public bool IsConfigured => (ViaServer || !string.IsNullOrWhiteSpace(Url)) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

public interface ILiveKitTokenService
{
    bool IsConfigured { get; }

    /// <summary>The URL a client that reached this server with <paramref name="request"/> should connect to.</summary>
    string UrlFor(HttpRequest request);

    /// <summary>Creates a token that lets <paramref name="userId"/> join <paramref name="room"/>.</summary>
    string CreateJoinToken(Guid userId, string displayName, string room, bool canPublish);

    /// <summary>
    /// Checks that a webhook request came from our LiveKit server: the Authorization header is a
    /// JWT signed with the API secret, issued by the API key, whose "sha256" claim is the base64
    /// SHA-256 of the body.
    /// </summary>
    bool IsValidWebhook(string? authorization, ReadOnlySpan<byte> body);
}

/// <summary>
/// Issues LiveKit access tokens: HS256 JWTs signed with the API secret, issuer = API key,
/// subject = participant identity, and a "video" grant object (see LiveKit's token docs).
/// </summary>
public sealed class LiveKitTokenService(IOptions<LiveKitSettings> options) : ILiveKitTokenService
{
    private readonly LiveKitSettings _settings = options.Value;

    public bool IsConfigured => _settings.IsConfigured;
    public string UrlFor(HttpRequest request) =>
        _settings.ViaServer
            ? $"{(request.IsHttps ? "wss" : "ws")}://{request.Host}{request.PathBase}{BundledLiveKitOptions.ProxyPath}"
            : _settings.Url ?? "";

    public string CreateJoinToken(Guid userId, string displayName, string room, bool canPublish)
    {
        if (!IsConfigured) throw new InvalidOperationException("LiveKit is not configured.");
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["iss"] = _settings.ApiKey!,
            ["sub"] = userId.ToString(),
            ["name"] = displayName,
            ["nbf"] = now.AddSeconds(-10).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(_settings.TokenLifetimeMinutes).ToUnixTimeSeconds(),
            ["video"] = new Dictionary<string, object>
            {
                ["room"] = room,
                ["roomJoin"] = true,
                ["canPublish"] = canPublish,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
            },
        };
        // Signed by hand: Microsoft.IdentityModel refuses HS256 keys under 128 bits, but LiveKit
        // accepts any secret (its --dev secret is literally "secret"). Operators should still use
        // long random secrets in production.
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.ApiSecret!), Encoding.ASCII.GetBytes($"{header}.{body}"));
        return $"{header}.{body}.{Base64Url(signature)}";
    }

    public bool IsValidWebhook(string? authorization, ReadOnlySpan<byte> body)
    {
        if (!IsConfigured || string.IsNullOrEmpty(authorization)) return false;
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : authorization;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.ApiSecret!), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2]))) return false;

            using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "HS256") return false;

            using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
            var root = claims.RootElement;
            if (root.GetProperty("iss").GetString() != _settings.ApiKey) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const long skew = 60;
            if (root.TryGetProperty("exp", out var exp) && exp.GetInt64() + skew < now) return false;
            if (root.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() - skew > now) return false;

            var bodyHash = Convert.FromBase64String(root.GetProperty("sha256").GetString() ?? "");
            return CryptographicOperations.FixedTimeEquals(bodyHash, SHA256.HashData(body));
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
