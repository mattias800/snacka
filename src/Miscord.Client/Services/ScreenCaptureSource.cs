namespace Miscord.Client.Services;

public enum ScreenCaptureSourceType
{
    Display,      // Full screen/monitor
    Window,       // Individual application window
    Application   // All windows of an application (macOS only)
}

public record ScreenCaptureSource(
    ScreenCaptureSourceType Type,
    string Id,              // Platform-specific ID (display index, window ID, or bundle ID)
    string Name,            // Human-readable name ("Display 1", "Safari - Google")
    string? AppName = null, // For windows: application name
    string? BundleId = null // For applications/windows on macOS: bundle identifier
);

/// <summary>
/// Resolution preset for screen sharing.
/// </summary>
public record ScreenShareResolution(int Width, int Height, string Label)
{
    public static ScreenShareResolution HD720 => new(1280, 720, "720p");
    public static ScreenShareResolution HD1080 => new(1920, 1080, "1080p");
    public static ScreenShareResolution QHD1440 => new(2560, 1440, "1440p");
    public static ScreenShareResolution UHD2160 => new(3840, 2160, "4K");

    public static IReadOnlyList<ScreenShareResolution> All => new[]
    {
        HD720, HD1080, QHD1440, UHD2160
    };

    public override string ToString() => Label;
}

/// <summary>
/// Framerate preset for screen sharing.
/// </summary>
public record ScreenShareFramerate(int Fps, string Label)
{
    public static ScreenShareFramerate Fps15 => new(15, "15 FPS");
    public static ScreenShareFramerate Fps30 => new(30, "30 FPS");
    public static ScreenShareFramerate Fps60 => new(60, "60 FPS");

    public static IReadOnlyList<ScreenShareFramerate> All => new[]
    {
        Fps15, Fps30, Fps60
    };

    public override string ToString() => Label;
}

/// <summary>
/// Settings for screen sharing including source, resolution, framerate, and audio options.
/// </summary>
public record ScreenShareSettings(
    ScreenCaptureSource Source,
    ScreenShareResolution Resolution,
    ScreenShareFramerate Framerate,
    bool IncludeAudio = false  // Whether to capture and share audio from the source
);
