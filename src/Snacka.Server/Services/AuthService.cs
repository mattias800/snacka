using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

public sealed partial class AuthService : IAuthService
{
    private readonly SnackaDbContext _db;
    private readonly JwtSettings _jwtSettings;
    private readonly IServerInviteService _inviteService;
    private readonly ICommunityService _communityService;
    private readonly INotificationService _notificationService;
    private readonly IRefreshTokenService _refreshTokens;

    // SECURITY: Password complexity regex - requires uppercase, lowercase, digit, and special character
    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[!@#$%^&*()_+\-=\[\]{}|;':"",./<>?\\`~]).{8,}$")]
    private static partial Regex PasswordComplexityRegex();

    public AuthService(
        SnackaDbContext db,
        IOptions<JwtSettings> jwtSettings,
        IServerInviteService inviteService,
        ICommunityService communityService,
        INotificationService notificationService,
        IRefreshTokenService refreshTokens)
    {
        _db = db;
        _jwtSettings = jwtSettings.Value;
        _inviteService = inviteService;
        _communityService = communityService;
        _notificationService = notificationService;
        _refreshTokens = refreshTokens;
    }

    /// <summary>
    /// SECURITY: Validates password complexity requirements.
    /// Throws InvalidOperationException if password doesn't meet requirements.
    /// </summary>
    private static void ValidatePasswordComplexity(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Password must be at least 8 characters long.");
        }

        if (!PasswordComplexityRegex().IsMatch(password))
        {
            throw new InvalidOperationException(
                "Password must contain at least one uppercase letter, one lowercase letter, one digit, and one special character.");
        }
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        // SECURITY: Validate password complexity
        ValidatePasswordComplexity(request.Password);

        // Validate invite code
        var invite = await _inviteService.ValidateInviteCodeAsync(request.InviteCode, cancellationToken);
        if (invite == null)
            throw new InvalidOperationException("Invalid or expired invite code.");

        var normalizedEmail = request.Email.ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
            throw new InvalidOperationException("Email is already registered.");

        if (await _db.Users.AnyAsync(u => u.Username == request.Username, cancellationToken))
            throw new InvalidOperationException("Username is already taken.");

        // Check if this will be the first user (server admin)
        var isFirstUser = !await _inviteService.HasAnyUsersAsync(cancellationToken);

        // Get inviter ID (who created the invite)
        var inviterId = await _inviteService.GetInviterIdAsync(request.InviteCode, cancellationToken);

        var user = new User
        {
            Username = request.Username,
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsOnline = true,
            IsServerAdmin = isFirstUser,  // First user becomes server admin
            InvitedById = inviterId
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        // Notify admins about new user registration (skip for first user since they become the admin)
        if (!isFirstUser)
        {
            await _notificationService.CreateNotificationsForAdminsAsync(
                NotificationType.UserJoinedServer,
                "New user registered",
                $"{user.Username} has joined the server",
                payload: new { UserId = user.Id, Username = user.Username },
                actorId: user.Id,
                excludeUserId: user.Id,
                cancellationToken: cancellationToken);
        }

        // Create default community for first user (server setup)
        if (isFirstUser)
        {
            await _communityService.CreateCommunityAsync(
                user.Id,
                new DTOs.CreateCommunityRequest("General", "Welcome to your new Snacka server!"),
                cancellationToken);
        }

        // Mark invite as used
        await _inviteService.UseInviteAsync(request.InviteCode, cancellationToken);

        return CreateAuthResponse(user, await _refreshTokens.IssueAsync(user.Id, cancellationToken));
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = request.Email.ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new InvalidOperationException("Invalid email or password.");

        user.IsOnline = true;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return CreateAuthResponse(user, await _refreshTokens.IssueAsync(user.Id, cancellationToken));
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var (userId, next) = await _refreshTokens.RotateAsync(refreshToken, cancellationToken);
        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new SecurityTokenException("User not found.");
        return CreateAuthResponse(user, next);
    }

    public async Task<UserProfileResponse> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            throw new InvalidOperationException("User not found.");

        return CreateUserProfileResponse(user);
    }

    public async Task<UserProfileResponse> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            throw new InvalidOperationException("User not found.");

        // null leaves a field alone; for the optional fields, blank clears it (an empty display name
        // would otherwise show as no name at all, since EffectiveDisplayName only falls back on null).
        if (request.Username is not null)
        {
            var username = request.Username.Trim();
            if (username.Length < 3)
                throw new InvalidOperationException("Username must be at least 3 characters.");
            if (await _db.Users.AnyAsync(u => u.Username == username && u.Id != userId, cancellationToken))
                throw new InvalidOperationException("Username is already taken.");
            user.Username = username;
        }

        if (request.DisplayName is not null)
            user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();

        if (request.Status is not null)
            user.Status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return CreateUserProfileResponse(user);
    }

    public async Task<UserProfileResponse> UpdateAvatarAsync(Guid userId, string? avatarFileName, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            throw new InvalidOperationException("User not found.");

        user.AvatarFileName = avatarFileName;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return CreateUserProfileResponse(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        // SECURITY: Validate password complexity
        ValidatePasswordComplexity(request.NewPassword);

        var user = await _db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            throw new InvalidOperationException("User not found.");

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            throw new InvalidOperationException("Current password is incorrect.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        // Sign out every other device. Their access tokens still work until they expire.
        await _refreshTokens.RevokeAllAsync(userId, cancellationToken);
    }

    public async Task DeleteAccountAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            throw new InvalidOperationException("Password is incorrect.");

        // Communities belong to their owner in the database: deleting the owner deletes them, with
        // every channel and message. Fine for a community of one, not for one others are in.
        var owned = await _db.Communities
            .Where(c => c.OwnerId == userId && _db.UserCommunities.Any(uc => uc.CommunityId == c.Id && uc.UserId != userId))
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);
        if (owned.Count > 0)
            throw new InvalidOperationException($"You own {string.Join(", ", owned)}. Transfer ownership to someone else, or delete it, first.");

        if (user.IsServerAdmin
            && !await _db.Users.AnyAsync(u => u.IsServerAdmin && u.Id != userId, cancellationToken)
            && await _db.Users.AnyAsync(u => u.Id != userId, cancellationToken))
            throw new InvalidOperationException("You're the server's only admin. Make someone else an admin first.");

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private AuthResponse CreateAuthResponse(User user, string refreshToken)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);
        return new AuthResponse(
            user.Id,
            user.Username,
            user.Email,
            user.IsServerAdmin,
            GenerateAccessToken(user, expiresAt),
            refreshToken,
            expiresAt
        );
    }

    private string GenerateAccessToken(User user, DateTime expiresAt)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Refresh tokens used to be JWTs carrying this claim. They are opaque now, but the old ones stay
    /// valid signatures until they expire, so the bearer handler still rejects them (#15).
    /// </summary>
    public const string TokenTypeClaim = "token_type";
    public const string RefreshTokenType = "refresh";

    private static UserProfileResponse CreateUserProfileResponse(User user)
    {
        return new UserProfileResponse(
            user.Id,
            user.Username,
            user.DisplayName,
            user.EffectiveDisplayName,
            user.Email,
            user.AvatarFileName,
            user.Status,
            user.IsOnline,
            user.IsServerAdmin,
            user.CreatedAt
        );
    }
}
