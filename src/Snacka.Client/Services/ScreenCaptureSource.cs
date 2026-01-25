namespace Snacka.Client.Services;

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
/// Quality preset for screen sharing, primarily controlling bitrate.
/// Higher bitrates provide better quality but require more bandwidth.
/// </summary>
public record ScreenShareQuality(int BitrateMbps, string Label, string Description)
{
    /// <summary>
    /// Smooth Motion - 4 Mbps. Best for presentations and static content.
    /// Lower bandwidth usage, may show artifacts during fast motion.
    /// </summary>
    public static ScreenShareQuality SmoothMotion => new(4, "Smooth Motion", "4 Mbps - Good for presentations");

    /// <summary>
    /// Balanced - 8 Mbps. Good balance of quality and bandwidth.
    /// Suitable for general use including light gaming at 1080p.
    /// </summary>
    public static ScreenShareQuality Balanced => new(8, "Balanced", "8 Mbps - Good for general use");

    /// <summary>
    /// High Quality - 15 Mbps. Better quality for detailed content and gaming.
    /// Good for 1080p@60fps or 1440p@30fps gaming.
    /// </summary>
    public static ScreenShareQuality HighQuality => new(15, "High Quality", "15 Mbps - Great for 1080p gaming");

    /// <summary>
    /// Gaming - 35 Mbps. Optimized for fast-paced gaming with lots of motion.
    /// Excellent for 1440p@60fps gaming with minimal artifacts.
    /// </summary>
    public static ScreenShareQuality Gaming => new(35, "Gaming", "35 Mbps - Best for 1440p@60fps");

    /// <summary>
    /// Ultra - 60 Mbps. Maximum quality for the best possible visual experience.
    /// Ideal for 4K gaming or competitive play where every detail matters.
    /// </summary>
    public static ScreenShareQuality Ultra => new(60, "Ultra", "60 Mbps - Maximum quality");

    public static IReadOnlyList<ScreenShareQuality> All => new[]
    {
        SmoothMotion, Balanced, HighQuality, Gaming, Ultra
    };

    public override string ToString() => $"{Label} ({Description})";
}

/// <summary>
/// Settings for screen sharing including source, resolution, framerate, quality, and audio options.
/// </summary>
public record ScreenShareSettings(
    ScreenCaptureSource Source,
    ScreenShareResolution Resolution,
    ScreenShareFramerate Framerate,
    ScreenShareQuality Quality,
    bool IncludeAudio = false  // Whether to capture and share audio from the source
);
