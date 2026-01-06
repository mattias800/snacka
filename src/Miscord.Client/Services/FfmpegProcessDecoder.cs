using System.Diagnostics;
using SIPSorceryMedia.Abstractions;

namespace Miscord.Client.Services;

/// <summary>
/// Video decoder that uses FFmpeg as a subprocess for decoding.
/// Works reliably on macOS where the native FFmpeg bindings don't work.
/// Outputs raw RGB frames.
/// </summary>
public class FfmpegProcessDecoder : IDisposable
{
    private Process? _ffmpegProcess;
    private BinaryWriter? _inputWriter;
    private readonly int _width;
    private readonly int _height;
    private readonly VideoCodecsEnum _codec;
    private readonly object _writeLock = new();
    private bool _isRunning;
    private Task? _outputReaderTask;
    private readonly CancellationTokenSource _cts = new();
    private int _frameCount;
    private readonly int _frameSize;

    /// <summary>
    /// Fired when a decoded frame is ready. Args: (width, height, rgbData)
    /// </summary>
    public event Action<int, int, byte[]>? OnDecodedFrame;

    public FfmpegProcessDecoder(int width, int height, VideoCodecsEnum codec = VideoCodecsEnum.H264)
    {
        _width = width;
        _height = height;
        _codec = codec;
        _frameSize = width * height * 3; // RGB24
    }

    public void Start()
    {
        if (_isRunning) return;

        var codecArg = _codec switch
        {
            VideoCodecsEnum.VP8 => "libvpx",
            VideoCodecsEnum.H264 => "h264",
            _ => "h264"
        };

        // Decode H264/VP8 Annex B input to raw RGB24 output
        // Ultra low-latency: minimal probing, no buffering
        // Scale to target size while preserving aspect ratio
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
                       $"-f {(_codec == VideoCodecsEnum.H264 ? "h264" : "ivf")} -i pipe:0 " +
                       $"-vf \"scale={_width}:{_height}:force_original_aspect_ratio=decrease,pad={_width}:{_height}:(ow-iw)/2:(oh-ih)/2\" " +
                       $"-f rawvideo -pix_fmt rgb24 pipe:1",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _ffmpegProcess = new Process { StartInfo = startInfo };
        _ffmpegProcess.Start();
        _inputWriter = new BinaryWriter(_ffmpegProcess.StandardInput.BaseStream);
        _isRunning = true;

        // Start reading decoded output
        _outputReaderTask = Task.Run(ReadDecodedOutputAsync);

        // Log stderr line by line for debugging
        Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _ffmpegProcess.StandardError.ReadLineAsync()) != null)
                {
                    if (line.Contains("error") || line.Contains("Error") || line.Contains("Invalid") || line.Contains("failed"))
                    {
                        Console.WriteLine($"FfmpegProcessDecoder ERROR: {line}");
                    }
                }
            }
            catch { }
        });

        Console.WriteLine($"FfmpegProcessDecoder: Started ({_width}x{_height}, {_codec})");
    }

    public void DecodeFrame(byte[] encodedData)
    {
        if (!_isRunning || _inputWriter == null) return;

        try
        {
            lock (_writeLock)
            {
                _inputWriter.Write(encodedData);
                _inputWriter.Flush();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FfmpegProcessDecoder: Write error - {ex.Message}");
        }
    }

    private async Task ReadDecodedOutputAsync()
    {
        var frameBuffer = new byte[_frameSize];
        var stream = _ffmpegProcess!.StandardOutput.BaseStream;
        var bytesInBuffer = 0;

        try
        {
            while (!_cts.Token.IsCancellationRequested && _ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                // Use a timeout to periodically check process status
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));

                int bytesRead;
                try
                {
                    var bytesToRead = _frameSize - bytesInBuffer;
                    bytesRead = await stream.ReadAsync(frameBuffer, bytesInBuffer, bytesToRead, readCts.Token);
                }
                catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                {
                    // Read timeout - check if process is still alive
                    if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
                    {
                        Console.WriteLine("FfmpegProcessDecoder: Process exited during read");
                        break;
                    }
                    continue; // Try again
                }

                if (bytesRead == 0) break;

                bytesInBuffer += bytesRead;

                // Emit complete frames
                while (bytesInBuffer >= _frameSize)
                {
                    var frameData = new byte[_frameSize];
                    Buffer.BlockCopy(frameBuffer, 0, frameData, 0, _frameSize);

                    _frameCount++;
                    if (_frameCount <= 5 || _frameCount % 100 == 0)
                    {
                        Console.WriteLine($"FfmpegProcessDecoder: Decoded frame {_frameCount}, {_width}x{_height}");
                    }

                    OnDecodedFrame?.Invoke(_width, _height, frameData);

                    // Shift remaining bytes
                    var remaining = bytesInBuffer - _frameSize;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(frameBuffer, _frameSize, frameBuffer, 0, remaining);
                    }
                    bytesInBuffer = remaining;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FfmpegProcessDecoder: Read error - {ex.Message}");
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();

        try
        {
            _inputWriter?.Dispose();
            _ffmpegProcess?.StandardInput.Close();
            _ffmpegProcess?.WaitForExit(1000);
            _ffmpegProcess?.Kill();
            _ffmpegProcess?.Dispose();
        }
        catch { }

        _outputReaderTask?.Wait(1000);
        _cts.Dispose();

        Console.WriteLine("FfmpegProcessDecoder: Disposed");
    }
}
