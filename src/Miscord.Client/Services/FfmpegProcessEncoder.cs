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

    public FfmpegProcessEncoder(int width, int height, int fps = 15, VideoCodecsEnum codec = VideoCodecsEnum.VP8)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _codec = codec;
    }

    public void Start()
    {
        if (_isRunning) return;

        var codecArg = _codec switch
        {
            VideoCodecsEnum.VP8 => "-c:v libvpx -deadline realtime -cpu-used 8 -b:v 500k",
            VideoCodecsEnum.H264 => "-c:v libx264 -preset ultrafast -tune zerolatency -b:v 500k",
            _ => "-c:v libvpx -deadline realtime -cpu-used 8 -b:v 500k"
        };

        // Use IVF format for VP8 (simple container we can parse)
        var outputFormat = _codec == VideoCodecsEnum.VP8 ? "ivf" : "h264";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-f rawvideo -pixel_format bgr24 -video_size {_width}x{_height} -framerate {_fps} -i pipe:0 " +
                       $"{codecArg} -f {outputFormat} pipe:1",
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

        // Log stderr for debugging
        Task.Run(async () =>
        {
            try
            {
                var stderr = await _ffmpegProcess.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Console.WriteLine($"FfmpegProcessEncoder stderr: {stderr.Substring(0, Math.Min(500, stderr.Length))}");
                }
            }
            catch { }
        });

        Console.WriteLine($"FfmpegProcessEncoder: Started ({_width}x{_height} @ {_fps}fps, {_codec})");
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
                if (totalBytesRead < 1000 || totalBytesRead % 100000 < bytesRead)
                {
                    Console.WriteLine($"FfmpegProcessEncoder: Read {bytesRead} bytes (total: {totalBytesRead})");
                }

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
        // We need to find frame boundaries - a new frame starts with:
        // - SPS (NAL type 7)
        // - PPS (NAL type 8)
        // - IDR slice (NAL type 5)
        // - Non-IDR slice (NAL type 1)

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

        if (nalStarts.Count == 0)
        {
            return; // No complete NAL units yet
        }

        // Group NAL units into frames (Access Units)
        // A frame typically contains: [SPS, PPS,] IDR/non-IDR slice(s)
        var frameStart = 0;
        var lastEmittedEnd = 0;

        for (int i = 1; i < nalStarts.Count; i++)
        {
            var nalStart = nalStarts[i];
            var headerOffset = (data[nalStart + 2] == 1) ? 3 : 4;
            var nalType = data[nalStart + headerOffset] & 0x1F;

            // New frame starts with slice NAL (type 1 or 5) after non-slice NALs
            // Or if we see another slice after a slice
            if (nalType == 1 || nalType == 5)
            {
                // Check if previous NAL was also a slice - if so, emit frame
                var prevNalStart = nalStarts[i - 1];
                var prevHeaderOffset = (data[prevNalStart + 2] == 1) ? 3 : 4;
                var prevNalType = data[prevNalStart + prevHeaderOffset] & 0x1F;

                if (prevNalType == 1 || prevNalType == 5)
                {
                    // Emit previous frame
                    var frameEnd = nalStart;
                    var frameSize = frameEnd - frameStart;
                    if (frameSize > 0)
                    {
                        var frameData = new byte[frameSize];
                        Buffer.BlockCopy(data, frameStart, frameData, 0, frameSize);

                        _frameCount++;
                        if (_frameCount <= 5 || _frameCount % 100 == 0)
                        {
                            Console.WriteLine($"FfmpegProcessEncoder: Emitting H264 frame {_frameCount}, size={frameSize} bytes");
                        }

                        OnEncodedFrame?.Invoke(durationRtpUnits, frameData);
                        lastEmittedEnd = frameEnd;
                    }
                    frameStart = nalStart;
                }
            }
        }

        // Keep unprocessed data in buffer
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
