using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IConfiguration _configuration;
    private readonly IServerInviteService _inviteService;
    private readonly IHubContext<SnackaHub> _hubContext;
    private readonly SnackaDbContext _db;

    public AuthController(
        IAuthService authService,
        IConfiguration configuration,
        IServerInviteService inviteService,
        IHubContext<SnackaHub> hubContext,
        SnackaDbContext db)
    {
        _authService = authService;
        _configuration = configuration;
        _inviteService = inviteService;
        _hubContext = hubContext;
        _db = db;
    }

    [HttpGet("server-info")]
    public async Task<ActionResult<SetupServerInfoResponse>> GetServerInfo(CancellationToken cancellationToken)
    {
        var hasUsers = await _inviteService.HasAnyUsersAsync(cancellationToken);

        return Ok(new SetupServerInfoResponse(
            HasUsers: hasUsers,
            AllowRegistration: _configuration.GetValue("ServerInfo:AllowRegistration", true)
        ));
    }

    [HttpPost("register")]
    [EnableRateLimiting("register")]
    public async Task<ActionResult<AuthResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _authService.RegisterAsync(request, cancellationToken);

            // Broadcast to all connected clients that a new user registered
            // This allows admin panels to update in real-time
            var newUser = await _db.Users
                .Include(u => u.InvitedBy)
                .FirstOrDefaultAsync(u => u.Id == response.UserId, cancellationToken);

            if (newUser != null)
            {
                var adminUserResponse = new AdminUserResponse(
                    newUser.Id,
                    newUser.Username,
                    newUser.Email,
                    newUser.IsServerAdmin,
                    newUser.IsOnline,
                    newUser.CreatedAt,
                    newUser.InvitedBy?.Username
                );
                await _hubContext.Clients.All.SendAsync("UserRegistered", adminUserResponse, cancellationToken);
            }

            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<AuthResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _authService.LoginAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPost("refresh")]
    [EnableRateLimiting("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _authService.RefreshTokenAsync(request.RefreshToken, cancellationToken);
            return Ok(response);
        }
        catch (SecurityTokenException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IImageProcessingService _imageService;
    private readonly SnackaDbContext _db;

    public UsersController(
        IAuthService authService,
        IImageProcessingService imageService,
        SnackaDbContext db)
    {
        _authService = authService;
        _imageService = imageService;
        _db = db;
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserProfileResponse>> GetProfile(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            var profile = await _authService.GetProfileAsync(userId.Value, cancellationToken);
            return Ok(profile);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("me")]
    public async Task<ActionResult<UserProfileResponse>> UpdateProfile(
        [FromBody] UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            var profile = await _authService.UpdateProfileAsync(userId.Value, request, cancellationToken);
            return Ok(profile);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("me/password")]
    public async Task<ActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            await _authService.ChangePasswordAsync(userId.Value, request, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("me")]
    public async Task<ActionResult> DeleteAccount(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            await _authService.DeleteAccountAsync(userId.Value, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("me/avatar")]
    public async Task<ActionResult<AvatarUploadResponse>> UploadAvatar(
        IFormFile file,
        [FromQuery] double? cropX,
        [FromQuery] double? cropY,
        [FromQuery] double? cropWidth,
        [FromQuery] double? cropHeight,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        // Validate the file
        var validation = _imageService.ValidateAvatar(file.FileName, file.Length);
        if (!validation.IsValid)
            return BadRequest(new { error = validation.ErrorMessage });

        // Create crop region if all parameters provided
        CropRegion? cropRegion = null;
        if (cropX.HasValue && cropY.HasValue && cropWidth.HasValue && cropHeight.HasValue)
        {
            cropRegion = new CropRegion(cropX.Value, cropY.Value, cropWidth.Value, cropHeight.Value);
        }

        // Process and save the avatar
        await using var stream = file.OpenReadStream();
        var result = await _imageService.ProcessAvatarAsync(stream, cropRegion, cancellationToken);

        if (!result.Success)
            return BadRequest(new { error = result.ErrorMessage });

        try
        {
            // Update the user's avatar in the database
            var profile = await _authService.UpdateAvatarAsync(userId.Value, result.FileName, cancellationToken);
            return Ok(new AvatarUploadResponse(profile.Avatar, true));
        }
        catch (InvalidOperationException ex)
        {
            // Clean up the saved file if database update fails
            await _imageService.DeleteAvatarAsync(result.FileName, cancellationToken);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("me/avatar")]
    public async Task<ActionResult> DeleteAvatar(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            // Get current avatar filename before deleting
            var profile = await _authService.GetProfileAsync(userId.Value, cancellationToken);
            var oldAvatar = profile.Avatar;

            // Clear avatar in database
            await _authService.UpdateAvatarAsync(userId.Value, null, cancellationToken);

            // Delete the file
            if (!string.IsNullOrEmpty(oldAvatar))
            {
                await _imageService.DeleteAvatarAsync(oldAvatar, cancellationToken);
            }

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a user's avatar image.
    /// Requires authentication but not community membership - avatars are visible to all logged-in users.
    /// Authentication via query string (?access_token=xxx) is supported for img tags.
    /// </summary>
    [HttpGet("{userId:guid}/avatar")]
    [Authorize]
    public async Task<ActionResult> GetUserAvatar(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await _authService.GetProfileAsync(userId, cancellationToken);
            if (string.IsNullOrEmpty(profile.Avatar))
                return NotFound();

            var result = await _imageService.GetAvatarAsync(profile.Avatar, cancellationToken);
            if (result == null)
                return NotFound();

            return File(result.Stream, result.ContentType);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Search for users by username. Returns users that match the search query.
    /// Excludes the current user from results.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<UserSearchResult>>> SearchUsers(
        [FromQuery] string q,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Array.Empty<UserSearchResult>());

        var queryLower = q.ToLowerInvariant();

        var users = await _db.Users
            .Where(u => u.Id != userId.Value && u.Username.ToLower().Contains(queryLower))
            .OrderBy(u => u.Username)
            .Take(20)
            .Select(u => new UserSearchResult(
                u.Id,
                u.Username,
                u.DisplayName ?? u.Username,
                u.AvatarFileName,
                u.IsOnline
            ))
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

public record SetupServerInfoResponse(bool HasUsers, bool AllowRegistration);
