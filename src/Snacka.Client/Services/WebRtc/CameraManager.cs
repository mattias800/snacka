using System.Diagnostics;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages camera capture and H.264 encoding using native platform tools.
/// Requires SnackaCaptureVideoToolbox (macOS), SnackaCaptureWindows, or SnackaCaptureLinux.
/// </summary>
public class CameraManager : IAsyncDisposable
{
    private readonly ISettingsStore? _settingsStore;
    private readonly NativeCaptureLocator _captureLocator;

    // Native capture
    private Process? _cameraProcess;
    private Task? _stderrTask;

    private CancellationTokenSource? _videoCts;
    private Task? _videoCaptureTask;
    private bool _isCameraOn;
    private int _sentCameraFrameCount;

    // Default video capture settings (used if settings not available)
    private const int DefaultVideoWidth = 640;
    private const int DefaultVideoHeight = 360;
    private const int DefaultVideoFps = 15;
    private const int DefaultVideoBitrateMbps = 2;

    /// <summary>
    /// Gets video settings from user preferences or uses defaults.
    /// Width is calculated from height assuming 16:9 aspect ratio.
    /// </summary>
    private (int width, int height, int fps, int bitrateMbps) GetVideoSettings()
    {
        if (_settingsStore == null)
        {
            return (DefaultVideoWidth, DefaultVideoHeight, DefaultVideoFps, DefaultVideoBitrateMbps);
        }

        var settings = _settingsStore.Settings;

        // Use height from settings, calculate width assuming 16:9 aspect ratio
        var height = settings.CameraHeight > 0 ? settings.CameraHeight : DefaultVideoHeight;
        var width = CalculateWidthFor16x9(height);

        var fps = settings.CameraFramerate > 0 ? settings.CameraFramerate : DefaultVideoFps;
        var bitrate = settings.CameraBitrateMbps > 0 ? settings.CameraBitrateMbps : DefaultVideoBitrateMbps;

        return (width, height, fps, bitrate);
    }

    /// <summary>
    /// Calculates width for a given height assuming 16:9 aspect ratio.
    /// </summary>
    private static int CalculateWidthFor16x9(int height)
    {
        // 16:9 aspect ratio: width = height * 16 / 9
        // Round to nearest even number for video encoding compatibility
        var width = (int)Math.Round(height * 16.0 / 9.0);
        return width % 2 == 0 ? width : width + 1;
    }

    /// <summary>
    /// Gets whether the camera is currently active.
    /// </summary>
    public bool IsCameraOn => _isCameraOn;

    /// <summary>
    /// Fired when a video frame has been encoded. Args: (durationRtpUnits, encodedSample)
    /// WebRtcService subscribes to this to send frames to connections.
    /// </summary>
    public event Action<uint, byte[]>? OnFrameEncoded;

    /// <summary>
    /// Fired when a local video frame is captured (for self-preview). Args: (width, height, rgbData)
    /// </summary>
    public event Action<int, int, byte[]>? OnLocalFrameCaptured;

    public CameraManager(ISettingsStore? settingsStore)
    {
        _settingsStore = settingsStore;
        _captureLocator = new NativeCaptureLocator();
    }

    /// <summary>
    /// Sets the camera on or off.
    /// </summary>
    public async Task SetCameraAsync(bool enabled)
    {
        if (_isCameraOn == enabled) return;

        Console.WriteLine($"CameraManager: Camera = {enabled}");

        if (enabled)
        {
            // Start capture BEFORE setting state - if it throws, state remains false
            await StartAsync();
            _isCameraOn = true;
        }
        else
        {
            _isCameraOn = false;
            await StopAsync();
        }
    }

    /// <summary>
    /// Starts camera capture and encoding using native tools.
    /// </summary>
    public async Task StartAsync()
    {
        if (_cameraProcess != null) return;

        // Get video device from settings
        var devicePath = _settingsStore?.Settings.VideoDevice ?? "0";

        // Require native camera capture
        if (!_captureLocator.IsNativeCameraCaptureAvailable())
        {
            throw new InvalidOperationException(
                "Native camera capture tool not available. " +
                "Please build SnackaCaptureVideoToolbox (macOS), SnackaCaptureWindows, or SnackaCaptureLinux.");
        }

        try
        {
            await StartNativeCaptureAsync(devicePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CameraManager: Failed to start video capture - {ex.Message}");
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts camera capture using native tools (VideoToolbox on macOS, Media Foundation on Windows, VAAPI on Linux)
    /// </summary>
    private async Task StartNativeCaptureAsync(string devicePath)
    {
        var nativeCapturePath = _captureLocator.GetNativeCameraCapturePath();
        if (nativeCapturePath == null)
        {
            throw new InvalidOperationException("Native camera capture path not found");
        }

        var (width, height, fps, bitrateMbps) = GetVideoSettings();
        var args = _captureLocator.GetNativeCameraCaptureArgs(devicePath, width, height, fps, bitrateMbps);

        Console.WriteLine($"CameraManager: Starting native camera capture ({width}x{height}@{fps}fps, {bitrateMbps}Mbps): {nativeCapturePath} {args}");

        _cameraProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nativeCapturePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _cameraProcess.Start();

        _videoCts = new CancellationTokenSource();

        // Start stderr packet parser for preview frames
        _stderrTask = Task.Run(() =>
        {
            try
            {
                var stream = _cameraProcess.StandardError.BaseStream;
                var parser = new StderrPacketParser(stream);

                parser.OnPreviewPacket += packet =>
                {
                    // Convert NV12 to RGB for OnLocalFrameCaptured
                    if (packet.Format == PreviewFormat.NV12)
                    {
                        var rgbData = VideoDecoderManager.ConvertNv12ToRgb(packet.PixelData, packet.Width, packet.Height);
                        OnLocalFrameCaptured?.Invoke(packet.Width, packet.Height, rgbData);
                    }
                };

                parser.OnLogMessage += msg =>
                {
                    Console.WriteLine($"CameraManager [native]: {msg}");
                };

                parser.ParseLoop(_videoCts.Token);
            }
            catch (Exception ex)
            {
                if (!_videoCts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"CameraManager: Stderr parser error: {ex.Message}");
                }
            }
        });

        // Start H.264 capture loop
        _videoCaptureTask = Task.Run(() => CameraH264Loop(_videoCts.Token, fps));

        Console.WriteLine("CameraManager: Native camera capture started (direct H.264 + preview)");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops camera capture and encoding.
    /// </summary>
    public async Task StopAsync()
    {
        Console.WriteLine("CameraManager: Stopping video capture...");

        _videoCts?.Cancel();

        if (_videoCaptureTask != null)
        {
            try
            {
                await _videoCaptureTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("CameraManager: Video capture task did not stop in time");
            }
            catch (OperationCanceledException) { }
            _videoCaptureTask = null;
        }

        if (_stderrTask != null)
        {
            try
            {
                await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            _stderrTask = null;
        }

        _videoCts?.Dispose();
        _videoCts = null;

        // Stop native capture process
        if (_cameraProcess != null)
        {
            try
            {
                if (!_cameraProcess.HasExited)
                {
                    _cameraProcess.Kill();
                    await _cameraProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CameraManager: Error stopping native capture process: {ex.Message}");
            }
            _cameraProcess.Dispose();
            _cameraProcess = null;
        }

        _isCameraOn = false;
        Console.WriteLine("CameraManager: Video capture stopped");
    }

    /// <summary>
    /// Reads H.264 NAL units from native camera capture process (AVCC format).
    /// Converts to Annex B format and fires OnFrameEncoded.
    /// </summary>
    private void CameraH264Loop(CancellationToken token, int fps)
    {
        var frameCount = 0;
        var nalUnitCount = 0;
        var lengthBuffer = new byte[4];
        var rtpDuration = (uint)(90000 / fps);

        Console.WriteLine($"CameraManager: Direct H.264 capture loop starting @ {fps}fps (AVCC format)");

        try
        {
            var stream = _cameraProcess?.StandardOutput.BaseStream;
            if (stream == null) return;

            var frameData = new MemoryStream();
            var annexBPrefix = new byte[] { 0x00, 0x00, 0x00, 0x01 };
            var isKeyframeInProgress = false;

            while (!token.IsCancellationRequested && _cameraProcess != null && !_cameraProcess.HasExited)
            {
                // Read 4-byte NAL length prefix (big-endian)
                var bytesRead = 0;
                while (bytesRead < 4 && !token.IsCancellationRequested)
                {
                    var read = stream.Read(lengthBuffer, bytesRead, 4 - bytesRead);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead < 4) break;

                // Parse NAL length (big-endian)
                var nalLength = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) |
                               (lengthBuffer[2] << 8) | lengthBuffer[3];

                if (nalLength <= 0 || nalLength > 10_000_000)
                {
                    Console.WriteLine($"CameraManager: Invalid NAL length {nalLength}, skipping");
                    continue;
                }

                // Read NAL data
                var nalData = new byte[nalLength];
                bytesRead = 0;
                while (bytesRead < nalLength && !token.IsCancellationRequested)
                {
                    var read = stream.Read(nalData, bytesRead, nalLength - bytesRead);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead < nalLength) break;

                nalUnitCount++;

                // Parse NAL type
                var nalType = nalData[0] & 0x1F;
                var isKeyframeNal = nalType == 7 || nalType == 8 || nalType == 5;

                // NAL type 7 = SPS (start of new keyframe sequence)
                if (nalType == 7 && frameData.Length > 0)
                {
                    // Emit previous frame
                    var frameBytes = frameData.ToArray();
                    frameCount++;

                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        Console.WriteLine($"CameraManager: Sending H.264 frame {frameCount}, NALs={nalUnitCount}, size={frameBytes.Length}");
                    }

                    _sentCameraFrameCount++;
                    OnFrameEncoded?.Invoke(rtpDuration, frameBytes);

                    frameData.SetLength(0);
                    isKeyframeInProgress = true;
                }
                else if (!isKeyframeNal && isKeyframeInProgress && nalType != 1)
                {
                    isKeyframeInProgress = false;
                }

                // Write NAL with Annex B prefix
                frameData.Write(annexBPrefix, 0, 4);
                frameData.Write(nalData, 0, nalData.Length);

                // NAL type 1 = P-frame (non-IDR slice)
                if (nalType == 1 && !isKeyframeInProgress)
                {
                    var frameBytes = frameData.ToArray();
                    frameCount++;

                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        Console.WriteLine($"CameraManager: Sending H.264 P-frame {frameCount}, size={frameBytes.Length}");
                    }

                    _sentCameraFrameCount++;
                    OnFrameEncoded?.Invoke(rtpDuration, frameBytes);
                    frameData.SetLength(0);
                }
                // NAL type 5 = IDR (keyframe)
                else if (nalType == 5)
                {
                    var frameBytes = frameData.ToArray();
                    frameCount++;

                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        Console.WriteLine($"CameraManager: Sending H.264 I-frame {frameCount}, size={frameBytes.Length}");
                    }

                    _sentCameraFrameCount++;
                    OnFrameEncoded?.Invoke(rtpDuration, frameBytes);
                    frameData.SetLength(0);
                    isKeyframeInProgress = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"CameraManager: H.264 loop error: {ex.Message}");
            }
        }

        Console.WriteLine($"CameraManager: H.264 capture loop ended after {frameCount} frames");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
