namespace Miscord.Shared.Models;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Content { get; set; }
    public required Guid AuthorId { get; set; }
    public User? Author { get; set; }
    public required Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsEdited => UpdatedAt > CreatedAt;

    /// <summary>
    /// Optional reference to the message this is replying to
    /// </summary>
    public Guid? ReplyToId { get; set; }
    public Message? ReplyTo { get; set; }

    /// <summary>
    /// Reactions on this message
    /// </summary>
    public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();

    /// <summary>
    /// Whether this message is pinned to the channel
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// When this message was pinned (null if not pinned)
    /// </summary>
    public DateTime? PinnedAt { get; set; }

    /// <summary>
    /// User who pinned this message
    /// </summary>
    public Guid? PinnedByUserId { get; set; }
    public User? PinnedBy { get; set; }
}
