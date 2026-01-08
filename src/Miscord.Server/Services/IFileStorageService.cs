namespace Miscord.Server.Services;

/// <summary>
/// Service for storing and retrieving files.
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// Validates a file before upload.
    /// </summary>
    FileValidationResult ValidateFile(string fileName, long fileSize, string contentType);

    /// <summary>
    /// Stores a file and returns the stored file info.
    /// </summary>
    Task<StoredFileResult> StoreFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a file by its stored filename.
    /// </summary>
    Task<FileRetrievalResult?> GetFileAsync(string storedFileName, CancellationToken ct = default);

    /// <summary>
    /// Deletes a file by its stored filename.
    /// </summary>
    Task DeleteFileAsync(string storedFileName, CancellationToken ct = default);

    /// <summary>
    /// Checks if a file is an image based on content type and extension.
    /// </summary>
    bool IsImageFile(string contentType, string fileName);

    /// <summary>
    /// Checks if a file is audio based on content type and extension.
    /// </summary>
    bool IsAudioFile(string contentType, string fileName);
}

/// <summary>
/// Result of file validation.
/// </summary>
public record FileValidationResult(bool IsValid, string? ErrorMessage);

/// <summary>
/// Result of storing a file.
/// </summary>
public record StoredFileResult(string StoredFileName, string SanitizedFileName, bool IsImage, bool IsAudio);

/// <summary>
/// Result of retrieving a file.
/// </summary>
public record FileRetrievalResult(Stream Stream, string ContentType, string FileName);
