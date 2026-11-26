using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.Data;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

/// <summary>Issues, rotates and revokes refresh tokens (#15). See <see cref="RefreshToken"/>.</summary>
public interface IRefreshTokenService
{
    /// <summary>Starts a new family (a sign-in) and returns its first token.</summary>
    Task<string> IssueAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a token for its replacement. Throws <see cref="SecurityTokenException"/> if it isn't valid.</summary>
    Task<(Guid UserId, string NewToken)> RotateAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Signs out the token's family. Unknown tokens are ignored.</summary>
    Task RevokeFamilyAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Signs out every device of the user.</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class RefreshTokenService : IRefreshTokenService
{
    /// <summary>
    /// A token used again within this window is a race between two requests from the same client
    /// (two tabs waking up at once), not theft: it gets a token of its own instead of revoking the family.
    /// </summary>
    internal static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    private readonly SnackaDbContext _db;
    private readonly JwtSettings _settings;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(SnackaDbContext db, IOptions<JwtSettings> settings, ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> IssueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var token = Add(userId, Guid.NewGuid());
        await _db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<(Guid UserId, string NewToken)> RotateAsync(string token, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var stored = await FindAsync(token, cancellationToken);
        if (stored is null || stored.RevokedAt is not null || stored.ExpiresAt <= now)
            throw new SecurityTokenException("Invalid or expired refresh token.");

        if (stored.UsedAt is { } usedAt && now - usedAt > ReuseGrace)
        {
            _logger.LogWarning("Refresh token reused for user {UserId}; signing out that device", stored.UserId);
            await RevokeAsync(_db.RefreshTokens.Where(t => t.FamilyId == stored.FamilyId), now, cancellationToken);
            throw new SecurityTokenException("Refresh token was already used.");
        }

        stored.UsedAt ??= now;
        var next = Add(stored.UserId, stored.FamilyId);

        // Forget this user's tokens that can no longer be used.
        var dead = await _db.RefreshTokens
            .Where(t => t.UserId == stored.UserId && t.ExpiresAt < now)
            .ToListAsync(cancellationToken);
        _db.RefreshTokens.RemoveRange(dead);

        await _db.SaveChangesAsync(cancellationToken);
        return (stored.UserId, next);
    }

    public async Task RevokeFamilyAsync(string token, CancellationToken cancellationToken = default)
    {
        var stored = await FindAsync(token, cancellationToken);
        if (stored is not null)
            await RevokeAsync(_db.RefreshTokens.Where(t => t.FamilyId == stored.FamilyId), DateTime.UtcNow, cancellationToken);
    }

    public Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default) =>
        RevokeAsync(_db.RefreshTokens.Where(t => t.UserId == userId), DateTime.UtcNow, cancellationToken);

    private string Add(Guid userId, Guid familyId)
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(token),
            ExpiresAt = DateTime.UtcNow.AddDays(_settings.RefreshTokenExpirationDays)
        });
        return token;
    }

    private Task<RefreshToken?> FindAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        return _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }

    private async Task RevokeAsync(IQueryable<RefreshToken> tokens, DateTime now, CancellationToken cancellationToken)
    {
        // Loaded and saved rather than ExecuteUpdate: the in-memory test provider doesn't support it.
        foreach (var t in await tokens.Where(t => t.RevokedAt == null).ToListAsync(cancellationToken))
            t.RevokedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
