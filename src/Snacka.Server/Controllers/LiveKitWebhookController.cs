using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

/// <summary>
/// Receives LiveKit webhooks (#52) and keeps voice presence (who is in which voice channel) in
/// the database, so every community member sees it, also the ones who aren't in voice. Clients
/// get the same VoiceParticipantJoined/Left events as from the legacy SFU.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/livekit/webhook")]
public class LiveKitWebhookController(
    SnackaDbContext db,
    IVoiceService voice,
    ILiveKitTokenService liveKit,
    IHubContext<SnackaHub> hub,
    ILogger<LiveKitWebhookController> logger) : ControllerBase
{
    /// <summary>LiveKit can deliver "left" after the same user's next "joined" (e.g. a second device).</summary>
    private static readonly TimeSpan StaleLeaveTolerance = TimeSpan.FromSeconds(5);

    [HttpPost]
    [Consumes("application/webhook+json", "application/json")]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();
        if (!liveKit.IsValidWebhook(Request.Headers.Authorization.ToString(), body))
            return Unauthorized();

        WebhookEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<WebhookEvent>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return BadRequest();
        }
        if (evt?.Room?.Name is not { } roomName || !TryParseRoom(roomName, out var channelId))
            return Ok(); // Not one of our voice rooms; nothing to do.

        switch (evt.Event)
        {
            case "participant_joined" when Guid.TryParse(evt.Participant?.Identity, out var userId):
                await OnJoinedAsync(channelId, userId, cancellationToken);
                break;
            case "participant_left" when Guid.TryParse(evt.Participant?.Identity, out var userId):
                await OnLeftAsync(channelId, userId, evt.Participant!.JoinedAt, cancellationToken);
                break;
            case "room_finished":
                await OnRoomFinishedAsync(channelId, cancellationToken);
                break;
        }
        return Ok();
    }

    private async Task OnJoinedAsync(Guid channelId, Guid userId, CancellationToken cancellationToken)
    {
        var previous = await voice.GetUserCurrentChannelAsync(userId, cancellationToken);
        if (previous == channelId) return;

        VoiceParticipantResponse participant;
        try
        {
            participant = await voice.JoinChannelAsync(channelId, userId, cancellationToken);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
        {
            // Tokens are only issued to members, so this means membership or the channel changed.
            logger.LogWarning(e, "Ignoring LiveKit join of {UserId} to {ChannelId}", userId, channelId);
            return;
        }

        if (previous is { } left) await BroadcastLeftAsync(left, userId, cancellationToken);
        if (await CommunityOfAsync(channelId, cancellationToken) is { } communityId)
            await hub.Clients.Group($"community:{communityId}")
                .SendAsync("VoiceParticipantJoined", new VoiceParticipantJoinedEvent(channelId, participant), cancellationToken);
    }

    private async Task OnLeftAsync(Guid channelId, Guid userId, long? liveKitJoinedAt, CancellationToken cancellationToken)
    {
        var row = await db.VoiceParticipants.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.UserId == userId, cancellationToken);
        if (row is null) return;
        if (liveKitJoinedAt is { } joined && DateTimeOffset.FromUnixTimeSeconds(joined).UtcDateTime + StaleLeaveTolerance < row.JoinedAt)
            return; // An older session of this user ending; the current one is still there.

        await voice.LeaveChannelAsync(channelId, userId, cancellationToken);
        await BroadcastLeftAsync(channelId, userId, cancellationToken);
    }

    private async Task OnRoomFinishedAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var userIds = await db.VoiceParticipants.Where(p => p.ChannelId == channelId).Select(p => p.UserId).ToListAsync(cancellationToken);
        foreach (var userId in userIds)
        {
            await voice.LeaveChannelAsync(channelId, userId, cancellationToken);
            await BroadcastLeftAsync(channelId, userId, cancellationToken);
        }
    }

    private async Task BroadcastLeftAsync(Guid channelId, Guid userId, CancellationToken cancellationToken)
    {
        if (await CommunityOfAsync(channelId, cancellationToken) is { } communityId)
            await hub.Clients.Group($"community:{communityId}")
                .SendAsync("VoiceParticipantLeft", new VoiceParticipantLeftEvent(channelId, userId), cancellationToken);
    }

    private Task<Guid?> CommunityOfAsync(Guid channelId, CancellationToken cancellationToken) =>
        db.Channels.Where(c => c.Id == channelId).Select(c => (Guid?)c.CommunityId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Voice rooms are named "channel-{channelId}" by <see cref="VoiceController"/>.</summary>
    internal static bool TryParseRoom(string room, out Guid channelId)
    {
        channelId = Guid.Empty;
        return room.StartsWith(VoiceController.RoomPrefix, StringComparison.Ordinal)
               && Guid.TryParse(room.AsSpan(VoiceController.RoomPrefix.Length), out channelId);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // The subset of LiveKit's WebhookEvent we use (protobuf JSON: camelCase, int64 as strings).
    private sealed record WebhookEvent(string? Event, RoomInfo? Room, ParticipantInfo? Participant);
    private sealed record RoomInfo(string? Name);
    private sealed record ParticipantInfo(
        string? Identity,
        [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
        long? JoinedAt);
}
