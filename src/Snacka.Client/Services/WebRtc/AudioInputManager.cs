using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using Snacka.Client.Services;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages microphone capture, voice activity detection (VAD), and automatic gain control (AGC).
/// Extracted from WebRtcService for single responsibility.
/// </summary>
public class AudioInputManager : IAsyncDisposable
{
    private readonly ISettingsStore? _settingsStore;

    private SDL2AudioSource? _audioSource;
    private bool _isMuted;
    private bool _isSpeaking;
    private DateTime _lastAudioActivity = DateTime.MinValue;
    private Timer? _speakingTimer;
    private bool _loggedSampleRate; // Log sample rate once per session

    // Voice activity detection constants
    private const int SpeakingTimeoutMs = 200;
    private const int SpeakingCheckIntervalMs = 50;

    // Automatic Gain Control (AGC) for consistent microphone levels
    private float _agcGain = 1.0f;
    private const float AgcTargetRms = 3000f;         // Target RMS level (reduced from 6000)
    private const float AgcMinGain = 1.0f;
    private const float AgcMaxGain = 8.0f;            // Max 8x boost (reduced from 16x)
    private const float AgcAttackCoeff = 0.1f;
    private const float AgcReleaseCoeff = 0.005f;
    private const float AgcSilenceThreshold = 200f;
    private const float BaselineInputBoost = 1.5f;    // 1.5x baseline (reduced from 4x)

    /// <summary>
    /// Gets whether the user is currently speaking (voice activity detected).
    /// </summary>
    public bool IsSpeaking => _isSpeaking;

    /// <summary>
    /// Gets whether the microphone is muted.
    /// </summary>
    public bool IsMuted => _isMuted;

    /// <summary>
    /// Gets the underlying audio source for accessing encoded audio samples.
    /// Used by WebRtcService to route audio to the SFU connection.
    /// </summary>
    public SDL2AudioSource? AudioSource => _audioSource;

    /// <summary>
    /// Fired when speaking state changes.
    /// </summary>
    public event Action<bool>? SpeakingChanged;

    public AudioInputManager(ISettingsStore? settingsStore)
    {
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// Initializes the microphone capture and starts audio processing.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_audioSource != null) return;

        try
        {
            // Ensure SDL2 audio is initialized
            NativeLibraryInitializer.EnsureSdl2AudioInitialized();

            // Use selected audio input device from settings
            // Enable Opus for high-quality 48kHz audio
            var audioEncoder = new AudioEncoder(includeOpus: true);
            var inputDevice = _settingsStore?.Settings.AudioInputDevice ?? string.Empty;
            _audioSource = new SDL2AudioSource(inputDevice, audioEncoder);

            // Subscribe to raw audio samples for voice activity detection
            _audioSource.OnAudioSourceRawSample += OnAudioSourceRawSample;

            // Subscribe to error events
            _audioSource.OnAudioSourceError += (error) =>
            {
                Console.WriteLine($"AudioInputManager: Audio source error: {error}");
            };

            // Set audio format before starting - prefer Opus for high quality (48kHz)
            var formats = _audioSource.GetAudioSourceFormats();
            if (formats.Count > 0)
            {
                var selectedFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats.FirstOrDefault(f => f.FormatName == "PCMU");
                if (selectedFormat.FormatName == null)
                    selectedFormat = formats[0];
                _audioSource.SetAudioSourceFormat(selectedFormat);
                Console.WriteLine($"AudioInputManager: Selected audio format: {selectedFormat.FormatName} ({selectedFormat.ClockRate}Hz)");
            }

            // Start the speaking check timer
            _speakingTimer = new Timer(CheckSpeakingTimeout, null, SpeakingCheckIntervalMs, SpeakingCheckIntervalMs);

            // Start capturing audio
            await _audioSource.StartAudio();

            if (_isMuted)
            {
                await _audioSource.PauseAudio();
            }

            Console.WriteLine("AudioInputManager: Microphone initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioInputManager: Failed to initialize: {ex.Message}");
            _audioSource = null;
        }
    }

    /// <summary>
    /// Sets the mute state for the microphone.
    /// </summary>
    public void SetMuted(bool muted)
    {
        _isMuted = muted;
        Console.WriteLine($"AudioInputManager: Muted = {muted}");

        if (_audioSource != null)
        {
            try
            {
                if (muted)
                {
                    _ = _audioSource.PauseAudio();
                }
                else
                {
                    _ = _audioSource.ResumeAudio();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInputManager: Error setting mute state: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sets the audio format for encoding.
    /// </summary>
    public void SetAudioFormat(AudioFormat format)
    {
        _audioSource?.SetAudioSourceFormat(format);
    }

    /// <summary>
    /// Gets available audio formats.
    /// </summary>
    public List<AudioFormat> GetAudioFormats()
    {
        return _audioSource?.GetAudioSourceFormats() ?? new List<AudioFormat>();
    }

    /// <summary>
    /// Stops and disposes audio capture resources.
    /// </summary>
    public async Task StopAsync()
    {
        _speakingTimer?.Dispose();
        _speakingTimer = null;

        // Reset speaking state
        if (_isSpeaking)
        {
            _isSpeaking = false;
            SpeakingChanged?.Invoke(false);
        }

        if (_audioSource != null)
        {
            try
            {
                _audioSource.OnAudioSourceRawSample -= OnAudioSourceRawSample;
                await _audioSource.CloseAudio();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInputManager: Error stopping: {ex.Message}");
            }
            _audioSource = null;
        }

        _loggedSampleRate = false;
    }

    private void OnAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        if (_isMuted || sample.Length == 0) return;

        // Log sample rate and validate consistency once per session
        if (!_loggedSampleRate)
        {
            _loggedSampleRate = true;
            int sampleRateHz = AudioResampler.ToHz(samplingRate);
            int expectedSamples = AudioPipelineDiagnostics.CalculateExpectedSamples(sampleRateHz, (int)durationMilliseconds);
            int detectedRate = AudioPipelineDiagnostics.DetectSampleRateFromCount(sample.Length, (int)durationMilliseconds);

            Console.WriteLine($"AudioInputManager: Raw sample rate: {sampleRateHz}Hz (enum: {samplingRate}), " +
                            $"samples: {sample.Length} (expected: {expectedSamples}), " +
                            $"duration: {durationMilliseconds}ms, " +
                            $"detected rate: {detectedRate}Hz");

            // Validate sample consistency and warn if mismatch detected
            var mismatchError = AudioPipelineDiagnostics.ValidateSampleConsistency(samplingRate, durationMilliseconds, sample.Length);
            if (mismatchError != null)
            {
                Console.WriteLine($"AudioInputManager: WARNING - {mismatchError}");
            }
        }

        // Get user settings
        var manualGain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Step 1: Calculate input RMS before any processing
        double sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += (double)sample[i] * sample[i];
        }
        float inputRms = (float)Math.Sqrt(sumOfSquares / sample.Length);

        // Step 2: Update AGC gain (only if not silence)
        if (inputRms > AgcSilenceThreshold)
        {
            float desiredGain = AgcTargetRms / inputRms;
            desiredGain = Math.Clamp(desiredGain, AgcMinGain, AgcMaxGain);

            if (desiredGain < _agcGain)
            {
                // Attack: getting quieter (loud input), adjust quickly
                _agcGain += (desiredGain - _agcGain) * AgcAttackCoeff;
            }
            else
            {
                // Release: getting louder (quiet input), adjust slowly
                _agcGain += (desiredGain - _agcGain) * AgcReleaseCoeff;
            }
        }

        // Step 3: Calculate total gain = baseline boost * AGC * manual adjustment
        float totalGain = BaselineInputBoost * _agcGain * manualGain;

        // Step 4: Apply total gain to samples
        for (int i = 0; i < sample.Length; i++)
        {
            float gainedSample = sample[i] * totalGain;
            // Soft clipping to prevent harsh distortion on peaks
            if (gainedSample > 30000f)
                gainedSample = 30000f + (gainedSample - 30000f) * 0.1f;
            else if (gainedSample < -30000f)
                gainedSample = -30000f + (gainedSample + 30000f) * 0.1f;
            // Final hard clamp
            sample[i] = (short)Math.Clamp(gainedSample, short.MinValue, short.MaxValue);
        }

        // Step 5: Calculate output RMS for voice activity detection
        sumOfSquares = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            sumOfSquares += (double)sample[i] * sample[i];
        }
        double outputRms = Math.Sqrt(sumOfSquares / sample.Length);

        // Normalize RMS to 0-1 range for gate comparison
        double normalizedRms = Math.Min(1.0, outputRms / 10000.0);

        // Apply gate: only consider as voice activity if above threshold
        var effectiveThreshold = gateEnabled ? gateThreshold : 0.0;
        var isAboveGate = normalizedRms > effectiveThreshold;

        // If gate is enabled and audio is below threshold, zero out the samples
        if (gateEnabled && !isAboveGate)
        {
            Array.Clear(sample, 0, sample.Length);
        }

        if (isAboveGate)
        {
            _lastAudioActivity = DateTime.UtcNow;
            if (!_isSpeaking)
            {
                _isSpeaking = true;
                SpeakingChanged?.Invoke(true);
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
