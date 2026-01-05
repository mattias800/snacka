using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace Miscord.Client.Services;

public interface IVideoDeviceService : IDisposable
{
    IReadOnlyList<VideoDeviceInfo> GetCameraDevices();

    bool IsTestingCamera { get; }

    Task StartCameraTestAsync(string? devicePath, Action<byte[], int, int> onFrameReceived);
    Task StopTestAsync();
}

public record VideoDeviceInfo(string Path, string Name);

/// <summary>
/// Cross-platform video device service using Emgu CV (OpenCV wrapper).
/// Supports macOS, Linux, and Windows.
/// </summary>
public class VideoDeviceService : IVideoDeviceService
{
    private VideoCapture? _capture;
    private CancellationTokenSource? _testCts;
    private Action<byte[], int, int>? _onFrameReceived;
    private Task? _captureTask;

    private const int PreviewWidth = 640;
    private const int PreviewHeight = 360;
    private const int TargetFps = 15;

    public bool IsTestingCamera => _capture != null;

    // Constructor accepts ISettingsStore for compatibility, but we don't need it
    public VideoDeviceService(ISettingsStore? settingsStore = null)
    {
    }

    public IReadOnlyList<VideoDeviceInfo> GetCameraDevices()
    {
        Console.WriteLine("VideoDeviceService: Enumerating camera devices...");

        // Try platform-specific enumeration first for better device names
        var devices = GetCameraDevicesViaPlatformTools();
        if (devices.Count > 0)
        {
            return devices;
        }

        // Fall back to OpenCV probing
        return GetCameraDevicesViaOpenCV();
    }

    private IReadOnlyList<VideoDeviceInfo> GetCameraDevicesViaPlatformTools()
    {
        if (OperatingSystem.IsMacOS())
        {
            return GetCameraDevicesViaMacOS();
        }
        if (OperatingSystem.IsLinux())
        {
            return GetCameraDevicesViaLinux();
        }
        // Windows: fall back to OpenCV
        return Array.Empty<VideoDeviceInfo>();
    }

    private IReadOnlyList<VideoDeviceInfo> GetCameraDevicesViaMacOS()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-f avfoundation -list_devices true -i \"\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return Array.Empty<VideoDeviceInfo>();

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            var devices = new List<VideoDeviceInfo>();
            var lines = stderr.Split('\n');
            var inVideoDevices = false;

            foreach (var line in lines)
            {
                if (line.Contains("AVFoundation video devices:"))
                {
                    inVideoDevices = true;
                    continue;
                }
                if (line.Contains("AVFoundation audio devices:"))
                {
                    inVideoDevices = false;
                    continue;
                }

                if (inVideoDevices && line.Contains("[") && line.Contains("]"))
                {
                    var match = Regex.Match(line, @"\[(\d+)\]\s+(.+)$");
                    if (match.Success)
                    {
                        var index = match.Groups[1].Value;
                        var name = match.Groups[2].Value.Trim();
                        devices.Add(new VideoDeviceInfo(index, name));
                        Console.WriteLine($"  - Camera {index}: {name}");
                    }
                }
            }

            Console.WriteLine($"VideoDeviceService: Found {devices.Count} cameras via ffmpeg (macOS)");
            return devices;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoDeviceService: macOS enumeration failed - {ex.Message}");
            return Array.Empty<VideoDeviceInfo>();
        }
    }

    private IReadOnlyList<VideoDeviceInfo> GetCameraDevicesViaLinux()
    {
        try
        {
            // Use v4l2-ctl or enumerate /dev/video*
            var devices = new List<VideoDeviceInfo>();

            for (int i = 0; i < 10; i++)
            {
                var devicePath = $"/dev/video{i}";
                if (File.Exists(devicePath))
                {
                    var name = $"Video Device {i}";

                    // Try to get device name via v4l2-ctl
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "v4l2-ctl",
                            Arguments = $"--device={devicePath} --info",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var process = System.Diagnostics.Process.Start(psi);
                        if (process != null)
                        {
                            var output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit(2000);

                            var match = Regex.Match(output, @"Card type\s*:\s*(.+)");
                            if (match.Success)
                            {
                                name = match.Groups[1].Value.Trim();
                            }
                        }
                    }
                    catch { }

                    devices.Add(new VideoDeviceInfo(i.ToString(), name));
                    Console.WriteLine($"  - Camera {i}: {name}");
                }
            }

            Console.WriteLine($"VideoDeviceService: Found {devices.Count} cameras via /dev/video* (Linux)");
            return devices;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoDeviceService: Linux enumeration failed - {ex.Message}");
            return Array.Empty<VideoDeviceInfo>();
        }
    }

    private IReadOnlyList<VideoDeviceInfo> GetCameraDevicesViaOpenCV()
    {
        Console.WriteLine("VideoDeviceService: Falling back to OpenCV enumeration...");
        var devices = new List<VideoDeviceInfo>();

        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var testCapture = new VideoCapture(i);
                if (testCapture.IsOpened)
                {
                    var width = testCapture.Get(CapProp.FrameWidth);
                    var height = testCapture.Get(CapProp.FrameHeight);
                    var name = $"Camera {i}";

                    var backend = testCapture.BackendName;
                    if (!string.IsNullOrEmpty(backend))
                    {
                        name = $"Camera {i} ({backend})";
                    }

                    devices.Add(new VideoDeviceInfo(i.ToString(), name));
                    Console.WriteLine($"  - Found: {name} ({width}x{height})");
                }
            }
            catch
            {
                // Camera not available
            }
        }

        Console.WriteLine($"VideoDeviceService: Found {devices.Count} cameras via OpenCV");
        return devices;
    }

    public async Task StartCameraTestAsync(string? devicePath, Action<byte[], int, int> onFrameReceived)
    {
        await StopTestAsync();

        _onFrameReceived = onFrameReceived;
        _testCts = new CancellationTokenSource();

        // Parse device index (default to 0)
        var deviceIndex = 0;
        if (!string.IsNullOrEmpty(devicePath) && int.TryParse(devicePath, out var parsed))
        {
            deviceIndex = parsed;
        }

        Console.WriteLine($"VideoDeviceService: Starting camera capture on device {deviceIndex}...");

        try
        {
            // Use platform-specific backend to match enumeration
            // macOS: AVFoundation (matches ffmpeg avfoundation enumeration)
            // Linux: V4L2 (matches /dev/video* enumeration)
            // Windows: default (MSMF or DirectShow)
            var backend = VideoCapture.API.Any;
            if (OperatingSystem.IsMacOS())
            {
                backend = VideoCapture.API.AVFoundation;
            }
            else if (OperatingSystem.IsLinux())
            {
                backend = VideoCapture.API.V4L2;
            }

            Console.WriteLine($"VideoDeviceService: Using backend: {backend}");
            _capture = new VideoCapture(deviceIndex, backend);

            if (!_capture.IsOpened)
            {
                throw new InvalidOperationException($"Failed to open camera {deviceIndex}");
            }

            // Set capture properties
            _capture.Set(CapProp.FrameWidth, PreviewWidth);
            _capture.Set(CapProp.FrameHeight, PreviewHeight);
            _capture.Set(CapProp.Fps, TargetFps);

            var actualWidth = (int)_capture.Get(CapProp.FrameWidth);
            var actualHeight = (int)_capture.Get(CapProp.FrameHeight);
            var actualFps = _capture.Get(CapProp.Fps);

            Console.WriteLine($"VideoDeviceService: Camera opened - {actualWidth}x{actualHeight} @ {actualFps}fps");

            // Start capture loop in background
            var token = _testCts.Token;
            _captureTask = Task.Run(() => CaptureLoop(actualWidth, actualHeight, token), token);

            Console.WriteLine("VideoDeviceService: Camera capture started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoDeviceService: Failed to start camera - {ex.Message}");
            await StopTestAsync();
            throw;
        }
    }

    private void CaptureLoop(int width, int height, CancellationToken token)
    {
        using var frame = new Mat();
        var frameIntervalMs = 1000 / TargetFps;

        while (!token.IsCancellationRequested && _capture != null)
        {
            try
            {
                if (!_capture.Read(frame) || frame.IsEmpty)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Convert frame to RGB byte array
                using var rgbFrame = new Mat();
                CvInvoke.CvtColor(frame, rgbFrame, ColorConversion.Bgr2Rgb);

                // Get raw bytes
                var frameWidth = rgbFrame.Width;
                var frameHeight = rgbFrame.Height;
                var dataSize = frameWidth * frameHeight * 3; // RGB24
                var data = new byte[dataSize];

                System.Runtime.InteropServices.Marshal.Copy(rgbFrame.DataPointer, data, 0, dataSize);

                // Invoke callback
                _onFrameReceived?.Invoke(data, frameWidth, frameHeight);

                // Throttle to target FPS
                Thread.Sleep(frameIntervalMs);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VideoDeviceService: Frame capture error - {ex.Message}");
                Thread.Sleep(100);
            }
        }

        Console.WriteLine("VideoDeviceService: Capture loop ended");
    }

    public async Task StopTestAsync()
    {
        Console.WriteLine("VideoDeviceService: Stopping camera test...");

        _testCts?.Cancel();

        if (_captureTask != null)
        {
            try
            {
                await _captureTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("VideoDeviceService: Capture task did not stop in time");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            _captureTask = null;
        }

        _testCts?.Dispose();
        _testCts = null;

        if (_capture != null)
        {
            _capture.Dispose();
            _capture = null;
        }

        _onFrameReceived = null;
        Console.WriteLine("VideoDeviceService: Camera test stopped");
    }

    public void Dispose()
    {
        StopTestAsync().GetAwaiter().GetResult();
    }
}
