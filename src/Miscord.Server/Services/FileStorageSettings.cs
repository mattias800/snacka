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
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".txt", ".doc", ".docx", ".xls", ".xlsx", ".zip", ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac"];

    /// <summary>
    /// Extensions that are considered images (for inline preview).
    /// </summary>
    public string[] AllowedImageExtensions { get; set; } =
        [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <summary>
    /// Extensions that are considered audio (for inline player).
    /// </summary>
    public string[] AllowedAudioExtensions { get; set; } =
        [".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac"];

    /// <summary>
    /// Maximum number of files per message.
    /// </summary>
    public int MaxFilesPerMessage { get; set; } = 10;

    /// <summary>
    /// Subdirectory for avatar images within BasePath.
    /// </summary>
    public string AvatarSubPath { get; set; } = "avatars";

    /// <summary>
    /// Size in pixels for avatar images (square, e.g., 256 = 256x256).
    /// </summary>
    public int AvatarSize { get; set; } = 256;

    /// <summary>
    /// Maximum file size for avatar uploads in bytes (default: 5MB).
    /// </summary>
    public long MaxAvatarSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Allowed extensions for avatar uploads.
    /// </summary>
    public string[] AllowedAvatarExtensions { get; set; } =
        [".jpg", ".jpeg", ".png", ".webp"];
}
