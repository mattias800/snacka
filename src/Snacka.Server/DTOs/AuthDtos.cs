using System.ComponentModel.DataAnnotations;

namespace Snacka.Server.DTOs;

public record RegisterRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, EmailAddress] string Email,
    [Required, StringLength(100, MinimumLength = 8)] string Password,
    string? InviteCode  // Optional for first user (server setup)
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
    [StringLength(32)] string? DisplayName,  // Custom display name, UTF-8 with emojis; "" clears it
    [StringLength(128)] string? Status        // "" clears it
);

/// <summary>
/// Hub event "UserProfileUpdated": sent to everyone who shares a community or conversation with the user
/// (and the user's other sessions) when their name or avatar changes. No email: that stays private.
/// </summary>
public record UserProfileUpdatedEvent(
    Guid UserId,
    string Username,
    string EffectiveDisplayName,
    string? Avatar
);

/// <summary>Deleting the account needs the password, so a stolen session can't do it.</summary>
public record DeleteAccountRequest([Required] string Password);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, StringLength(100, MinimumLength = 8)] string NewPassword
);
