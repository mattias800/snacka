using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public interface IVoiceService
{
    Task<VoiceParticipantResponse> JoinChannelAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);
    Task LeaveChannelAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);
    Task LeaveAllChannelsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<VoiceParticipantResponse?> UpdateStateAsync(Guid channelId, Guid userId, VoiceStateUpdate update, CancellationToken cancellationToken = default);
    Task<IEnumerable<VoiceParticipantResponse>> GetParticipantsAsync(Guid channelId, CancellationToken cancellationToken = default);
    Task<VoiceParticipantResponse?> GetParticipantAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);
    Task<Guid?> GetUserCurrentChannelAsync(Guid userId, CancellationToken cancellationToken = default);

    // Admin voice control methods
    Task<VoiceParticipantResponse?> SetServerMuteAsync(Guid channelId, Guid targetUserId, bool isServerMuted, CancellationToken cancellationToken = default);
    Task<VoiceParticipantResponse?> SetServerDeafenAsync(Guid channelId, Guid targetUserId, bool isServerDeafened, CancellationToken cancellationToken = default);
    Task<(VoiceParticipantResponse participant, Guid fromChannelId)?> MoveUserAsync(Guid targetUserId, Guid targetChannelId, CancellationToken cancellationToken = default);
}

public class VoiceService : IVoiceService
{
    private readonly MiscordDbContext _db;
    private readonly ILogger<VoiceService> _logger;

    public VoiceService(MiscordDbContext db, ILogger<VoiceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<VoiceParticipantResponse> JoinChannelAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default)
    {
        var channel = await _db.Channels
            .Include(c => c.Community)
            .FirstOrDefaultAsync(c => c.Id == channelId && c.Type == ChannelType.Voice, cancellationToken)
            ?? throw new InvalidOperationException("Voice channel not found");

        var isMember = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId && uc.CommunityId == channel.CommunityId, cancellationToken);

        if (!isMember)
            throw new UnauthorizedAccessException("User is not a member of this community");

        // Leave any existing voice channel first
        await LeaveAllChannelsAsync(userId, cancellationToken);

        var user = await _db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("User not found");

        var participant = new VoiceParticipant
        {
            UserId = userId,
            ChannelId = channelId,
            IsMuted = false,
            IsDeafened = false,
            IsScreenSharing = false,
            IsCameraOn = false,
            JoinedAt = DateTime.UtcNow
        };

        _db.VoiceParticipants.Add(participant);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {Username} joined voice channel {ChannelId}", user.Username, channelId);

        return new VoiceParticipantResponse(
            participant.Id,
            participant.UserId,
            user.Username,
            participant.ChannelId,
            participant.IsMuted,
            participant.IsDeafened,
            participant.IsServerMuted,
            participant.IsServerDeafened,
            participant.IsScreenSharing,
            participant.ScreenShareHasAudio,
            participant.IsCameraOn,
            participant.JoinedAt
        );
    }

    public async Task LeaveChannelAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default)
    {
        var participant = await _db.VoiceParticipants
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.UserId == userId, cancellationToken);

        if (participant is not null)
        {
            _db.VoiceParticipants.Remove(participant);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User {UserId} left voice channel {ChannelId}", userId, channelId);
        }
    }

    public async Task LeaveAllChannelsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var participants = await _db.VoiceParticipants
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);

        if (participants.Count > 0)
        {
            _db.VoiceParticipants.RemoveRange(participants);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("User {UserId} left all voice channels", userId);
        }
    }

    public async Task<VoiceParticipantResponse?> UpdateStateAsync(Guid channelId, Guid userId, VoiceStateUpdate update, CancellationToken cancellationToken = default)
    {
        var participant = await _db.VoiceParticipants
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.UserId == userId, cancellationToken);

        if (participant is null)
            return null;

        if (update.IsMuted.HasValue)
            participant.IsMuted = update.IsMuted.Value;
        if (update.IsDeafened.HasValue)
            participant.IsDeafened = update.IsDeafened.Value;
        if (update.IsScreenSharing.HasValue)
            participant.IsScreenSharing = update.IsScreenSharing.Value;
        if (update.ScreenShareHasAudio.HasValue)
            participant.ScreenShareHasAudio = update.ScreenShareHasAudio.Value;
        if (update.IsCameraOn.HasValue)
            participant.IsCameraOn = update.IsCameraOn.Value;

        await _db.SaveChangesAsync(cancellationToken);

        return new VoiceParticipantResponse(
            participant.Id,
            participant.UserId,
            participant.User!.Username,
            participant.ChannelId,
            participant.IsMuted,
            participant.IsDeafened,
            participant.IsServerMuted,
            participant.IsServerDeafened,
            participant.IsScreenSharing,
            participant.ScreenShareHasAudio,
            participant.IsCameraOn,
            participant.JoinedAt
        );
    }

    public async Task<IEnumerable<VoiceParticipantResponse>> GetParticipantsAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        return await _db.VoiceParticipants
            .Include(p => p.User)
            .Where(p => p.ChannelId == channelId)
            .Select(p => new VoiceParticipantResponse(
                p.Id,
                p.UserId,
                p.User!.Username,
                p.ChannelId,
                p.IsMuted,
                p.IsDeafened,
                p.IsServerMuted,
                p.IsServerDeafened,
                p.IsScreenSharing,
                p.ScreenShareHasAudio,
                p.IsCameraOn,
                p.JoinedAt
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<VoiceParticipantResponse?> GetParticipantAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.VoiceParticipants
            .Include(p => p.User)
            .Where(p => p.ChannelId == channelId && p.UserId == userId)
            .Select(p => new VoiceParticipantResponse(
                p.Id,
                p.UserId,
                p.User!.Username,
                p.ChannelId,
                p.IsMuted,
                p.IsDeafened,
                p.IsServerMuted,
                p.IsServerDeafened,
                p.IsScreenSharing,
                p.ScreenShareHasAudio,
                p.IsCameraOn,
                p.JoinedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid?> GetUserCurrentChannelAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var participant = await _db.VoiceParticipants
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        return participant?.ChannelId;
    }

    /// <summary>
    /// Sets server-mute status for a user (admin action).
    /// </summary>
    public async Task<VoiceParticipantResponse?> SetServerMuteAsync(
        Guid channelId,
        Guid targetUserId,
        bool isServerMuted,
        CancellationToken cancellationToken = default)
    {
        var participant = await _db.VoiceParticipants
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.UserId == targetUserId, cancellationToken);

        if (participant is null) return null;

        participant.IsServerMuted = isServerMuted;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Server mute set for user {UserId} in channel {ChannelId}: {IsServerMuted}",
            targetUserId, channelId, isServerMuted);

        return new VoiceParticipantResponse(
            participant.Id,
            participant.UserId,
            participant.User!.Username,
            participant.ChannelId,
            participant.IsMuted,
            participant.IsDeafened,
            participant.IsServerMuted,
            participant.IsServerDeafened,
            participant.IsScreenSharing,
            participant.ScreenShareHasAudio,
            participant.IsCameraOn,
            participant.JoinedAt
        );
    }

    /// <summary>
    /// Sets server-deafen status for a user (admin action).
    /// Server deafen also implies server mute.
    /// </summary>
    public async Task<VoiceParticipantResponse?> SetServerDeafenAsync(
        Guid channelId,
        Guid targetUserId,
        bool isServerDeafened,
        CancellationToken cancellationToken = default)
    {
        var participant = await _db.VoiceParticipants
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.ChannelId == channelId && p.UserId == targetUserId, cancellationToken);

        if (participant is null) return null;

        participant.IsServerDeafened = isServerDeafened;
        // Server deafen implies server mute
        if (isServerDeafened)
        {
            participant.IsServerMuted = true;
        }
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Server deafen set for user {UserId} in channel {ChannelId}: {IsServerDeafened}",
            targetUserId, channelId, isServerDeafened);

        return new VoiceParticipantResponse(
            participant.Id,
            participant.UserId,
            participant.User!.Username,
            participant.ChannelId,
            participant.IsMuted,
            participant.IsDeafened,
            participant.IsServerMuted,
            participant.IsServerDeafened,
            participant.IsScreenSharing,
            participant.ScreenShareHasAudio,
            participant.IsCameraOn,
            participant.JoinedAt
        );
    }

    /// <summary>
    /// Moves a user from their current voice channel to a different one (admin action).
    /// </summary>
    public async Task<(VoiceParticipantResponse participant, Guid fromChannelId)?> MoveUserAsync(
        Guid targetUserId,
        Guid targetChannelId,
        CancellationToken cancellationToken = default)
    {
        var participant = await _db.VoiceParticipants
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == targetUserId, cancellationToken);

        if (participant is null) return null;

        var oldChannelId = participant.ChannelId;

        // Verify target channel exists and is a voice channel
        var targetChannel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == targetChannelId && c.Type == Miscord.Shared.Models.ChannelType.Voice, cancellationToken);

        if (targetChannel is null)
            throw new InvalidOperationException("Target voice channel not found");

        // Move the participant
        participant.ChannelId = targetChannelId;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} moved from channel {OldChannelId} to {NewChannelId}",
            targetUserId, oldChannelId, targetChannelId);

        var response = new VoiceParticipantResponse(
            participant.Id,
            participant.UserId,
            participant.User!.Username,
            participant.ChannelId,
            participant.IsMuted,
            participant.IsDeafened,
            participant.IsServerMuted,
            participant.IsServerDeafened,
            participant.IsScreenSharing,
            participant.ScreenShareHasAudio,
            participant.IsCameraOn,
            participant.JoinedAt
        );

        return (response, oldChannelId);
    }
}
