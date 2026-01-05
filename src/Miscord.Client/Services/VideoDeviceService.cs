using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Abstractions;

namespace Miscord.Client.Services;

public interface IVideoDeviceService : IDisposable
{
    IReadOnlyList<VideoDeviceInfo> GetCameraDevices();

    bool IsTestingCamera { get; }

    Task StartCameraTestAsync(string? devicePath, Action<byte[], int, int> onFrameReceived);
    Task StopTestAsync();
}

public record VideoDeviceInfo(string Path, string Name);

public class VideoDeviceService : IVideoDeviceService
{
    private static bool _ffmpegInitialized;
    private static readonly object _ffmpegInitLock = new();

    private readonly ISettingsStore? _settingsStore;
    private FFmpegCameraSource? _testCameraSource;
    private Action<byte[], int, int>? _onFrameReceived;
    private CancellationTokenSource? _testCts;

    public bool IsTestingCamera => _testCameraSource != null;

    public VideoDeviceService(ISettingsStore? settingsStore = null)
    {
        _settingsStore = settingsStore;
        EnsureFFmpegInitialized();
    }

    private static void EnsureFFmpegInitialized()
    {
        if (_ffmpegInitialized) return;

        lock (_ffmpegInitLock)
        {
            if (_ffmpegInitialized) return;

            try
            {
                // Initialize FFmpeg - it will try to find libraries in standard locations
                // On macOS with Homebrew: /opt/homebrew/lib or /usr/local/lib
                // On Windows with winget: typically in PATH
                // On Linux: /usr/lib or /usr/local/lib
                FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_WARNING);
                Console.WriteLine("VideoDeviceService: FFmpeg initialized successfully");
                _ffmpegInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VideoDeviceService: Failed to initialize FFmpeg - {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("  Make sure FFmpeg is installed:");
                Console.WriteLine("    macOS: brew install ffmpeg");
                Console.WriteLine("    Windows: winget install ffmpeg");
                Console.WriteLine("    Linux: apt install ffmpeg");
                _ffmpegInitialized = true; // Mark as attempted even if failed
            }
        }
    }

    public IReadOnlyList<VideoDeviceInfo> GetCameraDevices()
    {
        try
        {
            EnsureFFmpegInitialized();
            Console.WriteLine("VideoDeviceService: Getting camera devices...");

            var devices = FFmpegCameraManager.GetCameraDevices();
            if (devices == null)
            {
                return Array.Empty<VideoDeviceInfo>();
            }
            var result = devices.Select(d => new VideoDeviceInfo(d.Path, d.Name)).ToList();

            Console.WriteLine($"VideoDeviceService: Found {result.Count} camera devices");
            foreach (var device in result)
            {
                Console.WriteLine($"  - Camera: {device.Name} ({device.Path})");
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoDeviceService: Failed to get camera devices - {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<VideoDeviceInfo>();
        }
    }

    public async Task StartCameraTestAsync(string? devicePath, Action<byte[], int, int> onFrameReceived)
    {
        await StopTestAsync();

        _onFrameReceived = onFrameReceived;
        _testCts = new CancellationTokenSource();

        try
        {
            EnsureFFmpegInitialized();

            // If no device path specified, use the first available camera
            if (string.IsNullOrEmpty(devicePath))
            {
                var devices = FFmpegCameraManager.GetCameraDevices();
                if (devices == null || devices.Count == 0)
                {
                    throw new InvalidOperationException("No camera devices found");
                }
                devicePath = devices[0].Path;
            }

            _testCameraSource = new FFmpegCameraSource(devicePath);

            // Subscribe to video frames
            _testCameraSource.OnVideoSourceRawSample += OnVideoFrame;

            // Subscribe to error events
            _testCameraSource.OnVideoSourceError += (error) =>
            {
                Console.WriteLine($"VideoDeviceService: Camera error: {error}");
            };

            await _testCameraSource.StartVideo();
            Console.WriteLine($"VideoDeviceService: Started camera test on device: {devicePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoDeviceService: Failed to start camera test - {ex.Message}");
            await StopTestAsync();
            throw;
        }
    }

    private void OnVideoFrame(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
    {
        // Forward the frame to the UI
        // The sample is in the specified pixel format (usually I420 or RGB24)
        _onFrameReceived?.Invoke(sample, width, height);
    }

    public async Task StopTestAsync()
    {
        _testCts?.Cancel();
        _testCts?.Dispose();
        _testCts = null;

        if (_testCameraSource != null)
        {
            try
            {
                _testCameraSource.OnVideoSourceRawSample -= OnVideoFrame;
                await _testCameraSource.CloseVideo();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VideoDeviceService: Error closing camera - {ex.Message}");
            }
            _testCameraSource = null;
        }

        _onFrameReceived = null;
        Console.WriteLine("VideoDeviceService: Stopped camera test");
    }

    public void Dispose()
    {
        StopTestAsync().GetAwaiter().GetResult();
    }
}
