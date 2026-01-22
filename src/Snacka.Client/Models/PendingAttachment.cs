namespace Snacka.Client.Models;

/// <summary>
/// Represents a file pending upload with a message.
/// </summary>
public class PendingAttachment : IDisposable
{
    public required string FileName { get; init; }
    public required Stream Stream { get; init; }
    public required long Size { get; init; }
    public required string ContentType { get; init; }

    public string FormattedSize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / (1024.0 * 1024.0):F1} MB"
    };

    public void Dispose()
    {
        Stream.Dispose();
        GC.SuppressFinalize(this);
    }
}
