namespace Miscord.Shared.Models;

/// <summary>
/// Identifies the type of video stream in a WebRTC connection.
/// Each user can have multiple streams (camera + screen share) simultaneously.
/// </summary>
public enum VideoStreamType
{
    /// <summary>
    /// Video from the user's camera/webcam.
    /// </summary>
    Camera = 0,

    /// <summary>
    /// Video from screen capture/screen sharing.
    /// </summary>
    ScreenShare = 1
}
