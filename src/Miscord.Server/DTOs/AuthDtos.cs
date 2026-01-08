using System.ComponentModel.DataAnnotations;

namespace Miscord.Server.DTOs;

public record RegisterRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, EmailAddress] string Email,
    [Required, StringLength(100, MinimumLength = 8)] string Password,
    [Required] string InviteCode
);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password
);

public record AuthResponse(
    Guid UserId,
    string Username,
    string Email,
    bool IsServerAdmin,
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt
);

public record RefreshTokenRequest([Required] string RefreshToken);

public record UserProfileResponse(
    Guid Id,
    string Username,
    string? DisplayName,  // Custom display name, UTF-8 with emojis
    string EffectiveDisplayName,  // DisplayName ?? Username
    string Email,
    string? Avatar,  // AvatarFileName from model
    string? Status,
    bool IsOnline,
    bool IsServerAdmin,
    DateTime CreatedAt
);

public record AvatarUploadResponse(
    string? Avatar,
    bool Success
);

public record UpdateProfileRequest(
    [StringLength(50, MinimumLength = 3)] string? Username,
    [StringLength(32)] string? DisplayName,  // Custom display name, UTF-8 with emojis
    string? Status
);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, StringLength(100, MinimumLength = 8)] string NewPassword
);
