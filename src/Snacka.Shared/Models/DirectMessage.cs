namespace Snacka.Shared.Models;

public class DirectMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Content { get; set; }

    /// <summary>
    /// The conversation this message belongs to.
    /// </summary>
    public required Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    /// <summary>
    /// The author of this message.
    /// </summary>
    public required Guid SenderId { get; set; }
    public User? Sender { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
