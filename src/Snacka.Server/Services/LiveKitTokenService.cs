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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

public interface ILiveKitTokenService
{
    bool IsConfigured { get; }
    string Url { get; }

    /// <summary>Creates a token that lets <paramref name="userId"/> join <paramref name="room"/>.</summary>
    string CreateJoinToken(Guid userId, string displayName, string room, bool canPublish);
}

/// <summary>
/// Issues LiveKit access tokens: HS256 JWTs signed with the API secret, issuer = API key,
/// subject = participant identity, and a "video" grant object (see LiveKit's token docs).
/// </summary>
public sealed class LiveKitTokenService(IOptions<LiveKitSettings> options) : ILiveKitTokenService
{
    private readonly LiveKitSettings _settings = options.Value;

    public bool IsConfigured => _settings.IsConfigured;
    public string Url => _settings.Url ?? "";

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

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
