using Microsoft.Extensions.Options;

namespace Miscord.Server.Services;

/// <summary>
/// Local file system implementation of file storage.
/// </summary>
public class FileStorageService : IFileStorageService
{
    private readonly FileStorageSettings _settings;
    private readonly ILogger<FileStorageService> _logger;
    private readonly string _uploadPath;

    public FileStorageService(IOptions<FileStorageSettings> settings, ILogger<FileStorageService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _uploadPath = Path.GetFullPath(_settings.BasePath);

        // Ensure upload directory exists
        Directory.CreateDirectory(_uploadPath);
        _logger.LogInformation("File storage initialized at: {Path}", _uploadPath);
    }

    public FileValidationResult ValidateFile(string fileName, long fileSize, string contentType)
    {
        // Check file size
        if (fileSize <= 0)
            return new FileValidationResult(false, "File is empty");

        if (fileSize > _settings.MaxFileSizeBytes)
        {
            var maxMb = _settings.MaxFileSizeBytes / (1024 * 1024);
            return new FileValidationResult(false, $"File size exceeds limit of {maxMb}MB");
        }

        // Check extension
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            return new FileValidationResult(false, "File must have an extension");

        if (!_settings.AllowedExtensions.Contains(ext))
            return new FileValidationResult(false, $"File type '{ext}' is not allowed");

        return new FileValidationResult(true, null);
    }

    public async Task<StoredFileResult> StoreFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken ct = default)
    {
        var sanitizedName = SanitizeFileName(fileName);
        var ext = Path.GetExtension(sanitizedName).ToLowerInvariant();
        var storedName = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(_uploadPath, storedName);

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await fileStream.CopyToAsync(fs, ct);

        var isImage = IsImageFile(contentType, fileName);
        var isAudio = IsAudioFile(contentType, fileName);

        _logger.LogInformation("Stored file '{OriginalName}' as '{StoredName}' ({Size} bytes, IsImage={IsImage}, IsAudio={IsAudio})",
            fileName, storedName, fs.Length, isImage, isAudio);

        return new StoredFileResult(storedName, sanitizedName, isImage, isAudio);
    }

    public Task<FileRetrievalResult?> GetFileAsync(string storedFileName, CancellationToken ct = default)
    {
        // Prevent directory traversal attacks
        var sanitizedName = Path.GetFileName(storedFileName);
        var filePath = Path.Combine(_uploadPath, sanitizedName);
        var fullPath = Path.GetFullPath(filePath);

        // Ensure the resolved path is within our upload directory
        if (!fullPath.StartsWith(_uploadPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Attempted directory traversal attack: {FileName}", storedFileName);
            return Task.FromResult<FileRetrievalResult?>(null);
        }

        if (!File.Exists(fullPath))
            return Task.FromResult<FileRetrievalResult?>(null);

        var contentType = GetContentType(sanitizedName);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        return Task.FromResult<FileRetrievalResult?>(new FileRetrievalResult(stream, contentType, sanitizedName));
    }

    public Task DeleteFileAsync(string storedFileName, CancellationToken ct = default)
    {
        var sanitizedName = Path.GetFileName(storedFileName);
        var filePath = Path.Combine(_uploadPath, sanitizedName);
        var fullPath = Path.GetFullPath(filePath);

        // Ensure the resolved path is within our upload directory
        if (!fullPath.StartsWith(_uploadPath, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Attempted directory traversal in delete: {FileName}", storedFileName);
            return Task.CompletedTask;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted file: {StoredName}", storedFileName);
        }

        return Task.CompletedTask;
    }

    public bool IsImageFile(string contentType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return _settings.AllowedImageExtensions.Contains(ext) ||
               contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAudioFile(string contentType, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return _settings.AllowedAudioExtensions.Contains(ext) ||
               contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Where(c => !invalid.Contains(c)).ToArray());

        // Ensure we have a valid name
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "file";

        // Limit length
        if (sanitized.Length > 200)
        {
            var ext = Path.GetExtension(sanitized);
            var name = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = name[..Math.Min(name.Length, 200 - ext.Length)] + ext;
        }

        return sanitized;
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".zip" => "application/zip",
            ".tar" => "application/x-tar",
            ".gz" => "application/gzip",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            _ => "application/octet-stream"
        };
    }
}
