using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Miscord.Server.Data;
using Miscord.Server.DTOs;
using Miscord.Server.Services;
using Miscord.Server.Services.Sfu;
using Miscord.Shared.Models;

namespace Miscord.Server.Hubs;

public record UserPresence(Guid UserId, string Username, bool IsOnline);

[Authorize]
public class MiscordHub : Hub
{
    private readonly MiscordDbContext _db;
    private readonly IVoiceService _voiceService;
    private readonly ISfuService _sfuService;
    private readonly IHubContext<MiscordHub> _hubContext;
    private readonly ILogger<MiscordHub> _logger;
    private static readonly Dictionary<string, Guid> ConnectedUsers = new();
    private static readonly Dictionary<Guid, string> UserConnections = new(); // UserId -> ConnectionId
    private static readonly object Lock = new();

    public MiscordHub(MiscordDbContext db, IVoiceService voiceService, ISfuService sfuService, IHubContext<MiscordHub> hubContext, ILogger<MiscordHub> logger)
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
            }

            _logger.LogInformation("User {Username} connected", user.Username);
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
            // Leave any voice channels on disconnect
            var currentChannel = await _voiceService.GetUserCurrentChannelAsync(userId.Value);
            if (currentChannel.HasValue)
            {
                // Get the channel to find its community
                var channel = await _db.Channels
                    .FirstOrDefaultAsync(c => c.Id == currentChannel.Value);

                // Remove SFU session
                _sfuService.RemoveSession(currentChannel.Value, userId.Value);

                await _voiceService.LeaveChannelAsync(currentChannel.Value, userId.Value);

                // Notify ALL users in the community
                if (channel is not null)
                {
                    await Clients.Group($"community:{channel.CommunityId}")
                        .SendAsync("VoiceParticipantLeft", new VoiceParticipantLeftEvent(currentChannel.Value, userId.Value));
                }
            }

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

        var onlineUsers = await _db.Users
            .Where(u => u.IsOnline)
            .Select(u => new UserPresence(u.Id, u.Username, true))
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
        var wasCameraOn = currentParticipant?.IsCameraOn ?? false;
        var wasScreenSharing = currentParticipant?.IsScreenSharing ?? false;

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

        await Task.CompletedTask;
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

        // Get the channel to find its community
        var channel = await _db.Channels
            .FirstOrDefaultAsync(c => c.Id == message.ChannelId);

        if (channel is null) return;

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
