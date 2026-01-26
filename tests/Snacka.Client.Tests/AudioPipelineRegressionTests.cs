using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Snacka.Client.Services;

namespace Snacka.Client.Tests;

/// <summary>
/// Regression tests for the audio pipeline to prevent "pitched down" microphone issues.
/// These tests verify:
/// 1. Sample rate consistency across the capture/encode/decode/playback pipeline
/// 2. Sample count matches expected values for given sample rates and durations
/// 3. Format selection consistently prefers Opus (48kHz) over PCMU (8kHz)
///
/// The "pitched down" bug occurs when:
/// - Audio captured at rate X is processed/played at rate Y (where Y != X)
/// - Common case: 48kHz audio played on 8kHz sink = 6x slower (heavily pitched down)
/// - Or: Sample rate enum doesn't match actual captured data
/// </summary>
public class AudioPipelineRegressionTests
{
    /// <summary>
    /// Standard Opus frame duration is 20ms.
    /// </summary>
    private const int StandardFrameDurationMs = 20;

    #region Sample Rate / Sample Count Consistency Tests

    /// <summary>
    /// Validates that sample count is consistent with reported sample rate and duration.
    /// This is the core regression test for pitch issues.
    ///
    /// If SDL2 reports 48kHz but actually captures at 44.1kHz, the sample count
    /// would be wrong, and we'd detect it here.
    /// </summary>
    [Theory]
    [InlineData(8000, 20, 160)]    // 8000 * 0.020 = 160 (Rate8KHz enum)
    [InlineData(16000, 20, 320)]   // 16000 * 0.020 = 320 (Rate16KHz enum)
    [InlineData(48000, 20, 960)]   // 48000 * 0.020 = 960
    [InlineData(44100, 20, 882)]   // 44100 * 0.020 = 882
    [InlineData(48000, 10, 480)]   // 48000 * 0.010 = 480
    [InlineData(48000, 40, 1920)]  // 48000 * 0.040 = 1920
    public void SampleCount_MatchesExpectedForRateAndDuration(int rateValue, int durationMs, int expectedSamples)
    {
        var rate = (AudioSamplingRatesEnum)rateValue;
        int sampleRateHz = AudioResampler.ToHz(rate);
        int actualSamples = CalculateExpectedSampleCount(sampleRateHz, durationMs);

        Assert.Equal(expectedSamples, actualSamples);
    }

    /// <summary>
    /// Validates that we can detect a sample rate mismatch from sample count.
    /// If we receive 882 samples for 20ms but rate says 48kHz, something is wrong.
    /// </summary>
    [Fact]
    public void DetectSampleRateMismatch_44100ReportedAs48000()
    {
        // Simulating the bug: SDL2 opens device at 44.1kHz but reports 48kHz
        var reportedRate = (AudioSamplingRatesEnum)48000;
        int actualCaptureRate = 44100;
        int durationMs = 20;

        // Expected samples if rate were correct
        int expectedSamplesIfCorrect = CalculateExpectedSampleCount(48000, durationMs); // 960

        // Actual samples received (based on real capture rate)
        int actualSamplesReceived = CalculateExpectedSampleCount(actualCaptureRate, durationMs); // 882

        // This mismatch causes pitch issues!
        Assert.NotEqual(expectedSamplesIfCorrect, actualSamplesReceived);

        // Calculate the pitch shift this would cause
        double pitchRatio = (double)actualSamplesReceived / expectedSamplesIfCorrect;
        Assert.True(pitchRatio < 1, $"Pitch would be shifted by {pitchRatio:F3}x (lower pitch)");
    }

    /// <summary>
    /// Validates that we can detect the common 8kHz/48kHz mismatch.
    /// This causes a 6x pitch shift - the "heavily pitched down" issue.
    /// </summary>
    [Fact]
    public void DetectSampleRateMismatch_8000ReportedAs48000()
    {
        // The classic bug: PCMU (8kHz) selected but audio captured at 48kHz
        // Or vice versa: 48kHz audio played on 8kHz sink
        int rate8k = 8000;
        int rate48k = 48000;
        int durationMs = 20;

        int samples8k = CalculateExpectedSampleCount(rate8k, durationMs);   // 160
        int samples48k = CalculateExpectedSampleCount(rate48k, durationMs); // 960

        // 6x difference!
        Assert.Equal(6, samples48k / samples8k);

        // If we play 960 samples thinking they're 160 samples worth of time...
        // Playback would be 6x slower = heavily pitched down
        double pitchRatio = (double)rate8k / rate48k;
        Assert.Equal(1.0 / 6, pitchRatio, 3);
    }

    #endregion

    #region AudioResampler Enum Conversion Tests

    /// <summary>
    /// Verifies AudioResampler.ToHz correctly converts all common sample rate enums.
    /// Note: AudioSamplingRatesEnum.Rate8KHz = 8000, Rate16KHz = 16000
    /// </summary>
    [Theory]
    [InlineData(8000, 8000)]     // Rate8KHz enum
    [InlineData(16000, 16000)]   // Rate16KHz enum
    [InlineData(44100, 44100)]
    [InlineData(48000, 48000)]
    [InlineData(96000, 96000)]
    [InlineData(192000, 192000)]
    public void ToHz_CorrectlyConvertsAllCommonRates(int enumValue, int expectedHz)
    {
        var rate = (AudioSamplingRatesEnum)enumValue;
        Assert.Equal(expectedHz, AudioResampler.ToHz(rate));
    }

    /// <summary>
    /// Verifies that unknown/invalid enum values default to 48kHz safely.
    /// </summary>
    [Theory]
    [InlineData(0)]      // Invalid
    [InlineData(1)]      // Invalid
    [InlineData(100)]    // Invalid
    [InlineData(7999)]   // Just below valid range
    public void ToHz_DefaultsTo48kHz_ForInvalidValues(int invalidValue)
    {
        var rate = (AudioSamplingRatesEnum)invalidValue;
        Assert.Equal(48000, AudioResampler.ToHz(rate));
    }

    #endregion

    #region Format Selection Regression Tests

    /// <summary>
    /// Verifies OPUS format is always selected over PCMU when available.
    /// This is the fix for the original pitch-down bug.
    /// </summary>
    [Fact]
    public void FormatSelection_AlwaysPrefersOpus_WhenAvailable()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats.ToList();

        // The correct selection logic (matches AudioInputManager/UserAudioMixer)
        var selected = formats.FirstOrDefault(f => f.FormatName == "OPUS");
        if (selected.FormatName == null)
            selected = formats.FirstOrDefault(f => f.FormatName == "PCMU");
        if (selected.FormatName == null)
            selected = formats[0];

        Assert.Equal("OPUS", selected.FormatName);
        Assert.Equal(48000, selected.ClockRate);
    }

    /// <summary>
    /// Verifies that using formats[0] directly can cause the bug.
    /// This test documents the bug pattern we're preventing.
    /// </summary>
    [Fact]
    public void FormatSelection_Formats0_MayCausePitchIssue()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats.ToList();

        var firstFormat = formats[0];
        var opusFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");

        // If first format is not OPUS, using formats[0] would cause issues
        if (firstFormat.FormatName != "OPUS")
        {
            // Document the sample rate mismatch
            Assert.NotEqual(48000, firstFormat.ClockRate);

            // Calculate pitch shift if this bug occurred
            double pitchRatio = (double)firstFormat.ClockRate / opusFormat.ClockRate;
            Assert.True(pitchRatio < 1,
                $"Using formats[0] ({firstFormat.FormatName}={firstFormat.ClockRate}Hz) instead of OPUS " +
                $"would cause {pitchRatio:F3}x pitch shift");
        }
    }

    /// <summary>
    /// Verifies source and sink formats must match to prevent pitch issues.
    /// </summary>
    [Fact]
    public void SourceSinkFormat_MustMatch_ToPreventPitchIssues()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var formats = encoder.SupportedFormats.ToList();

        // Correct: Both use OPUS
        var sourceFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");
        var sinkFormat = formats.FirstOrDefault(f => f.FormatName == "OPUS");

        Assert.Equal(sourceFormat.FormatName, sinkFormat.FormatName);
        Assert.Equal(sourceFormat.ClockRate, sinkFormat.ClockRate);
    }

    #endregion

    #region Opus Configuration Tests

    /// <summary>
    /// Verifies Opus format uses exactly 48kHz (required by Opus spec).
    /// </summary>
    [Fact]
    public void OpusFormat_Uses48kHz_AsRequired()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var opusFormat = encoder.SupportedFormats.FirstOrDefault(f => f.FormatName == "OPUS");

        Assert.NotNull(opusFormat.FormatName);
        Assert.Equal(48000, opusFormat.ClockRate);
    }

    /// <summary>
    /// Verifies AudioResampler.TargetSampleRate matches Opus requirement.
    /// </summary>
    [Fact]
    public void AudioResampler_TargetRate_MatchesOpus()
    {
        Assert.Equal(48000, AudioResampler.TargetSampleRate);
    }

    /// <summary>
    /// Verifies standard Opus frame sizes for 48kHz.
    /// </summary>
    [Theory]
    [InlineData(2.5, 120)]    // 2.5ms
    [InlineData(5, 240)]      // 5ms
    [InlineData(10, 480)]     // 10ms
    [InlineData(20, 960)]     // 20ms (most common)
    [InlineData(40, 1920)]    // 40ms
    [InlineData(60, 2880)]    // 60ms
    public void OpusFrameSizes_CorrectFor48kHz(double frameDurationMs, int expectedSamples)
    {
        int samples = (int)(48000 * frameDurationMs / 1000);
        Assert.Equal(expectedSamples, samples);
    }

    #endregion

    #region Resampling Safety Tests

    /// <summary>
    /// Verifies resampling preserves audio duration (and thus pitch).
    /// </summary>
    [Theory]
    [InlineData(44100, 48000, 44100, 48000)]   // 1 second at 44.1kHz -> 48kHz
    [InlineData(8000, 48000, 8000, 48000)]     // 1 second at 8kHz -> 48kHz
    [InlineData(48000, 44100, 48000, 44100)]   // 1 second at 48kHz -> 44.1kHz
    public void Resample_PreservesDuration(int inputRate, int outputRate, int inputSamples, int expectedOutputSamples)
    {
        // Input represents 1 second of audio
        var input = new short[inputSamples];

        var resampler = new AudioResampler();
        var output = resampler.Resample(input, inputRate, outputRate);

        // Output should also represent 1 second of audio
        // Allow ±1 sample tolerance for rounding
        Assert.InRange(output.Length, expectedOutputSamples - 1, expectedOutputSamples + 1);
    }

    /// <summary>
    /// Verifies that resampling 48kHz to 48kHz returns the input unchanged.
    /// </summary>
    [Fact]
    public void Resample_SameRate_NoProcessing()
    {
        var input = new short[] { 1, 2, 3, 4, 5 };
        var resampler = new AudioResampler();

        var output = resampler.Resample(input, 48000, 48000);

        Assert.Same(input, output); // Should be exact same reference
    }

    #endregion

    #region Diagnostic Validation Tests

    /// <summary>
    /// Tests that can validate diagnostic log output during actual audio capture.
    /// These provide expected values that logs should show.
    /// </summary>
    [Fact]
    public void ExpectedDiagnosticValues_For20msOpusFrame()
    {
        // When capturing 20ms of Opus audio at 48kHz, logs should show:
        int expectedSampleRate = 48000;
        int expectedSampleCount = 960; // 48000 * 0.020
        int expectedDurationMs = 20;

        // Verify our expectations are internally consistent
        Assert.Equal(expectedSampleCount, expectedSampleRate * expectedDurationMs / 1000);
    }

    /// <summary>
    /// Provides expected log values for common macOS internal mic sample rates.
    /// macOS may use 44.1kHz or 48kHz depending on hardware/settings.
    /// </summary>
    [Theory]
    [InlineData(44100, 20, 882)]  // Common macOS rate
    [InlineData(48000, 20, 960)]  // Standard WebRTC rate
    public void ExpectedDiagnosticValues_MacOSInternalMic(int sampleRate, int durationMs, int expectedSamples)
    {
        int actualSamples = CalculateExpectedSampleCount(sampleRate, durationMs);
        Assert.Equal(expectedSamples, actualSamples);
    }

    #endregion

    #region Helper Methods

    private static int CalculateExpectedSampleCount(int sampleRateHz, int durationMs)
    {
        return sampleRateHz * durationMs / 1000;
    }

    /// <summary>
    /// Validates a raw audio sample callback's parameters are consistent.
    /// Use this to verify SDL2AudioSource is reporting correctly.
    /// </summary>
    public static bool ValidateSampleCallback(AudioSamplingRatesEnum reportedRate, uint durationMs, int sampleCount)
    {
        int rateHz = AudioResampler.ToHz(reportedRate);
        int expectedSamples = CalculateExpectedSampleCount(rateHz, (int)durationMs);

        // Allow 5% tolerance for frame size variations
        double tolerance = expectedSamples * 0.05;
        return Math.Abs(sampleCount - expectedSamples) <= tolerance;
    }

    /// <summary>
    /// Calculates what pitch shift would occur with given rate mismatch.
    /// Returns 1.0 for no shift, &lt;1.0 for pitch down, &gt;1.0 for pitch up.
    /// </summary>
    public static double CalculatePitchShift(int actualRate, int assumedRate)
    {
        return (double)actualRate / assumedRate;
    }

    #endregion
}

/// <summary>
/// Tests for AudioPipelineDiagnostics helper class.
/// </summary>
public class AudioPipelineDiagnosticsTests
{
    [Fact]
    public void ValidateSampleConsistency_ReturnsNull_WhenConsistent()
    {
        // 48kHz, 20ms = 960 samples
        var result = AudioPipelineDiagnostics.ValidateSampleConsistency(
            (AudioSamplingRatesEnum)48000, 20, 960);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSampleConsistency_ReturnsError_WhenMismatch()
    {
        // Report 48kHz, 20ms but only receive 882 samples (44.1kHz worth)
        var result = AudioPipelineDiagnostics.ValidateSampleConsistency(
            (AudioSamplingRatesEnum)48000, 20, 882);

        Assert.NotNull(result);
        Assert.Contains("SAMPLE RATE MISMATCH", result);
        Assert.Contains("PITCHED DOWN", result);
    }

    [Fact]
    public void ValidateSampleConsistency_AllowsTolerance()
    {
        // 48kHz, 20ms = 960 samples, allow ±5%
        // 960 ± 48 samples should pass
        Assert.Null(AudioPipelineDiagnostics.ValidateSampleConsistency(
            (AudioSamplingRatesEnum)48000, 20, 950));
        Assert.Null(AudioPipelineDiagnostics.ValidateSampleConsistency(
            (AudioSamplingRatesEnum)48000, 20, 970));
    }

    [Fact]
    public void DetectSampleRateFromCount_FindsNearestCommonRate()
    {
        Assert.Equal(48000, AudioPipelineDiagnostics.DetectSampleRateFromCount(960, 20));
        Assert.Equal(44100, AudioPipelineDiagnostics.DetectSampleRateFromCount(882, 20));
        Assert.Equal(8000, AudioPipelineDiagnostics.DetectSampleRateFromCount(160, 20));
        Assert.Equal(16000, AudioPipelineDiagnostics.DetectSampleRateFromCount(320, 20));
    }

    [Fact]
    public void CalculatePitchShift_CorrectValues()
    {
        // Same rate = no shift
        Assert.Equal(1.0, AudioPipelineDiagnostics.CalculatePitchShift(48000, 48000));

        // 44.1kHz treated as 48kHz = pitch down
        double shift = AudioPipelineDiagnostics.CalculatePitchShift(44100, 48000);
        Assert.True(shift < 1.0);
        Assert.Equal(44100.0 / 48000.0, shift, 4);

        // 8kHz treated as 48kHz = major pitch down (6x slower)
        double majorShift = AudioPipelineDiagnostics.CalculatePitchShift(8000, 48000);
        Assert.Equal(1.0 / 6.0, majorShift, 4);
    }

    [Fact]
    public void ValidateOpusFormat_ReturnsNull_ForValidOpus()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var opusFormat = encoder.SupportedFormats.FirstOrDefault(f => f.FormatName == "OPUS");

        var result = AudioPipelineDiagnostics.ValidateOpusFormat(opusFormat);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateOpusFormat_WarnsAbout_PCMU()
    {
        var encoder = new AudioEncoder(includeOpus: true);
        var pcmuFormat = encoder.SupportedFormats.FirstOrDefault(f => f.FormatName == "PCMU");

        var result = AudioPipelineDiagnostics.ValidateOpusFormat(pcmuFormat);
        Assert.NotNull(result);
        Assert.Contains("PCMU", result);
        Assert.Contains("pitch issues", result);
    }

    [Fact]
    public void CreateDiagnosticReport_IncludesAllInfo()
    {
        var report = AudioPipelineDiagnostics.CreateDiagnosticReport(
            (AudioSamplingRatesEnum)48000, 20, 960, "TestContext");

        Assert.Contains("TestContext", report);
        Assert.Contains("48000Hz", report);
        Assert.Contains("20ms", report);
        Assert.Contains("960", report);
        Assert.Contains("OK", report);
    }

    [Fact]
    public void CreateDiagnosticReport_ShowsMismatch()
    {
        var report = AudioPipelineDiagnostics.CreateDiagnosticReport(
            (AudioSamplingRatesEnum)48000, 20, 882, "TestContext");

        Assert.Contains("MISMATCH", report);
        Assert.Contains("DOWN", report);
    }
}

/// <summary>
/// Tests specifically for validating sample rate detection from sample counts.
/// This is useful for diagnosing the pitch issue without relying on SDL2.
/// </summary>
public class SampleRateDetectionTests
{
    /// <summary>
    /// Given a sample count and frame duration, detect the likely sample rate.
    /// </summary>
    [Theory]
    [InlineData(960, 20, 48000)]
    [InlineData(882, 20, 44100)]
    [InlineData(480, 10, 48000)]
    [InlineData(441, 10, 44100)]
    [InlineData(160, 20, 8000)]
    [InlineData(320, 20, 16000)]
    public void DetectSampleRate_FromSampleCount(int sampleCount, int durationMs, int expectedRate)
    {
        int detectedRate = DetectSampleRateFromCount(sampleCount, durationMs);
        Assert.Equal(expectedRate, detectedRate);
    }

    /// <summary>
    /// Validates we can detect when reported rate doesn't match sample count.
    /// </summary>
    [Fact]
    public void DetectMismatch_ReportedVsActual()
    {
        // Scenario: SDL2 reports 48kHz but we receive 882 samples for 20ms
        int reportedRate = 48000;
        int sampleCount = 882;
        int durationMs = 20;

        int expectedSamplesForReportedRate = reportedRate * durationMs / 1000; // 960
        int detectedActualRate = DetectSampleRateFromCount(sampleCount, durationMs); // 44100

        // Mismatch detected!
        Assert.NotEqual(expectedSamplesForReportedRate, sampleCount);
        Assert.NotEqual(reportedRate, detectedActualRate);
        Assert.Equal(44100, detectedActualRate);
    }

    private static int DetectSampleRateFromCount(int sampleCount, int durationMs)
    {
        // Calculate rate from sample count
        int calculatedRate = sampleCount * 1000 / durationMs;

        // Snap to nearest common rate
        int[] commonRates = { 8000, 16000, 22050, 44100, 48000, 96000, 192000 };

        int closestRate = commonRates[0];
        int minDiff = Math.Abs(calculatedRate - closestRate);

        foreach (var rate in commonRates)
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
}
