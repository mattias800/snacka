using System.ComponentModel.DataAnnotations;

namespace Snacka.Shared.Models;

public class UserCommunity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid UserId { get; set; }
    public User? User { get; set; }
    public required Guid CommunityId { get; set; }
    public Community? Community { get; set; }
    public required UserRole Role { get; set; } = UserRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Per-community display name override (nickname). Supports UTF-8 including emojis.
    /// If set, this is shown instead of the user's default display name in this community.
    /// </summary>
    [MaxLength(32)]
    public string? DisplayNameOverride { get; set; }
}

public enum UserRole
{
    Owner,
    Admin,
    Moderator,
    Member
}
