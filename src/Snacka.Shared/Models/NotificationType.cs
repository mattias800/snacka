namespace Snacka.Shared.Models;

/// <summary>
/// Types of notifications. Stored as string in DB for extensibility.
/// Should mirror client-side ActivityType enum.
/// </summary>
public static class NotificationType
{
    /// <summary>
    /// User joined the server. Sent to all server admins.
    /// </summary>
    public const string UserJoinedServer = "UserJoinedServer";

    /// <summary>
    /// User joined a community. Sent to all community members.
    /// </summary>
    public const string UserJoinedCommunity = "UserJoinedCommunity";

    /// <summary>
    /// User left the server or community.
    /// </summary>
    public const string UserLeft = "UserLeft";

    /// <summary>
    /// User was mentioned in a channel message. Sent to mentioned user.
    /// </summary>
    public const string Mention = "Mention";

    /// <summary>
    /// New direct message received. Sent to DM recipient.
    /// </summary>
    public const string DirectMessage = "DirectMessage";

    /// <summary>
    /// New message in a channel with unread messages.
    /// </summary>
    public const string ChannelMessage = "ChannelMessage";

    /// <summary>
    /// User was invited to a community. Sent to invited user.
    /// </summary>
    public const string CommunityInvite = "CommunityInvite";

    /// <summary>
    /// Thread reply received. Sent to thread parent author.
    /// </summary>
    public const string ThreadReply = "ThreadReply";
}
