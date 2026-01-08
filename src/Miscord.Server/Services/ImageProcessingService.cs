using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace Miscord.Server.Services;

/// <summary>
/// Image processing service using ImageSharp.
/// </summary>
public class ImageProcessingService : IImageProcessingService
{
    private readonly FileStorageSettings _settings;
    private readonly ILogger<ImageProcessingService> _logger;
    private readonly string _avatarPath;

    public ImageProcessingService(IOptions<FileStorageSettings> settings, ILogger<ImageProcessingService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _avatarPath = Path.Combine(Path.GetFullPath(_settings.BasePath), _settings.AvatarSubPath);

        // Ensure avatar directory exists
        Directory.CreateDirectory(_avatarPath);
        _logger.LogInformation("Avatar storage initialized at: {Path}", _avatarPath);
    }

    public AvatarValidationResult ValidateAvatar(string fileName, long fileSize)
    {
        if (fileSize <= 0)
            return new AvatarValidationResult(false, "File is empty");

        if (fileSize > _settings.MaxAvatarSizeBytes)
        {
            var maxMb = _settings.MaxAvatarSizeBytes / (1024 * 1024);
            return new AvatarValidationResult(false, $"Avatar size exceeds limit of {maxMb}MB");
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            return new AvatarValidationResult(false, "File must have an extension");

        if (!_settings.AllowedAvatarExtensions.Contains(ext))
            return new AvatarValidationResult(false, $"File type '{ext}' is not allowed for avatars");

        return new AvatarValidationResult(true);
    }

    public async Task<AvatarProcessResult> ProcessAvatarAsync(
        Stream sourceStream,
        CropRegion? cropRegion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var image = await Image.LoadAsync(sourceStream, cancellationToken);

            // Apply crop if specified
            if (cropRegion != null)
            {
                var sourceWidth = image.Width;
                var sourceHeight = image.Height;

                // Convert relative coordinates to absolute pixels
                var cropX = (int)(cropRegion.X * sourceWidth);
                var cropY = (int)(cropRegion.Y * sourceHeight);
                var cropWidth = (int)(cropRegion.Width * sourceWidth);
                var cropHeight = (int)(cropRegion.Height * sourceHeight);

                // Ensure crop region is valid
                cropX = Math.Max(0, Math.Min(cropX, sourceWidth - 1));
                cropY = Math.Max(0, Math.Min(cropY, sourceHeight - 1));
                cropWidth = Math.Max(1, Math.Min(cropWidth, sourceWidth - cropX));
                cropHeight = Math.Max(1, Math.Min(cropHeight, sourceHeight - cropY));

                _logger.LogDebug(
                    "Cropping avatar: source {SrcW}x{SrcH}, crop region ({X},{Y}) {W}x{H}",
                    sourceWidth, sourceHeight, cropX, cropY, cropWidth, cropHeight);

                image.Mutate(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropWidth, cropHeight)));
            }

            // Resize to target avatar size (square)
            var targetSize = _settings.AvatarSize;
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(targetSize, targetSize),
                Mode = ResizeMode.Crop // Crop to fill the square, maintaining aspect ratio
            }));

            // Generate unique filename
            var storedFileName = $"{Guid.NewGuid()}.png";
            var filePath = Path.Combine(_avatarPath, storedFileName);

            // Save as PNG (lossless, good quality for avatars)
            await image.SaveAsync(filePath, new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestCompression
            }, cancellationToken);

            _logger.LogInformation(
                "Processed and saved avatar as {FileName} ({Width}x{Height})",
                storedFileName, targetSize, targetSize);

            return new AvatarProcessResult(storedFileName, true);
        }
        catch (UnknownImageFormatException ex)
        {
            _logger.LogWarning(ex, "Failed to process avatar: unknown image format");
            return new AvatarProcessResult(string.Empty, false, "Invalid image format");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process avatar");
            return new AvatarProcessResult(string.Empty, false, "Failed to process image");
        }
    }

    public Task DeleteAvatarAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            return Task.CompletedTask;

        // Prevent directory traversal
        var sanitizedName = Path.GetFileName(fileName);
        var filePath = Path.Combine(_avatarPath, sanitizedName);
        var fullPath = Path.GetFullPath(filePath);

        if (!fullPath.StartsWith(_avatarPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Attempted directory traversal in avatar delete: {FileName}", fileName);
            return Task.CompletedTask;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted avatar: {FileName}", sanitizedName);
        }

        return Task.CompletedTask;
    }

    public Task<AvatarFileResult?> GetAvatarAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            return Task.FromResult<AvatarFileResult?>(null);

        // Prevent directory traversal
        var sanitizedName = Path.GetFileName(fileName);
        var filePath = Path.Combine(_avatarPath, sanitizedName);
        var fullPath = Path.GetFullPath(filePath);

        if (!fullPath.StartsWith(_avatarPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Attempted directory traversal in avatar get: {FileName}", fileName);
            return Task.FromResult<AvatarFileResult?>(null);
        }

        if (!File.Exists(fullPath))
            return Task.FromResult<AvatarFileResult?>(null);

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var contentType = GetContentType(sanitizedName);

        return Task.FromResult<AvatarFileResult?>(new AvatarFileResult(stream, contentType));
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
