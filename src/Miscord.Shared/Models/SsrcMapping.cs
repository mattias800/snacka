namespace Miscord.Shared.Models;

/// <summary>
/// Event sent when a user's microphone audio SSRC is discovered by the server.
/// </summary>
public record SsrcMappingEvent(Guid ChannelId, Guid UserId, uint AudioSsrc);

/// <summary>
/// Event sent when a user's screen share audio SSRC is discovered by the server.
/// Screen audio is separate from microphone audio and should only be played when watching the screen share.
/// </summary>
public record ScreenAudioSsrcMappingEvent(Guid ChannelId, Guid UserId, uint ScreenAudioSsrc);

/// <summary>
/// Batch event containing all current SSRC mappings for a channel.
/// Sent to new users when they join a voice channel.
/// </summary>
public record SsrcMappingBatchEvent(Guid ChannelId, List<UserSsrcMapping> Mappings);

/// <summary>
/// Individual user's SSRC mapping information.
/// </summary>
public record UserSsrcMapping(Guid UserId, string Username, uint? AudioSsrc, uint? ScreenAudioSsrc = null);
