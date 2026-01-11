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

    // SECURITY: Password complexity regex - requires uppercase, lowercase, digit, and special character
    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[!@#$%^&*()_+\-=\[\]{}|;':"",./<>?\\`~]).{8,}$")]
    private static partial Regex PasswordComplexityRegex();

    public AuthService(SnackaDbContext db, IOptions<JwtSettings> jwtSettings, IServerInviteService inviteService)
    {
        _db = db;
        _jwtSettings = jwtSettings.Value;
        _inviteService = inviteService;
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
        
        if (invite == null && string.IsNullOrEmpty(request.InviteCode))
        {
            // In development, allow registration without an invite code if none provided
            // The server will create a temporary invite for tracking
            invite = await _inviteService.CreateInviteAsync(null, maxUses: 1, cancellationToken: cancellationToken);
        }
        
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

        // Mark invite as used
        await _inviteService.UseInviteAsync(request.InviteCode, cancellationToken);

        return GenerateTokens(user);
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

        return GenerateTokens(user);
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var principal = GetPrincipalFromExpiredToken(refreshToken);
        var userIdClaim = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            throw new SecurityTokenException("Invalid refresh token.");

        var user = await _db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            throw new SecurityTokenException("User not found.");

        return GenerateTokens(user);
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

        if (request.Username is not null)
        {
            if (await _db.Users.AnyAsync(u => u.Username == request.Username && u.Id != userId, cancellationToken))
                throw new InvalidOperationException("Username is already taken.");
            user.Username = request.Username;
        }

        if (request.DisplayName is not null)
            user.DisplayName = request.DisplayName;

        if (request.Status is not null)
            user.Status = request.Status;

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
    }

    public async Task DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FindAsync([userId], cancellationToken);
        if (user is null)
            throw new InvalidOperationException("User not found.");

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private AuthResponse GenerateTokens(User user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes);
        var accessToken = GenerateAccessToken(user, expiresAt);
        var refreshToken = GenerateRefreshToken(user);

        return new AuthResponse(
            user.Id,
            user.Username,
            user.Email,
            user.IsServerAdmin,
            accessToken,
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

    private string GenerateRefreshToken(User user)
    {
        var expiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays);
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("token_type", "refresh")
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

    private ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey)),
            ValidateLifetime = false,
            ValidIssuer = _jwtSettings.Issuer,
            ValidAudience = _jwtSettings.Audience
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);

        if (securityToken is not JwtSecurityToken jwtSecurityToken ||
            !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
        {
            throw new SecurityTokenException("Invalid token.");
        }

        return principal;
    }

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
