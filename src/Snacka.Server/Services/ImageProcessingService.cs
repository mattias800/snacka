using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Snacka.Server.Services;

/// <summary>
/// Avatar processing with SkiaSharp (MIT, using Google Skia decoders). It replaced ImageSharp 4,
/// whose Release builds require a commercial license key.
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

    /// <summary>Largest source image we decode (about 8K x 6K); bigger files are rejected before decoding.</summary>
    private const long MaxSourcePixels = 50_000_000;

    public async Task<AvatarProcessResult> ProcessAvatarAsync(
        Stream sourceStream,
        CropRegion? cropRegion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Avatars are size-limited by ValidateAvatar, so buffering is fine (Skia needs a seekable source).
            using var buffer = new MemoryStream();
            await sourceStream.CopyToAsync(buffer, cancellationToken);
            using var data = SKData.CreateCopy(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                _logger.LogWarning("Failed to process avatar: unknown image format");
                return new AvatarProcessResult(string.Empty, false, "Invalid image format");
            }
            if ((long)codec.Info.Width * codec.Info.Height > MaxSourcePixels)
                return new AvatarProcessResult(string.Empty, false, "Image dimensions are too large");

            using var source = SKBitmap.Decode(codec);
            if (source is null)
                return new AvatarProcessResult(string.Empty, false, "Invalid image format");

            // Region of the source to use: the requested crop (relative coordinates), then the
            // centered square inside it (a "cover" resize).
            var region = new SKRectI(0, 0, source.Width, source.Height);
            if (cropRegion != null)
            {
                var x = Math.Clamp((int)(cropRegion.X * source.Width), 0, source.Width - 1);
                var y = Math.Clamp((int)(cropRegion.Y * source.Height), 0, source.Height - 1);
                var w = Math.Clamp((int)(cropRegion.Width * source.Width), 1, source.Width - x);
                var h = Math.Clamp((int)(cropRegion.Height * source.Height), 1, source.Height - y);
                region = new SKRectI(x, y, x + w, y + h);
                _logger.LogDebug("Cropping avatar: source {SrcW}x{SrcH}, crop {Region}", source.Width, source.Height, region);
            }
            var side = Math.Min(region.Width, region.Height);
            var square = SKRectI.Create(region.Left + (region.Width - side) / 2, region.Top + (region.Height - side) / 2, side, side);

            var targetSize = _settings.AvatarSize;
            using var surface = SKSurface.Create(new SKImageInfo(targetSize, targetSize, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Could not allocate the avatar surface.");
            using (var image = SKImage.FromBitmap(source))
            {
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawImage(image, SKRect.Create(square.Left, square.Top, square.Width, square.Height),
                    SKRect.Create(0, 0, targetSize, targetSize), new SKSamplingOptions(SKCubicResampler.Mitchell));
            }

            // Generate unique filename
            var storedFileName = $"{Guid.NewGuid()}.png";
            var filePath = Path.Combine(_avatarPath, storedFileName);

            // Save as PNG (lossless, good quality for avatars). Re-encoding also drops any metadata.
            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            await using (var file = File.Create(filePath))
                await file.WriteAsync(png.ToArray(), cancellationToken);

            _logger.LogInformation(
                "Processed and saved avatar as {FileName} ({Width}x{Height})",
                storedFileName, targetSize, targetSize);

            return new AvatarProcessResult(storedFileName, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
