using System.Diagnostics;
using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Services;

/// <summary>
/// Output pixel format for the decoder.
/// </summary>
public enum DecoderOutputFormat
{
    /// <summary>RGB24 format (3 bytes per pixel) - for software rendering</summary>
    Rgb24,
    /// <summary>NV12 format (1.5 bytes per pixel, YUV 4:2:0) - for GPU rendering</summary>
    Nv12
}

/// <summary>
/// Video decoder that uses FFmpeg as a subprocess for decoding.
/// Works reliably on macOS where the native FFmpeg bindings don't work.
/// Supports both RGB24 (software) and NV12 (GPU) output formats.
/// </summary>
public class FfmpegProcessDecoder : IDisposable
{
    private Process? _ffmpegProcess;
    private BinaryWriter? _inputWriter;
    private readonly int _width;
    private readonly int _height;
    private readonly VideoCodecsEnum _codec;
    private readonly DecoderOutputFormat _outputFormat;
    private readonly object _writeLock = new();
    private bool _isRunning;
    private Task? _outputReaderTask;
    private readonly CancellationTokenSource _cts = new();
    private int _frameCount;
    private readonly int _frameSize;
    private int _timeoutCount;

    // Frame dropping for latency control
    private int _pendingInputFrames;
    private int _droppedFrameCount;
    private const int MaxPendingFrames = 10; // Drop frames if more than 10 pending (allows ~330ms buffer at 30fps)

    /// <summary>
    /// Fired when a decoded frame is ready.
    /// For RGB24: rgbData is width * height * 3 bytes
    /// For NV12: nv12Data is width * height * 1.5 bytes (Y plane + UV plane)
    /// </summary>
    public event Action<int, int, byte[]>? OnDecodedFrame;

    /// <summary>
    /// Gets the output format of this decoder.
    /// </summary>
    public DecoderOutputFormat OutputFormat => _outputFormat;

    private static string? _ffmpegPath;

    public FfmpegProcessDecoder(int width, int height, VideoCodecsEnum codec = VideoCodecsEnum.H264, DecoderOutputFormat outputFormat = DecoderOutputFormat.Rgb24)
    {
        _width = width;
        _height = height;
        _codec = codec;
        _outputFormat = outputFormat;

        // Calculate frame size based on output format
        _frameSize = outputFormat switch
        {
            DecoderOutputFormat.Rgb24 => width * height * 3,
            DecoderOutputFormat.Nv12 => width * height * 3 / 2,
            _ => width * height * 3
        };
    }

    /// <summary>
    /// Gets the path to the ffmpeg executable. Searches common paths on macOS/Linux
    /// since app bundles don't have Homebrew in PATH.
    /// </summary>
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
                    Console.WriteLine($"FfmpegProcessDecoder: Found ffmpeg at {path}");
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
                    Console.WriteLine($"FfmpegProcessDecoder: Found ffmpeg at {path}");
                    return _ffmpegPath;
                }
            }
        }

        _ffmpegPath = "ffmpeg";
        Console.WriteLine("FfmpegProcessDecoder: Using ffmpeg from PATH");
        return _ffmpegPath;
    }

    public void Start()
    {
        if (_isRunning) return;

        // Output pixel format
        var pixFmt = _outputFormat switch
        {
            DecoderOutputFormat.Nv12 => "nv12",
            _ => "rgb24"
        };

        // Build decoder arguments with hardware acceleration where available
        var (hwAccelArgs, decoderName) = GetHardwareDecoderArgs(pixFmt);
        var inputFormat = _codec == VideoCodecsEnum.H264 ? "h264" : "ivf";

        // Decode H264/VP8 to raw output
        // Ultra low-latency settings:
        // -probesize 32 = minimum bytes to probe for lowest latency
        // -analyzeduration 0 = don't analyze duration
        // -fflags nobuffer+fastseek = no buffering, fast seeking
        // -flags low_delay = low delay decoding mode
        // -fps_mode passthrough = no frame rate conversion
        var ffmpegArgs = $"-probesize 32 -analyzeduration 0 " +
                        $"-fflags nobuffer+fastseek -flags low_delay " +
                        $"{hwAccelArgs}" +
                        $"-f {inputFormat} -framerate 30 -i pipe:0 " +
                        $"-fps_mode passthrough -f rawvideo -pix_fmt {pixFmt} -s {_width}x{_height} pipe:1";

        var ffmpegPath = GetFfmpegPath();
        Console.WriteLine($"FfmpegProcessDecoder: Command: {ffmpegPath} {ffmpegArgs}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = ffmpegArgs,
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
                var vtErrorCount = 0;
                while ((line = await _ffmpegProcess.StandardError.ReadLineAsync()) != null)
                {
                    lineCount++;

                    // Skip repetitive VideoToolbox errors (these are expected during init and at keyframes)
                    // Just count them and log a summary
                    if (line.Contains("vt decoder cb:") ||
                        line.Contains("hardware accelerator failed") ||
                        line.Contains("Error submitting packet to decoder"))
                    {
                        vtErrorCount++;
                        // Log only first occurrence
                        if (vtErrorCount == 1)
                        {
                            Console.WriteLine($"FfmpegProcessDecoder stderr: {line} (subsequent similar messages will be suppressed)");
                        }
                        continue;
                    }

                    // Log first 20 lines and any errors/warnings (excluding VT errors already handled)
                    if (lineCount <= 20 ||
                        line.Contains("error") || line.Contains("Error") ||
                        line.Contains("Invalid") || line.Contains("failed") ||
                        line.Contains("missing") || line.Contains("corrupt") ||
                        line.Contains("non-existing") || line.Contains("Warning"))
                    {
                        Console.WriteLine($"FfmpegProcessDecoder stderr: {line}");
                    }
                }

                if (vtErrorCount > 1)
                {
                    Console.WriteLine($"FfmpegProcessDecoder: Suppressed {vtErrorCount - 1} additional VideoToolbox reconfig messages");
                }
            }
            catch { }
        });

        Console.WriteLine($"FfmpegProcessDecoder: Started ({_width}x{_height}, {_codec}, decoder={decoderName}, output={_outputFormat})");
    }

    /// <summary>
    /// Gets hardware decoder arguments based on platform.
    /// Returns (ffmpegArgs, decoderName) tuple.
    /// NOTE: Hardware decoding disabled due to VideoToolbox -12909 errors with some H.264 streams.
    /// Software decoding is more reliable and still fast enough for real-time video.
    /// </summary>
    private (string args, string name) GetHardwareDecoderArgs(string outputPixFmt)
    {
        // Note: outputPixFmt parameter reserved for future use if needed
        _ = outputPixFmt;

        // Hardware decoding disabled - VideoToolbox has compatibility issues with some H.264 streams
        // causing -12909 errors. Software decoding is more reliable.
        return ("", "software");
    }

    private static bool IsHwAccelAvailable(string hwaccel)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetFfmpegPath(),
                Arguments = "-hide_banner -hwaccels",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            return output.Contains(hwaccel);
        }
        catch
        {
            return false;
        }
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

        // Drop frames if decoder is falling behind (latency control)
        var pending = Interlocked.Increment(ref _pendingInputFrames);
        if (pending > MaxPendingFrames)
        {
            Interlocked.Decrement(ref _pendingInputFrames);
            _droppedFrameCount++;
            if (_droppedFrameCount <= 5 || _droppedFrameCount % 100 == 0)
            {
                Console.WriteLine($"FfmpegProcessDecoder: Dropping frame {_inputFrameCount} (pending={pending}, dropped={_droppedFrameCount}) - decoder falling behind");
            }
            return;
        }

        if (_inputFrameCount <= 5 || _inputFrameCount % 100 == 0)
        {
            Console.WriteLine($"FfmpegProcessDecoder: Writing frame {_inputFrameCount}, size={encodedData.Length}, pending={pending}");
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
            Interlocked.Decrement(ref _pendingInputFrames);
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
                    // if (bytesRead > 0 && (bytesInBuffer < 1000 || bytesInBuffer % 1000000 < bytesRead))
                    // {
                    //     Console.WriteLine($"FfmpegProcessDecoder: Read {bytesRead} bytes, buffer={bytesInBuffer + bytesRead}/{_frameSize}");
                    // }
                }
                catch (OperationCanceledException) when (!_cts.Token.IsCancellationRequested)
                {
                    // Read timeout - check if process is still alive
                    _timeoutCount++;
                    // Only log first 3 timeouts and then every 100th to reduce noise
                    if (_timeoutCount <= 3 || _timeoutCount % 100 == 0)
                    {
                        Console.WriteLine($"FfmpegProcessDecoder: Read timeout #{_timeoutCount}, process alive={_ffmpegProcess != null && !_ffmpegProcess.HasExited}");
                    }
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
                    Interlocked.Decrement(ref _pendingInputFrames);

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
