namespace Snacka.Shared.Models;

/// <summary>
/// Tracks the last read message for a user in a conversation.
/// Used for showing unread indicators in the conversation list.
/// </summary>
public class ConversationReadState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The conversation that was read.
    /// </summary>
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    /// <summary>
    /// The user whose read state this is.
    /// </summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// The ID of the last message that was read.
    /// </summary>
    public Guid? LastReadMessageId { get; set; }
    public DirectMessage? LastReadMessage { get; set; }

    /// <summary>
    /// Timestamp of when the conversation was last read.
    /// </summary>
    public DateTime? LastReadAt { get; set; }
}
