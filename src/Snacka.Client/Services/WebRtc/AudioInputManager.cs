using SIPSorcery.Media;
using SIPSorceryMedia.SDL2;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using Snacka.Client.Services;
using Snacka.Client.Services.Audio;

namespace Snacka.Client.Services.WebRtc;

/// <summary>
/// Manages microphone capture, voice activity detection (VAD), and automatic gain control (AGC).
/// Supports both native platform capture and SDL2 fallback.
/// Extracted from WebRtcService for single responsibility.
/// </summary>
public class AudioInputManager : IAsyncDisposable
{
    private readonly ISettingsStore? _settingsStore;
    private readonly NativeCaptureLocator? _nativeLocator;

    private SDL2AudioSource? _audioSource;
    private NativeMicrophoneManager? _nativeMicManager;
    private bool _useNativeCapture;
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

    // Audio processor for AEC and noise suppression
    private IAudioProcessor? _audioProcessor;
    private short[]? _processorOutputBuffer;

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

    /// <summary>
    /// Fired when encoded audio is available from native capture.
    /// Used to route audio to WebRtcService when using native capture.
    /// </summary>
    public event Action<uint, byte[]>? OnNativeEncodedSample;

    /// <summary>
    /// Gets whether native microphone capture is being used.
    /// </summary>
    public bool IsUsingNativeCapture => _useNativeCapture;

    public AudioInputManager(ISettingsStore? settingsStore, NativeCaptureLocator? nativeLocator = null)
    {
        _settingsStore = settingsStore;
        _nativeLocator = nativeLocator;
    }

    /// <summary>
    /// Initializes the microphone capture and starts audio processing.
    /// Tries native capture first, falls back to SDL2 if unavailable.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_audioSource != null || _nativeMicManager != null) return;

        var inputDevice = _settingsStore?.Settings.AudioInputDevice ?? string.Empty;

        // Try native capture first
        if (_nativeLocator != null && _nativeLocator.IsNativeMicrophoneCaptureAvailable())
        {
            try
            {
                Console.WriteLine("AudioInputManager: Attempting native microphone capture...");

                _nativeMicManager = new NativeMicrophoneManager(_nativeLocator, _settingsStore);

                // Subscribe to events
                _nativeMicManager.SpeakingChanged += (speaking) =>
                {
                    _isSpeaking = speaking;
                    SpeakingChanged?.Invoke(speaking);
                };

                _nativeMicManager.OnEncodedSample += (duration, sample) =>
                {
                    OnNativeEncodedSample?.Invoke(duration, sample);
                };

                // Use device index 0 if no specific device selected, otherwise try to find the device
                var micId = string.IsNullOrEmpty(inputDevice) ? "0" : inputDevice;

                // Get noise suppression and echo cancellation settings (default: enabled)
                var noiseSuppression = _settingsStore?.Settings.NoiseSuppression ?? true;
                var echoCancellation = _settingsStore?.Settings.EchoCancellation ?? true;
                Console.WriteLine($"AudioInputManager: Noise suppression = {noiseSuppression}, Echo cancellation = {echoCancellation}");

                if (await _nativeMicManager.StartAsync(micId, noiseSuppression, echoCancellation))
                {
                    _useNativeCapture = true;

                    if (_isMuted)
                    {
                        _nativeMicManager.SetMuted(true);
                    }

                    Console.WriteLine("AudioInputManager: Using native microphone capture");
                    return;
                }
                else
                {
                    Console.WriteLine("AudioInputManager: Native capture failed, falling back to SDL2");
                    await _nativeMicManager.DisposeAsync();
                    _nativeMicManager = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInputManager: Native capture error: {ex.Message}, falling back to SDL2");
                if (_nativeMicManager != null)
                {
                    await _nativeMicManager.DisposeAsync();
                    _nativeMicManager = null;
                }
            }
        }

        // Fall back to SDL2
        try
        {
            Console.WriteLine("AudioInputManager: Using SDL2 microphone capture");

            // Ensure SDL2 audio is initialized
            NativeLibraryInitializer.EnsureSdl2AudioInitialized();

            // Use selected audio input device from settings
            // Enable Opus for high-quality 48kHz audio
            var audioEncoder = new AudioEncoder(includeOpus: true);
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

            _useNativeCapture = false;
            Console.WriteLine("AudioInputManager: Microphone initialized (SDL2)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioInputManager: Failed to initialize SDL2: {ex.Message}");
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

        if (_nativeMicManager != null)
        {
            _nativeMicManager.SetMuted(muted);
        }
        else if (_audioSource != null)
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
    /// Sets the audio processor for AEC and noise suppression.
    /// </summary>
    public void SetAudioProcessor(IAudioProcessor? processor)
    {
        _audioProcessor = processor;
        _processorOutputBuffer = null; // Will be allocated on first use

        // Also set on native mic manager if using native capture
        _nativeMicManager?.SetAudioProcessor(processor);

        Console.WriteLine($"AudioInputManager: Audio processor {(processor != null ? "set" : "cleared")}");
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

        // Stop native capture if used
        if (_nativeMicManager != null)
        {
            try
            {
                await _nativeMicManager.DisposeAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInputManager: Error stopping native capture: {ex.Message}");
            }
            _nativeMicManager = null;
        }

        // Stop SDL2 capture if used
        if (_audioSource != null)
        {
            try
            {
                _audioSource.OnAudioSourceRawSample -= OnAudioSourceRawSample;
                await _audioSource.CloseAudio();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioInputManager: Error stopping SDL2: {ex.Message}");
            }
            _audioSource = null;
        }

        _useNativeCapture = false;
        _loggedSampleRate = false;
    }

    private void OnAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        if (_isMuted || sample.Length == 0) return;

        // Log sample rate and validate consistency once per session
        int sampleRateHz = AudioResampler.ToHz(samplingRate);
        if (!_loggedSampleRate)
        {
            _loggedSampleRate = true;
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

        // Step 0: Process through WebRTC APM (AEC + noise suppression) if available
        if (_audioProcessor != null && (_audioProcessor.IsAecActive || _audioProcessor.IsNoiseSuppressionEnabled))
        {
            // Allocate output buffer if needed
            if (_processorOutputBuffer == null || _processorOutputBuffer.Length < sample.Length)
            {
                _processorOutputBuffer = new short[sample.Length];
            }

            // Determine number of channels (SDL2 capture is typically mono)
            int channels = 1; // SDL2AudioSource uses mono

            // Process through WebRTC APM
            int processedCount = _audioProcessor.ProcessCaptureAudio(
                sample.AsSpan(),
                _processorOutputBuffer.AsSpan(),
                sampleRateHz,
                channels);

            // Copy processed audio back to sample array
            if (processedCount > 0)
            {
                _processorOutputBuffer.AsSpan(0, processedCount).CopyTo(sample.AsSpan());
            }
        }

        // Get user settings
        var manualGain = _settingsStore?.Settings.InputGain ?? 1.0f;
        var gateEnabled = _settingsStore?.Settings.GateEnabled ?? true;
        var gateThreshold = _settingsStore?.Settings.GateThreshold ?? 0.02f;

        // Step 1: Calculate input RMS before AGC processing
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
