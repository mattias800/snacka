using SIPSorcery.Net;

namespace Miscord.Server.Services.Sfu;

/// <summary>
/// Top-level SFU service that manages all voice channel sessions.
/// </summary>
public interface ISfuService
{
    /// <summary>
    /// Creates or retrieves a session for a user in a voice channel.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="userId">The user ID</param>
    /// <returns>The SFU session for the user</returns>
    SfuSession GetOrCreateSession(Guid channelId, Guid userId);

    /// <summary>
    /// Gets an existing session for a user in a channel.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="userId">The user ID</param>
    /// <returns>The session if it exists, null otherwise</returns>
    SfuSession? GetSession(Guid channelId, Guid userId);

    /// <summary>
    /// Removes a session when a user leaves a voice channel.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="userId">The user ID</param>
    void RemoveSession(Guid channelId, Guid userId);

    /// <summary>
    /// Gets the channel manager for a voice channel.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <returns>The channel manager if it exists, null otherwise</returns>
    SfuChannelManager? GetChannelManager(Guid channelId);

    /// <summary>
    /// Adds a viewer to a user's screen share.
    /// Screen share RTP will only be forwarded to users who have opted in.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="streamerUserId">The user who is sharing their screen</param>
    /// <param name="viewerUserId">The user who wants to watch the screen share</param>
    void AddScreenShareViewer(Guid channelId, Guid streamerUserId, Guid viewerUserId);

    /// <summary>
    /// Removes a viewer from a user's screen share.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="streamerUserId">The user who is sharing their screen</param>
    /// <param name="viewerUserId">The user who no longer wants to watch</param>
    void RemoveScreenShareViewer(Guid channelId, Guid streamerUserId, Guid viewerUserId);

    /// <summary>
    /// Clears all viewers for a user's screen share (e.g., when they stop sharing).
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="streamerUserId">The user who stopped sharing their screen</param>
    void ClearScreenShareViewers(Guid channelId, Guid streamerUserId);

    /// <summary>
    /// Checks if a user is watching another user's screen share.
    /// </summary>
    /// <param name="channelId">The voice channel ID</param>
    /// <param name="streamerUserId">The user who is sharing their screen</param>
    /// <param name="viewerUserId">The user to check</param>
    /// <returns>True if the viewer is watching the screen share</returns>
    bool IsWatchingScreenShare(Guid channelId, Guid streamerUserId, Guid viewerUserId);

    /// <summary>
    /// Fired when an ICE candidate needs to be sent to a client.
    /// Args: (userId, channelId, candidate)
    /// </summary>
    event Action<Guid, Guid, RTCIceCandidate>? OnIceCandidateForClient;

    /// <summary>
    /// Fired when a session's connection state changes.
    /// Args: (userId, channelId, state)
    /// </summary>
    event Action<Guid, Guid, RTCPeerConnectionState>? OnSessionStateChanged;
}
