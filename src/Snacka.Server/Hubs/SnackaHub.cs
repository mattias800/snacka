using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Server.Services.Sfu;
using Snacka.Shared.Models;

namespace Snacka.Server.Hubs;

public record UserPresence(Guid UserId, string Username, bool IsOnline);

[Authorize]
public class SnackaHub : Hub
{
    private readonly SnackaDbContext _db;
    private readonly IVoiceService _voiceService;
    private readonly ISfuService _sfuService;
    private readonly IHubContext<SnackaHub> _hubContext;
    private readonly ILogger<SnackaHub> _logger;
    private static readonly Dictionary<string, Guid> ConnectedUsers = new();
    private static readonly Dictionary<Guid, string> UserConnections = new(); // UserId -> ConnectionId
    private static readonly object Lock = new();

    public SnackaHub(SnackaDbContext db, IVoiceService voiceService, ISfuService sfuService, IHubContext<SnackaHub> hubContext, ILogger<SnackaHub> logger)
    {
        _db = db;
        _voiceService = voiceService;
        _sfuService = sfuService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (userId is null)
        {
            Context.Abort();
            return;
        }

        lock (Lock)
        {
            ConnectedUsers[Context.ConnectionId] = userId.Value;
            UserConnections[userId.Value] = Context.ConnectionId;
        }

        try
        {
            var user = await _db.Users.FindAsync(userId.Value);
            if (user is not null)
            {
                user.IsOnline = true;
                user.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                // Notify all clients about user coming online
                await Clients.Others.SendAsync("UserOnline", new UserPresence(user.Id, user.Username, true));

                // Add user to their community groups
                var communityIds = await _db.UserCommunities
                    .Where(uc => uc.UserId == userId.Value)
                    .Select(uc => uc.CommunityId)
                    .ToListAsync();

                foreach (var communityId in communityIds)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"community:{communityId}");
                    _logger.LogInformation("User {Username} added to group community:{CommunityId}", user.Username, communityId);
                }

                _logger.LogInformation("User {Username} connected, added to {Count} community groups", user.Username, communityIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update user online status during connect for user {UserId}", userId.Value);
            // Continue with connection - user is authenticated but status update failed
            // They can still use the app, status will be wrong until next reconnect
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Guid? userId;
        lock (Lock)
        {
            if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var id))
            {
                userId = id;
                ConnectedUsers.Remove(Context.ConnectionId);
                UserConnections.Remove(id);
            }
            else
            {
                userId = null;
            }
        }

        if (userId is not null)
        {
            // Leave any voice channels on disconnect - wrap in try-catch for DB resilience
            try
            {
                var currentChannel = await _voiceService.GetUserCurrentChannelAsync(userId.Value);
                if (currentChannel.HasValue)
                {
                    // Remove SFU session first (in-memory, won't fail)
                    _sfuService.RemoveSession(currentChannel.Value, userId.Value);

                    // Get the channel to find its community
                    Guid? communityId = null;
                    try
                    {
                        var channel = await _db.Channels
                            .FirstOrDefaultAsync(c => c.Id == currentChannel.Value);
                        communityId = channel?.CommunityId;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to get channel info during disconnect for user {UserId}", userId.Value);
                    }

                    try
                    {
                        await _voiceService.LeaveChannelAsync(currentChannel.Value, userId.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to leave voice channel during disconnect for user {UserId}", userId.Value);
                    }

                    // Notify users in the community (best effort)
                    if (communityId.HasValue)
                    {
                        try
                        {
                            await Clients.Group($"community:{communityId.Value}")
                                .SendAsync("VoiceParticipantLeft", new VoiceParticipantLeftEvent(currentChannel.Value, userId.Value));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to notify voice participant left for user {UserId}", userId.Value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get current voice channel during disconnect for user {UserId}", userId.Value);
                // Continue with disconnect handling even if voice cleanup fails
            }

            // Update user online status - wrap in try-catch for DB resilience
            try
            {
                var user = await _db.Users.FindAsync(userId.Value);
                if (user is not null)
                {
                    user.IsOnline = false;
                    user.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();

                    await Clients.Others.SendAsync("UserOffline", new UserPresence(user.Id, user.Username, false));
                    _logger.LogInformation("User {Username} disconnected", user.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update user offline status for user {UserId}", userId.Value);
                // Still try to notify clients even if DB update failed
                try
                {
                    await Clients.Others.SendAsync("UserOffline", new UserPresence(userId.Value, "Unknown", false));
                }
                catch
                {
                    // Ignore notification failures
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinServer(Guid communityId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var isMember = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId.Value && uc.CommunityId == communityId);

        if (isMember)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"community:{communityId}");
            _logger.LogInformation("User joined community group: {CommunityId}", communityId);
        }
    }

    public async Task LeaveServer(Guid communityId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"community:{communityId}");
        _logger.LogInformation("User left community group: {CommunityId}", communityId);
    }

    public async Task JoinChannel(Guid channelId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var channel = await _db.Channels
            .Include(c => c.Community)
            .FirstOrDefaultAsync(c => c.Id == channelId);

        if (channel is null) return;

        var isMember = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId.Value && uc.CommunityId == channel.CommunityId);

        if (isMember)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"channel:{channelId}");
            _logger.LogInformation("User joined channel group: {ChannelId}", channelId);
        }
    }

    public async Task LeaveChannel(Guid channelId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channel:{channelId}");
        _logger.LogInformation("User left channel group: {ChannelId}", channelId);
    }

    public async Task<IEnumerable<UserPresence>> GetOnlineUsers()
    {
        var userId = GetUserId();
        if (userId is null) return [];

        // SECURITY: Only return online users from communities the current user is a member of
        // This prevents user enumeration across the entire server
        var userCommunityIds = await _db.UserCommunities
            .Where(uc => uc.UserId == userId)
            .Select(uc => uc.CommunityId)
            .ToListAsync();

        var onlineUsers = await _db.UserCommunities
            .Where(uc => userCommunityIds.Contains(uc.CommunityId) && uc.User.IsOnline)
            .Select(uc => new UserPresence(uc.UserId, uc.User.Username, true))
            .Distinct()
            .ToListAsync();

        return onlineUsers;
    }

    // ==================== Typing Indicator Methods ====================

    /// <summary>
    /// Notifies other users in a channel that the current user is typing.
    /// </summary>
    public async Task SendTyping(Guid channelId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var user = await _db.Users.FindAsync(userId.Value);
        if (user is null) return;

        // Get the channel to verify membership and get community
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        var isMember = await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId.Value && uc.CommunityId == channel.CommunityId);
        if (!isMember) return;

        // Broadcast typing event to others in the channel
        await Clients.OthersInGroup($"channel:{channelId}")
            .SendAsync("UserTyping", new TypingEvent(channelId, userId.Value, user.Username, user.EffectiveDisplayName));
    }

    /// <summary>
    /// Notifies a user that someone is typing in a DM conversation.
    /// </summary>
    public async Task SendDMTyping(Guid recipientUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify users share at least one community (basic relationship check)
        if (!await DoUsersShareCommunityAsync(userId.Value, recipientUserId))
        {
            _logger.LogWarning("User {UserId} attempted to send DM typing to {RecipientId} without shared community",
                userId.Value, recipientUserId);
            return;
        }

        var user = await _db.Users.FindAsync(userId.Value);
        if (user is null) return;

        // Find recipient's connection
        string? targetConnectionId;
        lock (Lock)
        {
            UserConnections.TryGetValue(recipientUserId, out targetConnectionId);
        }

        if (targetConnectionId is not null)
        {
            await Clients.Client(targetConnectionId)
                .SendAsync("DMUserTyping", new DMTypingEvent(userId.Value, user.Username, user.EffectiveDisplayName));
        }
    }

    // ==================== Voice Channel Methods ====================

    public async Task<VoiceParticipantResponse?> JoinVoiceChannel(Guid channelId)
    {
        var userId = GetUserId();
        if (userId is null) return null;

        try
        {
            // Get the channel to find its community
            var channel = await _db.Channels
                .Include(c => c.Community)
                .FirstOrDefaultAsync(c => c.Id == channelId && c.Type == Shared.Models.ChannelType.Voice);
            if (channel is null) return null;

            // Leave current voice channel if in one
            var currentChannel = await _voiceService.GetUserCurrentChannelAsync(userId.Value);
            if (currentChannel.HasValue)
            {
                await LeaveVoiceChannel(currentChannel.Value);
            }

            var participant = await _voiceService.JoinChannelAsync(channelId, userId.Value);

            // Join the voice channel SignalR group (for WebRTC signaling)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"voice:{channelId}");

            // Create SFU session for this user
            var session = _sfuService.GetOrCreateSession(channelId, userId.Value);

            // Capture connection ID for ICE candidate callback
            var connectionId = Context.ConnectionId;

            // Subscribe to ICE candidates from this session
            // Use captured hubContext since the hub instance may be disposed when this fires
            var hubContext = _hubContext;

            // Subscribe to audio SSRC discovery for per-user volume control
            session.OnAudioSsrcDiscovered += (sess, audioSsrc) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Broadcast to all participants in the voice channel
                        await hubContext.Clients.Group($"voice:{channelId}")
                            .SendAsync("UserAudioSsrcMapped", new SsrcMappingEvent(channelId, sess.UserId, audioSsrc));
                        _logger.LogDebug("Broadcast mic audio SSRC {Ssrc} for user {UserId} in channel {ChannelId}",
                            audioSsrc, sess.UserId, channelId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast mic audio SSRC for user {UserId}", sess.UserId);
                    }
                });
            };

            // Subscribe to screen audio SSRC discovery - client will filter based on watching status
            session.OnScreenAudioSsrcDiscovered += (sess, screenAudioSsrc) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Broadcast to all participants in the voice channel
                        await hubContext.Clients.Group($"voice:{channelId}")
                            .SendAsync("UserScreenAudioSsrcMapped", new ScreenAudioSsrcMappingEvent(channelId, sess.UserId, screenAudioSsrc));
                        _logger.LogDebug("Broadcast screen audio SSRC {Ssrc} for user {UserId} in channel {ChannelId}",
                            screenAudioSsrc, sess.UserId, channelId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast screen audio SSRC for user {UserId}", sess.UserId);
                    }
                });
            };

            // Subscribe to camera video SSRC discovery - client needs this to route video to correct decoder
            session.OnCameraVideoSsrcDiscovered += (sess, cameraVideoSsrc) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Broadcast to all participants in the voice channel
                        await hubContext.Clients.Group($"voice:{channelId}")
                            .SendAsync("UserCameraVideoSsrcMapped", new CameraVideoSsrcMappingEvent(channelId, sess.UserId, cameraVideoSsrc));
                        _logger.LogInformation("Broadcast camera video SSRC {Ssrc} for user {UserId} in channel {ChannelId}",
                            cameraVideoSsrc, sess.UserId, channelId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast camera video SSRC for user {UserId}", sess.UserId);
                    }
                });
            };

            session.OnIceCandidate += candidate =>
            {
                // Queue the ICE candidate to be sent asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? targetConnectionId;
                        lock (Lock)
                        {
                            UserConnections.TryGetValue(userId.Value, out targetConnectionId);
                        }

                        if (targetConnectionId != null)
                        {
                            await hubContext.Clients.Client(targetConnectionId).SendAsync("SfuIceCandidate", new
                            {
                                Candidate = candidate.candidate,
                                SdpMid = candidate.sdpMid,
                                SdpMLineIndex = candidate.sdpMLineIndex
                            });
                            _logger.LogDebug("Sent ICE candidate to user {UserId}", userId.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send ICE candidate to user {UserId}", userId.Value);
                    }
                });
            };

            // Add media tracks and create offer
            session.AddMediaTracks();
            var sdpOffer = await session.CreateOfferAsync();

            // Send SFU offer to the client
            await Clients.Caller.SendAsync("SfuOffer", new { Sdp = sdpOffer, ChannelId = channelId });
            _logger.LogInformation("Sent SFU offer to user {UserId} for channel {ChannelId}", userId.Value, channelId);

            // Send existing SSRC mappings to the newly joined user (for per-user volume control)
            var existingMappings = _sfuService.GetAudioSsrcMappings(channelId, uid =>
            {
                // Look up username from cache or database
                var existingParticipant = _voiceService.GetParticipantsAsync(channelId).GetAwaiter().GetResult()
                    .FirstOrDefault(p => p.UserId == uid);
                return existingParticipant?.Username ?? "Unknown";
            });
            if (existingMappings.Count > 0)
            {
                await Clients.Caller.SendAsync("SsrcMappingsBatch", new SsrcMappingBatchEvent(channelId, existingMappings));
                _logger.LogDebug("Sent {Count} existing SSRC mappings to user {UserId}", existingMappings.Count, userId.Value);
            }

            // Notify ALL users in the community (so everyone can see who's in voice)
            await Clients.OthersInGroup($"community:{channel.CommunityId}")
                .SendAsync("VoiceParticipantJoined", new VoiceParticipantJoinedEvent(channelId, participant));

            _logger.LogInformation("User {UserId} joined voice channel {ChannelId}", userId.Value, channelId);

            return participant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join voice channel {ChannelId}", channelId);
            return null;
        }
    }

    /// <summary>
    /// Receives the SDP answer from a client for the SFU connection.
    /// </summary>
    public async Task SendSfuAnswer(Guid channelId, string sdp)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var session = _sfuService.GetSession(channelId, userId.Value);
        if (session is null)
        {
            _logger.LogWarning("No SFU session found for user {UserId} in channel {ChannelId}", userId.Value, channelId);
            return;
        }

        session.SetRemoteAnswer(sdp);
        _logger.LogInformation("Set SFU answer from user {UserId} for channel {ChannelId}", userId.Value, channelId);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Receives an ICE candidate from a client for the SFU connection.
    /// </summary>
    public async Task SendSfuIceCandidate(Guid channelId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var session = _sfuService.GetSession(channelId, userId.Value);
        if (session is null)
        {
            _logger.LogWarning("No SFU session found for user {UserId} in channel {ChannelId}", userId.Value, channelId);
            return;
        }

        session.AddIceCandidate(candidate, sdpMid, sdpMLineIndex);
        _logger.LogDebug("Added ICE candidate from user {UserId} for channel {ChannelId}", userId.Value, channelId);

        await Task.CompletedTask;
    }

    public async Task LeaveVoiceChannel(Guid channelId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Get the channel to find its community
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId);

        // Remove SFU session
        _sfuService.RemoveSession(channelId, userId.Value);

        await _voiceService.LeaveChannelAsync(channelId, userId.Value);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"voice:{channelId}");

        // Notify ALL users in the community (so everyone can see who left voice)
        if (channel is not null)
        {
            await Clients.Group($"community:{channel.CommunityId}")
                .SendAsync("VoiceParticipantLeft", new VoiceParticipantLeftEvent(channelId, userId.Value));
        }

        _logger.LogInformation("User {UserId} left voice channel {ChannelId}", userId.Value, channelId);
    }

    public async Task<IEnumerable<VoiceParticipantResponse>> GetVoiceParticipants(Guid channelId)
    {
        return await _voiceService.GetParticipantsAsync(channelId);
    }

    public async Task UpdateVoiceState(Guid channelId, VoiceStateUpdate update)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Get the channel and user info
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId);
        var user = await _db.Users.FindAsync(userId.Value);

        // Get current state before update
        var currentParticipant = await _voiceService.GetParticipantAsync(channelId, userId.Value);
        if (currentParticipant is null) return;

        // Prevent unmuting if server-muted
        if (update.IsMuted == false && currentParticipant.IsServerMuted)
        {
            _logger.LogWarning("User {UserId} tried to unmute but is server-muted", userId.Value);
            return;
        }

        // Prevent undeafening if server-deafened
        if (update.IsDeafened == false && currentParticipant.IsServerDeafened)
        {
            _logger.LogWarning("User {UserId} tried to undeafen but is server-deafened", userId.Value);
            return;
        }

        var wasCameraOn = currentParticipant.IsCameraOn;
        var wasScreenSharing = currentParticipant.IsScreenSharing;

        var participant = await _voiceService.UpdateStateAsync(channelId, userId.Value, update);
        if (participant is not null && channel is not null)
        {
            // Notify ALL users in the community about the state change
            await Clients.OthersInGroup($"community:{channel.CommunityId}")
                .SendAsync("VoiceStateChanged", new VoiceStateChangedEvent(channelId, userId.Value, update));

            // Broadcast video stream start/stop events
            var username = user?.Username ?? "Unknown";

            // Camera started
            if (update.IsCameraOn == true && !wasCameraOn)
            {
                await Clients.OthersInGroup($"community:{channel.CommunityId}")
                    .SendAsync("VideoStreamStarted", new
                    {
                        ChannelId = channelId,
                        UserId = userId.Value,
                        Username = username,
                        StreamType = Shared.Models.VideoStreamType.Camera
                    });
                _logger.LogInformation("User {Username} started camera in channel {ChannelId}", username, channelId);
            }
            // Camera stopped
            else if (update.IsCameraOn == false && wasCameraOn)
            {
                await Clients.OthersInGroup($"community:{channel.CommunityId}")
                    .SendAsync("VideoStreamStopped", new
                    {
                        ChannelId = channelId,
                        UserId = userId.Value,
                        StreamType = Shared.Models.VideoStreamType.Camera
                    });
                _logger.LogInformation("User {Username} stopped camera in channel {ChannelId}", username, channelId);
            }

            // Screen share started
            if (update.IsScreenSharing == true && !wasScreenSharing)
            {
                await Clients.OthersInGroup($"community:{channel.CommunityId}")
                    .SendAsync("VideoStreamStarted", new
                    {
                        ChannelId = channelId,
                        UserId = userId.Value,
                        Username = username,
                        StreamType = Shared.Models.VideoStreamType.ScreenShare
                    });
                _logger.LogInformation("User {Username} started screen share in channel {ChannelId}", username, channelId);
            }
            // Screen share stopped
            else if (update.IsScreenSharing == false && wasScreenSharing)
            {
                // Clear all viewers when screen share stops
                _sfuService.ClearScreenShareViewers(channelId, userId.Value);

                await Clients.OthersInGroup($"community:{channel.CommunityId}")
                    .SendAsync("VideoStreamStopped", new
                    {
                        ChannelId = channelId,
                        UserId = userId.Value,
                        StreamType = Shared.Models.VideoStreamType.ScreenShare
                    });
                _logger.LogInformation("User {Username} stopped screen share in channel {ChannelId}", username, channelId);
            }
        }
    }

    public async Task UpdateSpeakingState(Guid channelId, bool isSpeaking)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Get the channel to find its community
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId);

        if (channel is not null)
        {
            // Notify ALL users in the community about the speaking state change
            await Clients.OthersInGroup($"community:{channel.CommunityId}")
                .SendAsync("SpeakingStateChanged", new SpeakingStateChangedEvent(channelId, userId.Value, isSpeaking));
        }
    }

    // ==================== Admin Voice Channel Methods ====================

    /// <summary>
    /// Admin action to server-mute a user. Server-muted users cannot unmute themselves.
    /// </summary>
    public async Task ServerMuteUser(Guid channelId, Guid targetUserId, bool isServerMuted)
    {
        var adminUserId = GetUserId();
        if (adminUserId is null) return;

        // Get channel for authorization
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        // Check admin permissions
        var adminRole = await GetUserRoleInCommunity(adminUserId.Value, channel.CommunityId);
        if (adminRole != Shared.Models.UserRole.Owner && adminRole != Shared.Models.UserRole.Admin)
        {
            throw new HubException("You don't have permission to server mute users.");
        }

        var participant = await _voiceService.SetServerMuteAsync(channelId, targetUserId, isServerMuted);
        if (participant is null) return;

        var admin = await _db.Users.FindAsync(adminUserId.Value);

        // Broadcast to community
        await Clients.Group($"community:{channel.CommunityId}")
            .SendAsync("ServerVoiceStateChanged", new ServerVoiceStateChangedEvent(
                channelId,
                targetUserId,
                isServerMuted,
                null,
                adminUserId.Value,
                admin?.Username ?? "Admin"
            ));

        _logger.LogInformation("Admin {AdminId} server-muted user {TargetId} in channel {ChannelId}: {IsMuted}",
            adminUserId.Value, targetUserId, channelId, isServerMuted);
    }

    /// <summary>
    /// Admin action to server-deafen a user. Server-deafened users cannot undeafen themselves.
    /// Server deafen also implies server mute.
    /// </summary>
    public async Task ServerDeafenUser(Guid channelId, Guid targetUserId, bool isServerDeafened)
    {
        var adminUserId = GetUserId();
        if (adminUserId is null) return;

        // Get channel for authorization
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == channelId);
        if (channel is null) return;

        // Check admin permissions
        var adminRole = await GetUserRoleInCommunity(adminUserId.Value, channel.CommunityId);
        if (adminRole != Shared.Models.UserRole.Owner && adminRole != Shared.Models.UserRole.Admin)
        {
            throw new HubException("You don't have permission to server deafen users.");
        }

        var participant = await _voiceService.SetServerDeafenAsync(channelId, targetUserId, isServerDeafened);
        if (participant is null) return;

        var admin = await _db.Users.FindAsync(adminUserId.Value);

        // Broadcast to community (note: server deafen also sets server mute)
        await Clients.Group($"community:{channel.CommunityId}")
            .SendAsync("ServerVoiceStateChanged", new ServerVoiceStateChangedEvent(
                channelId,
                targetUserId,
                isServerDeafened ? true : null, // If deafening, also muting
                isServerDeafened,
                adminUserId.Value,
                admin?.Username ?? "Admin"
            ));

        _logger.LogInformation("Admin {AdminId} server-deafened user {TargetId} in channel {ChannelId}: {IsDeafened}",
            adminUserId.Value, targetUserId, channelId, isServerDeafened);
    }

    /// <summary>
    /// Admin action to move a user to a different voice channel.
    /// </summary>
    public async Task MoveUser(Guid targetUserId, Guid targetChannelId)
    {
        var adminUserId = GetUserId();
        if (adminUserId is null) return;

        // Get current participant info for authorization
        var currentParticipant = await _db.VoiceParticipants
            .Include(p => p.Channel)
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.UserId == targetUserId);
        if (currentParticipant?.Channel is null) return;

        var communityId = currentParticipant.Channel.CommunityId;

        // Verify target channel is in same community
        var targetChannel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == targetChannelId && c.CommunityId == communityId);
        if (targetChannel is null)
        {
            throw new HubException("Target channel must be in the same community.");
        }

        // Check admin permissions
        var adminRole = await GetUserRoleInCommunity(adminUserId.Value, communityId);
        if (adminRole != Shared.Models.UserRole.Owner && adminRole != Shared.Models.UserRole.Admin)
        {
            throw new HubException("You don't have permission to move users.");
        }

        var result = await _voiceService.MoveUserAsync(targetUserId, targetChannelId);
        if (result is null) return;

        var (participant, fromChannelId) = result.Value;
        var admin = await _db.Users.FindAsync(adminUserId.Value);
        var targetUser = currentParticipant.User;

        // Update SFU session (remove from old channel)
        _sfuService.RemoveSession(fromChannelId, targetUserId);

        // Broadcast move event to community
        await Clients.Group($"community:{communityId}")
            .SendAsync("UserMoved", new UserMovedEvent(
                targetUserId,
                targetUser?.Username ?? "User",
                fromChannelId,
                targetChannelId,
                adminUserId.Value,
                admin?.Username ?? "Admin"
            ));

        // Also send left/joined events for UI consistency
        await Clients.Group($"community:{communityId}")
            .SendAsync("VoiceParticipantLeft", new VoiceParticipantLeftEvent(fromChannelId, targetUserId));
        await Clients.Group($"community:{communityId}")
            .SendAsync("VoiceParticipantJoined", new VoiceParticipantJoinedEvent(targetChannelId, participant));

        _logger.LogInformation("Admin {AdminId} moved user {TargetId} from {FromChannel} to {ToChannel}",
            adminUserId.Value, targetUserId, fromChannelId, targetChannelId);
    }

    /// <summary>
    /// Gets the user's role in a community.
    /// </summary>
    private async Task<Shared.Models.UserRole?> GetUserRoleInCommunity(Guid userId, Guid communityId)
    {
        var membership = await _db.UserCommunities
            .FirstOrDefaultAsync(uc => uc.UserId == userId && uc.CommunityId == communityId);
        return membership?.Role;
    }

    /// <summary>
    /// Gets the voice channel ID where a user is currently participating.
    /// Returns null if user is not in any voice channel.
    /// </summary>
    private async Task<Guid?> GetUserVoiceChannelAsync(Guid userId)
    {
        var participant = await _db.VoiceParticipants
            .Where(p => p.UserId == userId)
            .Select(p => p.ChannelId)
            .FirstOrDefaultAsync();
        return participant == Guid.Empty ? null : participant;
    }

    /// <summary>
    /// Checks if two users are in the same voice channel.
    /// </summary>
    private async Task<bool> AreUsersInSameVoiceChannelAsync(Guid userId1, Guid userId2)
    {
        var channels = await _db.VoiceParticipants
            .Where(p => p.UserId == userId1 || p.UserId == userId2)
            .Select(p => new { p.UserId, p.ChannelId })
            .ToListAsync();

        var user1Channel = channels.FirstOrDefault(c => c.UserId == userId1)?.ChannelId;
        var user2Channel = channels.FirstOrDefault(c => c.UserId == userId2)?.ChannelId;

        return user1Channel.HasValue && user2Channel.HasValue && user1Channel == user2Channel;
    }

    /// <summary>
    /// Checks if two users share at least one community.
    /// </summary>
    private async Task<bool> DoUsersShareCommunityAsync(Guid userId1, Guid userId2)
    {
        var user1Communities = _db.UserCommunities
            .Where(uc => uc.UserId == userId1)
            .Select(uc => uc.CommunityId);

        return await _db.UserCommunities
            .AnyAsync(uc => uc.UserId == userId2 && user1Communities.Contains(uc.CommunityId));
    }

    // ==================== Screen Share Watching Methods ====================

    /// <summary>
    /// Request to start watching another user's screen share.
    /// Screen shares require explicit opt-in (unlike camera which is auto-forwarded).
    /// </summary>
    public async Task WatchScreenShare(Guid channelId, Guid streamerUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        _sfuService.AddScreenShareViewer(channelId, streamerUserId, userId.Value);
        _logger.LogInformation("User {ViewerId} started watching {StreamerId}'s screen share in channel {ChannelId}",
            userId.Value, streamerUserId, channelId);

        // Send the streamer's screen audio SSRC to the viewer (if they have one)
        // This is needed because the viewer might have missed the initial broadcast
        var streamerSession = _sfuService.GetSession(channelId, streamerUserId);
        if (streamerSession?.ScreenAudioSsrc != null)
        {
            await Clients.Caller.SendAsync("UserScreenAudioSsrcMapped",
                new ScreenAudioSsrcMappingEvent(channelId, streamerUserId, streamerSession.ScreenAudioSsrc.Value));
            _logger.LogDebug("Sent screen audio SSRC {Ssrc} for streamer {StreamerId} to viewer {ViewerId}",
                streamerSession.ScreenAudioSsrc.Value, streamerUserId, userId.Value);
        }
    }

    /// <summary>
    /// Stop watching another user's screen share.
    /// </summary>
    public async Task StopWatchingScreenShare(Guid channelId, Guid streamerUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        _sfuService.RemoveScreenShareViewer(channelId, streamerUserId, userId.Value);
        _logger.LogInformation("User {ViewerId} stopped watching {StreamerId}'s screen share in channel {ChannelId}",
            userId.Value, streamerUserId, channelId);

        await Task.CompletedTask;
    }

    // ==================== Drawing Annotation Methods ====================

    /// <summary>
    /// Send a drawing annotation to all users viewing a screen share.
    /// Broadcasts to all users in the voice channel.
    /// </summary>
    public async Task SendAnnotation(AnnotationMessage message)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify user is in the voice channel they're trying to annotate
        var userChannelId = await GetUserVoiceChannelAsync(userId.Value);
        if (userChannelId != message.ChannelId)
        {
            _logger.LogWarning("User {UserId} attempted to annotate channel {ChannelId} without being in it",
                userId.Value, message.ChannelId);
            return;
        }

        // Broadcast annotation to all users in the voice channel
        await Clients.Group($"voice:{message.ChannelId}")
            .SendAsync("ReceiveAnnotation", message);

        _logger.LogDebug("User {UserId} sent annotation ({Action}) for screen share by {SharerUserId} in channel {ChannelId}",
            userId.Value, message.Action, message.SharerUserId, message.ChannelId);
    }

    /// <summary>
    /// Clear all annotations for a screen share session.
    /// </summary>
    public async Task ClearAnnotations(Guid channelId, Guid sharerUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify user is in the voice channel
        var userChannelId = await GetUserVoiceChannelAsync(userId.Value);
        if (userChannelId != channelId)
        {
            _logger.LogWarning("User {UserId} attempted to clear annotations in channel {ChannelId} without being in it",
                userId.Value, channelId);
            return;
        }

        var clearMessage = new AnnotationMessage
        {
            ChannelId = channelId,
            SharerUserId = sharerUserId,
            Action = "clear"
        };

        await Clients.Group($"voice:{channelId}")
            .SendAsync("ReceiveAnnotation", clearMessage);

        _logger.LogInformation("User {UserId} cleared annotations for screen share by {SharerUserId} in channel {ChannelId}",
            userId.Value, sharerUserId, channelId);
    }

    // ==================== WebRTC Signaling Methods ====================

    public async Task SendWebRtcOffer(Guid targetUserId, string sdp)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify both users are in the same voice channel
        if (!await AreUsersInSameVoiceChannelAsync(userId.Value, targetUserId))
        {
            _logger.LogWarning("User {UserId} attempted WebRTC offer to {TargetUserId} without being in same voice channel",
                userId.Value, targetUserId);
            return;
        }

        string? targetConnectionId;
        lock (Lock)
        {
            UserConnections.TryGetValue(targetUserId, out targetConnectionId);
        }

        if (targetConnectionId is not null)
        {
            await Clients.Client(targetConnectionId)
                .SendAsync("WebRtcOffer", new { FromUserId = userId.Value, Sdp = sdp });
            _logger.LogDebug("Sent WebRTC offer from {FromUser} to {ToUser}", userId.Value, targetUserId);
        }
    }

    public async Task SendWebRtcAnswer(Guid targetUserId, string sdp)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify both users are in the same voice channel
        if (!await AreUsersInSameVoiceChannelAsync(userId.Value, targetUserId))
        {
            _logger.LogWarning("User {UserId} attempted WebRTC answer to {TargetUserId} without being in same voice channel",
                userId.Value, targetUserId);
            return;
        }

        string? targetConnectionId;
        lock (Lock)
        {
            UserConnections.TryGetValue(targetUserId, out targetConnectionId);
        }

        if (targetConnectionId is not null)
        {
            await Clients.Client(targetConnectionId)
                .SendAsync("WebRtcAnswer", new { FromUserId = userId.Value, Sdp = sdp });
            _logger.LogDebug("Sent WebRTC answer from {FromUser} to {ToUser}", userId.Value, targetUserId);
        }
    }

    public async Task SendIceCandidate(Guid targetUserId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify both users are in the same voice channel
        if (!await AreUsersInSameVoiceChannelAsync(userId.Value, targetUserId))
        {
            _logger.LogWarning("User {UserId} attempted to send ICE candidate to {TargetUserId} without being in same voice channel",
                userId.Value, targetUserId);
            return;
        }

        string? targetConnectionId;
        lock (Lock)
        {
            UserConnections.TryGetValue(targetUserId, out targetConnectionId);
        }

        if (targetConnectionId is not null)
        {
            await Clients.Client(targetConnectionId)
                .SendAsync("IceCandidate", new
                {
                    FromUserId = userId.Value,
                    Candidate = candidate,
                    SdpMid = sdpMid,
                    SdpMLineIndex = sdpMLineIndex
                });
            _logger.LogDebug("Sent ICE candidate from {FromUser} to {ToUser}", userId.Value, targetUserId);
        }
    }

    private Guid? GetUserId()
    {
        var userIdClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
