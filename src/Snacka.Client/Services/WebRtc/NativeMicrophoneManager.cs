using System.Diagnostics;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Snacka.Client.Services.Audio;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages native microphone capture using platform-specific tools.
/// Spawns native capture process and reads MCAP audio packets from stderr.
/// Applies AGC and noise gate, then encodes to Opus.
/// </summary>
public class NativeMicrophoneManager : IAsyncDisposable
{
    private readonly NativeCaptureLocator _locator;
    private readonly ISettingsStore? _settingsStore;

    private Process? _captureProcess;
    private CancellationTokenSource? _cts;
    private Task? _audioReadTask;
    private AudioEncoder? _audioEncoder;
    private AudioFormat? _audioFormat;
    private bool _isRunning;
    private bool _isMuted;
    private bool _isSpeaking;
    private DateTime _lastAudioActivity = DateTime.MinValue;
    private Timer? _speakingTimer;

    // Audio processor for AEC (noise suppression handled by native capture)
    private IAudioProcessor? _audioProcessor;
    private short[]? _processorOutputBuffer;

    // Use centralized MCAP parser
    private const int McapHeaderSize = McapParser.HeaderSize;

    // Voice activity detection constants
    private const int SpeakingTimeoutMs = 200;
    private const int SpeakingCheckIntervalMs = 50;

    // Automatic Gain Control (AGC) for consistent microphone levels
    private readonly AudioGainProcessor _gainProcessor = new();

    /// <summary>
    /// Gets whether the user is currently speaking (voice activity detected).
    /// </summary>
    public bool IsSpeaking => _isSpeaking;

    /// <summary>
    /// Gets whether the microphone is muted.
    /// </summary>
    public bool IsMuted => _isMuted;

    /// <summary>
    /// Gets whether native microphone capture is running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Fired when speaking state changes.
    /// </summary>
    public event Action<bool>? SpeakingChanged;

    /// <summary>
    /// Fired when encoded audio data is available.
    /// Parameters: durationRtpUnits, encodedSample
    /// </summary>
    public event Action<uint, byte[]>? OnEncodedSample;

    /// <summary>
    /// Fired when raw PCM audio is available (after AGC/gate processing, before encoding).
    /// Parameters: samples (16-bit stereo), normalizedRms (0-1)
    /// Used by settings test for level meter and loopback.
    /// </summary>
    public event Action<short[], float>? OnRawSample;

    public NativeMicrophoneManager(NativeCaptureLocator locator, ISettingsStore? settingsStore)
    {
        _locator = locator;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// Sets the audio processor for AEC.
    /// Note: Noise suppression is handled by native capture (RNNoise), so only AEC is used from this processor.
    /// </summary>
    public void SetAudioProcessor(IAudioProcessor? processor)
    {
        _audioProcessor = processor;
        _processorOutputBuffer = null;
        Console.WriteLine($"NativeMicrophoneManager: Audio processor {(processor != null ? "set" : "cleared")}");
    }

    /// <summary>
    /// Starts native microphone capture with the specified device.
    /// </summary>
    /// <param name="microphoneId">Device ID or index to capture from</param>
    /// <param name="noiseSuppression">Whether to enable AI-powered noise suppression (default: true)</param>
    /// <param name="echoCancellation">Whether to enable acoustic echo cancellation (default: true)</param>
    public async Task<bool> StartAsync(string microphoneId, bool noiseSuppression = true, bool echoCancellation = true)
    {
        if (_isRunning)
        {
            Console.WriteLine("NativeMicrophoneManager: Already running");
            return true;
        }

        var capturePath = _locator.GetNativeMicrophoneCapturePath();
        if (capturePath == null || !File.Exists(capturePath))
        {
            Console.WriteLine("NativeMicrophoneManager: Native capture tool not available");
            return false;
        }

        var args = _locator.GetNativeMicrophoneCaptureArgs(microphoneId, noiseSuppression, echoCancellation);

        Console.WriteLine($"NativeMicrophoneManager: Starting native capture: {capturePath} {args}");

        try
        {
            // Initialize Opus encoder
            _audioEncoder = new AudioEncoder(includeOpus: true);

            // Get OPUS format and configure for stereo
            var opusFormat = _audioEncoder.SupportedFormats.FirstOrDefault(f => f.FormatName == "OPUS");
            if (!string.IsNullOrEmpty(opusFormat.FormatName))
            {
                // Create stereo format (2 channels)
                _audioFormat = new AudioFormat(
                    opusFormat.Codec,
                    opusFormat.FormatID,
                    opusFormat.ClockRate,
                    2, // stereo
                    opusFormat.Parameters);
                Console.WriteLine($"NativeMicrophoneManager: Audio encoder initialized ({_audioFormat.Value.FormatName} {_audioFormat.Value.ClockRate}Hz stereo)");
            }
            else
            {
                Console.WriteLine("NativeMicrophoneManager: Warning - Opus format not available");
            }

            _cts = new CancellationTokenSource();

            _captureProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = capturePath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _captureProcess.Exited += (s, e) =>
            {
                Console.WriteLine($"NativeMicrophoneManager: Process exited with code {_captureProcess?.ExitCode}");
                _isRunning = false;
            };

            _captureProcess.Start();
            _isRunning = true;

            // Start reading audio from stderr
            _audioReadTask = Task.Run(() => ReadAudioLoopAsync(_cts.Token), _cts.Token);

            // Start the speaking check timer
            _speakingTimer = new Timer(CheckSpeakingTimeout, null, SpeakingCheckIntervalMs, SpeakingCheckIntervalMs);

            Console.WriteLine("NativeMicrophoneManager: Native microphone capture started");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NativeMicrophoneManager: Failed to start: {ex.Message}");
            await StopAsync();
            return false;
        }
    }

    /// <summary>
    /// Stops native microphone capture.
    /// </summary>
    public async Task StopAsync()
    {
        _speakingTimer?.Dispose();
        _speakingTimer = null;

        _cts?.Cancel();

        if (_captureProcess != null && !_captureProcess.HasExited)
        {
            try
            {
                _captureProcess.Kill();
                await _captureProcess.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"NativeMicrophoneManager: Error stopping process: {ex.Message}");
            }
        }

        if (_audioReadTask != null)
        {
            try
            {
                await _audioReadTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _captureProcess?.Dispose();
        _captureProcess = null;
        _cts?.Dispose();
        _cts = null;
        _audioReadTask = null;
        _audioEncoder = null;
        _isRunning = false;

        // Reset speaking state
        if (_isSpeaking)
        {
            _isSpeaking = false;
            SpeakingChanged?.Invoke(false);
        }

        Console.WriteLine("NativeMicrophoneManager: Stopped");
    }

    /// <summary>
    /// Sets the mute state for the microphone.
    /// When muted, audio is captured but not sent.
    /// </summary>
    public void SetMuted(bool muted)
    {
        _isMuted = muted;
        Console.WriteLine($"NativeMicrophoneManager: Muted = {muted}");
    }

    private async Task ReadAudioLoopAsync(CancellationToken ct)
    {
        if (_captureProcess == null) return;

        var stderr = _captureProcess.StandardError.BaseStream;
        var headerBuffer = new byte[McapHeaderSize];
        long packetCount = 0;
        int invalidHeaderCount = 0;

        try
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                // Read MCAP header (24 bytes)
                int headerRead = 0;
                while (headerRead < McapHeaderSize && !ct.IsCancellationRequested)
                {
                    int bytesRead = await stderr.ReadAsync(
                        headerBuffer.AsMemory(headerRead, McapHeaderSize - headerRead), ct);

                    if (bytesRead == 0)
                    {
                        Console.WriteLine("NativeMicrophoneManager: EOF on stderr");
                        return;
                    }
                    headerRead += bytesRead;
                }

                // Parse header
                var header = McapParser.ParseHeader(headerBuffer);
                if (header == null)
                {
                    // Log message or other non-MCAP data received - scan for next MCAP magic
                    invalidHeaderCount++;
                    if (invalidHeaderCount <= 5)
                    {
                        Console.WriteLine("NativeMicrophoneManager: Invalid MCAP header, scanning for sync...");
                    }

                    // Scan the buffer for the MCAP magic bytes to resync
                    int syncOffset = McapParser.ScanForMagic(headerBuffer, 1);
                    if (syncOffset > 0)
                    {
                        // Found magic bytes - shift buffer and read remaining bytes
                        int remaining = McapHeaderSize - syncOffset;
                        Buffer.BlockCopy(headerBuffer, syncOffset, headerBuffer, 0, remaining);

                        // Read the rest of the header
                        while (remaining < McapHeaderSize && !ct.IsCancellationRequested)
                        {
                            int bytesRead = await stderr.ReadAsync(
                                headerBuffer.AsMemory(remaining, McapHeaderSize - remaining), ct);
                            if (bytesRead == 0) return;
                            remaining += bytesRead;
                        }

                        // Try parsing again
                        header = McapParser.ParseHeader(headerBuffer);
                    }

                    if (header == null)
                    {
                        // Still no valid header - read byte by byte until we find magic
                        if (!await McapParser.ScanForMagicAsync(stderr, headerBuffer, ct))
                        {
                            return; // EOF
                        }
                        header = McapParser.ParseHeader(headerBuffer);
                        if (header == null) continue; // Still invalid, try again
                    }
                }

                // Read audio data
                int dataSize = (int)header.SampleCount * 4; // 2 channels * 2 bytes
                var audioData = new byte[dataSize];
                int dataRead = 0;
                while (dataRead < dataSize && !ct.IsCancellationRequested)
                {
                    int bytesRead = await stderr.ReadAsync(
                        audioData.AsMemory(dataRead, dataSize - dataRead), ct);

                    if (bytesRead == 0)
                    {
                        Console.WriteLine("NativeMicrophoneManager: EOF reading audio data");
                        return;
                    }
                    dataRead += bytesRead;
                }

                packetCount++;
                if (packetCount <= 5 || packetCount % 500 == 0)
                {
                    Console.WriteLine($"NativeMicrophoneManager: Audio packet {packetCount} ({header.SampleCount} samples @ {header.SampleRate}Hz)");
                }

                // Process audio (AGC, gate, encode)
                ProcessAudioPacket(audioData, header.SampleCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NativeMicrophoneManager: Audio read error: {ex.Message}");
        }
    }

    private void ProcessAudioPacket(byte[] audioData, uint sampleCount)
    {
        if (_isMuted || sampleCount == 0) return;

        // Convert bytes to samples
        var samples = new short[sampleCount * 2]; // Stereo
        Buffer.BlockCopy(audioData, 0, samples, 0, audioData.Length);

        // Process through WebRTC APM for AEC and/or noise suppression if available
        // Note: Native capture already has RNNoise, but we also process through WebRTC APM for consistency
        if (_audioProcessor != null && (_audioProcessor.IsAecActive || _audioProcessor.IsNoiseSuppressionEnabled))
        {
            if (_processorOutputBuffer == null || _processorOutputBuffer.Length < samples.Length)
            {
                _processorOutputBuffer = new short[samples.Length];
            }

            // Native capture is 48kHz stereo
            int processedCount = _audioProcessor.ProcessCaptureAudio(
                samples.AsSpan(),
                _processorOutputBuffer.AsSpan(),
                48000,
                2); // stereo

            if (processedCount > 0)
            {
                _processorOutputBuffer.AsSpan(0, processedCount).CopyTo(samples.AsSpan());
            }
        }

        // Get user settings
        var manualGain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Process audio through AGC and gate
        var result = _gainProcessor.Process(samples, manualGain, gateEnabled, gateThreshold);

        // Update speaking state
        if (result.IsAboveGate)
        {
            _lastAudioActivity = DateTime.UtcNow;
            if (!_isSpeaking)
            {
                _isSpeaking = true;
                SpeakingChanged?.Invoke(true);
            }
        }

        // Fire raw sample event (for settings test level meter and loopback)
        OnRawSample?.Invoke(samples, result.NormalizedRms);

        // Encode to Opus and fire event
        if (_audioEncoder != null && _audioFormat.HasValue)
        {
            // Calculate duration in RTP units (48000 Hz / 1000 ms * durationMs)
            // 960 samples at 48kHz = 20ms
            var durationMs = (uint)(sampleCount * 1000 / 48000);
            var durationRtpUnits = durationMs * 48; // 48 samples per ms at 48kHz

            var encodedSample = _audioEncoder.EncodeAudio(samples, _audioFormat.Value);
            if (encodedSample != null && encodedSample.Length > 0)
            {
                OnEncodedSample?.Invoke(durationRtpUnits, encodedSample);
            }
        }
    }

    private void CheckSpeakingTimeout(object? state)
    {
        if (_isSpeaking && (DateTime.UtcNow - _lastAudioActivity).TotalMilliseconds > SpeakingTimeoutMs)
        {
            _isSpeaking = false;
            SpeakingChanged?.Invoke(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
