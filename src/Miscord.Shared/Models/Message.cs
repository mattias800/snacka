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
    /// Optional reference to the message this is replying to (for reply preview display)
    /// </summary>
    public Guid? ReplyToId { get; set; }
    public Message? ReplyTo { get; set; }

    /// <summary>
    /// If set, this message belongs to a thread. Points to the thread's parent message.
    /// </summary>
    public Guid? ThreadParentMessageId { get; set; }
    public Message? ThreadParentMessage { get; set; }

    /// <summary>
    /// All messages that are part of this message's thread (only populated on thread parent messages)
    /// </summary>
    public ICollection<Message> ThreadReplies { get; set; } = new List<Message>();

    /// <summary>
    /// Cached count of replies in this thread (only used on thread parent messages)
    /// </summary>
    public int ReplyCount { get; set; }

    /// <summary>
    /// Timestamp of the most recent reply in this thread (only used on thread parent messages)
    /// </summary>
    public DateTime? LastReplyAt { get; set; }

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

    /// <summary>
    /// File attachments on this message
    /// </summary>
    public ICollection<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
}
