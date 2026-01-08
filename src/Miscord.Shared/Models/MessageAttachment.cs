namespace Miscord.Shared.Models;

/// <summary>
/// Represents a file attachment associated with a message.
/// </summary>
public class MessageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The message this attachment belongs to.
    /// </summary>
    public required Guid MessageId { get; set; }
    public Message? Message { get; set; }

    /// <summary>
    /// Original filename (sanitized for safety).
    /// </summary>
    public required string FileName { get; set; }

    /// <summary>
    /// Unique filename used for storage on disk (GUID-based).
    /// </summary>
    public required string StoredFileName { get; set; }

    /// <summary>
    /// MIME content type of the file.
    /// </summary>
    public required string ContentType { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long FileSize { get; set; }

    /// <summary>
    /// Whether this attachment is an image (for inline preview).
    /// </summary>
    public bool IsImage { get; set; }

    /// <summary>
    /// When the file was uploaded.
    /// </summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
