namespace Snacka.Client.Services.Audio;

/// <summary>
/// Configuration for the audio gain processor.
/// </summary>
public class AudioGainConfig
{
    /// <summary>Target RMS level for AGC normalization.</summary>
    public float TargetRms { get; init; } = 3000f;

    /// <summary>Minimum AGC gain multiplier.</summary>
    public float MinGain { get; init; } = 1.0f;

    /// <summary>Maximum AGC gain multiplier.</summary>
    public float MaxGain { get; init; } = 8.0f;

    /// <summary>AGC attack coefficient (how fast gain decreases). Higher = faster.</summary>
    public float AttackCoeff { get; init; } = 0.1f;

    /// <summary>AGC release coefficient (how fast gain increases). Higher = faster.</summary>
    public float ReleaseCoeff { get; init; } = 0.005f;

    /// <summary>RMS threshold below which AGC doesn't adjust (silence detection).</summary>
    public float SilenceThreshold { get; init; } = 200f;

    /// <summary>Baseline boost applied before AGC.</summary>
    public float BaselineBoost { get; init; } = 1.5f;

    /// <summary>Soft clipping threshold (samples above this get compressed).</summary>
    public float SoftClipThreshold { get; init; } = 30000f;

    /// <summary>Soft clipping ratio (0.1 = 10% of excess is kept).</summary>
    public float SoftClipRatio { get; init; } = 0.1f;

    /// <summary>RMS normalization divisor for converting to 0-1 range.</summary>
    public float RmsNormalizationDivisor { get; init; } = 10000f;

    /// <summary>Default configuration matching NativeMicrophoneManager.</summary>
    public static AudioGainConfig Default => new();
}

/// <summary>
/// Result of processing audio through the gain processor.
/// </summary>
public readonly struct AudioGainResult
{
    /// <summary>RMS of the input signal before processing.</summary>
    public float InputRms { get; init; }

    /// <summary>RMS of the output signal after processing.</summary>
    public float OutputRms { get; init; }

    /// <summary>Normalized RMS (0-1 range) for level meters.</summary>
    public float NormalizedRms { get; init; }

    /// <summary>Whether the signal is above the gate threshold.</summary>
    public bool IsAboveGate { get; init; }

    /// <summary>Current AGC gain value.</summary>
    public float CurrentGain { get; init; }

    /// <summary>Total gain applied (baseline * AGC * manual).</summary>
    public float TotalGain { get; init; }
}

/// <summary>
/// Processes audio with Automatic Gain Control (AGC) and noise gate.
/// Extracted from NativeMicrophoneManager for testability.
///
/// The AGC algorithm:
/// 1. Calculate input RMS
/// 2. If above silence threshold, compute desired gain to reach target RMS
/// 3. Apply attack (fast) or release (slow) smoothing to gain changes
/// 4. Apply total gain (baseline * AGC * manual) with soft clipping
/// 5. Apply noise gate if enabled
/// </summary>
public class AudioGainProcessor
{
    private readonly AudioGainConfig _config;
    private float _agcGain = 1.0f;

    public AudioGainProcessor() : this(AudioGainConfig.Default)
    {
    }

    public AudioGainProcessor(AudioGainConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Gets the current AGC gain value.
    /// </summary>
    public float CurrentGain => _agcGain;

    /// <summary>
    /// Resets the AGC gain to 1.0.
    /// </summary>
    public void Reset()
    {
        _agcGain = 1.0f;
    }

    /// <summary>
    /// Calculates RMS (Root Mean Square) of audio samples.
    /// </summary>
    public static float CalculateRms(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0) return 0;

        double sumOfSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumOfSquares += (double)samples[i] * samples[i];
        }
        return (float)Math.Sqrt(sumOfSquares / samples.Length);
    }

    /// <summary>
    /// Calculates normalized RMS (0-1 range) for level meters.
    /// </summary>
    public float CalculateNormalizedRms(float rms)
    {
        return Math.Min(1.0f, rms / _config.RmsNormalizationDivisor);
    }

    /// <summary>
    /// Updates the AGC gain based on input RMS.
    /// Uses attack/release smoothing for natural-sounding gain changes.
    /// </summary>
    /// <param name="inputRms">RMS of the input signal</param>
    /// <returns>The new AGC gain value</returns>
    public float UpdateAgcGain(float inputRms)
    {
        // Don't adjust gain for silence
        if (inputRms <= _config.SilenceThreshold)
        {
            return _agcGain;
        }

        // Calculate desired gain to reach target RMS
        float desiredGain = _config.TargetRms / inputRms;
        desiredGain = Math.Clamp(desiredGain, _config.MinGain, _config.MaxGain);

        // Apply attack (fast) or release (slow) smoothing
        if (desiredGain < _agcGain)
        {
            // Gain needs to decrease (loud signal) - use fast attack
            _agcGain += (desiredGain - _agcGain) * _config.AttackCoeff;
        }
        else
        {
            // Gain needs to increase (quiet signal) - use slow release
            _agcGain += (desiredGain - _agcGain) * _config.ReleaseCoeff;
        }

        return _agcGain;
    }

    /// <summary>
    /// Calculates the total gain to apply.
    /// </summary>
    public float CalculateTotalGain(float manualGain = 1.0f)
    {
        return _config.BaselineBoost * _agcGain * manualGain;
    }

    /// <summary>
    /// Applies gain to a single sample with soft clipping.
    /// </summary>
    public short ApplyGainWithSoftClip(short sample, float gain)
    {
        float gained = sample * gain;

        // Soft clipping - compress samples above threshold
        if (gained > _config.SoftClipThreshold)
        {
            gained = _config.SoftClipThreshold + (gained - _config.SoftClipThreshold) * _config.SoftClipRatio;
        }
        else if (gained < -_config.SoftClipThreshold)
        {
            gained = -_config.SoftClipThreshold + (gained + _config.SoftClipThreshold) * _config.SoftClipRatio;
        }

        return (short)Math.Clamp(gained, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Applies gain to all samples in place with soft clipping.
    /// </summary>
    public void ApplyGain(Span<short> samples, float gain)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = ApplyGainWithSoftClip(samples[i], gain);
        }
    }

    /// <summary>
    /// Determines if a signal is above the noise gate threshold.
    /// </summary>
    public static bool IsAboveGateThreshold(float normalizedRms, float gateThreshold, bool gateEnabled)
    {
        if (!gateEnabled) return true;
        return normalizedRms > gateThreshold;
    }

    /// <summary>
    /// Applies noise gate - silences audio below threshold.
    /// </summary>
    public static void ApplyGate(Span<short> samples, bool isAboveGate, bool gateEnabled)
    {
        if (gateEnabled && !isAboveGate)
        {
            samples.Clear();
        }
    }

    /// <summary>
    /// Processes audio samples through the full gain pipeline.
    /// This is the main entry point that combines all processing steps.
    /// </summary>
    /// <param name="samples">Audio samples to process (modified in place)</param>
    /// <param name="manualGain">User-adjustable gain multiplier</param>
    /// <param name="gateEnabled">Whether noise gate is enabled</param>
    /// <param name="gateThreshold">Noise gate threshold (0-1 normalized RMS)</param>
    /// <returns>Processing result with RMS values and state</returns>
    public AudioGainResult Process(
        Span<short> samples,
        float manualGain = 1.0f,
        bool gateEnabled = true,
        float gateThreshold = 0.02f)
    {
        // Calculate input RMS
        float inputRms = CalculateRms(samples);

        // Update AGC
        UpdateAgcGain(inputRms);

        // Calculate and apply total gain
        float totalGain = CalculateTotalGain(manualGain);
        ApplyGain(samples, totalGain);

        // Calculate output RMS for gate decision
        float outputRms = CalculateRms(samples);
        float normalizedRms = CalculateNormalizedRms(outputRms);

        // Apply gate
        bool isAboveGate = IsAboveGateThreshold(normalizedRms, gateThreshold, gateEnabled);
        ApplyGate(samples, isAboveGate, gateEnabled);

        return new AudioGainResult
        {
            InputRms = inputRms,
            OutputRms = outputRms,
            NormalizedRms = normalizedRms,
            IsAboveGate = isAboveGate,
            CurrentGain = _agcGain,
            TotalGain = totalGain
        };
    }
}
