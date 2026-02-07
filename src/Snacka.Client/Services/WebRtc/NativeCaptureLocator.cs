using Snacka.Shared.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Locates native capture tools and builds command arguments for each platform.
/// Handles macOS (SnackaCaptureVideoToolbox), Windows (SnackaCaptureWindows), and Linux (SnackaCaptureLinux).
/// Extracted from WebRtcService for single responsibility.
/// </summary>
public class NativeCaptureLocator
{
    /// <summary>
    /// Checks if SnackaCaptureVideoToolbox (native ScreenCaptureKit) should be used.
    /// Returns true on macOS 13+ where we have the native capture tool.
    /// </summary>
    public bool ShouldUseSnackaCaptureVideoToolbox()
    {
        if (!OperatingSystem.IsMacOS()) return false;

        // Environment.OSVersion.Version returns Darwin kernel version on macOS
        // Darwin 22.x = macOS 13 Ventura, Darwin 23.x = macOS 14, Darwin 24.x = macOS 15
        // We need macOS 13+ for ScreenCaptureKit audio support
        var darwinVersion = Environment.OSVersion.Version.Major;
        if (darwinVersion < 22)
        {
            Console.WriteLine($"NativeCaptureLocator: macOS Darwin version {darwinVersion} < 22, SnackaCaptureVideoToolbox requires macOS 13+");
            return false;
        }

        // Check if the binary exists
        var snackaCapturePath = GetSnackaCaptureVideoToolboxPath();
        if (snackaCapturePath != null && File.Exists(snackaCapturePath))
        {
            Console.WriteLine($"NativeCaptureLocator: SnackaCaptureVideoToolbox available at {snackaCapturePath}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if SnackaCaptureWindows (native Desktop Duplication + WASAPI) should be used.
    /// Returns true on Windows 10+ where we have the native capture tool.
    /// </summary>
    public bool ShouldUseSnackaCaptureWindows()
    {
        if (!OperatingSystem.IsWindows()) return false;

        // Windows 10 build 1803+ required for Desktop Duplication API
        // Environment.OSVersion.Version.Build >= 17134 for Win10 1803
        var build = Environment.OSVersion.Version.Build;
        if (build < 17134)
        {
            Console.WriteLine($"NativeCaptureLocator: Windows build {build} < 17134, SnackaCaptureWindows requires Windows 10 1803+");
            return false;
        }

        // Check if the binary exists
        var capturePath = GetSnackaCaptureWindowsPath();
        if (capturePath != null && File.Exists(capturePath))
        {
            Console.WriteLine($"NativeCaptureLocator: SnackaCaptureWindows available at {capturePath}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if SnackaCaptureLinux (native X11 + VAAPI) should be used.
    /// Returns true on Linux where we have the native capture tool with VAAPI support.
    /// </summary>
    public bool ShouldUseSnackaCaptureLinux()
    {
        if (!OperatingSystem.IsLinux()) return false;

        // Check if the binary exists
        var capturePath = GetSnackaCaptureLinuxPath();
        if (capturePath != null && File.Exists(capturePath))
        {
            Console.WriteLine($"NativeCaptureLocator: SnackaCaptureLinux available at {capturePath}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the path to the SnackaCaptureVideoToolbox binary.
    /// </summary>
    public string? GetSnackaCaptureVideoToolboxPath()
    {
        // Look for SnackaCaptureVideoToolbox in several locations:
        // 1. Same directory as the app (bundled)
        // 2. Swift 6+ architecture-specific build paths (arm64-apple-macosx)
        // 3. Legacy Swift build paths (fallback)

        var appDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            // Bundled with app
            Path.Combine(appDir, "SnackaCaptureVideoToolbox"),
            // Swift 6+ architecture-specific build paths (arm64-apple-macosx)
            Path.Combine(appDir, "..", "SnackaCaptureVideoToolbox", ".build", "arm64-apple-macosx", "release", "SnackaCaptureVideoToolbox"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureVideoToolbox", ".build", "arm64-apple-macosx", "release", "SnackaCaptureVideoToolbox"),
            Path.Combine(appDir, "..", "SnackaCaptureVideoToolbox", ".build", "arm64-apple-macosx", "debug", "SnackaCaptureVideoToolbox"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureVideoToolbox", ".build", "arm64-apple-macosx", "debug", "SnackaCaptureVideoToolbox"),
            // Legacy Swift build paths (fallback)
            Path.Combine(appDir, "..", "SnackaCaptureVideoToolbox", ".build", "release", "SnackaCaptureVideoToolbox"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureVideoToolbox", ".build", "release", "SnackaCaptureVideoToolbox"),
            Path.Combine(appDir, "..", "SnackaCaptureVideoToolbox", ".build", "debug", "SnackaCaptureVideoToolbox"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureVideoToolbox", ".build", "debug", "SnackaCaptureVideoToolbox"),
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"NativeCaptureLocator: Found SnackaCaptureVideoToolbox at {fullPath}");
                return fullPath;
            }
        }

        Console.WriteLine("NativeCaptureLocator: SnackaCaptureVideoToolbox not found");
        return null;
    }

    /// <summary>
    /// Gets the path to the SnackaCaptureWindows binary.
    /// </summary>
    public string? GetSnackaCaptureWindowsPath()
    {
        // Look for SnackaCaptureWindows in several locations:
        // 1. Same directory as the app (bundled)
        // 2. CMake build paths (Release/Debug)

        var appDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            // Bundled with app
            Path.Combine(appDir, "SnackaCaptureWindows.exe"),
            // CMake build paths
            Path.Combine(appDir, "..", "SnackaCaptureWindows", "build", "bin", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureWindows", "build", "bin", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "SnackaCaptureWindows", "build", "bin", "Release", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureWindows", "build", "bin", "Release", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "SnackaCaptureWindows", "build", "bin", "Debug", "SnackaCaptureWindows.exe"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureWindows", "build", "bin", "Debug", "SnackaCaptureWindows.exe"),
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"NativeCaptureLocator: Found SnackaCaptureWindows at {fullPath}");
                return fullPath;
            }
        }

        Console.WriteLine("NativeCaptureLocator: SnackaCaptureWindows not found");
        return null;
    }

    /// <summary>
    /// Gets the path to the SnackaCaptureLinux binary.
    /// </summary>
    public string? GetSnackaCaptureLinuxPath()
    {
        // Look for SnackaCaptureLinux in several locations:
        // 1. Same directory as the app (bundled)
        // 2. CMake build paths
        // 3. Standard installation paths

        var appDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            // Bundled with app
            Path.Combine(appDir, "SnackaCaptureLinux"),
            // CMake build paths
            Path.Combine(appDir, "..", "SnackaCaptureLinux", "build", "bin", "SnackaCaptureLinux"),
            Path.Combine(appDir, "..", "..", "..", "..", "SnackaCaptureLinux", "build", "bin", "SnackaCaptureLinux"),
            // Standard installation paths
            "/usr/local/bin/SnackaCaptureLinux",
            "/usr/bin/SnackaCaptureLinux",
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"NativeCaptureLocator: Found SnackaCaptureLinux at {fullPath}");
                return fullPath;
            }
        }

        Console.WriteLine("NativeCaptureLocator: SnackaCaptureLinux not found");
        return null;
    }

    /// <summary>
    /// Builds SnackaCaptureVideoToolbox command arguments based on source and settings.
    /// </summary>
    /// <param name="source">The capture source (display, window, or application)</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="fps">Frames per second</param>
    /// <param name="captureAudio">Whether to capture system audio</param>
    /// <param name="bitrateMbps">Encoding bitrate in Mbps (default: 8)</param>
    public string GetSnackaCaptureVideoToolboxArgs(ScreenCaptureSource? source, int width, int height, int fps, bool captureAudio, int bitrateMbps = 8)
    {
        var args = new List<string>();

        // Source type
        if (source == null || source.Type == ScreenCaptureSourceType.Display)
        {
            var displayIndex = source?.Id ?? "0";
            args.Add($"--display {displayIndex}");
        }
        else if (source.Type == ScreenCaptureSourceType.Window)
        {
            args.Add($"--window {source.Id}");
        }
        else if (source.Type == ScreenCaptureSourceType.Application)
        {
            // Use bundleId for application capture (captures all windows of the app)
            var bundleId = source.BundleId ?? source.Id;
            args.Add($"--app {bundleId}");
        }

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Audio
        if (captureAudio)
        {
            args.Add("--audio");
            args.Add("--exclude-self");  // Don't capture SnackaCaptureVideoToolbox's own audio

            // Exclude Snacka.Client's audio to prevent capturing other users' voices
            // Try to get the bundle ID - for .NET apps this may be the process name or a custom ID
            var bundleId = GetAppBundleId();
            if (!string.IsNullOrEmpty(bundleId))
            {
                // Quote the bundle ID in case it contains spaces
                args.Add($"--exclude-app \"{bundleId}\"");
                Console.WriteLine($"NativeCaptureLocator: Will exclude audio from app: {bundleId}");
            }
        }

        // Use direct H.264 encoding via VideoToolbox (bypasses ffmpeg)
        args.Add("--encode");
        args.Add($"--bitrate {bitrateMbps}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Builds SnackaCaptureWindows command arguments based on source and settings.
    /// </summary>
    /// <param name="source">The capture source (display or window)</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="fps">Frames per second</param>
    /// <param name="captureAudio">Whether to capture system audio</param>
    /// <param name="bitrateMbps">Encoding bitrate in Mbps (default: 8)</param>
    public string GetSnackaCaptureWindowsArgs(ScreenCaptureSource? source, int width, int height, int fps, bool captureAudio, int bitrateMbps = 8)
    {
        var args = new List<string>();

        // Source type - Windows version doesn't have a "capture" subcommand
        if (source == null || source.Type == ScreenCaptureSourceType.Display)
        {
            var displayIndex = source?.Id ?? "0";
            args.Add($"--display {displayIndex}");
        }
        else if (source.Type == ScreenCaptureSourceType.Window)
        {
            // Windows uses HWND (window handle) for window capture
            args.Add($"--window {source.Id}");
        }
        // Note: Application capture not supported on Windows yet

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Audio - WASAPI loopback captures all system audio
        if (captureAudio)
        {
            args.Add("--audio");
        }

        // Use direct H.264 encoding via Media Foundation (NVENC/AMF/QuickSync)
        args.Add("--encode");
        args.Add($"--bitrate {bitrateMbps}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Builds SnackaCaptureLinux command arguments based on source and settings.
    /// </summary>
    /// <param name="source">The capture source (display)</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="fps">Frames per second</param>
    /// <param name="captureAudio">Whether to capture system audio</param>
    /// <param name="bitrateMbps">Encoding bitrate in Mbps (default: 8)</param>
    public string GetSnackaCaptureLinuxArgs(ScreenCaptureSource? source, int width, int height, int fps, bool captureAudio = false, int bitrateMbps = 8)
    {
        var args = new List<string>();

        // Source type - Linux version uses display index
        if (source == null || source.Type == ScreenCaptureSourceType.Display)
        {
            var displayIndex = source?.Id ?? "0";
            args.Add($"--display {displayIndex}");
        }
        // Note: Window capture not yet supported in SnackaCaptureLinux

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Audio capture via PulseAudio/PipeWire
        if (captureAudio)
        {
            args.Add("--audio");
        }

        // Use direct H.264 encoding via VAAPI
        args.Add("--encode");
        args.Add($"--bitrate {bitrateMbps}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Gets FFmpeg capture arguments based on platform and source.
    /// Returns (captureDevice, inputDevice, extraArgs).
    /// </summary>
    public (string captureDevice, string inputDevice, string extraArgs) GetFfmpegCaptureArgs(ScreenCaptureSource? source)
    {
        if (OperatingSystem.IsMacOS())
        {
            // macOS: avfoundation with "Capture screen N"
            var displayIndex = source?.Id ?? "0";
            return ("avfoundation", $"Capture screen {displayIndex}", "");
        }

        if (OperatingSystem.IsWindows())
        {
            if (source == null || source.Type == ScreenCaptureSourceType.Display)
            {
                // Windows display capture via gdigrab
                // For multi-monitor, we'd need to specify offset, but "desktop" captures primary
                return ("gdigrab", "desktop", "");
            }
            else
            {
                // Window capture via gdigrab with window title
                // The source.Id contains the window title for gdigrab
                return ("gdigrab", $"title={source.Id}", "");
            }
        }

        if (OperatingSystem.IsLinux())
        {
            if (source == null || source.Type == ScreenCaptureSourceType.Display)
            {
                // Linux display capture via x11grab
                // source.Id contains the :0.0+x,y format for multi-monitor
                var displayId = source?.Id ?? ":0.0";
                return ("x11grab", displayId, "");
            }
            else
            {
                // Window capture via x11grab with window ID
                // Need to get window geometry first - for now, use root window
                // TODO: Implement proper window capture with xwininfo
                Console.WriteLine("NativeCaptureLocator: Linux window capture not fully implemented, capturing root");
                return ("x11grab", ":0.0", "");
            }
        }

        // Fallback
        return ("x11grab", ":0.0", "");
    }

    /// <summary>
    /// Checks if native camera capture is available on the current platform.
    /// Returns true if the native capture tool supports camera capture.
    /// </summary>
    public bool IsNativeCameraCaptureAvailable()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ShouldUseSnackaCaptureVideoToolbox();
        }

        if (OperatingSystem.IsWindows())
        {
            return ShouldUseSnackaCaptureWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return ShouldUseSnackaCaptureLinux();
        }

        return false;
    }

    /// <summary>
    /// Gets the path to the native capture tool that supports camera capture.
    /// Returns null if no native camera capture tool is available.
    /// </summary>
    public string? GetNativeCameraCapturePath()
    {
        if (OperatingSystem.IsMacOS())
        {
            return GetSnackaCaptureVideoToolboxPath();
        }

        if (OperatingSystem.IsWindows())
        {
            return GetSnackaCaptureWindowsPath();
        }

        if (OperatingSystem.IsLinux())
        {
            return GetSnackaCaptureLinuxPath();
        }

        return null;
    }

    /// <summary>
    /// Builds native camera capture command arguments for any platform.
    /// </summary>
    /// <param name="cameraId">Camera device ID or index</param>
    /// <param name="width">Output width</param>
    /// <param name="height">Output height</param>
    /// <param name="fps">Frames per second</param>
    /// <param name="bitrateMbps">Encoding bitrate in Mbps</param>
    public string GetNativeCameraCaptureArgs(string cameraId, int width, int height, int fps, int bitrateMbps = 2)
    {
        if (OperatingSystem.IsMacOS())
        {
            return GetSnackaCaptureVideoToolboxCameraArgs(cameraId, width, height, fps, bitrateMbps);
        }

        if (OperatingSystem.IsWindows())
        {
            return GetSnackaCaptureWindowsCameraArgs(cameraId, width, height, fps, bitrateMbps);
        }

        if (OperatingSystem.IsLinux())
        {
            return GetSnackaCaptureLinuxCameraArgs(cameraId, width, height, fps, bitrateMbps);
        }

        throw new PlatformNotSupportedException("Native camera capture not supported on this platform");
    }

    /// <summary>
    /// Builds SnackaCaptureVideoToolbox camera capture arguments (macOS).
    /// </summary>
    private string GetSnackaCaptureVideoToolboxCameraArgs(string cameraId, int width, int height, int fps, int bitrateMbps)
    {
        var args = new List<string>();

        // Camera source - quote the ID in case it contains special characters
        args.Add($"--camera \"{cameraId}\"");

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Use direct H.264 encoding via VideoToolbox
        args.Add("--encode");
        args.Add($"--bitrate {bitrateMbps}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Builds SnackaCaptureWindows camera capture arguments (Windows).
    /// </summary>
    private string GetSnackaCaptureWindowsCameraArgs(string cameraId, int width, int height, int fps, int bitrateMbps)
    {
        var args = new List<string>();

        // Camera source - quote the ID in case it contains special characters
        args.Add($"--camera \"{cameraId}\"");

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Use direct H.264 encoding via Media Foundation
        args.Add("--encode");
        args.Add($"--bitrate {bitrateMbps}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Builds SnackaCaptureLinux camera capture arguments (Linux).
    /// </summary>
    private string GetSnackaCaptureLinuxCameraArgs(string cameraId, int width, int height, int fps, int bitrateMbps)
    {
        var args = new List<string>();

        // Camera source - quote the ID in case it contains special characters
        args.Add($"--camera \"{cameraId}\"");

        // Resolution and framerate
        args.Add($"--width {width}");
        args.Add($"--height {height}");
        args.Add($"--fps {fps}");

        // Use direct H.264 encoding via VAAPI
        args.Add("--encode");
        args.Add($"--bitrate {bitrateMbps}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// Gets the bundle identifier for the current app.
    /// On macOS, this is used to exclude our app's audio from screen capture.
    /// </summary>
    public string? GetAppBundleId()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        try
        {
            // For .NET apps, use the process name as a fallback
            // ScreenCaptureKit can find apps by bundle ID or we match by process name
            var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

            // If running from IDE/dotnet run, process name is typically "dotnet"
            // In that case, we can't reliably exclude ourselves
            if (processName == "dotnet")
            {
                Console.WriteLine("NativeCaptureLocator: Running via 'dotnet' - cannot determine bundle ID to exclude");
                return null;
            }

            // Use the process name - ScreenCaptureKit might match by app name
            // For a published app, this would be "Snacka.Client" or similar
            return processName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if native microphone capture is available on the current platform.
    /// </summary>
    public bool IsNativeMicrophoneCaptureAvailable()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ShouldUseSnackaCaptureVideoToolbox();
        }

        if (OperatingSystem.IsWindows())
        {
            return ShouldUseSnackaCaptureWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return ShouldUseSnackaCaptureLinux();
        }

        return false;
    }

    /// <summary>
    /// Gets the path to the native capture tool that supports microphone capture.
    /// </summary>
    public string? GetNativeMicrophoneCapturePath()
    {
        if (OperatingSystem.IsMacOS())
        {
            return GetSnackaCaptureVideoToolboxPath();
        }

        if (OperatingSystem.IsWindows())
        {
            return GetSnackaCaptureWindowsPath();
        }

        if (OperatingSystem.IsLinux())
        {
            return GetSnackaCaptureLinuxPath();
        }

        return null;
    }

    /// <summary>
    /// Builds native microphone capture command arguments for any platform.
    /// </summary>
    /// <param name="microphoneId">Microphone device ID or index</param>
    /// <param name="noiseSuppression">Whether to enable AI-powered noise suppression (default: true)</param>
    /// <param name="echoCancellation">Whether to enable acoustic echo cancellation (default: true, macOS only via VoiceProcessingIO)</param>
    public string GetNativeMicrophoneCaptureArgs(string microphoneId, bool noiseSuppression = true, bool echoCancellation = true)
    {
        // All platforms use the same argument format
        // Quote the ID in case it contains special characters or spaces
        var args = $"--microphone \"{microphoneId}\"";

        // Noise suppression is enabled by default in native tools, so only add flag when disabled
        if (!noiseSuppression)
        {
            args += " --no-noise-suppression";
        }

        // Echo cancellation is enabled by default on macOS (VoiceProcessingIO), so only add flag when disabled
        // On Linux, users should configure system-level AEC via PipeWire/PulseAudio
        // On Windows, we'll add support later via Voice Capture DSP
        if (!echoCancellation)
        {
            args += " --no-echo-cancellation";
        }

        return args;
    }

    /// <summary>
    /// Gets available microphones from the native capture tool.
    /// </summary>
    public async Task<List<MicrophoneInfo>> GetAvailableMicrophonesAsync()
    {
        var microphones = new List<MicrophoneInfo>();

        string? capturePath = null;
        if (OperatingSystem.IsMacOS())
        {
            capturePath = GetSnackaCaptureVideoToolboxPath();
        }
        else if (OperatingSystem.IsWindows())
        {
            capturePath = GetSnackaCaptureWindowsPath();
        }
        else if (OperatingSystem.IsLinux())
        {
            capturePath = GetSnackaCaptureLinuxPath();
        }

        if (capturePath == null || !File.Exists(capturePath))
        {
            Console.WriteLine("NativeCaptureLocator: Native capture tool not available for microphone enumeration");
            return microphones;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = capturePath,
                Arguments = "list --json --microphones-only",  // Fast path - only enumerate microphones
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            process.Start();

            // Read both stdout and stderr concurrently to avoid deadlock
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // Wait for process with timeout (15 seconds - needs to be generous as system may be under load)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                Console.WriteLine($"NativeCaptureLocator: Microphone enumeration timed out after {sw.ElapsedMilliseconds}ms");
                try { process.Kill(); } catch { }
                return microphones;
            }
            sw.Stop();
            Console.WriteLine($"NativeCaptureLocator: Microphone enumeration completed in {sw.ElapsedMilliseconds}ms");

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"NativeCaptureLocator: Failed to enumerate microphones: {error}");
                return microphones;
            }

            // Parse JSON output
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("microphones", out var microphonesElement))
            {
                foreach (var mic in microphonesElement.EnumerateArray())
                {
                    var id = mic.GetProperty("id").GetString() ?? "";
                    var name = mic.GetProperty("name").GetString() ?? "";
                    var index = mic.GetProperty("index").GetInt32();

                    microphones.Add(new MicrophoneInfo(id, name, index));
                }
            }

            Console.WriteLine($"NativeCaptureLocator: Found {microphones.Count} microphones via native tool");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NativeCaptureLocator: Error enumerating microphones: {ex.Message}");
        }

        return microphones;
    }
}
