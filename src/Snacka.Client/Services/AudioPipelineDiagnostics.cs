using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Services;

/// <summary>
/// Runtime diagnostics for detecting audio pipeline issues like sample rate mismatches.
/// Use these methods to validate audio samples and detect potential pitch problems.
/// </summary>
public static class AudioPipelineDiagnostics
{
    /// <summary>
    /// Common audio sample rates used in audio hardware and codecs.
    /// </summary>
    public static readonly int[] CommonSampleRates = { 8000, 16000, 22050, 32000, 44100, 48000, 96000, 192000 };

    /// <summary>
    /// Validates that a raw audio sample's count matches the reported sample rate and duration.
    /// Returns null if valid, or an error message describing the mismatch.
    /// </summary>
    /// <param name="reportedRate">The sample rate enum reported by SDL2</param>
    /// <param name="durationMs">The duration reported by SDL2</param>
    /// <param name="sampleCount">The actual number of samples received</param>
    /// <returns>Null if consistent, error message if mismatch detected</returns>
    public static string? ValidateSampleConsistency(AudioSamplingRatesEnum reportedRate, uint durationMs, int sampleCount)
    {
        int reportedRateHz = AudioResampler.ToHz(reportedRate);
        int expectedSamples = CalculateExpectedSamples(reportedRateHz, (int)durationMs);

        // Allow 5% tolerance for frame size variations
        double tolerance = Math.Max(10, expectedSamples * 0.05);
        int diff = Math.Abs(sampleCount - expectedSamples);

        if (diff > tolerance)
        {
            int detectedRate = DetectSampleRateFromCount(sampleCount, (int)durationMs);
            double pitchShift = (double)detectedRate / reportedRateHz;

            return $"SAMPLE RATE MISMATCH DETECTED! " +
                   $"Reported: {reportedRateHz}Hz (expected {expectedSamples} samples), " +
                   $"Received: {sampleCount} samples (suggests {detectedRate}Hz). " +
                   $"This would cause {pitchShift:F2}x pitch shift " +
                   $"({(pitchShift < 1 ? "PITCHED DOWN" : "PITCHED UP")})";
        }

        return null;
    }

    /// <summary>
    /// Detects the likely actual sample rate based on sample count and duration.
    /// Useful for diagnosing when SDL2 reports the wrong sample rate.
    /// </summary>
    public static int DetectSampleRateFromCount(int sampleCount, int durationMs)
    {
        if (durationMs <= 0) return 48000;

        // Calculate rate from sample count
        int calculatedRate = sampleCount * 1000 / durationMs;

        // Snap to nearest common rate
        int closestRate = CommonSampleRates[0];
        int minDiff = Math.Abs(calculatedRate - closestRate);

        foreach (var rate in CommonSampleRates)
        {
            int diff = Math.Abs(calculatedRate - rate);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestRate = rate;
            }
        }

        return closestRate;
    }

    /// <summary>
    /// Calculates expected sample count for a given sample rate and duration.
    /// </summary>
    public static int CalculateExpectedSamples(int sampleRateHz, int durationMs)
    {
        return sampleRateHz * durationMs / 1000;
    }

    /// <summary>
    /// Calculates the pitch shift ratio that would occur if audio at actualRate
    /// is processed/played as if it were at assumedRate.
    /// </summary>
    /// <returns>
    /// 1.0 = no pitch shift
    /// &lt;1.0 = pitch down (audio slower/deeper)
    /// &gt;1.0 = pitch up (audio faster/higher)
    /// </returns>
    public static double CalculatePitchShift(int actualRate, int assumedRate)
    {
        if (assumedRate == 0) return 1.0;
        return (double)actualRate / assumedRate;
    }

    /// <summary>
    /// Creates a diagnostic report for an audio sample callback.
    /// Use this during debugging to log comprehensive info.
    /// </summary>
    public static string CreateDiagnosticReport(
        AudioSamplingRatesEnum reportedRate,
        uint durationMs,
        int sampleCount,
        string context)
    {
        int reportedRateHz = AudioResampler.ToHz(reportedRate);
        int expectedSamples = CalculateExpectedSamples(reportedRateHz, (int)durationMs);
        int detectedRate = DetectSampleRateFromCount(sampleCount, (int)durationMs);

        bool isConsistent = Math.Abs(sampleCount - expectedSamples) <= Math.Max(10, expectedSamples * 0.05);

        var status = isConsistent ? "OK" : "MISMATCH";
        var pitchInfo = "";

        if (!isConsistent)
        {
            double pitchShift = (double)detectedRate / reportedRateHz;
            pitchInfo = $", PitchShift: {pitchShift:F2}x ({(pitchShift < 1 ? "DOWN" : "UP")})";
        }

        return $"[{context}] {status} | " +
               $"Reported: {reportedRateHz}Hz, " +
               $"Duration: {durationMs}ms, " +
               $"Samples: {sampleCount} (expected: {expectedSamples}), " +
               $"Detected rate: {detectedRate}Hz{pitchInfo}";
    }

    /// <summary>
    /// Checks if the format configuration is correct for Opus.
    /// </summary>
    public static string? ValidateOpusFormat(AudioFormat format)
    {
        if (string.IsNullOrEmpty(format.FormatName))
            return "No format configured";

        if (format.FormatName == "OPUS")
        {
            if (format.ClockRate != 48000)
                return $"Opus format has wrong clock rate: {format.ClockRate}Hz (should be 48000Hz)";
            return null; // OK
        }

        if (format.FormatName == "PCMU")
        {
            return $"Using PCMU format ({format.ClockRate}Hz) instead of OPUS (48000Hz) - this may cause pitch issues if Opus audio is expected";
        }

        return $"Unknown format: {format.FormatName} ({format.ClockRate}Hz)";
    }
}
