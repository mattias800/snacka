using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Services;

/// <summary>
/// Simple audio resampler using linear interpolation.
/// Designed for real-time voice audio conversion between hardware sample rates and the 48kHz Opus codec rate.
/// </summary>
public class AudioResampler
{
    /// <summary>
    /// The target sample rate for Opus codec (48kHz).
    /// </summary>
    public const int TargetSampleRate = 48000;

    /// <summary>
    /// Common hardware sample rates that may need resampling.
    /// </summary>
    public static readonly int[] CommonHardwareRates = { 44100, 48000, 96000, 192000 };

    /// <summary>
    /// Converts AudioSamplingRatesEnum to integer Hz.
    /// The enum integer value typically equals the sample rate directly.
    /// </summary>
    public static int ToHz(AudioSamplingRatesEnum rate)
    {
        // The enum value is often the sample rate itself
        int value = (int)rate;

        // If it's a reasonable sample rate, use it directly
        if (value >= 8000 && value <= 192000)
            return value;

        // Fallback for named enum values
        return rate switch
        {
            AudioSamplingRatesEnum.Rate8KHz => 8000,
            AudioSamplingRatesEnum.Rate16KHz => 16000,
            _ => TargetSampleRate // Default to 48kHz
        };
    }

    // State for maintaining continuity between chunks (prevents clicks)
    private float _lastInputSample;
    private double _resamplePosition;

    /// <summary>
    /// Resamples audio from one sample rate to another using linear interpolation.
    /// Thread-safe for the same instance if called sequentially.
    /// </summary>
    /// <param name="input">Input samples</param>
    /// <param name="inputRate">Input sample rate (e.g., 44100)</param>
    /// <param name="outputRate">Output sample rate (e.g., 48000)</param>
    /// <returns>Resampled audio at the output rate</returns>
    public short[] Resample(short[] input, int inputRate, int outputRate)
    {
        if (input.Length == 0)
            return Array.Empty<short>();

        // No resampling needed
        if (inputRate == outputRate)
            return input;

        double ratio = (double)inputRate / outputRate;
        int outputLength = (int)Math.Ceiling(input.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double srcPos = _resamplePosition + (i * ratio);
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            float sample1, sample2;

            if (srcIndex < 0)
            {
                sample1 = _lastInputSample;
                sample2 = srcIndex + 1 < input.Length ? input[srcIndex + 1] : (input.Length > 0 ? input[0] : 0);
            }
            else if (srcIndex >= input.Length)
            {
                sample1 = input.Length > 0 ? input[^1] : 0;
                sample2 = sample1;
            }
            else
            {
                sample1 = input[srcIndex];
                sample2 = srcIndex + 1 < input.Length ? input[srcIndex + 1] : sample1;
            }

            // Linear interpolation
            float interpolated = (float)(sample1 + (sample2 - sample1) * frac);
            output[i] = (short)Math.Clamp(interpolated, short.MinValue, short.MaxValue);
        }

        // Update state for next chunk
        if (input.Length > 0)
        {
            _lastInputSample = input[^1];
        }

        // Calculate remaining fractional position for continuity
        double totalConsumed = outputLength * ratio;
        _resamplePosition = totalConsumed - input.Length;

        return output;
    }

    /// <summary>
    /// Resets the resampler state. Call when starting a new audio stream.
    /// </summary>
    public void Reset()
    {
        _lastInputSample = 0;
        _resamplePosition = 0;
    }

    /// <summary>
    /// Resamples from hardware rate to 48kHz for encoding.
    /// </summary>
    public short[] ResampleToCodecRate(short[] input, int hardwareRate)
    {
        return Resample(input, hardwareRate, TargetSampleRate);
    }

    /// <summary>
    /// Resamples from 48kHz to hardware rate for playback.
    /// </summary>
    public short[] ResampleFromCodecRate(short[] input, int hardwareRate)
    {
        return Resample(input, TargetSampleRate, hardwareRate);
    }

    /// <summary>
    /// Calculates the output buffer size for a given input.
    /// Useful for pre-allocating buffers.
    /// </summary>
    public static int CalculateOutputLength(int inputLength, int inputRate, int outputRate)
    {
        if (inputRate == outputRate)
            return inputLength;

        double ratio = (double)inputRate / outputRate;
        return (int)Math.Ceiling(inputLength / ratio);
    }
}

/// <summary>
/// Provides audio resampling utilities without maintaining state.
/// Use for one-shot conversions where continuity between chunks isn't needed.
/// </summary>
public static class AudioResamplerUtils
{
    /// <summary>
    /// Stateless resample using linear interpolation.
    /// For continuous streams, use the AudioResampler class instance instead.
    /// </summary>
    public static short[] Resample(short[] input, int inputRate, int outputRate)
    {
        if (input.Length == 0)
            return Array.Empty<short>();

        if (inputRate == outputRate)
            return input;

        double ratio = (double)inputRate / outputRate;
        int outputLength = (int)Math.Ceiling(input.Length / ratio);
        var output = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            float sample1 = input[srcIndex];
            float sample2 = srcIndex + 1 < input.Length ? input[srcIndex + 1] : sample1;

            // Linear interpolation
            float interpolated = (float)(sample1 + (sample2 - sample1) * frac);
            output[i] = (short)Math.Clamp(interpolated, short.MinValue, short.MaxValue);
        }

        return output;
    }
}
