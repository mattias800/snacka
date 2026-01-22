namespace Snacka.Shared.Models;

/// <summary>
/// Represents a conversation, which can be either a 1:1 direct message or a group conversation.
/// </summary>
public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Optional name for group conversations. Null for 1:1 conversations.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Stored filename (GUID-based) for the group's icon image.
    /// Only used for group conversations.
    /// </summary>
    public string? IconFileName { get; set; }

    /// <summary>
    /// Whether this is a group conversation (3+ participants) or a 1:1 conversation.
    /// </summary>
    public bool IsGroup { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The user who created this conversation. Null if the creator was deleted.
    /// </summary>
    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    // Navigation properties
    public ICollection<ConversationParticipant> Participants { get; set; } = new List<ConversationParticipant>();
    public ICollection<DirectMessage> Messages { get; set; } = new List<DirectMessage>();
    public ICollection<ConversationReadState> ReadStates { get; set; } = new List<ConversationReadState>();
}
