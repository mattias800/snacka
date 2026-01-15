using System.Diagnostics;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages camera capture and H.264 encoding.
/// Supports native capture tools (macOS VideoToolbox) and OpenCV/FFmpeg fallback.
/// Extracted from WebRtcService for single responsibility.
/// </summary>
public class CameraManager : IAsyncDisposable
{
    private readonly ISettingsStore? _settingsStore;
    private readonly NativeCaptureLocator _captureLocator;

    // OpenCV capture (fallback)
    private VideoCapture? _videoCapture;
    private FfmpegProcessEncoder? _processEncoder;

    // Native capture
    private Process? _cameraProcess;
    private bool _isUsingNativeCapture;

    private CancellationTokenSource? _videoCts;
    private Task? _videoCaptureTask;
    private bool _isCameraOn;
    private int _sentCameraFrameCount;

    // Video capture settings
    private const int VideoWidth = 640;
    private const int VideoHeight = 480;
    private const int VideoFps = 15;
    private const int VideoBitrateMbps = 2;

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
    /// Starts camera capture and encoding.
    /// </summary>
    public async Task StartAsync()
    {
        if (_videoCapture != null || _cameraProcess != null) return;

        try
        {
            // Get video device from settings
            var devicePath = _settingsStore?.Settings.VideoDevice ?? "0";

            // Check if native camera capture is available
            if (_captureLocator.IsNativeCameraCaptureAvailable())
            {
                await StartNativeCaptureAsync(devicePath);
            }
            else
            {
                await StartOpenCVCaptureAsync(devicePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CameraManager: Failed to start video capture - {ex.Message}");
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts camera capture using native tools (VideoToolbox on macOS, etc.)
    /// </summary>
    private async Task StartNativeCaptureAsync(string devicePath)
    {
        var nativeCapturePath = _captureLocator.GetNativeCameraCapturePath();
        if (nativeCapturePath == null)
        {
            throw new InvalidOperationException("Native camera capture path not found");
        }

        var args = _captureLocator.GetNativeCameraCaptureArgs(devicePath, VideoWidth, VideoHeight, VideoFps, VideoBitrateMbps);

        Console.WriteLine($"CameraManager: Starting native camera capture: {nativeCapturePath} {args}");

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
        _isUsingNativeCapture = true;

        // Start reading stderr for logs (don't block on it)
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cameraProcess.HasExited)
                {
                    var line = await _cameraProcess.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        Console.WriteLine($"CameraManager [native]: {line}");
                    }
                }
            }
            catch { }
        });

        // Start H.264 capture loop
        _videoCts = new CancellationTokenSource();
        _videoCaptureTask = Task.Run(() => CameraH264Loop(_videoCts.Token, VideoFps));

        Console.WriteLine("CameraManager: Native camera capture started (direct H.264)");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Starts camera capture using OpenCV (fallback path).
    /// </summary>
    private async Task StartOpenCVCaptureAsync(string devicePath)
    {
        var deviceIndex = 0;
        if (int.TryParse(devicePath, out var parsed))
        {
            deviceIndex = parsed;
        }

        // Use AVFoundation on macOS, V4L2 on Linux for correct device mapping
        var backend = VideoCapture.API.Any;
        if (OperatingSystem.IsMacOS())
        {
            backend = VideoCapture.API.AVFoundation;
        }
        else if (OperatingSystem.IsLinux())
        {
            backend = VideoCapture.API.V4L2;
        }

        Console.WriteLine($"CameraManager: Starting OpenCV video capture on device {deviceIndex} with backend {backend}");
        _videoCapture = new VideoCapture(deviceIndex, backend);

        if (!_videoCapture.IsOpened)
        {
            throw new InvalidOperationException($"Failed to open camera {deviceIndex}");
        }

        // Set capture properties
        _videoCapture.Set(CapProp.FrameWidth, VideoWidth);
        _videoCapture.Set(CapProp.FrameHeight, VideoHeight);
        _videoCapture.Set(CapProp.Fps, VideoFps);

        var actualWidth = (int)_videoCapture.Get(CapProp.FrameWidth);
        var actualHeight = (int)_videoCapture.Get(CapProp.FrameHeight);

        Console.WriteLine($"CameraManager: Video capture opened - {actualWidth}x{actualHeight}");

        // Create FFmpeg process encoder for H264 (hardware accelerated on most platforms)
        _processEncoder = new FfmpegProcessEncoder(actualWidth, actualHeight, VideoFps, VideoCodecsEnum.H264);
        _processEncoder.OnEncodedFrame += OnEncoderFrameEncoded;
        _processEncoder.Start();
        Console.WriteLine("CameraManager: Video encoder created for H264");

        // Start capture loop
        _videoCts = new CancellationTokenSource();
        _videoCaptureTask = Task.Run(() => CaptureLoop(actualWidth, actualHeight, _videoCts.Token));

        Console.WriteLine("CameraManager: OpenCV video capture started");
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

        _videoCts?.Dispose();
        _videoCts = null;
        _isUsingNativeCapture = false;

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

        // Dispose video encoder (OpenCV path)
        if (_processEncoder != null)
        {
            _processEncoder.OnEncodedFrame -= OnEncoderFrameEncoded;
            _processEncoder.Dispose();
            _processEncoder = null;
        }

        // Dispose OpenCV capture
        if (_videoCapture != null)
        {
            _videoCapture.Dispose();
            _videoCapture = null;
        }

        _isCameraOn = false;
        Console.WriteLine("CameraManager: Video capture stopped");
    }

    private void OnEncoderFrameEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        _sentCameraFrameCount++;
        if (_sentCameraFrameCount <= 5 || _sentCameraFrameCount % 100 == 0)
        {
            Console.WriteLine($"CameraManager: Encoded camera frame {_sentCameraFrameCount}, size={encodedSample.Length}");
        }

        OnFrameEncoded?.Invoke(durationRtpUnits, encodedSample);
    }

    private void CaptureLoop(int width, int height, CancellationToken token)
    {
        using var frame = new Mat();
        var frameIntervalMs = 1000 / VideoFps;
        var frameCount = 0;

        Console.WriteLine($"CameraManager: Video capture loop starting - target {width}x{height} @ {VideoFps}fps");

        while (!token.IsCancellationRequested && _videoCapture != null)
        {
            try
            {
                if (!_videoCapture.Read(frame) || frame.IsEmpty)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Get frame dimensions and raw BGR bytes (OpenCV captures in BGR format)
                var frameWidth = frame.Width;
                var frameHeight = frame.Height;
                var dataSize = frameWidth * frameHeight * 3;

                frameCount++;
                if (frameCount == 1 || frameCount % 100 == 0)
                {
                    Console.WriteLine($"CameraManager: Captured frame {frameCount} - {frameWidth}x{frameHeight}");
                }

                // Get BGR data from OpenCV frame
                var bgrData = new byte[dataSize];
                System.Runtime.InteropServices.Marshal.Copy(frame.DataPointer, bgrData, 0, dataSize);

                // Send frame to encoder (encoding happens asynchronously in FfmpegProcessEncoder)
                if (_processEncoder != null)
                {
                    try
                    {
                        // Send to FFmpeg process for encoding
                        _processEncoder.EncodeFrame(bgrData);
                    }
                    catch (Exception encodeEx)
                    {
                        if (frameCount <= 5 || frameCount % 100 == 0)
                        {
                            Console.WriteLine($"CameraManager: Encoding error on frame {frameCount}: {encodeEx.Message}");
                        }
                    }
                }

                // Fire local preview event (convert BGR to RGB)
                if (OnLocalFrameCaptured != null && frameCount % 2 == 0) // Every other frame for performance
                {
                    var rgbData = ColorSpaceConverter.BgrToRgb(bgrData, frameWidth, frameHeight);
                    OnLocalFrameCaptured.Invoke(frameWidth, frameHeight, rgbData);
                }

                Thread.Sleep(frameIntervalMs);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CameraManager: Video capture error - {ex.Message}");
                Console.WriteLine($"CameraManager: Stack trace: {ex.StackTrace}");
                Thread.Sleep(100);
            }
        }

        Console.WriteLine($"CameraManager: Video capture loop ended after {frameCount} frames");
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
