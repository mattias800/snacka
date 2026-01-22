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
    private readonly INotificationService _notificationService;
    private readonly IConversationService _conversationService;
    private readonly ILogger<SnackaHub> _logger;
    // Multi-device support: one user can have multiple connections
    private static readonly Dictionary<string, Guid> ConnectedUsers = new();  // ConnectionId -> UserId
    private static readonly Dictionary<Guid, HashSet<string>> UserConnections = new();  // UserId -> ConnectionIds
    private static readonly Dictionary<Guid, string> VoiceConnections = new();  // UserId -> Voice ConnectionId (only one device in voice)
    private static readonly object Lock = new();

    // Gaming Station tracking (new simplified architecture)
    // MachineId -> GamingStationInfo
    private static readonly Dictionary<string, GamingStationInfo> GamingStations = new();

    /// <summary>
    /// Information about a registered gaming station.
    /// </summary>
    public record GamingStationInfo(
        Guid OwnerId,
        string OwnerUsername,
        string MachineId,
        string DisplayName,
        string ConnectionId,
        bool IsAvailable,
        Guid? CurrentChannelId = null,
        bool IsScreenSharing = false
    );

    public SnackaHub(
        SnackaDbContext db,
        IVoiceService voiceService,
        ISfuService sfuService,
        IHubContext<SnackaHub> hubContext,
        INotificationService notificationService,
        IConversationService conversationService,
        ILogger<SnackaHub> logger)
    {
        _db = db;
        _voiceService = voiceService;
        _sfuService = sfuService;
        _hubContext = hubContext;
        _notificationService = notificationService;
        _conversationService = conversationService;
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

        bool isFirstConnection;
        lock (Lock)
        {
            ConnectedUsers[Context.ConnectionId] = userId.Value;

            if (!UserConnections.TryGetValue(userId.Value, out var connections))
            {
                connections = new HashSet<string>();
                UserConnections[userId.Value] = connections;
            }
            isFirstConnection = connections.Count == 0;
            connections.Add(Context.ConnectionId);
        }

        try
        {
            var user = await _db.Users.FindAsync(userId.Value);
            if (user is not null)
            {
                // Only update online status and notify if this is the first connection
                if (isFirstConnection)
                {
                    user.IsOnline = true;
                    user.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();

                    // Notify all clients about user coming online
                    await Clients.Others.SendAsync("UserOnline", new UserPresence(user.Id, user.Username, true));
                }

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

                // Add user to their conversation groups
                var conversationIds = await _conversationService.GetUserConversationIdsAsync(userId.Value);
                foreach (var conversationId in conversationIds)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"conv:{conversationId}");
                }
                _logger.LogInformation("User {Username} added to {Count} conversation groups", user.Username, conversationIds.Count);

                // Send pending notifications to the user
                var unreadCount = await _notificationService.GetUnreadCountAsync(userId.Value);
                if (unreadCount > 0)
                {
                    var notifications = await _notificationService.GetNotificationsAsync(
                        userId.Value, skip: 0, take: 50, includeRead: false);
                    await Clients.Caller.SendAsync("PendingNotifications", notifications);
                    await Clients.Caller.SendAsync("UnreadNotificationCount", unreadCount);
                    _logger.LogInformation("Sent {Count} pending notifications to user {Username}", unreadCount, user.Username);
                }

                // Check if user is in a voice channel on another device
                var voiceParticipant = await _db.VoiceParticipants
                    .Include(p => p.Channel)
                    .FirstOrDefaultAsync(p => p.UserId == userId.Value);

                if (voiceParticipant != null)
                {
                    // User is in voice on another device - notify this connection
                    await Clients.Caller.SendAsync("VoiceSessionActiveOnOtherDevice", new
                    {
                        ChannelId = voiceParticipant.ChannelId,
                        ChannelName = voiceParticipant.Channel?.Name
                    });
                    _logger.LogInformation("User {Username} connected but is in voice on another device (channel {ChannelId})",
                        user.Username, voiceParticipant.ChannelId);
                }
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
        bool isLastConnection;
        bool isVoiceConnection;

        lock (Lock)
        {
            if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var id))
            {
                userId = id;
                ConnectedUsers.Remove(Context.ConnectionId);

                // Remove this connection from the user's connection set
                if (UserConnections.TryGetValue(id, out var connections))
                {
                    connections.Remove(Context.ConnectionId);
                    isLastConnection = connections.Count == 0;
                    if (isLastConnection)
                    {
                        UserConnections.Remove(id);
                    }
                }
                else
                {
                    isLastConnection = true;
                }

                // Check if this was the voice connection
                isVoiceConnection = VoiceConnections.TryGetValue(id, out var voiceConnId) &&
                                   voiceConnId == Context.ConnectionId;
                if (isVoiceConnection)
                {
                    VoiceConnections.Remove(id);
                }
            }
            else
            {
                userId = null;
                isLastConnection = false;
                isVoiceConnection = false;
            }
        }

        if (userId is not null)
        {
            // Only leave voice channel if this was the voice connection
            if (isVoiceConnection)
            {
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

                        // Notify other connections of this user that voice session ended
                        await Clients.User(userId.Value.ToString()).SendAsync("VoiceSessionEnded", new
                        {
                            Reason = "DeviceDisconnected"
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get current voice channel during disconnect for user {UserId}", userId.Value);
                    // Continue with disconnect handling even if voice cleanup fails
                }
            }

            // Handle gaming station disconnect cleanup
            try
            {
                var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
                if (stationService is not null)
                {
                    // Check if this connection was a station
                    await stationService.SetStationOfflineAsync(Context.ConnectionId);

                    // Check if this connection was a user connected to a station
                    await stationService.DisconnectUserByConnectionIdAsync(Context.ConnectionId);
                }

                // Clean up in-memory station tracking
                lock (Lock)
                {
                    // Find and remove any station connections
                    var stationsToRemove = StationConnections
                        .Where(kvp => kvp.Value == Context.ConnectionId)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var stationId in stationsToRemove)
                    {
                        StationConnections.Remove(stationId);
                        _logger.LogInformation("Station {StationId} disconnected", stationId);
                    }

                    // Find and remove any station user connections
                    var userConnectionsToRemove = StationUserConnections
                        .Where(kvp => kvp.Value == Context.ConnectionId)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in userConnectionsToRemove)
                    {
                        StationUserConnections.Remove(key);
                        _logger.LogInformation("User {UserId} disconnected from station {StationId}", key.Item2, key.Item1);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle gaming station disconnect cleanup for connection {ConnectionId}", Context.ConnectionId);
            }

            // Only update online status if this was the last connection
            if (isLastConnection)
            {
                try
                {
                    var user = await _db.Users.FindAsync(userId.Value);
                    if (user is not null)
                    {
                        user.IsOnline = false;
                        user.UpdatedAt = DateTime.UtcNow;
                        await _db.SaveChangesAsync();

                        await Clients.Others.SendAsync("UserOffline", new UserPresence(user.Id, user.Username, false));
                        _logger.LogInformation("User {Username} disconnected (last connection)", user.Username);
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
            else
            {
                _logger.LogInformation("User {UserId} disconnected one device, {Count} connections remaining",
                    userId.Value, GetConnectionCountForUser(userId.Value));
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Gets the number of active connections for a user.
    /// </summary>
    private static int GetConnectionCountForUser(Guid userId)
    {
        lock (Lock)
        {
            return UserConnections.TryGetValue(userId, out var connections) ? connections.Count : 0;
        }
    }

    /// <summary>
    /// Gets all connection IDs for a user except the current one.
    /// </summary>
    private IEnumerable<string> GetOtherConnectionsForUser(Guid userId)
    {
        lock (Lock)
        {
            if (UserConnections.TryGetValue(userId, out var connections))
            {
                return connections.Where(c => c != Context.ConnectionId).ToList();
            }
            return [];
        }
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
    /// Sends to all connected devices of the recipient.
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

        // Send to all of the recipient's connected devices
        await Clients.User(recipientUserId.ToString())
            .SendAsync("DMUserTyping", new DMTypingEvent(userId.Value, user.Username, user.EffectiveDisplayName));
    }

    /// <summary>
    /// Notifies other participants in a conversation that the current user is typing.
    /// </summary>
    public async Task SendConversationTyping(Guid conversationId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Verify user is a participant
        if (!await _conversationService.IsParticipantAsync(conversationId, userId.Value))
        {
            _logger.LogWarning("User {UserId} attempted to send typing to conversation {ConversationId} without being a participant",
                userId.Value, conversationId);
            return;
        }

        var user = await _db.Users.FindAsync(userId.Value);
        if (user is null) return;

        // Send to all participants in the conversation group (except sender)
        await Clients.OthersInGroup($"conv:{conversationId}")
            .SendAsync("ConversationUserTyping", new ConversationTypingEvent(conversationId, userId.Value, user.Username, user.EffectiveDisplayName));
    }

    /// <summary>
    /// Joins a conversation group. Called when a user is added to a conversation.
    /// </summary>
    public async Task JoinConversationGroup(Guid conversationId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Verify user is a participant
        if (!await _conversationService.IsParticipantAsync(conversationId, userId.Value))
        {
            _logger.LogWarning("User {UserId} attempted to join conversation group {ConversationId} without being a participant",
                userId.Value, conversationId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"conv:{conversationId}");
        _logger.LogInformation("User {UserId} joined conversation group {ConversationId}", userId.Value, conversationId);
    }

    /// <summary>
    /// Leaves a conversation group. Called when a user is removed from a conversation.
    /// </summary>
    public async Task LeaveConversationGroup(Guid conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conv:{conversationId}");
        var userId = GetUserId();
        _logger.LogInformation("User {UserId} left conversation group {ConversationId}", userId, conversationId);
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

            // Check if user is already in voice (possibly on another device)
            var currentChannel = await _voiceService.GetUserCurrentChannelAsync(userId.Value);
            if (currentChannel.HasValue)
            {
                // Notify other connections of this user that they're being disconnected from voice
                var otherConnections = GetOtherConnectionsForUser(userId.Value);
                foreach (var connId in otherConnections)
                {
                    try
                    {
                        await Clients.Client(connId).SendAsync("DisconnectedFromVoice", new
                        {
                            Reason = "JoinedFromAnotherDevice",
                            ChannelId = currentChannel.Value
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to notify connection {ConnectionId} of voice disconnect", connId);
                    }
                }

                // Clean up old SFU session before leaving
                _sfuService.RemoveSession(currentChannel.Value, userId.Value);

                // Leave the current voice channel
                await LeaveVoiceChannel(currentChannel.Value);
            }

            var participant = await _voiceService.JoinChannelAsync(channelId, userId.Value);

            // Enrich with gaming station info if this participant is a gaming station
            participant = EnrichWithGamingStationInfo(participant);

            // Track this connection as the voice connection for this user
            lock (Lock)
            {
                VoiceConnections[userId.Value] = Context.ConnectionId;
            }

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
                        // Send to the voice connection specifically (not all connections)
                        string? voiceConnectionId;
                        lock (Lock)
                        {
                            VoiceConnections.TryGetValue(userId.Value, out voiceConnectionId);
                        }

                        if (voiceConnectionId != null)
                        {
                            await hubContext.Clients.Client(voiceConnectionId).SendAsync("SfuIceCandidate", new
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

            // Update gaming station status if this connection is a gaming station
            await UpdateGamingStationChannelStatus(userId.Value, channelId);

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

        // Clear voice connection tracking
        lock (Lock)
        {
            VoiceConnections.Remove(userId.Value);
        }

        await _voiceService.LeaveChannelAsync(channelId, userId.Value);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"voice:{channelId}");

        // Notify ALL users in the community (so everyone can see who left voice)
        if (channel is not null)
        {
            await Clients.Group($"community:{channel.CommunityId}")
                .SendAsync("VoiceParticipantLeft", new VoiceParticipantLeftEvent(channelId, userId.Value));
        }

        // Notify all other connections of this user that voice session ended
        foreach (var connId in GetOtherConnectionsForUser(userId.Value))
        {
            try
            {
                await Clients.Client(connId).SendAsync("VoiceSessionEnded", new
                {
                    Reason = "LeftVoiceChannel",
                    ChannelId = channelId
                });
            }
            catch
            {
                // Ignore failures
            }
        }

        // Update gaming station status if this connection is a gaming station
        await UpdateGamingStationChannelStatus(userId.Value, null);

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

        // Send to the target user's voice connection (voice is single-device)
        string? targetVoiceConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(targetUserId, out targetVoiceConnectionId);
        }

        if (targetVoiceConnectionId is not null)
        {
            await Clients.Client(targetVoiceConnectionId)
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

        // Send to the target user's voice connection (voice is single-device)
        string? targetVoiceConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(targetUserId, out targetVoiceConnectionId);
        }

        if (targetVoiceConnectionId is not null)
        {
            await Clients.Client(targetVoiceConnectionId)
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

        // Send to the target user's voice connection (voice is single-device)
        string? targetVoiceConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(targetUserId, out targetVoiceConnectionId);
        }

        if (targetVoiceConnectionId is not null)
        {
            await Clients.Client(targetVoiceConnectionId)
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

    // ==================== Controller Streaming Methods ====================

    // Track active controller sessions: (ChannelId, HostUserId) -> List of (GuestUserId, Slot)
    private static readonly Dictionary<(Guid ChannelId, Guid HostUserId), List<(Guid GuestUserId, byte Slot)>> ControllerSessions = new();

    // Track pending controller access requests: (ChannelId, HostUserId, GuestUserId) -> RequestTime
    private static readonly Dictionary<(Guid, Guid, Guid), DateTime> PendingControllerRequests = new();

    /// <summary>
    /// Guest requests controller access from a host who is sharing their screen.
    /// </summary>
    public async Task RequestControllerAccess(Guid channelId, Guid hostUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify requester is in the same voice channel
        var userChannelId = await GetUserVoiceChannelAsync(userId.Value);
        if (userChannelId != channelId)
        {
            _logger.LogWarning("User {UserId} attempted to request controller access in channel {ChannelId} without being in it",
                userId.Value, channelId);
            return;
        }

        // Get requester info
        var requester = await _db.Users.FindAsync(userId.Value);
        if (requester is null) return;

        // Store pending request
        lock (Lock)
        {
            var key = (channelId, hostUserId, userId.Value);
            PendingControllerRequests[key] = DateTime.UtcNow;
        }

        // Send request to host (voice connection only - controller streaming is part of voice)
        string? hostConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(hostUserId, out hostConnectionId);
        }

        if (hostConnectionId is not null)
        {
            await Clients.Client(hostConnectionId)
                .SendAsync("ControllerAccessRequested", new ControllerAccessRequestedEvent(
                    channelId,
                    userId.Value,
                    requester.Username
                ));

            _logger.LogInformation("User {GuestId} ({GuestName}) requested controller access from {HostId} in channel {ChannelId}",
                userId.Value, requester.Username, hostUserId, channelId);
        }
    }

    /// <summary>
    /// Host accepts a guest's controller access request.
    /// </summary>
    public async Task AcceptControllerAccess(Guid channelId, Guid guestUserId, byte controllerSlot)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify host is in the channel and is the one being asked
        var userChannelId = await GetUserVoiceChannelAsync(userId.Value);
        if (userChannelId != channelId)
        {
            _logger.LogWarning("User {UserId} attempted to accept controller access in channel {ChannelId} without being in it",
                userId.Value, channelId);
            return;
        }

        // Verify there was a pending request
        bool hadPendingRequest;
        lock (Lock)
        {
            var key = (channelId, userId.Value, guestUserId);
            hadPendingRequest = PendingControllerRequests.Remove(key);
        }

        if (!hadPendingRequest)
        {
            _logger.LogWarning("No pending controller request from {GuestId} to {HostId} in channel {ChannelId}",
                guestUserId, userId.Value, channelId);
            return;
        }

        // Get host info
        var host = await _db.Users.FindAsync(userId.Value);
        if (host is null) return;

        // Add to active sessions
        lock (Lock)
        {
            var key = (channelId, userId.Value);
            if (!ControllerSessions.TryGetValue(key, out var sessions))
            {
                sessions = new List<(Guid, byte)>();
                ControllerSessions[key] = sessions;
            }

            // Remove any existing session for this guest
            sessions.RemoveAll(s => s.GuestUserId == guestUserId);
            sessions.Add((guestUserId, controllerSlot));
        }

        // Notify guest (voice connection only - controller streaming is part of voice)
        string? guestConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(guestUserId, out guestConnectionId);
        }

        if (guestConnectionId is not null)
        {
            await Clients.Client(guestConnectionId)
                .SendAsync("ControllerAccessAccepted", new ControllerAccessAcceptedEvent(
                    channelId,
                    userId.Value,
                    host.Username,
                    controllerSlot
                ));
        }

        _logger.LogInformation("Host {HostId} accepted controller access from {GuestId} as player {Slot} in channel {ChannelId}",
            userId.Value, guestUserId, controllerSlot + 1, channelId);
    }

    /// <summary>
    /// Host declines a guest's controller access request.
    /// </summary>
    public async Task DeclineControllerAccess(Guid channelId, Guid guestUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Remove pending request
        lock (Lock)
        {
            var key = (channelId, userId.Value, guestUserId);
            PendingControllerRequests.Remove(key);
        }

        // Notify guest (voice connection only - controller streaming is part of voice)
        string? guestConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(guestUserId, out guestConnectionId);
        }

        if (guestConnectionId is not null)
        {
            await Clients.Client(guestConnectionId)
                .SendAsync("ControllerAccessDeclined", new ControllerAccessDeclinedEvent(
                    channelId,
                    userId.Value
                ));
        }

        _logger.LogInformation("Host {HostId} declined controller access from {GuestId} in channel {ChannelId}",
            userId.Value, guestUserId, channelId);
    }

    /// <summary>
    /// Either party stops the controller sharing session.
    /// </summary>
    public async Task StopControllerAccess(Guid channelId, Guid otherUserId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        Guid hostUserId, guestUserId;
        string reason;
        bool wasRemoved = false;

        // Determine if caller is host or guest
        lock (Lock)
        {
            // Check if caller is host
            var hostKey = (channelId, userId.Value);
            if (ControllerSessions.TryGetValue(hostKey, out var sessions))
            {
                var removed = sessions.RemoveAll(s => s.GuestUserId == otherUserId);
                if (removed > 0)
                {
                    wasRemoved = true;
                    hostUserId = userId.Value;
                    guestUserId = otherUserId;
                    reason = "host_stopped";

                    // Clean up empty session list
                    if (sessions.Count == 0)
                    {
                        ControllerSessions.Remove(hostKey);
                    }
                }
                else
                {
                    hostUserId = otherUserId;
                    guestUserId = userId.Value;
                    reason = "guest_stopped";
                }
            }
            else
            {
                // Caller is guest, otherUserId is host
                hostUserId = otherUserId;
                guestUserId = userId.Value;
                reason = "guest_stopped";

                var guestHostKey = (channelId, otherUserId);
                if (ControllerSessions.TryGetValue(guestHostKey, out var hostSessions))
                {
                    var removed = hostSessions.RemoveAll(s => s.GuestUserId == userId.Value);
                    wasRemoved = removed > 0;

                    if (hostSessions.Count == 0)
                    {
                        ControllerSessions.Remove(guestHostKey);
                    }
                }
            }
        }

        if (!wasRemoved)
        {
            _logger.LogWarning("No active controller session to stop between {UserId} and {OtherId} in channel {ChannelId}",
                userId.Value, otherUserId, channelId);
            return;
        }

        // Notify both parties (voice connections only - controller streaming is part of voice)
        var stoppedEvent = new ControllerAccessStoppedEvent(channelId, hostUserId, guestUserId, reason);

        string? hostConnectionId, guestConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(hostUserId, out hostConnectionId);
            VoiceConnections.TryGetValue(guestUserId, out guestConnectionId);
        }

        if (hostConnectionId is not null)
        {
            await Clients.Client(hostConnectionId).SendAsync("ControllerAccessStopped", stoppedEvent);
        }
        if (guestConnectionId is not null)
        {
            await Clients.Client(guestConnectionId).SendAsync("ControllerAccessStopped", stoppedEvent);
        }

        _logger.LogInformation("Controller access stopped between host {HostId} and guest {GuestId} in channel {ChannelId} (reason: {Reason})",
            hostUserId, guestUserId, channelId, reason);
    }

    /// <summary>
    /// Guest sends controller state to host.
    /// High-frequency method - minimal logging.
    /// </summary>
    public async Task SendControllerState(ControllerStateMessage state)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify sender has active session with this host
        bool hasAccess = false;
        lock (Lock)
        {
            var key = (state.ChannelId, state.HostUserId);
            if (ControllerSessions.TryGetValue(key, out var sessions))
            {
                hasAccess = sessions.Any(s => s.GuestUserId == userId.Value);
            }
        }

        if (!hasAccess)
        {
            // Don't log every rejected state - could be spam
            return;
        }

        // Get sender info for the event
        var sender = await _db.Users.FindAsync(userId.Value);

        // Forward to host (voice connection only - controller streaming is part of voice)
        string? hostConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(state.HostUserId, out hostConnectionId);
        }

        if (hostConnectionId is not null)
        {
            await Clients.Client(hostConnectionId)
                .SendAsync("ControllerStateReceived", new ControllerStateReceivedEvent(
                    state.ChannelId,
                    userId.Value,
                    sender?.Username ?? "Unknown",
                    state
                ));
        }
    }

    /// <summary>
    /// Host sends rumble/vibration feedback to guest.
    /// High-frequency method - minimal logging.
    /// </summary>
    public async Task SendControllerRumble(ControllerRumbleMessage rumble)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // SECURITY: Verify sender is the host with an active session to this guest
        bool hasAccess = false;
        lock (Lock)
        {
            var key = (rumble.ChannelId, userId.Value);
            if (ControllerSessions.TryGetValue(key, out var sessions))
            {
                hasAccess = sessions.Any(s => s.GuestUserId == rumble.GuestUserId && s.Slot == rumble.ControllerSlot);
            }
        }

        if (!hasAccess)
        {
            return;
        }

        // Forward to guest (voice connection only - controller streaming is part of voice)
        string? guestConnectionId;
        lock (Lock)
        {
            VoiceConnections.TryGetValue(rumble.GuestUserId, out guestConnectionId);
        }

        if (guestConnectionId is not null)
        {
            await Clients.Client(guestConnectionId)
                .SendAsync("ControllerRumbleReceived", new ControllerRumbleReceivedEvent(
                    rumble.ChannelId,
                    userId.Value,
                    rumble
                ));
        }
    }

    /// <summary>
    /// Get active controller sessions for a host in a channel.
    /// </summary>
    public Task<List<(Guid GuestUserId, byte Slot)>> GetControllerSessions(Guid channelId)
    {
        var userId = GetUserId();
        if (userId is null) return Task.FromResult(new List<(Guid, byte)>());

        lock (Lock)
        {
            var key = (channelId, userId.Value);
            if (ControllerSessions.TryGetValue(key, out var sessions))
            {
                return Task.FromResult(sessions.ToList());
            }
        }

        return Task.FromResult(new List<(Guid, byte)>());
    }

    // ==================== Gaming Station Methods (New Architecture) ====================

    /// <summary>
    /// Called by a client to set its gaming station availability.
    /// When available=true, this device becomes a gaming station that can be remotely controlled.
    /// </summary>
    public async Task SetGamingStationAvailable(bool available, string? displayName, string machineId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var user = await _db.Users.FindAsync(userId.Value);
        if (user is null) return;

        var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName;

        lock (Lock)
        {
            if (available)
            {
                GamingStations[machineId] = new GamingStationInfo(
                    OwnerId: userId.Value,
                    OwnerUsername: user.Username,
                    MachineId: machineId,
                    DisplayName: effectiveDisplayName,
                    ConnectionId: Context.ConnectionId,
                    IsAvailable: true
                );
                _logger.LogInformation("Gaming station registered: {MachineId} ({DisplayName}) for user {Username}",
                    machineId, effectiveDisplayName, user.Username);
            }
            else
            {
                GamingStations.Remove(machineId);
                _logger.LogInformation("Gaming station unregistered: {MachineId} for user {Username}",
                    machineId, user.Username);
            }
        }

        // Notify all of this user's devices about the station status change
        await Clients.User(userId.Value.ToString())
            .SendAsync("GamingStationStatusChanged", new GamingStationStatusChangedEvent(
                userId.Value,
                user.Username,
                machineId,
                effectiveDisplayName,
                available,
                IsInVoiceChannel: false,
                CurrentChannelId: null,
                IsScreenSharing: false
            ));
    }

    /// <summary>
    /// Enriches a VoiceParticipantResponse with gaming station info if the participant is a gaming station.
    /// </summary>
    private VoiceParticipantResponse EnrichWithGamingStationInfo(VoiceParticipantResponse participant)
    {
        lock (Lock)
        {
            // Check if this participant is a gaming station
            var stationEntry = GamingStations.FirstOrDefault(kvp =>
                kvp.Value.OwnerId == participant.UserId &&
                kvp.Value.ConnectionId == Context.ConnectionId);

            if (stationEntry.Key is not null)
            {
                return participant with
                {
                    IsGamingStation = true,
                    GamingStationMachineId = stationEntry.Key
                };
            }
        }
        return participant;
    }

    /// <summary>
    /// Updates a gaming station's channel status when it joins or leaves a voice channel.
    /// Called automatically when any user joins/leaves a voice channel.
    /// </summary>
    private async Task UpdateGamingStationChannelStatus(Guid userId, Guid? channelId)
    {
        // Find the gaming station for this connection
        GamingStationInfo? station;
        string? machineId = null;

        lock (Lock)
        {
            // Find the gaming station that has this connection ID
            var connectionId = Context.ConnectionId;
            var entry = GamingStations.FirstOrDefault(kvp => kvp.Value.ConnectionId == connectionId);
            if (entry.Key is null) return; // Not a gaming station

            machineId = entry.Key;
            station = entry.Value;
        }

        if (station is null || station.OwnerId != userId) return;

        // Update the station's channel status
        var updatedStation = station with
        {
            CurrentChannelId = channelId,
            IsScreenSharing = channelId is null ? false : station.IsScreenSharing // Clear screen share when leaving
        };

        lock (Lock)
        {
            GamingStations[machineId] = updatedStation;
        }

        // Notify the owner's devices about the status change
        await Clients.User(userId.ToString())
            .SendAsync("GamingStationStatusChanged", new GamingStationStatusChangedEvent(
                station.OwnerId,
                station.OwnerUsername,
                machineId,
                station.DisplayName,
                station.IsAvailable,
                IsInVoiceChannel: channelId.HasValue,
                CurrentChannelId: channelId,
                IsScreenSharing: updatedStation.IsScreenSharing
            ));

        _logger.LogInformation("Gaming station {MachineId} channel status updated: ChannelId={ChannelId}",
            machineId, channelId);
    }

    /// <summary>
    /// Command a gaming station to join a voice channel.
    /// Only the owner can send commands to their own gaming stations.
    /// </summary>
    public async Task CommandStationJoinChannel(string targetMachineId, Guid channelId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        GamingStationInfo? station;
        lock (Lock)
        {
            GamingStations.TryGetValue(targetMachineId, out station);
        }

        if (station is null || station.OwnerId != userId.Value)
        {
            _logger.LogWarning("User {UserId} attempted to command station {MachineId} they don't own",
                userId.Value, targetMachineId);
            return;
        }

        var channel = await _db.Channels.FindAsync(channelId);
        if (channel is null) return;

        // Send command to the gaming station
        await Clients.Client(station.ConnectionId)
            .SendAsync("StationCommandJoinChannel", new StationCommandJoinChannelEvent(channelId, channel.Name));

        _logger.LogInformation("User {UserId} commanded station {MachineId} to join channel {ChannelId}",
            userId.Value, targetMachineId, channelId);
    }

    /// <summary>
    /// Command a gaming station to leave its current voice channel.
    /// </summary>
    public async Task CommandStationLeaveChannel(string targetMachineId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        GamingStationInfo? station;
        lock (Lock)
        {
            GamingStations.TryGetValue(targetMachineId, out station);
        }

        if (station is null || station.OwnerId != userId.Value) return;

        await Clients.Client(station.ConnectionId)
            .SendAsync("StationCommandLeaveChannel", new StationCommandLeaveChannelEvent());

        _logger.LogInformation("User {UserId} commanded station {MachineId} to leave channel",
            userId.Value, targetMachineId);
    }

    /// <summary>
    /// Command a gaming station to start screen sharing.
    /// </summary>
    public async Task CommandStationStartScreenShare(string targetMachineId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        GamingStationInfo? station;
        lock (Lock)
        {
            GamingStations.TryGetValue(targetMachineId, out station);
        }

        if (station is null || station.OwnerId != userId.Value) return;

        await Clients.Client(station.ConnectionId)
            .SendAsync("StationCommandStartScreenShare", new StationCommandStartScreenShareEvent());

        _logger.LogInformation("User {UserId} commanded station {MachineId} to start screen share",
            userId.Value, targetMachineId);
    }

    /// <summary>
    /// Command a gaming station to stop screen sharing.
    /// </summary>
    public async Task CommandStationStopScreenShare(string targetMachineId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        GamingStationInfo? station;
        lock (Lock)
        {
            GamingStations.TryGetValue(targetMachineId, out station);
        }

        if (station is null || station.OwnerId != userId.Value) return;

        await Clients.Client(station.ConnectionId)
            .SendAsync("StationCommandStopScreenShare", new StationCommandStopScreenShareEvent());

        _logger.LogInformation("User {UserId} commanded station {MachineId} to stop screen share",
            userId.Value, targetMachineId);
    }

    /// <summary>
    /// Command a gaming station to disable gaming station mode.
    /// </summary>
    public async Task CommandStationDisable(string targetMachineId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        GamingStationInfo? station;
        lock (Lock)
        {
            GamingStations.TryGetValue(targetMachineId, out station);
        }

        if (station is null || station.OwnerId != userId.Value) return;

        await Clients.Client(station.ConnectionId)
            .SendAsync("StationCommandDisable", new StationCommandDisableEvent());

        // Remove from tracking
        lock (Lock)
        {
            GamingStations.Remove(targetMachineId);
        }

        _logger.LogInformation("User {UserId} commanded station {MachineId} to disable gaming station mode",
            userId.Value, targetMachineId);
    }

    /// <summary>
    /// Send keyboard input to the gaming station in the specified voice channel.
    /// Only the owner can send input to their own stations.
    /// </summary>
    public async Task SendStationKeyboardInput(Guid channelId, StationKeyboardInput input)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Find the gaming station in this channel that belongs to this user
        GamingStationInfo? station;
        lock (Lock)
        {
            station = GamingStations.Values
                .FirstOrDefault(s => s.OwnerId == userId.Value && s.CurrentChannelId == channelId);
        }

        if (station is null) return;

        // Forward input to the gaming station
        await Clients.Client(station.ConnectionId)
            .SendAsync("StationKeyboardInput", new StationKeyboardInputEvent(userId.Value, input));
    }

    /// <summary>
    /// Send mouse input to the gaming station in the specified voice channel.
    /// </summary>
    public async Task SendStationMouseInput(Guid channelId, StationMouseInput input)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Find the gaming station in this channel that belongs to this user
        GamingStationInfo? station;
        lock (Lock)
        {
            station = GamingStations.Values
                .FirstOrDefault(s => s.OwnerId == userId.Value && s.CurrentChannelId == channelId);
        }

        if (station is null) return;

        // Forward input to the gaming station
        await Clients.Client(station.ConnectionId)
            .SendAsync("StationMouseInput", new StationMouseInputEvent(userId.Value, input));
    }

    // ==================== Gaming Station Event DTOs (New Architecture) ====================

    public record GamingStationStatusChangedEvent(
        Guid UserId,
        string Username,
        string MachineId,
        string DisplayName,
        bool IsAvailable,
        bool IsInVoiceChannel,
        Guid? CurrentChannelId,
        bool IsScreenSharing
    );

    public record StationCommandJoinChannelEvent(Guid ChannelId, string ChannelName);
    public record StationCommandLeaveChannelEvent();
    public record StationCommandStartScreenShareEvent();
    public record StationCommandStopScreenShareEvent();
    public record StationCommandDisableEvent();
    public record StationKeyboardInputEvent(Guid FromUserId, StationKeyboardInput Input);
    public record StationMouseInputEvent(Guid FromUserId, StationMouseInput Input);

    // ==================== Gaming Station Methods (Old Architecture - Deprecated) ====================

    // Track station connections: StationId -> ConnectionId
    private static readonly Dictionary<Guid, string> StationConnections = new();

    // Track users connected to stations: (StationId, UserId) -> ConnectionId
    private static readonly Dictionary<(Guid, Guid), string> StationUserConnections = new();

    /// <summary>
    /// Called by a gaming station when it comes online.
    /// DEPRECATED: Use SetGamingStationAvailable instead.
    /// </summary>
    public async Task StationOnline(Guid stationId, string machineId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
        if (stationService is null) return;

        var station = await stationService.SetStationOnlineAsync(stationId, userId.Value, machineId, Context.ConnectionId);
        if (station is null)
        {
            _logger.LogWarning("Station {StationId} failed to come online for user {UserId}", stationId, userId.Value);
            return;
        }

        lock (Lock)
        {
            StationConnections[stationId] = Context.ConnectionId;
        }

        // Add to station group
        await Groups.AddToGroupAsync(Context.ConnectionId, $"station:{stationId}");

        // Notify users with access to this station
        await Clients.User(userId.Value.ToString())
            .SendAsync("StationOnline", new StationOnlineEvent(stationId, station.Name, station.OwnerId));

        _logger.LogInformation("Station {StationId} ({StationName}) came online", stationId, station.Name);
    }

    /// <summary>
    /// Called by a gaming station when it goes offline gracefully.
    /// </summary>
    public async Task StationOffline(Guid stationId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
        if (stationService is null) return;

        await stationService.SetStationOfflineAsync(Context.ConnectionId);

        lock (Lock)
        {
            StationConnections.Remove(stationId);
        }

        // Remove from station group
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"station:{stationId}");

        // Notify users with access
        await Clients.User(userId.Value.ToString())
            .SendAsync("StationOffline", new StationOfflineEvent(stationId));

        _logger.LogInformation("Station {StationId} went offline", stationId);
    }

    /// <summary>
    /// Called by a user to connect to a gaming station.
    /// </summary>
    public async Task<StationSessionUserResponse?> ConnectToStation(Guid stationId, StationInputMode inputMode)
    {
        var userId = GetUserId();
        if (userId is null) return null;

        var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
        if (stationService is null) return null;

        try
        {
            var sessionUser = await stationService.ConnectUserAsync(
                stationId, userId.Value, Context.ConnectionId, inputMode);

            lock (Lock)
            {
                StationUserConnections[(stationId, userId.Value)] = Context.ConnectionId;
            }

            // Add to station group
            await Groups.AddToGroupAsync(Context.ConnectionId, $"station:{stationId}");

            // Get user info
            var user = await _db.Users.FindAsync(userId.Value);

            // Notify the station that a user is connecting
            string? stationConnectionId;
            lock (Lock)
            {
                StationConnections.TryGetValue(stationId, out stationConnectionId);
            }

            if (stationConnectionId is not null)
            {
                await Clients.Client(stationConnectionId)
                    .SendAsync("UserConnecting", new UserConnectingToStationEvent(
                        userId.Value,
                        user?.Username ?? "Unknown",
                        user?.EffectiveDisplayName ?? "Unknown",
                        inputMode
                    ));
            }

            // Notify others in the station
            await Clients.OthersInGroup($"station:{stationId}")
                .SendAsync("UserConnectedToStation", new UserConnectedToStationEvent(
                    stationId,
                    userId.Value,
                    user?.Username ?? "Unknown",
                    user?.EffectiveDisplayName ?? "Unknown",
                    sessionUser.PlayerSlot,
                    inputMode
                ));

            _logger.LogInformation("User {UserId} connected to station {StationId}", userId.Value, stationId);
            return sessionUser;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect user {UserId} to station {StationId}", userId.Value, stationId);
            return null;
        }
    }

    /// <summary>
    /// Called by a user to disconnect from a gaming station.
    /// </summary>
    public async Task DisconnectFromStation(Guid stationId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
        if (stationService is null) return;

        await stationService.DisconnectUserAsync(stationId, userId.Value);

        lock (Lock)
        {
            StationUserConnections.Remove((stationId, userId.Value));
        }

        // Remove from station group
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"station:{stationId}");

        // Notify others in the station
        await Clients.OthersInGroup($"station:{stationId}")
            .SendAsync("UserDisconnectedFromStation", new UserDisconnectedFromStationEvent(stationId, userId.Value));

        _logger.LogInformation("User {UserId} disconnected from station {StationId}", userId.Value, stationId);
    }

    /// <summary>
    /// Station sends WebRTC offer to a connecting user.
    /// </summary>
    public async Task SendStationOffer(Guid userId, string sdp)
    {
        var stationUserId = GetUserId();
        if (stationUserId is null) return;

        // Get the station for this connection
        var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
        if (stationService is null) return;

        var station = await stationService.GetStationByConnectionIdAsync(Context.ConnectionId);
        if (station is null) return;

        // Send offer to user's station connection
        string? userConnectionId;
        lock (Lock)
        {
            StationUserConnections.TryGetValue((station.Id, userId), out userConnectionId);
        }

        if (userConnectionId is not null)
        {
            await Clients.Client(userConnectionId)
                .SendAsync("StationOffer", new StationWebRtcOffer(station.Id, userId, sdp));
            _logger.LogDebug("Sent station WebRTC offer from station {StationId} to user {UserId}", station.Id, userId);
        }
    }

    /// <summary>
    /// User sends WebRTC answer to station.
    /// </summary>
    public async Task SendStationAnswer(Guid stationId, string sdp)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Send answer to station
        string? stationConnectionId;
        lock (Lock)
        {
            StationConnections.TryGetValue(stationId, out stationConnectionId);
        }

        if (stationConnectionId is not null)
        {
            await Clients.Client(stationConnectionId)
                .SendAsync("StationAnswer", new StationWebRtcAnswer(stationId, sdp));
            _logger.LogDebug("Sent WebRTC answer from user {UserId} to station {StationId}", userId.Value, stationId);
        }
    }

    /// <summary>
    /// Exchange ICE candidates for station streaming.
    /// </summary>
    public async Task SendStationIceCandidate(Guid stationId, Guid? targetUserId, string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Determine if sender is station or user
        string? targetConnectionId;
        lock (Lock)
        {
            if (targetUserId.HasValue)
            {
                // Sender is station, target is user
                StationUserConnections.TryGetValue((stationId, targetUserId.Value), out targetConnectionId);
            }
            else
            {
                // Sender is user, target is station
                StationConnections.TryGetValue(stationId, out targetConnectionId);
            }
        }

        if (targetConnectionId is not null)
        {
            await Clients.Client(targetConnectionId)
                .SendAsync("StationIceCandidate", new StationIceCandidate(
                    stationId, targetUserId ?? userId.Value, candidate, sdpMid, sdpMLineIndex));
        }
    }

    // NOTE: SendStationKeyboardInput and SendStationMouseInput with StationId parameter
    // have been replaced by the new voice-channel-based methods that take channelId.

    /// <summary>
    /// User sends controller input to station.
    /// </summary>
    public async Task SendStationControllerInput(StationControllerInput input)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Verify user is connected to station with appropriate input mode
        var stationService = Context.GetHttpContext()?.RequestServices.GetService<IGamingStationService>();
        if (stationService is null) return;

        var permission = await stationService.GetUserPermissionAsync(input.StationId, userId.Value);
        if (permission is null || permission < StationPermission.Controller)
        {
            return; // No permission for controller input
        }

        // Update last input time (throttle for high-frequency controller events)
        _ = stationService.UpdateLastInputAsync(input.StationId, userId.Value);

        // Forward to station
        string? stationConnectionId;
        lock (Lock)
        {
            StationConnections.TryGetValue(input.StationId, out stationConnectionId);
        }

        if (stationConnectionId is not null)
        {
            await Clients.Client(stationConnectionId)
                .SendAsync("ControllerInput", input);
        }
    }

    private Guid? GetUserId()
    {
        var userIdClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
