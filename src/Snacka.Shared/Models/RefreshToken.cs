namespace Snacka.Shared.Models;

/// <summary>
/// A refresh token, stored only as a hash. Every use rotates it: the token is marked used and a new
/// one in the same family (one sign-in on one device) replaces it. Presenting a used token again
/// means it was copied, so the whole family is revoked (#15).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 of the token, hex encoded.</summary>
    public required string TokenHash { get; set; }

    /// <summary>All tokens descended from one sign-in share a family.</summary>
    public Guid FamilyId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the token was exchanged for a new one.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>When the token's family was signed out, or revoked because of reuse or a password change.</summary>
    public DateTime? RevokedAt { get; set; }
}
