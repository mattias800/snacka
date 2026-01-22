namespace Snacka.Shared.Models;

/// <summary>
/// Represents a user's participation in a conversation.
/// </summary>
public class ConversationParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// When this user joined the conversation.
    /// </summary>
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The user who added this participant. Null if they are an original creator.
    /// </summary>
    public Guid? AddedById { get; set; }
    public User? AddedBy { get; set; }
}
