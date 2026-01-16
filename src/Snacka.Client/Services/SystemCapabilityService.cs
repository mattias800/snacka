using System.Diagnostics;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Client.Services.WebRtc;

namespace Snacka.Client.Services;

/// <summary>
/// Service for detecting and reporting system capabilities at startup.
/// Outputs diagnostics to stdout for testing and debugging.
/// </summary>
public sealed class SystemCapabilityService : ISystemCapabilityService
{
    public bool IsHardwareEncoderAvailable { get; private set; }
    public string? HardwareEncoderName { get; private set; }
    public bool IsHardwareDecoderAvailable { get; private set; }
    public string? HardwareDecoderName { get; private set; }
    public bool IsNativeCaptureAvailable { get; private set; }
    public string? NativeCaptureName { get; private set; }
    public bool IsFullHardwareAccelerationAvailable =>
        IsHardwareEncoderAvailable && IsHardwareDecoderAvailable && IsNativeCaptureAvailable;

    private readonly List<string> _warnings = new();
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Runs all capability checks and outputs results to stdout.
    /// Should be called at application startup.
    /// </summary>
    public void CheckCapabilities()
    {
        Console.WriteLine("");
        Console.WriteLine("=== System Capability Check ===");
        Console.WriteLine("");

        CheckNativeCapture();
        CheckHardwareEncoder();
        CheckHardwareDecoder();

        PrintSummary();
    }

    private void CheckNativeCapture()
    {
        Console.WriteLine("Checking native capture tools...");

        var locator = new NativeCaptureLocator();

        if (OperatingSystem.IsMacOS())
        {
            if (locator.ShouldUseSnackaCaptureVideoToolbox())
            {
                IsNativeCaptureAvailable = true;
                NativeCaptureName = "SnackaCaptureVideoToolbox";
                var path = locator.GetSnackaCaptureVideoToolboxPath();
                Console.WriteLine($"  [OK] Native capture: SnackaCaptureVideoToolbox");
                Console.WriteLine($"       Path: {path}");
            }
            else
            {
                IsNativeCaptureAvailable = false;
                Console.WriteLine("  [WARN] Native capture: SnackaCaptureVideoToolbox NOT FOUND");
                Console.WriteLine("         Screen share will use FFmpeg software path (higher CPU usage)");
                _warnings.Add("SnackaCaptureVideoToolbox not found. Screen sharing will use more CPU.");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            if (locator.ShouldUseSnackaCaptureWindows())
            {
                IsNativeCaptureAvailable = true;
                NativeCaptureName = "SnackaCaptureWindows";
                var path = locator.GetSnackaCaptureWindowsPath();
                Console.WriteLine($"  [OK] Native capture: SnackaCaptureWindows");
                Console.WriteLine($"       Path: {path}");
            }
            else
            {
                IsNativeCaptureAvailable = false;
                Console.WriteLine("  [WARN] Native capture: SnackaCaptureWindows NOT FOUND");
                Console.WriteLine("         Screen share will use FFmpeg software path (higher CPU usage)");
                _warnings.Add("SnackaCaptureWindows not found. Screen sharing will use more CPU.");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (locator.ShouldUseSnackaCaptureLinux())
            {
                IsNativeCaptureAvailable = true;
                NativeCaptureName = "SnackaCaptureLinux";
                var path = locator.GetSnackaCaptureLinuxPath();
                Console.WriteLine($"  [OK] Native capture: SnackaCaptureLinux");
                Console.WriteLine($"       Path: {path}");
            }
            else
            {
                IsNativeCaptureAvailable = false;
                Console.WriteLine("  [WARN] Native capture: SnackaCaptureLinux NOT FOUND");
                Console.WriteLine("         Screen share will use FFmpeg software path (higher CPU usage)");
                _warnings.Add("SnackaCaptureLinux not found. Screen sharing will use more CPU.");
            }
        }
        else
        {
            Console.WriteLine("  [WARN] Unsupported platform for native capture");
            _warnings.Add("Unsupported platform for hardware-accelerated capture.");
        }

        Console.WriteLine("");
    }

    private void CheckHardwareEncoder()
    {
        Console.WriteLine("Checking hardware video encoders...");

        if (OperatingSystem.IsMacOS())
        {
            // VideoToolbox is always available on macOS
            IsHardwareEncoderAvailable = true;
            HardwareEncoderName = "VideoToolbox";
            Console.WriteLine("  [OK] Hardware encoder: VideoToolbox (Apple Silicon/Intel)");
        }
        else if (OperatingSystem.IsWindows())
        {
            CheckWindowsHardwareEncoders();
        }
        else if (OperatingSystem.IsLinux())
        {
            CheckLinuxHardwareEncoders();
        }
        else
        {
            Console.WriteLine("  [WARN] Unsupported platform for hardware encoding");
            _warnings.Add("Unsupported platform for hardware-accelerated encoding.");
        }

        if (!IsHardwareEncoderAvailable)
        {
            Console.WriteLine("  [WARN] No hardware encoder found - will use libx264 (CPU-only, high usage)");
            _warnings.Add("No hardware video encoder available. Screen sharing will use software encoding (high CPU usage).");
        }

        Console.WriteLine("");
    }

    private void CheckWindowsHardwareEncoders()
    {
        // Check in order of preference: NVENC, AMF, QuickSync
        var encoders = new[]
        {
            ("h264_nvenc", "NVENC", "NVIDIA GPU"),
            ("h264_amf", "AMF", "AMD GPU"),
            ("h264_qsv", "QuickSync", "Intel GPU"),
        };

        foreach (var (encoder, name, description) in encoders)
        {
            Console.WriteLine($"  Checking {name} ({description})...");
            if (IsEncoderAvailable(encoder))
            {
                IsHardwareEncoderAvailable = true;
                HardwareEncoderName = name;
                Console.WriteLine($"  [OK] Hardware encoder: {name} ({description})");
                return;
            }
            else
            {
                Console.WriteLine($"       {name} not available");
            }
        }
    }

    private void CheckLinuxHardwareEncoders()
    {
        Console.WriteLine("  Checking VAAPI...");
        if (IsEncoderAvailable("h264_vaapi"))
        {
            IsHardwareEncoderAvailable = true;
            HardwareEncoderName = "VAAPI";
            Console.WriteLine("  [OK] Hardware encoder: VAAPI (Intel/AMD GPU)");
        }
        else
        {
            Console.WriteLine("       VAAPI not available");
        }
    }

    private void CheckHardwareDecoder()
    {
        Console.WriteLine("Checking hardware video decoders...");

        try
        {
            var isAvailable = HardwareVideoDecoderFactory.IsAvailable();

            if (isAvailable)
            {
                IsHardwareDecoderAvailable = true;

                if (OperatingSystem.IsMacOS())
                {
                    HardwareDecoderName = "VideoToolbox";
                    Console.WriteLine("  [OK] Hardware decoder: VideoToolbox");
                }
                else if (OperatingSystem.IsWindows())
                {
                    HardwareDecoderName = "MediaFoundation";
                    Console.WriteLine("  [OK] Hardware decoder: Media Foundation (D3D11)");
                }
                else if (OperatingSystem.IsLinux())
                {
                    HardwareDecoderName = "VAAPI";
                    Console.WriteLine("  [OK] Hardware decoder: VAAPI");
                }

                // Verify decoder can be created
                var decoder = HardwareVideoDecoderFactory.Create();
                if (decoder != null)
                {
                    Console.WriteLine("       Decoder instance created successfully");
                    decoder.Dispose();
                }
                else
                {
                    Console.WriteLine("  [WARN] Decoder available but instance creation failed");
                    _warnings.Add("Hardware decoder detection succeeded but instance creation failed.");
                }
            }
            else
            {
                IsHardwareDecoderAvailable = false;
                Console.WriteLine("  [WARN] No hardware decoder available");
                Console.WriteLine("         Video playback will use software decoding (higher CPU usage)");
                _warnings.Add("No hardware video decoder available. Video playback will use more CPU.");
            }
        }
        catch (Exception ex)
        {
            IsHardwareDecoderAvailable = false;
            Console.WriteLine($"  [ERROR] Hardware decoder check failed: {ex.Message}");
            _warnings.Add($"Hardware decoder check failed: {ex.Message}");
        }

        Console.WriteLine("");
    }

    private void PrintSummary()
    {
        Console.WriteLine("=== Capability Summary ===");
        Console.WriteLine("");
        Console.WriteLine($"  Native Capture:    {(IsNativeCaptureAvailable ? $"[OK] {NativeCaptureName}" : "[MISSING]")}");
        Console.WriteLine($"  Hardware Encoder:  {(IsHardwareEncoderAvailable ? $"[OK] {HardwareEncoderName}" : "[MISSING] (using libx264)")}");
        Console.WriteLine($"  Hardware Decoder:  {(IsHardwareDecoderAvailable ? $"[OK] {HardwareDecoderName}" : "[MISSING]")}");
        Console.WriteLine("");

        if (IsFullHardwareAccelerationAvailable)
        {
            Console.WriteLine("  Status: Full hardware acceleration available");
        }
        else
        {
            Console.WriteLine("  Status: REDUCED PERFORMANCE - Some hardware acceleration unavailable");
            Console.WriteLine("");
            Console.WriteLine("  Warnings:");
            foreach (var warning in _warnings)
            {
                Console.WriteLine($"    - {warning}");
            }
        }

        Console.WriteLine("");
        Console.WriteLine("=== End Capability Check ===");
        Console.WriteLine("");
    }

    private static string? _ffmpegPath;

    private static string GetFfmpegPath()
    {
        if (_ffmpegPath != null) return _ffmpegPath;

        if (OperatingSystem.IsMacOS())
        {
            var paths = new[]
            {
                "/opt/homebrew/bin/ffmpeg",
                "/opt/homebrew/opt/ffmpeg@6/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/local/opt/ffmpeg@6/bin/ffmpeg",
                "/usr/bin/ffmpeg"
            };

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    _ffmpegPath = path;
                    return _ffmpegPath;
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var paths = new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" };
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    _ffmpegPath = path;
                    return _ffmpegPath;
                }
            }
        }

        _ffmpegPath = "ffmpeg";
        return _ffmpegPath;
    }

    private static bool IsEncoderAvailable(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetFfmpegPath(),
                Arguments = "-hide_banner -encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            return output.Contains(encoder);
        }
        catch
        {
            return false;
        }
    }
}
