using System.Diagnostics;
using SIPSorceryMedia.Abstractions;

namespace Miscord.Client.Services;

/// <summary>
/// Video encoder that uses FFmpeg as a subprocess for encoding.
/// Works reliably on macOS where the native FFmpeg bindings don't work.
/// Outputs raw VP8/H264 frames suitable for WebRTC.
/// </summary>
public class FfmpegProcessEncoder : IDisposable
{
    private Process? _ffmpegProcess;
    private BinaryWriter? _inputWriter;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly VideoCodecsEnum _codec;
    private readonly string _inputPixelFormat;
    private readonly object _lock = new();
    private bool _isRunning;
    private Task? _outputReaderTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _ivfHeaderSkipped;
    private readonly MemoryStream _buffer = new();
    private int _frameCount;
    private readonly List<byte> _h264Buffer = new();

    /// <summary>
    /// Fired when an encoded frame is ready. The data is raw VP8/H264 frame data.
    /// </summary>
    public event Action<uint, byte[]>? OnEncodedFrame;

    /// <summary>
    /// Creates a new FFmpeg encoder.
    /// </summary>
    /// <param name="width">Video width</param>
    /// <param name="height">Video height</param>
    /// <param name="fps">Frames per second</param>
    /// <param name="codec">Output codec (VP8 or H264)</param>
    /// <param name="inputPixelFormat">Input pixel format: "nv12" for hardware path, "bgr24" for software</param>
    public FfmpegProcessEncoder(int width, int height, int fps = 15, VideoCodecsEnum codec = VideoCodecsEnum.VP8, string inputPixelFormat = "bgr24")
    {
        _width = width;
        _height = height;
        _fps = fps;
        _codec = codec;
        _inputPixelFormat = inputPixelFormat;
    }

    private static string? _detectedEncoder;

    private static string GetH264EncoderArgs()
    {
        // Cache the detected encoder to avoid repeated probing
        if (_detectedEncoder != null)
            return _detectedEncoder;

        if (OperatingSystem.IsMacOS())
        {
            // macOS: VideoToolbox is always available
            // -g 10 = keyframe every 10 frames (~0.33s at 30fps) for fast stream start when viewers join
            // -bf 0 = no B-frames (critical for low-latency streaming - B-frames require reordering)
            // -profile:v baseline = baseline profile doesn't support B-frames
            // -b:v 6000k = higher bitrate for screen share text clarity
            // -bufsize 500k = buffer for smoother streaming
            _detectedEncoder = "-c:v h264_videotoolbox -realtime 1 -profile:v baseline -g 10 -bf 0 -b:v 6000k -maxrate 8000k -bufsize 500k";
            Console.WriteLine("FfmpegProcessEncoder: Using h264_videotoolbox (Apple Silicon/Intel)");
            return _detectedEncoder;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows: Try hardware encoders in order of preference
            // 1. NVIDIA NVENC (h264_nvenc)
            // 2. AMD AMF (h264_amf)
            // 3. Intel QuickSync (h264_qsv)
            // 4. Software fallback (libx264)
            // -g 60 = keyframe every 60 frames (~2s at 30fps) so new viewers get a keyframe quickly

            var hwEncoders = new[]
            {
                ("h264_nvenc", "-c:v h264_nvenc -preset p4 -tune ll -g 60 -bf 0 -b:v 3000k -maxrate 3000k -bufsize 1500k", "NVIDIA NVENC"),
                ("h264_amf", "-c:v h264_amf -quality speed -g 60 -bf 0 -b:v 3000k -maxrate 3000k -bufsize 1500k", "AMD AMF"),
                ("h264_qsv", "-c:v h264_qsv -preset veryfast -g 60 -bf 0 -b:v 3000k -maxrate 3000k -bufsize 1500k", "Intel QuickSync"),
            };

            foreach (var (encoder, args, name) in hwEncoders)
            {
                if (IsEncoderAvailable(encoder))
                {
                    _detectedEncoder = args;
                    Console.WriteLine($"FfmpegProcessEncoder: Using {encoder} ({name})");
                    return _detectedEncoder;
                }
            }
        }

        if (OperatingSystem.IsLinux())
        {
            // Linux: Try VAAPI first, then software
            if (IsEncoderAvailable("h264_vaapi"))
            {
                _detectedEncoder = "-vaapi_device /dev/dri/renderD128 -c:v h264_vaapi -g 60 -bf 0 -b:v 3000k -maxrate 3000k -bufsize 1500k";
                Console.WriteLine("FfmpegProcessEncoder: Using h264_vaapi (Linux VA-API)");
                return _detectedEncoder;
            }
        }

        // Fallback to software encoding
        _detectedEncoder = "-c:v libx264 -preset ultrafast -tune zerolatency -g 15 -bf 0 -b:v 1500k -maxrate 1500k -bufsize 750k";
        Console.WriteLine("FfmpegProcessEncoder: Using libx264 (software)");
        return _detectedEncoder;
    }

    private static bool IsEncoderAvailable(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-hide_banner -encoders",
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

    public void Start()
    {
        if (_isRunning) return;

        var codecArg = _codec switch
        {
            VideoCodecsEnum.VP8 => "-c:v libvpx -deadline realtime -cpu-used 8 -b:v 500k",
            // H264 encoding - use hardware acceleration when available
            VideoCodecsEnum.H264 => GetH264EncoderArgs(),
            _ => "-c:v libvpx -deadline realtime -cpu-used 8 -b:v 500k"
        };

        // Use IVF format for VP8 (simple container we can parse)
        var outputFormat = _codec == VideoCodecsEnum.VP8 ? "ivf" : "h264";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            // Ultra low-latency: no buffering, flush packets immediately
            // NV12 is native to VideoToolbox - zero conversion overhead
            Arguments = $"-fflags nobuffer -flags low_delay -strict experimental " +
                       $"-f rawvideo -pixel_format {_inputPixelFormat} -video_size {_width}x{_height} -framerate {_fps} -i pipe:0 " +
                       $"{codecArg} -flush_packets 1 -f {outputFormat} pipe:1",
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
        _ivfHeaderSkipped = false;

        // Start reading encoded output
        _outputReaderTask = Task.Run(ReadEncodedOutputAsync);

        // Log stderr line by line for debugging (ReadToEndAsync blocks until process exits)
        Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _ffmpegProcess.StandardError.ReadLineAsync()) != null)
                {
                    if (line.Contains("error") || line.Contains("Error") || line.Contains("Invalid") || line.Contains("failed"))
                    {
                        Console.WriteLine($"FfmpegProcessEncoder ERROR: {line}");
                    }
                }
            }
            catch { }
        });

        Console.WriteLine($"FfmpegProcessEncoder: Started ({_width}x{_height} @ {_fps}fps, {_codec}, input={_inputPixelFormat})");
    }

    public void EncodeFrame(byte[] bgrData)
    {
        if (!_isRunning || _inputWriter == null) return;

        try
        {
            lock (_lock)
            {
                _inputWriter.Write(bgrData);
                _inputWriter.Flush();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FfmpegProcessEncoder: Write error - {ex.Message}");
        }
    }

    private async Task ReadEncodedOutputAsync()
    {
        var buffer = new byte[65536];
        var stream = _ffmpegProcess!.StandardOutput.BaseStream;
        var totalBytesRead = 0;

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead == 0) break;

                totalBytesRead += bytesRead;

                // Add to buffer
                _buffer.Write(buffer, 0, bytesRead);

                // Parse frames based on codec
                if (_codec == VideoCodecsEnum.VP8)
                {
                    ParseIvfFrames();
                }
                else if (_codec == VideoCodecsEnum.H264)
                {
                    ParseH264Frames();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FfmpegProcessEncoder: Read error - {ex.Message}");
        }
    }

    private void ParseIvfFrames()
    {
        var data = _buffer.ToArray();
        var offset = 0;

        // Skip IVF file header (32 bytes) on first read
        if (!_ivfHeaderSkipped && data.Length >= 32)
        {
            offset = 32;
            _ivfHeaderSkipped = true;
        }

        // Parse IVF frame headers (12 bytes each: 4 bytes size + 8 bytes timestamp)
        while (offset + 12 <= data.Length)
        {
            var frameSize = BitConverter.ToInt32(data, offset);
            var timestamp = BitConverter.ToInt64(data, offset + 4);

            if (offset + 12 + frameSize > data.Length)
            {
                // Not enough data for complete frame, wait for more
                break;
            }

            // Extract raw frame data
            var frameData = new byte[frameSize];
            Buffer.BlockCopy(data, offset + 12, frameData, 0, frameSize);

            // Calculate RTP timestamp units (90kHz clock)
            var durationRtpUnits = (uint)(90000 / _fps);

            _frameCount++;
            if (_frameCount <= 5 || _frameCount % 100 == 0)
            {
                Console.WriteLine($"FfmpegProcessEncoder: Emitting VP8 frame {_frameCount}, size={frameSize} bytes");
            }

            OnEncodedFrame?.Invoke(durationRtpUnits, frameData);

            offset += 12 + frameSize;
        }

        // Keep remaining data in buffer
        if (offset > 0)
        {
            var remaining = data.Length - offset;
            _buffer.SetLength(0);
            if (remaining > 0)
            {
                _buffer.Write(data, offset, remaining);
            }
        }
    }

    private void ParseH264Frames()
    {
        var data = _buffer.ToArray();
        var durationRtpUnits = (uint)(90000 / _fps);

        // H264 Annex B format: NAL units separated by start codes (0x00 0x00 0x00 0x01 or 0x00 0x00 0x01)
        // For low latency, emit each complete NAL unit immediately instead of waiting for frame boundaries

        // Find all NAL unit start positions
        var nalStarts = new List<int>();
        for (int i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 0 && data[i + 3] == 1)
                {
                    nalStarts.Add(i);
                    i += 3;
                }
                else if (data[i + 2] == 1)
                {
                    nalStarts.Add(i);
                    i += 2;
                }
            }
        }

        if (nalStarts.Count < 2)
        {
            return; // Need at least 2 start codes to know where one NAL ends
        }

        // Emit each complete NAL unit immediately (except the last one which may be incomplete)
        var lastEmittedEnd = 0;
        for (int i = 0; i < nalStarts.Count - 1; i++)
        {
            var nalStart = nalStarts[i];
            var nalEnd = nalStarts[i + 1];
            var nalSize = nalEnd - nalStart;

            if (nalSize > 0)
            {
                var nalData = new byte[nalSize];
                Buffer.BlockCopy(data, nalStart, nalData, 0, nalSize);

                _frameCount++;
                if (_frameCount <= 5 || _frameCount % 100 == 0)
                {
                    var headerOffset = (data[nalStart + 2] == 1) ? 3 : 4;
                    var nalType = data[nalStart + headerOffset] & 0x1F;
                    Console.WriteLine($"FfmpegProcessEncoder: Emitting H264 NAL {_frameCount}, type={nalType}, size={nalSize} bytes");
                }

                OnEncodedFrame?.Invoke(durationRtpUnits, nalData);
                lastEmittedEnd = nalEnd;
            }
        }

        // Keep the last incomplete NAL unit in buffer
        _buffer.SetLength(0);
        if (lastEmittedEnd < data.Length)
        {
            _buffer.Write(data, lastEmittedEnd, data.Length - lastEmittedEnd);
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
        _buffer.Dispose();

        Console.WriteLine("FfmpegProcessEncoder: Disposed");
    }
}
