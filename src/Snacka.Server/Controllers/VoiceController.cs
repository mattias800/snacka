using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

/// <summary>
/// Voice channels (#52): LiveKit tokens and who's in voice. Voice media goes through LiveKit, not
/// this server; presence is updated from LiveKit's webhooks (<see cref="LiveKitWebhookController"/>).
/// </summary>
[ApiController]
[Authorize]
[Route("api/voice")]
public class VoiceController(SnackaDbContext db, ILiveKitTokenService liveKit, IVoiceService voice) : ControllerBase
{
    /// <summary>LiveKit room name for a voice channel is this prefix + the channel id.</summary>
    public const string RoomPrefix = "channel-";

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
        var room = $"{RoomPrefix}{channelId}";
        var token = liveKit.CreateJoinToken(userId, user.EffectiveDisplayName, room, canPublish: true);
        return Ok(new VoiceTokenResponse(liveKit.Url, token, room));
    }

    /// <summary>Everyone currently in a voice channel of the community, for the channel list.</summary>
    [HttpGet("communities/{communityId:guid}/participants")]
    public async Task<ActionResult<List<VoiceParticipantResponse>>> GetParticipants(Guid communityId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        await db.EnsureCommunityMemberAsync(communityId, userId, cancellationToken);

        var channelIds = await db.Channels.AsNoTracking()
            .Where(c => c.CommunityId == communityId && c.Type == ChannelType.Voice)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var result = new List<VoiceParticipantResponse>();
        foreach (var channelId in channelIds)
            result.AddRange(await voice.GetParticipantsAsync(channelId, cancellationToken));
        return Ok(result);
    }
}
