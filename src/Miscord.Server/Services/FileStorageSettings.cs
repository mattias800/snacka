namespace Miscord.Server.Services;

/// <summary>
/// Configuration settings for file storage.
/// </summary>
public class FileStorageSettings
{
    public const string SectionName = "FileStorage";

    /// <summary>
    /// Base path for file storage (relative or absolute).
    /// </summary>
    public string BasePath { get; set; } = "./uploads";

    /// <summary>
    /// Maximum file size in bytes (default: 25MB).
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// Allowed file extensions for upload.
    /// </summary>
    public string[] AllowedExtensions { get; set; } =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".txt", ".doc", ".docx", ".xls", ".xlsx", ".zip"];

    /// <summary>
    /// Extensions that are considered images (for inline preview).
    /// </summary>
    public string[] AllowedImageExtensions { get; set; } =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <summary>
    /// Maximum number of files per message.
    /// </summary>
    public int MaxFilesPerMessage { get; set; } = 10;
}
