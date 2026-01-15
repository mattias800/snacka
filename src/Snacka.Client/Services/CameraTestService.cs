using System.Diagnostics;
using Snacka.Client.Services.WebRtc;

namespace Snacka.Client.Services;

/// <summary>
/// Service for testing camera with dual preview (raw capture + H.264 encoded).
/// Used in video settings to show users what their camera looks like and
/// how the encoded stream differs from the raw capture.
/// </summary>
public class CameraTestService : IDisposable
{
    private readonly NativeCaptureLocator _captureLocator;
    private Process? _captureProcess;
    private FfmpegProcessDecoder? _h264Decoder;
    private Task? _stderrParserTask;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private int _rawFrameCount;
    private int _encodedFrameCount;

    /// <summary>
    /// Fired when a raw preview frame is received (NV12 format for GPU rendering).
    /// Parameters: width, height, nv12Data
    /// </summary>
    public event Action<int, int, byte[]>? OnRawNv12FrameReceived;

    /// <summary>
    /// Fired when a decoded H.264 frame is received (NV12 format for GPU rendering).
    /// Parameters: width, height, nv12Data
    /// </summary>
    public event Action<int, int, byte[]>? OnEncodedNv12FrameReceived;

    /// <summary>
    /// Fired when an error occurs during capture/decode.
    /// </summary>
    public event Action<string>? OnError;

    public bool IsRunning => _isRunning;
    public int RawFrameCount => _rawFrameCount;
    public int EncodedFrameCount => _encodedFrameCount;

    public CameraTestService()
    {
        _captureLocator = new NativeCaptureLocator();
    }

    /// <summary>
    /// Starts camera test with dual preview output.
    /// </summary>
    /// <param name="cameraId">Camera device ID or index</param>
    /// <param name="height">Video height (480, 720, 1080). Width calculated assuming 16:9 aspect ratio.</param>
    /// <param name="fps">Frame rate</param>
    /// <param name="bitrateMbps">Encoding bitrate in Mbps</param>
    public async Task StartAsync(string cameraId, int height, int fps, int bitrateMbps)
    {
        if (_isRunning)
        {
            await StopAsync();
        }

        _rawFrameCount = 0;
        _encodedFrameCount = 0;
        _cts = new CancellationTokenSource();

        // Calculate width assuming 16:9 aspect ratio (most common for webcams)
        // The GPU renderer's aspect ratio correction will handle any mismatch
        var width = CalculateWidthFor16x9(height);

        // Get native capture tool path
        var capturePath = _captureLocator.GetNativeCameraCapturePath();
        if (capturePath == null)
        {
            OnError?.Invoke("Native capture tool not found. Ensure SnackaCaptureVideoToolbox (macOS), SnackaCaptureWindows, or SnackaCaptureLinux is built.");
            return;
        }

        // Build arguments with preview enabled
        var args = _captureLocator.GetNativeCameraCaptureArgs(cameraId, width, height, fps, bitrateMbps, outputPreview: true, previewFps: 15);

        Console.WriteLine($"CameraTestService: Starting capture: {capturePath} {args}");

        try
        {
            // Start H.264 decoder first (with NV12 output for GPU rendering)
            _h264Decoder = new FfmpegProcessDecoder(width, height, outputFormat: DecoderOutputFormat.Nv12);
            _h264Decoder.OnDecodedFrame += OnH264FrameDecoded;
            _h264Decoder.Start();

            // Start native capture process
            var startInfo = new ProcessStartInfo
            {
                FileName = capturePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _captureProcess = new Process { StartInfo = startInfo };
            _captureProcess.Start();

            _isRunning = true;

            // Start parsing stderr for preview frames
            var token = _cts.Token;
            _stderrParserTask = Task.Run(() => ParseStderrLoop(width, height, token), token);

            // Start piping stdout (H.264) to decoder
            _ = Task.Run(() => PipeH264ToDecoder(token), token);

            Console.WriteLine($"CameraTestService: Capture started, pid={_captureProcess.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CameraTestService: Failed to start - {ex.Message}");
            OnError?.Invoke($"Failed to start capture: {ex.Message}");
            await StopAsync();
        }
    }

    private void ParseStderrLoop(int expectedWidth, int expectedHeight, CancellationToken token)
    {
        if (_captureProcess == null) return;

        try
        {
            var parser = new StderrPacketParser(_captureProcess.StandardError.BaseStream);

            parser.OnPreviewPacket += packet =>
            {
                _rawFrameCount++;

                if (_rawFrameCount <= 5 || _rawFrameCount % 100 == 0)
                {
                    Console.WriteLine($"CameraTestService: Raw preview frame {_rawFrameCount}, {packet.Width}x{packet.Height}");
                }

                // Fire NV12 event directly (no CPU conversion - GPU will handle YUV→RGB)
                OnRawNv12FrameReceived?.Invoke(packet.Width, packet.Height, packet.PixelData);
            };

            parser.OnLogMessage += message =>
            {
                Console.WriteLine($"CameraTestService (native log): {message}");
            };

            parser.ParseLoop(token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"CameraTestService: Stderr parser error - {ex.Message}");
            }
        }
    }

    private async Task PipeH264ToDecoder(CancellationToken token)
    {
        if (_captureProcess == null || _h264Decoder == null) return;

        try
        {
            var stdout = _captureProcess.StandardOutput.BaseStream;
            var readBuffer = new byte[64 * 1024];
            long totalBytesIn = 0;
            long totalBytesOut = 0;

            // Buffer for accumulating AVCC data and converting to Annex B
            var avccBuffer = new MemoryStream();

            while (!token.IsCancellationRequested)
            {
                var bytesRead = await stdout.ReadAsync(readBuffer, 0, readBuffer.Length, token);
                if (bytesRead == 0)
                {
                    Console.WriteLine("CameraTestService: H.264 stream ended");
                    break;
                }

                totalBytesIn += bytesRead;

                // Accumulate data
                avccBuffer.Write(readBuffer, 0, bytesRead);

                // Convert AVCC to Annex B and send complete NAL units to decoder
                var annexBData = ConvertAvccToAnnexB(avccBuffer);
                if (annexBData.Length > 0)
                {
                    totalBytesOut += annexBData.Length;
                    if (totalBytesOut <= 10000 || totalBytesOut % 100000 < annexBData.Length)
                    {
                        Console.WriteLine($"CameraTestService: Converted {bytesRead} AVCC bytes to {annexBData.Length} Annex B bytes (total in: {totalBytesIn}, out: {totalBytesOut})");
                    }

                    _h264Decoder.DecodeFrame(annexBData);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"CameraTestService: H.264 pipe error - {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Converts H.264 data from AVCC format (4-byte length prefix) to Annex B format (start codes).
    /// Native capture tools output AVCC, but FFmpeg expects Annex B.
    /// </summary>
    private static byte[] ConvertAvccToAnnexB(MemoryStream avccBuffer)
    {
        var avccData = avccBuffer.ToArray();
        var annexBStream = new MemoryStream();

        // Annex B start code (4 bytes)
        var startCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

        int offset = 0;
        int lastProcessedOffset = 0;

        while (offset + 4 <= avccData.Length)
        {
            // Read 4-byte big-endian NAL unit length
            int nalLength = (avccData[offset] << 24) |
                           (avccData[offset + 1] << 16) |
                           (avccData[offset + 2] << 8) |
                           avccData[offset + 3];

            // Sanity check: NAL length should be reasonable (< 10MB)
            if (nalLength <= 0 || nalLength > 10 * 1024 * 1024)
            {
                // Invalid length - might be corrupted data or we're not at a NAL boundary
                // Skip this byte and try again
                offset++;
                continue;
            }

            // Check if we have the complete NAL unit
            if (offset + 4 + nalLength > avccData.Length)
            {
                // Incomplete NAL unit - wait for more data
                break;
            }

            // Write Annex B start code
            annexBStream.Write(startCode, 0, startCode.Length);

            // Write NAL unit data (skip the 4-byte length prefix)
            annexBStream.Write(avccData, offset + 4, nalLength);

            offset += 4 + nalLength;
            lastProcessedOffset = offset;
        }

        // Keep unprocessed bytes in the buffer for next iteration
        if (lastProcessedOffset > 0)
        {
            var remaining = avccData.Length - lastProcessedOffset;
            avccBuffer.SetLength(0);
            if (remaining > 0)
            {
                avccBuffer.Write(avccData, lastProcessedOffset, remaining);
            }
        }

        return annexBStream.ToArray();
    }

    private void OnH264FrameDecoded(int width, int height, byte[] nv12Data)
    {
        _encodedFrameCount++;

        if (_encodedFrameCount <= 5 || _encodedFrameCount % 100 == 0)
        {
            Console.WriteLine($"CameraTestService: Decoded H.264 frame {_encodedFrameCount}, {width}x{height}");
        }

        // Fire NV12 event directly (GPU will handle YUV→RGB)
        OnEncodedNv12FrameReceived?.Invoke(width, height, nv12Data);
    }

    /// <summary>
    /// Stops camera test.
    /// </summary>
    public async Task StopAsync()
    {
        Console.WriteLine("CameraTestService: Stopping...");

        _isRunning = false;
        _cts?.Cancel();

        // Wait for parser task
        if (_stderrParserTask != null)
        {
            try
            {
                await _stderrParserTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("CameraTestService: Parser task did not stop in time");
            }
            catch (OperationCanceledException) { }
        }

        // Stop decoder
        _h264Decoder?.Dispose();
        _h264Decoder = null;

        // Stop capture process
        if (_captureProcess != null)
        {
            try
            {
                if (!_captureProcess.HasExited)
                {
                    _captureProcess.Kill();
                    await _captureProcess.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CameraTestService: Error stopping capture - {ex.Message}");
            }

            _captureProcess.Dispose();
            _captureProcess = null;
        }

        _cts?.Dispose();
        _cts = null;

        Console.WriteLine($"CameraTestService: Stopped (raw frames: {_rawFrameCount}, encoded frames: {_encodedFrameCount})");
    }

    /// <summary>
    /// Calculates width for a given height assuming 16:9 aspect ratio.
    /// Common webcam heights: 480 → 853, 720 → 1280, 1080 → 1920
    /// </summary>
    private static int CalculateWidthFor16x9(int height)
    {
        // 16:9 aspect ratio: width = height * 16 / 9
        // Round to nearest even number for video encoding compatibility
        var width = (int)Math.Round(height * 16.0 / 9.0);
        return width % 2 == 0 ? width : width + 1;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
