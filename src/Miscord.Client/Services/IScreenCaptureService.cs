namespace Miscord.Client.Services;

public interface IScreenCaptureService
{
    /// <summary>
    /// Gets all available screen capture sources (displays, windows, and applications).
    /// </summary>
    IReadOnlyList<ScreenCaptureSource> GetAvailableSources();

    /// <summary>
    /// Gets only display sources.
    /// </summary>
    IReadOnlyList<ScreenCaptureSource> GetDisplays();

    /// <summary>
    /// Gets only window sources.
    /// </summary>
    IReadOnlyList<ScreenCaptureSource> GetWindows();

    /// <summary>
    /// Gets only application sources (macOS only, via ScreenCaptureKit).
    /// </summary>
    IReadOnlyList<ScreenCaptureSource> GetApplications();
}
