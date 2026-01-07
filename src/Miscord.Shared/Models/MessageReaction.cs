namespace Miscord.Shared.Models;

/// <summary>
/// Represents a reaction (emoji) on a message.
/// </summary>
public class MessageReaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The emoji character (e.g., "👍", "❤️", "😂")
    /// </summary>
    public required string Emoji { get; set; }

    public required Guid MessageId { get; set; }
    public Message? Message { get; set; }

    public required Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
