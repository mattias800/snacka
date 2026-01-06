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
        // Low-latency settings: single thread, no buffering, flush immediately
        // -framerate 30 tells FFmpeg expected input rate for better timing
        // -vsync 0 disables frame rate conversion/buffering
        var inputFormat = _codec == VideoCodecsEnum.H264 ? "h264" : "ivf";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-threads 1 -fflags nobuffer -flags low_delay -framerate 30 " +
                       $"-f {inputFormat} -i pipe:0 " +
                       $"-vsync 0 -threads 1 -f rawvideo -pix_fmt rgb24 -s {_width}x{_height} pipe:1",
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

        // Log stderr line by line for debugging (log all output, not just errors)
        Task.Run(async () =>
        {
            try
            {
                string? line;
                var lineCount = 0;
                while ((line = await _ffmpegProcess.StandardError.ReadLineAsync()) != null)
                {
                    lineCount++;
                    // Log first 20 lines and any errors/warnings
                    if (lineCount <= 20 ||
                        line.Contains("error") || line.Contains("Error") ||
                        line.Contains("Invalid") || line.Contains("failed") ||
                        line.Contains("missing") || line.Contains("corrupt") ||
                        line.Contains("non-existing") || line.Contains("Warning"))
                    {
                        Console.WriteLine($"FfmpegProcessDecoder stderr: {line}");
                    }
                }
            }
            catch { }
        });

        Console.WriteLine($"FfmpegProcessDecoder: Started ({_width}x{_height}, {_codec})");
    }

    private int _inputFrameCount;

    public void DecodeFrame(byte[] encodedData)
    {
        if (!_isRunning || _inputWriter == null)
        {
            Console.WriteLine($"FfmpegProcessDecoder: DecodeFrame skipped - isRunning={_isRunning}, hasWriter={_inputWriter != null}");
            return;
        }

        _inputFrameCount++;
        if (_inputFrameCount <= 5 || _inputFrameCount % 100 == 0)
        {
            Console.WriteLine($"FfmpegProcessDecoder: Writing frame {_inputFrameCount}, size={encodedData.Length}");
        }

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
                    if (bytesRead > 0 && (bytesInBuffer < 1000 || bytesInBuffer % 1000000 < bytesRead))
                    {
                        Console.WriteLine($"FfmpegProcessDecoder: Read {bytesRead} bytes, buffer={bytesInBuffer + bytesRead}/{_frameSize}");
                    }
                }
                catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                {
                    // Read timeout - check if process is still alive
                    Console.WriteLine($"FfmpegProcessDecoder: Read timeout, process alive={_ffmpegProcess != null && !_ffmpegProcess.HasExited}");
                    if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
                    {
                        Console.WriteLine("FfmpegProcessDecoder: Process exited during read");
                        break;
                    }
                    continue; // Try again
                }

                if (bytesRead == 0)
                {
                    Console.WriteLine("FfmpegProcessDecoder: Read returned 0, EOF");
                    break;
                }

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
