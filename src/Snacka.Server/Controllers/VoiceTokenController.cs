using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

/// <summary>Hands out LiveKit tokens for voice channels (#52). Voice media goes through LiveKit, not this server.</summary>
[ApiController]
[Authorize]
[Route("api/voice")]
public class VoiceTokenController(SnackaDbContext db, ILiveKitTokenService liveKit) : ControllerBase
{
    [HttpPost("channels/{channelId:guid}/token")]
    public async Task<ActionResult<VoiceTokenResponse>> GetToken(Guid channelId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        if (!liveKit.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Voice is not configured on this server." });

        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
            return NotFound(new { error = "Channel not found." });
        if (channel.Type != ChannelType.Voice)
            return BadRequest(new { error = "Not a voice channel." });

        await db.EnsureChannelMemberAsync(channelId, userId, cancellationToken); // 403 for non-members

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken);
        var room = $"channel-{channelId}";
        var token = liveKit.CreateJoinToken(userId, user.EffectiveDisplayName, room, canPublish: true);
        return Ok(new VoiceTokenResponse(liveKit.Url, token, room));
    }
}
