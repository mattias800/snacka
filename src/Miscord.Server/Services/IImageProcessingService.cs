namespace Miscord.Server.Services;

/// <summary>
/// Service for processing images (crop, resize, etc.).
/// </summary>
public interface IImageProcessingService
{
    /// <summary>
    /// Processes an avatar image: crops to specified region, resizes to target size, and saves.
    /// </summary>
    /// <param name="sourceStream">The source image stream.</param>
    /// <param name="cropRegion">The region to crop (relative coordinates 0-1). Null for no cropping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the stored filename.</returns>
    Task<AvatarProcessResult> ProcessAvatarAsync(
        Stream sourceStream,
        CropRegion? cropRegion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an avatar file.
    /// </summary>
    /// <param name="fileName">The stored filename to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAvatarAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an avatar file stream.
    /// </summary>
    /// <param name="fileName">The stored filename.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>File stream and content type, or null if not found.</returns>
    Task<AvatarFileResult?> GetAvatarAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates an avatar upload.
    /// </summary>
    /// <param name="fileName">Original filename.</param>
    /// <param name="fileSize">File size in bytes.</param>
    /// <returns>Validation result.</returns>
    AvatarValidationResult ValidateAvatar(string fileName, long fileSize);
}

/// <summary>
/// Represents a rectangular crop region using relative coordinates (0-1).
/// </summary>
public record CropRegion(
    /// <summary>X position of the crop region (0 = left edge, 1 = right edge).</summary>
    double X,
    /// <summary>Y position of the crop region (0 = top edge, 1 = bottom edge).</summary>
    double Y,
    /// <summary>Width of the crop region (0-1, relative to image width).</summary>
    double Width,
    /// <summary>Height of the crop region (0-1, relative to image height).</summary>
    double Height
);

/// <summary>
/// Result of processing an avatar.
/// </summary>
public record AvatarProcessResult(
    /// <summary>The stored filename (GUID-based).</summary>
    string FileName,
    /// <summary>Whether processing was successful.</summary>
    bool Success,
    /// <summary>Error message if processing failed.</summary>
    string? ErrorMessage = null
);

/// <summary>
/// Result of retrieving an avatar file.
/// </summary>
public record AvatarFileResult(
    /// <summary>The file stream.</summary>
    Stream Stream,
    /// <summary>Content type (e.g., "image/png").</summary>
    string ContentType
);

/// <summary>
/// Result of validating an avatar upload.
/// </summary>
public record AvatarValidationResult(
    /// <summary>Whether the avatar is valid.</summary>
    bool IsValid,
    /// <summary>Error message if invalid.</summary>
    string? ErrorMessage = null
);
