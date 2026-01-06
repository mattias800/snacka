namespace Miscord.Shared.Models;

/// <summary>
/// Sent when a user starts a video stream (camera or screen share).
/// For screen shares, clients should show a "Watch" button instead of auto-playing.
/// </summary>
public class VideoStreamStarted
{
    public required Guid ChannelId { get; set; }
    public required Guid UserId { get; set; }
    public required string Username { get; set; }
    public required VideoStreamType StreamType { get; set; }
}

/// <summary>
/// Sent when a user stops a video stream.
/// </summary>
public class VideoStreamStopped
{
    public required Guid ChannelId { get; set; }
    public required Guid UserId { get; set; }
    public required VideoStreamType StreamType { get; set; }
}

/// <summary>
/// Request from a client to start watching another user's screen share.
/// Camera streams are auto-forwarded; screen shares require explicit opt-in.
/// </summary>
public class WatchScreenShareRequest
{
    public required Guid ChannelId { get; set; }
    public required Guid StreamerUserId { get; set; }
}

/// <summary>
/// Request from a client to stop watching a user's screen share.
/// </summary>
public class StopWatchingScreenShareRequest
{
    public required Guid ChannelId { get; set; }
    public required Guid StreamerUserId { get; set; }
}
