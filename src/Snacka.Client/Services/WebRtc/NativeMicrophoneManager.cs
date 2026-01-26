using System.Diagnostics;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

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

    // MCAP header size (24 bytes)
    private const int McapHeaderSize = 24;
    private const uint McapMagic = 0x4D434150; // "MCAP" in big-endian

    // Voice activity detection constants
    private const int SpeakingTimeoutMs = 200;
    private const int SpeakingCheckIntervalMs = 50;

    // Automatic Gain Control (AGC) for consistent microphone levels
    private float _agcGain = 1.0f;
    private const float AgcTargetRms = 3000f;
    private const float AgcMinGain = 1.0f;
    private const float AgcMaxGain = 8.0f;
    private const float AgcAttackCoeff = 0.1f;
    private const float AgcReleaseCoeff = 0.005f;
    private const float AgcSilenceThreshold = 200f;
    private const float BaselineInputBoost = 1.5f;

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
    /// Starts native microphone capture with the specified device.
    /// </summary>
    /// <param name="microphoneId">Device ID or index to capture from</param>
    /// <param name="noiseSuppression">Whether to enable AI-powered noise suppression (default: true)</param>
    public async Task<bool> StartAsync(string microphoneId, bool noiseSuppression = true)
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

        var args = _locator.GetNativeMicrophoneCaptureArgs(microphoneId, noiseSuppression);

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
                var header = ParseMcapHeader(headerBuffer);
                if (header == null)
                {
                    Console.WriteLine("NativeMicrophoneManager: Invalid MCAP header");
                    continue;
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

    private McapHeader? ParseMcapHeader(byte[] buffer)
    {
        // Read magic (big-endian - this is the only big-endian field)
        uint magic = (uint)(buffer[0] << 24 | buffer[1] << 16 | buffer[2] << 8 | buffer[3]);
        if (magic != McapMagic)
        {
            return null;
        }

        // All other multi-byte fields are little-endian (native byte order from Swift/C++)
        return new McapHeader
        {
            Version = buffer[4],
            BitsPerSample = buffer[5],
            Channels = buffer[6],
            IsFloat = buffer[7],
            // Little-endian: least significant byte first
            SampleCount = (uint)(buffer[8] | buffer[9] << 8 | buffer[10] << 16 | buffer[11] << 24),
            SampleRate = (uint)(buffer[12] | buffer[13] << 8 | buffer[14] << 16 | buffer[15] << 24),
            Timestamp = (ulong)(
                (ulong)buffer[16] | (ulong)buffer[17] << 8 |
                (ulong)buffer[18] << 16 | (ulong)buffer[19] << 24 |
                (ulong)buffer[20] << 32 | (ulong)buffer[21] << 40 |
                (ulong)buffer[22] << 48 | (ulong)buffer[23] << 56)
        };
    }

    private void ProcessAudioPacket(byte[] audioData, uint sampleCount)
    {
        if (_isMuted || sampleCount == 0) return;

        // Convert bytes to samples
        var samples = new short[sampleCount * 2]; // Stereo
        Buffer.BlockCopy(audioData, 0, samples, 0, audioData.Length);

        // Get user settings
        var manualGain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Calculate input RMS
        double sumOfSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumOfSquares += (double)samples[i] * samples[i];
        }
        float inputRms = (float)Math.Sqrt(sumOfSquares / samples.Length);

        // Update AGC gain
        if (inputRms > AgcSilenceThreshold)
        {
            float desiredGain = AgcTargetRms / inputRms;
            desiredGain = Math.Clamp(desiredGain, AgcMinGain, AgcMaxGain);

            if (desiredGain < _agcGain)
            {
                _agcGain += (desiredGain - _agcGain) * AgcAttackCoeff;
            }
            else
            {
                _agcGain += (desiredGain - _agcGain) * AgcReleaseCoeff;
            }
        }

        // Calculate total gain
        float totalGain = BaselineInputBoost * _agcGain * manualGain;

        // Apply gain to samples
        for (int i = 0; i < samples.Length; i++)
        {
            float gainedSample = samples[i] * totalGain;
            // Soft clipping
            if (gainedSample > 30000f)
                gainedSample = 30000f + (gainedSample - 30000f) * 0.1f;
            else if (gainedSample < -30000f)
                gainedSample = -30000f + (gainedSample + 30000f) * 0.1f;
            samples[i] = (short)Math.Clamp(gainedSample, short.MinValue, short.MaxValue);
        }

        // Calculate output RMS for VAD
        sumOfSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumOfSquares += (double)samples[i] * samples[i];
        }
        double outputRms = Math.Sqrt(sumOfSquares / samples.Length);
        double normalizedRms = Math.Min(1.0, outputRms / 10000.0);

        // Apply gate
        var effectiveThreshold = gateEnabled ? gateThreshold : 0.0;
        var isAboveGate = normalizedRms > effectiveThreshold;

        if (gateEnabled && !isAboveGate)
        {
            Array.Clear(samples, 0, samples.Length);
        }

        // Update speaking state
        if (isAboveGate)
        {
            _lastAudioActivity = DateTime.UtcNow;
            if (!_isSpeaking)
            {
                _isSpeaking = true;
                SpeakingChanged?.Invoke(true);
            }
        }

        // Fire raw sample event (for settings test level meter and loopback)
        OnRawSample?.Invoke(samples, (float)normalizedRms);

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

    private class McapHeader
    {
        public byte Version { get; set; }
        public byte BitsPerSample { get; set; }
        public byte Channels { get; set; }
        public byte IsFloat { get; set; }
        public uint SampleCount { get; set; }
        public uint SampleRate { get; set; }
        public ulong Timestamp { get; set; }
    }
}
