using System.Diagnostics;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Shared.Models;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages screen capture, encoding, and audio for screen sharing.
/// Supports native capture tools (macOS VideoToolbox, Windows MF, Linux VAAPI) and ffmpeg fallback.
/// Extracted from WebRtcService for single responsibility.
/// </summary>
public class ScreenShareManager : IAsyncDisposable
{
    private readonly NativeCaptureLocator _captureLocator;

    // Screen capture state
    private Process? _screenCaptureProcess;
    private FfmpegProcessEncoder? _screenEncoder;
    private CancellationTokenSource? _screenCts;
    private Task? _screenCaptureTask;
    private Task? _screenAudioTask;
    private bool _isScreenSharing;
    private bool _isUsingNativeCapture;
    private bool _isUsingDirectH264;
    private ScreenShareSettings? _currentSettings;

    // Screen capture constants
    private const int DefaultScreenWidth = 1920;
    private const int DefaultScreenHeight = 1080;
    private const int DefaultScreenFps = 30;

    // Screen audio encoding
    private uint _screenAudioTimestamp;
    private AudioEncoder? _screenAudioEncoder;
    private AudioFormat? _screenAudioFormat;
    private const int ScreenAudioSampleRate = 48000;
    private const int ScreenAudioChannels = 2;
    private const int AudioPacketDurationMs = 20;
    private int _screenAudioDiagCount;
    private int _screenAudioEncodeCount;

    // Audio packet magic number for native capture protocol
    private const uint SnackaCaptureAudioMagic = 0x5041434D; // "MCAP" in little-endian

    private int _sentScreenFrameCount;

    // Hardware video decoder for local preview (same pipeline as receiving)
    private IHardwareVideoDecoder? _localPreviewDecoder;
    private byte[]? _sps;
    private byte[]? _pps;
    private bool _hardwareDecoderFailed;
    private int _previewWidth;
    private int _previewHeight;

    /// <summary>
    /// Gets whether screen sharing is currently active.
    /// </summary>
    public bool IsScreenSharing => _isScreenSharing;

    /// <summary>
    /// Gets the current screen share settings.
    /// </summary>
    public ScreenShareSettings? CurrentSettings => _currentSettings;

    /// <summary>
    /// Fired when a video frame is encoded and ready to send. Args: (durationRtpUnits, encodedSample)
    /// </summary>
    public event Action<uint, byte[]>? OnVideoFrameEncoded;

    /// <summary>
    /// Fired when screen audio is encoded and ready to send. Args: (timestamp, opusData)
    /// </summary>
    public event Action<uint, byte[]>? OnAudioEncoded;

    /// <summary>
    /// Fired when a local preview frame is available. Args: (width, height, rgbData)
    /// This is only used as fallback when hardware decoding is unavailable.
    /// </summary>
    public event Action<int, int, byte[]>? OnLocalPreviewFrame;

    /// <summary>
    /// Fired when the hardware preview decoder is ready. Args: (streamType, decoder)
    /// The UI should embed the decoder's native view for zero-copy GPU rendering.
    /// </summary>
    public event Action<VideoStreamType, IHardwareVideoDecoder>? HardwarePreviewReady;

    public ScreenShareManager()
    {
        _captureLocator = new NativeCaptureLocator();
    }

    /// <summary>
    /// Sets screen sharing on or off.
    /// </summary>
    public async Task SetScreenSharingAsync(bool enabled, ScreenShareSettings? settings = null)
    {
        if (_isScreenSharing == enabled) return;

        _currentSettings = settings;
        Console.WriteLine($"ScreenShareManager: Screen Sharing = {enabled}");

        if (enabled)
        {
            await StartAsync(settings);
            _isScreenSharing = true;
        }
        else
        {
            _isScreenSharing = false;
            await StopAsync();
        }
    }

    /// <summary>
    /// Starts screen capture with the given settings.
    /// </summary>
    public async Task StartAsync(ScreenShareSettings? settings = null)
    {
        if (_screenCaptureProcess != null) return;

        var screenWidth = settings?.Resolution.Width ?? DefaultScreenWidth;
        var screenHeight = settings?.Resolution.Height ?? DefaultScreenHeight;
        var screenFps = settings?.Framerate.Fps ?? DefaultScreenFps;
        var source = settings?.Source;
        var captureAudio = settings?.IncludeAudio ?? false;

        try
        {
            Console.WriteLine($"ScreenShareManager: Starting screen capture... (source: {source?.Name ?? "default"}, {screenWidth}x{screenHeight} @ {screenFps}fps)");

            // Check if we should use native capture
            string? nativeCapturePath = null;
            string nativeCaptureArgs = "";

            if (_captureLocator.ShouldUseSnackaCaptureVideoToolbox())
            {
                nativeCapturePath = _captureLocator.GetSnackaCaptureVideoToolboxPath();
                if (nativeCapturePath != null)
                {
                    nativeCaptureArgs = _captureLocator.GetSnackaCaptureVideoToolboxArgs(source, screenWidth, screenHeight, screenFps, captureAudio);
                }
            }
            else if (_captureLocator.ShouldUseSnackaCaptureWindows())
            {
                nativeCapturePath = _captureLocator.GetSnackaCaptureWindowsPath();
                if (nativeCapturePath != null)
                {
                    nativeCaptureArgs = _captureLocator.GetSnackaCaptureWindowsArgs(source, screenWidth, screenHeight, screenFps, captureAudio);
                }
            }
            else if (_captureLocator.ShouldUseSnackaCaptureLinux())
            {
                nativeCapturePath = _captureLocator.GetSnackaCaptureLinuxPath();
                if (nativeCapturePath != null)
                {
                    nativeCaptureArgs = _captureLocator.GetSnackaCaptureLinuxArgs(source, screenWidth, screenHeight, screenFps);
                }
            }

            _isUsingNativeCapture = nativeCapturePath != null;
            _isUsingDirectH264 = (_captureLocator.ShouldUseSnackaCaptureVideoToolbox() ||
                                  _captureLocator.ShouldUseSnackaCaptureWindows() ||
                                  _captureLocator.ShouldUseSnackaCaptureLinux()) && nativeCapturePath != null;

            // Only create ffmpeg encoder if we're not getting direct H.264
            if (!_isUsingDirectH264)
            {
                var inputPixelFormat = _isUsingNativeCapture ? "nv12" : "bgr24";
                _screenEncoder = new FfmpegProcessEncoder(screenWidth, screenHeight, screenFps, VideoCodecsEnum.H264, inputPixelFormat);
                _screenEncoder.OnEncodedFrame += OnEncoderFrameEncoded;
                _screenEncoder.Start();
            }
            else
            {
                var encoderName = OperatingSystem.IsMacOS() ? "VideoToolbox" :
                                  OperatingSystem.IsWindows() ? "Media Foundation (NVENC/AMF/QSV)" :
                                  "VAAPI";
                Console.WriteLine($"ScreenShareManager: Using direct H.264 encoding from {encoderName} (bypassing ffmpeg)");
            }

            if (_isUsingNativeCapture)
            {
                await StartNativeCaptureAsync(nativeCapturePath!, nativeCaptureArgs, screenWidth, screenHeight, screenFps, captureAudio);
            }
            else
            {
                await StartFfmpegCaptureAsync(source, screenWidth, screenHeight, screenFps);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenShareManager: Failed to start screen capture: {ex.Message}");
            await StopAsync();
            throw;
        }
    }

    private async Task StartNativeCaptureAsync(string capturePath, string args, int width, int height, int fps, bool captureAudio)
    {
        // Initialize Opus encoder for screen audio if audio capture is enabled
        if (captureAudio)
        {
            _screenAudioEncoder = new AudioEncoder(includeOpus: true);
            var opusFormat = _screenAudioEncoder.SupportedFormats.FirstOrDefault(f => f.FormatName == "OPUS");
            _screenAudioTimestamp = 0;
            if (!string.IsNullOrEmpty(opusFormat.FormatName))
            {
                var stereoOpusFormat = new AudioFormat(
                    opusFormat.Codec,
                    opusFormat.FormatID,
                    opusFormat.ClockRate,
                    ScreenAudioChannels,
                    opusFormat.Parameters);
                _screenAudioFormat = stereoOpusFormat;
                Console.WriteLine($"ScreenShareManager: Screen audio encoder initialized ({stereoOpusFormat.FormatName} {stereoOpusFormat.ClockRate}Hz, {stereoOpusFormat.ChannelCount} ch stereo)");

                if (stereoOpusFormat.ClockRate != 48000)
                {
                    Console.WriteLine($"ScreenShareManager: WARNING - Screen audio encoder is {stereoOpusFormat.ClockRate}Hz, expected 48000Hz!");
                }
            }
            else
            {
                Console.WriteLine("ScreenShareManager: Warning - Opus format not available for screen audio");
            }
        }

        Console.WriteLine($"ScreenShareManager: Using native capture: {capturePath} {args}");

        _screenCaptureProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = capturePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _screenCaptureProcess.Start();

        _screenCts = new CancellationTokenSource();

        // Store dimensions for hardware decoder initialization
        _previewWidth = width;
        _previewHeight = height;

        if (_isUsingDirectH264)
        {
            _screenCaptureTask = Task.Run(() => ScreenCaptureH264Loop(_screenCts.Token, fps));
        }
        else
        {
            _screenCaptureTask = Task.Run(() => ScreenCaptureLoop(_screenCts.Token, width, height, fps));
        }
        _screenAudioTask = Task.Run(() => ScreenAudioLoop(_screenCts.Token));

        Console.WriteLine($"ScreenShareManager: Screen capture started with native capture (audio: {captureAudio}, directH264: {_isUsingDirectH264})");

        await Task.CompletedTask;
    }

    private async Task StartFfmpegCaptureAsync(ScreenCaptureSource? source, int width, int height, int fps)
    {
        var ffmpegPath = "ffmpeg";
        var (captureDevice, inputDevice, extraArgs) = _captureLocator.GetFfmpegCaptureArgs(source);

        string args;
        if (OperatingSystem.IsMacOS())
        {
            args = $"-f avfoundation -capture_cursor 1 -pixel_format uyvy422 -i \"{inputDevice}\" " +
                   $"-vf \"fps={fps},scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgr24\" " +
                   $"-f rawvideo -pix_fmt bgr24 pipe:1";
        }
        else
        {
            args = $"-f {captureDevice} {extraArgs}-framerate {fps} -i \"{inputDevice}\" " +
                   $"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgr24\" " +
                   $"-f rawvideo -pix_fmt bgr24 pipe:1";
        }

        Console.WriteLine($"ScreenShareManager: Screen capture command: {ffmpegPath} {args}");

        _screenCaptureProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _screenCaptureProcess.Start();

        _screenCts = new CancellationTokenSource();
        _screenCaptureTask = Task.Run(() => ScreenCaptureLoop(_screenCts.Token, width, height, fps));

        Console.WriteLine("ScreenShareManager: Screen capture started with ffmpeg (no audio)");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops screen capture.
    /// </summary>
    public async Task StopAsync()
    {
        Console.WriteLine("ScreenShareManager: Stopping screen capture...");

        _screenCts?.Cancel();

        if (_screenCaptureTask != null)
        {
            try
            {
                await _screenCaptureTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("ScreenShareManager: Screen capture task did not stop in time");
            }
            catch (OperationCanceledException) { }
            _screenCaptureTask = null;
        }

        if (_screenAudioTask != null)
        {
            try
            {
                await _screenAudioTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("ScreenShareManager: Screen audio task did not stop in time");
            }
            catch (OperationCanceledException) { }
            _screenAudioTask = null;
        }

        _screenCts?.Dispose();
        _screenCts = null;
        _isUsingNativeCapture = false;
        _isUsingDirectH264 = false;

        if (_screenCaptureProcess != null)
        {
            try
            {
                if (!_screenCaptureProcess.HasExited)
                {
                    _screenCaptureProcess.Kill();
                    await _screenCaptureProcess.WaitForExitAsync();
                }
            }
            catch { }
            _screenCaptureProcess.Dispose();
            _screenCaptureProcess = null;
        }

        if (_screenEncoder != null)
        {
            _screenEncoder.OnEncodedFrame -= OnEncoderFrameEncoded;
            _screenEncoder.Dispose();
            _screenEncoder = null;
        }

        // Stop hardware preview decoder
        _localPreviewDecoder?.Dispose();
        _localPreviewDecoder = null;
        _sps = null;
        _pps = null;
        _hardwareDecoderFailed = false;

        _screenAudioEncoder = null;
        _screenAudioFormat = null;
        _isScreenSharing = false;

        Console.WriteLine("ScreenShareManager: Screen capture stopped");
    }

    private void OnEncoderFrameEncoded(uint durationRtpUnits, byte[] encodedSample)
    {
        _sentScreenFrameCount++;
        if (_sentScreenFrameCount <= 5 || _sentScreenFrameCount % 100 == 0)
        {
            Console.WriteLine($"ScreenShareManager: Encoded screen frame {_sentScreenFrameCount}, size={encodedSample.Length}");
        }

        OnVideoFrameEncoded?.Invoke(durationRtpUnits, encodedSample);
    }

    private void ScreenCaptureLoop(CancellationToken token, int width, int height, int fps)
    {
        var isNv12 = _isUsingNativeCapture;
        var frameSize = isNv12 ? (width * height * 3 / 2) : (width * height * 3);
        var buffer = new byte[frameSize];
        var frameCount = 0;
        var previewSkip = Math.Max(1, fps / 15);

        Console.WriteLine($"ScreenShareManager: Screen capture loop starting - {width}x{height} @ {fps}fps (format: {(isNv12 ? "NV12" : "BGR24")})");

        try
        {
            var stream = _screenCaptureProcess?.StandardOutput.BaseStream;
            if (stream == null) return;

            while (!token.IsCancellationRequested && _screenCaptureProcess != null && !_screenCaptureProcess.HasExited)
            {
                var bytesRead = 0;
                while (bytesRead < frameSize && !token.IsCancellationRequested)
                {
                    var read = stream.Read(buffer, bytesRead, frameSize - bytesRead);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead < frameSize) break;

                frameCount++;

                _screenEncoder?.EncodeFrame(buffer);

                if (frameCount % previewSkip == 0)
                {
                    byte[] rgbData;
                    if (isNv12)
                    {
                        rgbData = VideoDecoderManager.ConvertNv12ToRgb(buffer, width, height);
                    }
                    else
                    {
                        var rgbSize = width * height * 3;
                        rgbData = new byte[rgbSize];
                        for (var i = 0; i < rgbSize; i += 3)
                        {
                            rgbData[i] = buffer[i + 2];
                            rgbData[i + 1] = buffer[i + 1];
                            rgbData[i + 2] = buffer[i];
                        }
                    }
                    OnLocalPreviewFrame?.Invoke(width, height, rgbData);
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"ScreenShareManager: Screen capture loop error: {ex.Message}");
            }
        }

        Console.WriteLine($"ScreenShareManager: Screen capture loop ended after {frameCount} frames");
    }

    private void ScreenCaptureH264Loop(CancellationToken token, int fps)
    {
        var frameCount = 0;
        var nalUnitCount = 0;
        var lengthBuffer = new byte[4];
        var rtpDuration = (uint)(90000 / fps);

        Console.WriteLine($"ScreenShareManager: Direct H.264 capture loop starting @ {fps}fps (AVCC format, hardware preview)");

        try
        {
            var stream = _screenCaptureProcess?.StandardOutput.BaseStream;
            if (stream == null) return;

            var frameData = new MemoryStream();
            var annexBPrefix = new byte[] { 0x00, 0x00, 0x00, 0x01 };
            var isKeyframeInProgress = false;

            while (!token.IsCancellationRequested && _screenCaptureProcess != null && !_screenCaptureProcess.HasExited)
            {
                var bytesRead = 0;
                while (bytesRead < 4 && !token.IsCancellationRequested)
                {
                    var read = stream.Read(lengthBuffer, bytesRead, 4 - bytesRead);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead < 4) break;

                var nalLength = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) |
                               (lengthBuffer[2] << 8) | lengthBuffer[3];

                if (nalLength <= 0 || nalLength > 10_000_000)
                {
                    Console.WriteLine($"ScreenShareManager: Invalid NAL length {nalLength}, skipping");
                    continue;
                }

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

                var nalType = nalData[0] & 0x1F;
                var isKeyframeNal = nalType == 7 || nalType == 8 || nalType == 5;

                // Store SPS/PPS for hardware decoder initialization
                if (nalType == 7) // SPS
                {
                    _sps = nalData;
                    Console.WriteLine($"ScreenShareManager: Stored SPS ({nalData.Length} bytes)");
                    TryInitializeHardwareDecoder();
                }
                else if (nalType == 8) // PPS
                {
                    _pps = nalData;
                    Console.WriteLine($"ScreenShareManager: Stored PPS ({nalData.Length} bytes)");
                    TryInitializeHardwareDecoder();
                }

                if (nalType == 7 && frameData.Length > 0)
                {
                    var frameBytes = frameData.ToArray();
                    frameCount++;

                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        Console.WriteLine($"ScreenShareManager: Sending H.264 frame {frameCount}, NALs={nalUnitCount}, size={frameBytes.Length}");
                    }

                    OnVideoFrameEncoded?.Invoke(rtpDuration, frameBytes);

                    frameData.SetLength(0);
                    isKeyframeInProgress = true;
                }
                else if (!isKeyframeNal && isKeyframeInProgress && nalType != 1)
                {
                    isKeyframeInProgress = false;
                }

                frameData.Write(annexBPrefix, 0, 4);
                frameData.Write(nalData, 0, nalData.Length);

                // Feed VCL NAL units to hardware decoder for preview
                if (_localPreviewDecoder != null && (nalType == 1 || nalType == 5))
                {
                    var isKeyframe = nalType == 5;
                    _localPreviewDecoder.DecodeAndRender(nalData, isKeyframe);
                }

                if (nalType == 1 && !isKeyframeInProgress)
                {
                    var frameBytes = frameData.ToArray();
                    frameCount++;

                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        Console.WriteLine($"ScreenShareManager: Sending H.264 P-frame {frameCount}, size={frameBytes.Length}");
                    }

                    OnVideoFrameEncoded?.Invoke(rtpDuration, frameBytes);
                    frameData.SetLength(0);
                }
                else if (nalType == 5)
                {
                    var frameBytes = frameData.ToArray();
                    frameCount++;

                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        Console.WriteLine($"ScreenShareManager: Sending H.264 I-frame {frameCount}, size={frameBytes.Length}");
                    }

                    OnVideoFrameEncoded?.Invoke(rtpDuration, frameBytes);
                    frameData.SetLength(0);
                    isKeyframeInProgress = false;
                }
            }

            if (frameData.Length > 0)
            {
                var frameBytes = frameData.ToArray();
                frameCount++;
                OnVideoFrameEncoded?.Invoke(rtpDuration, frameBytes);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"ScreenShareManager: H.264 capture loop error: {ex.Message}");
            }
        }

        Console.WriteLine($"ScreenShareManager: H.264 capture loop ended after {frameCount} frames, {nalUnitCount} NAL units");
    }

    private void ScreenAudioLoop(CancellationToken token)
    {
        Console.WriteLine("ScreenShareManager: Screen audio loop starting");
        var audioPacketCount = 0;
        var skippedBytes = 0;

        try
        {
            var stream = _screenCaptureProcess?.StandardError.BaseStream;
            if (stream == null) return;

            var scanBuffer = new byte[4];
            var scanIndex = 0;
            var headerBuffer = new byte[24];

            while (!token.IsCancellationRequested && _screenCaptureProcess != null && !_screenCaptureProcess.HasExited)
            {
                bool foundMagic = false;
                while (!foundMagic && !token.IsCancellationRequested)
                {
                    var b = stream.ReadByte();
                    if (b < 0) break;

                    scanBuffer[0] = scanBuffer[1];
                    scanBuffer[1] = scanBuffer[2];
                    scanBuffer[2] = scanBuffer[3];
                    scanBuffer[3] = (byte)b;
                    scanIndex++;

                    if (scanIndex < 4) continue;

                    var magic = BitConverter.ToUInt32(scanBuffer, 0);
                    if (magic == SnackaCaptureAudioMagic)
                    {
                        foundMagic = true;
                        Array.Copy(scanBuffer, 0, headerBuffer, 0, 4);
                    }
                    else if (scanIndex > 4)
                    {
                        skippedBytes++;
                    }
                }

                if (!foundMagic) break;

                var peekBytes = new byte[4];
                var peekRead = 0;
                while (peekRead < 4)
                {
                    var r = stream.Read(peekBytes, peekRead, 4 - peekRead);
                    if (r == 0) break;
                    peekRead += r;
                }
                if (peekRead < 4) break;
                Array.Copy(peekBytes, 0, headerBuffer, 4, 4);

                var byte4 = peekBytes[0];
                var byte5 = peekBytes[1];
                var byte6 = peekBytes[2];
                var byte7 = peekBytes[3];

                bool isV2Header = byte4 == 2 && (byte5 == 16 || byte5 == 32) && byte6 >= 1 && byte6 <= 8;

                if (audioPacketCount == 0)
                {
                    Console.WriteLine($"ScreenShareManager: Audio header bytes 4-7: [{byte4}, {byte5}, {byte6}, {byte7}], detected as {(isV2Header ? "v2" : "v1")}");
                }
                int headerSize = isV2Header ? 24 : 16;

                var headerRead = 8;
                while (headerRead < headerSize && !token.IsCancellationRequested)
                {
                    var read = stream.Read(headerBuffer, headerRead, headerSize - headerRead);
                    if (read == 0) break;
                    headerRead += read;
                }

                if (headerRead < headerSize) break;

                uint sampleCount;
                uint sampleRate;
                byte bitsPerSample;
                byte channels;
                bool isFloat;

                if (isV2Header)
                {
                    bitsPerSample = headerBuffer[5];
                    channels = headerBuffer[6];
                    isFloat = headerBuffer[7] != 0;
                    sampleCount = BitConverter.ToUInt32(headerBuffer, 8);
                    sampleRate = BitConverter.ToUInt32(headerBuffer, 12);
                }
                else
                {
                    sampleCount = BitConverter.ToUInt32(headerBuffer, 4);
                    bitsPerSample = 16;
                    channels = 2;
                    isFloat = false;
                    sampleRate = 48000;

                    if (audioPacketCount == 0)
                    {
                        Console.WriteLine("ScreenShareManager: Detected v1 audio header format - assuming 16-bit stereo 48kHz.");
                    }
                }

                if (sampleCount > 48000 * 10)
                {
                    Console.WriteLine($"ScreenShareManager: Invalid audio sample count {sampleCount}, skipping");
                    scanIndex = 0;
                    continue;
                }

                var bytesPerSample = bitsPerSample / 8;
                var bytesPerFrame = bytesPerSample * channels;
                var audioSize = (int)(sampleCount * bytesPerFrame);
                var audioBuffer = new byte[audioSize];

                var audioRead = 0;
                while (audioRead < audioSize && !token.IsCancellationRequested)
                {
                    var read = stream.Read(audioBuffer, audioRead, audioSize - audioRead);
                    if (read == 0) break;
                    audioRead += read;
                }

                if (audioRead < audioSize) break;

                audioPacketCount++;
                if (audioPacketCount <= 5 || audioPacketCount % 100 == 0)
                {
                    Console.WriteLine($"ScreenShareManager: Screen audio packet {audioPacketCount}, samples={sampleCount}, {sampleRate}Hz, {bitsPerSample}-bit, {channels}ch, {(isFloat ? "float" : "int")}, skipped={skippedBytes} bytes");
                }

                scanIndex = 0;

                ProcessScreenShareAudio(audioBuffer, sampleCount, sampleRate, bitsPerSample, channels, isFloat);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Console.WriteLine($"ScreenShareManager: Screen audio loop error: {ex.Message}");
            }
        }

        Console.WriteLine($"ScreenShareManager: Screen audio loop ended after {audioPacketCount} packets, skipped {skippedBytes} bytes");
    }

    private void ProcessScreenShareAudio(byte[] pcmData, uint sampleCount, uint sampleRate, byte bitsPerSample, byte channelCount, bool isFloat)
    {
        if (_screenAudioEncoder == null || !_screenAudioFormat.HasValue || pcmData.Length == 0) return;

        var format = _screenAudioFormat.Value;

        int frameCount = (int)sampleCount;
        var stereoSamples = new short[frameCount * 2];

        for (int i = 0; i < frameCount && (i * 4 + 3) < pcmData.Length; i++)
        {
            short left = (short)(pcmData[i * 4] | (pcmData[i * 4 + 1] << 8));
            short right = (short)(pcmData[i * 4 + 2] | (pcmData[i * 4 + 3] << 8));
            stereoSamples[i * 2] = left;
            stereoSamples[i * 2 + 1] = right;
        }

        _screenAudioDiagCount++;
        if (_screenAudioDiagCount <= 3)
        {
            short peak = 0;
            for (int i = 0; i < Math.Min(200, stereoSamples.Length); i++)
            {
                if (Math.Abs(stereoSamples[i]) > Math.Abs(peak)) peak = stereoSamples[i];
            }
            Console.WriteLine($"ScreenShareManager: Screen audio diag #{_screenAudioDiagCount}: {frameCount} stereo frames @ {sampleRate}Hz {bitsPerSample}-bit -> {stereoSamples.Length} samples stereo, peak={peak}");
        }

        const int framesPerPacket = ScreenAudioSampleRate * AudioPacketDurationMs / 1000;
        const int samplesPerPacket = framesPerPacket * ScreenAudioChannels;

        int offset = 0;
        while (offset + samplesPerPacket <= stereoSamples.Length)
        {
            var packetSamples = new short[samplesPerPacket];
            Array.Copy(stereoSamples, offset, packetSamples, 0, samplesPerPacket);

            try
            {
                var opusData = _screenAudioEncoder.EncodeAudio(packetSamples, format);

                if (opusData != null && opusData.Length > 0)
                {
                    _screenAudioEncodeCount++;
                    if (_screenAudioEncodeCount <= 5)
                    {
                        Console.WriteLine($"ScreenShareManager: Screen audio encode #{_screenAudioEncodeCount}: {samplesPerPacket} stereo samples -> {opusData.Length} bytes Opus");
                    }

                    OnAudioEncoded?.Invoke(_screenAudioTimestamp, opusData);

                    _screenAudioTimestamp += (uint)framesPerPacket;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ScreenShareManager: Error encoding screen audio: {ex.Message}");
            }

            offset += samplesPerPacket;
        }
    }

    /// <summary>
    /// Tries to initialize the hardware decoder when both SPS and PPS are available.
    /// </summary>
    private void TryInitializeHardwareDecoder()
    {
        if (_localPreviewDecoder != null || _hardwareDecoderFailed)
            return;

        if (_sps == null || _pps == null)
            return;

        if (!HardwareVideoDecoderFactory.IsAvailable())
        {
            Console.WriteLine("ScreenShareManager: Hardware decoding not available for preview");
            _hardwareDecoderFailed = true;
            return;
        }

        Console.WriteLine("ScreenShareManager: Creating hardware decoder for local preview...");
        var decoder = HardwareVideoDecoderFactory.Create();
        if (decoder == null)
        {
            Console.WriteLine("ScreenShareManager: Failed to create hardware decoder");
            _hardwareDecoderFailed = true;
            return;
        }

        if (decoder.Initialize(_previewWidth, _previewHeight, _sps, _pps))
        {
            _localPreviewDecoder = decoder;
            Console.WriteLine($"ScreenShareManager: Hardware preview decoder ready ({_previewWidth}x{_previewHeight})");

            // Notify UI that hardware decoder is ready for embedding
            HardwarePreviewReady?.Invoke(VideoStreamType.ScreenShare, decoder);
        }
        else
        {
            decoder.Dispose();
            _hardwareDecoderFailed = true;
            Console.WriteLine("ScreenShareManager: Failed to initialize hardware decoder for preview");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
