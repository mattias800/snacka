using Snacka.Client.Services.Audio;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for AudioGainProcessor to verify AGC and noise gate behavior.
/// These tests validate gain calculations, attack/release dynamics, and gate thresholds.
/// </summary>
public class AudioGainProcessorTests
{
    #region RMS Calculation Tests

    [Fact]
    public void CalculateRms_EmptyArray_ReturnsZero()
    {
        var samples = Array.Empty<short>();
        float rms = AudioGainProcessor.CalculateRms(samples);
        Assert.Equal(0, rms);
    }

    [Fact]
    public void CalculateRms_AllZeros_ReturnsZero()
    {
        var samples = new short[100];
        float rms = AudioGainProcessor.CalculateRms(samples);
        Assert.Equal(0, rms);
    }

    [Fact]
    public void CalculateRms_ConstantValue_ReturnsAbsoluteValue()
    {
        // RMS of constant value is the absolute value
        var samples = new short[100];
        Array.Fill(samples, (short)1000);

        float rms = AudioGainProcessor.CalculateRms(samples);

        Assert.Equal(1000, rms, precision: 1);
    }

    [Fact]
    public void CalculateRms_NegativeConstant_ReturnsAbsoluteValue()
    {
        var samples = new short[100];
        Array.Fill(samples, (short)-1000);

        float rms = AudioGainProcessor.CalculateRms(samples);

        Assert.Equal(1000, rms, precision: 1);
    }

    [Fact]
    public void CalculateRms_SineWave_ReturnsExpectedValue()
    {
        // RMS of a sine wave is amplitude / sqrt(2)
        var samples = new short[1000];
        const short amplitude = 10000;

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(amplitude * Math.Sin(2 * Math.PI * i / 100));
        }

        float rms = AudioGainProcessor.CalculateRms(samples);
        float expectedRms = amplitude / (float)Math.Sqrt(2);

        Assert.InRange(rms, expectedRms * 0.95f, expectedRms * 1.05f);
    }

    [Fact]
    public void CalculateRms_LoudSignal_ReturnsHigherValue()
    {
        var quietSamples = new short[100];
        var loudSamples = new short[100];

        Array.Fill(quietSamples, (short)1000);
        Array.Fill(loudSamples, (short)10000);

        float quietRms = AudioGainProcessor.CalculateRms(quietSamples);
        float loudRms = AudioGainProcessor.CalculateRms(loudSamples);

        Assert.True(loudRms > quietRms);
        Assert.Equal(10, loudRms / quietRms, precision: 1);
    }

    #endregion

    #region Normalized RMS Tests

    [Fact]
    public void CalculateNormalizedRms_Zero_ReturnsZero()
    {
        var processor = new AudioGainProcessor();
        float normalized = processor.CalculateNormalizedRms(0);
        Assert.Equal(0, normalized);
    }

    [Fact]
    public void CalculateNormalizedRms_AtDivisor_ReturnsOne()
    {
        var config = new AudioGainConfig { RmsNormalizationDivisor = 10000 };
        var processor = new AudioGainProcessor(config);

        float normalized = processor.CalculateNormalizedRms(10000);

        Assert.Equal(1.0f, normalized);
    }

    [Fact]
    public void CalculateNormalizedRms_AboveDivisor_ClampsToOne()
    {
        var config = new AudioGainConfig { RmsNormalizationDivisor = 10000 };
        var processor = new AudioGainProcessor(config);

        float normalized = processor.CalculateNormalizedRms(20000);

        Assert.Equal(1.0f, normalized);
    }

    [Fact]
    public void CalculateNormalizedRms_HalfDivisor_ReturnsHalf()
    {
        var config = new AudioGainConfig { RmsNormalizationDivisor = 10000 };
        var processor = new AudioGainProcessor(config);

        float normalized = processor.CalculateNormalizedRms(5000);

        Assert.Equal(0.5f, normalized);
    }

    #endregion

    #region AGC Update Tests

    [Fact]
    public void UpdateAgcGain_BelowSilenceThreshold_DoesNotChange()
    {
        var config = new AudioGainConfig { SilenceThreshold = 200 };
        var processor = new AudioGainProcessor(config);

        float initialGain = processor.CurrentGain;
        processor.UpdateAgcGain(100); // Below threshold

        Assert.Equal(initialGain, processor.CurrentGain);
    }

    [Fact]
    public void UpdateAgcGain_AtSilenceThreshold_DoesNotChange()
    {
        var config = new AudioGainConfig { SilenceThreshold = 200 };
        var processor = new AudioGainProcessor(config);

        float initialGain = processor.CurrentGain;
        processor.UpdateAgcGain(200); // At threshold

        Assert.Equal(initialGain, processor.CurrentGain);
    }

    [Fact]
    public void UpdateAgcGain_LoudSignal_DecreasesGain()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            MinGain = 0.1f, // Allow gain to decrease below 1.0
            SilenceThreshold = 200,
            AttackCoeff = 0.5f // Fast attack for testing
        };
        var processor = new AudioGainProcessor(config);

        // Signal is louder than target
        processor.UpdateAgcGain(6000);

        Assert.True(processor.CurrentGain < 1.0f);
    }

    [Fact]
    public void UpdateAgcGain_QuietSignal_IncreasesGain()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            SilenceThreshold = 200,
            ReleaseCoeff = 0.5f // Fast release for testing
        };
        var processor = new AudioGainProcessor(config);

        // Signal is quieter than target
        processor.UpdateAgcGain(1000);

        Assert.True(processor.CurrentGain > 1.0f);
    }

    [Fact]
    public void UpdateAgcGain_AttackIsFasterThanRelease()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            MinGain = 0.1f, // Allow gain to decrease below 1.0
            SilenceThreshold = 200,
            AttackCoeff = 0.1f,
            ReleaseCoeff = 0.005f
        };

        // Test attack (gain decrease)
        var attackProcessor = new AudioGainProcessor(config);
        attackProcessor.UpdateAgcGain(6000); // Loud - needs gain decrease
        float attackChange = Math.Abs(1.0f - attackProcessor.CurrentGain);

        // Test release (gain increase)
        var releaseProcessor = new AudioGainProcessor(config);
        releaseProcessor.UpdateAgcGain(1000); // Quiet - needs gain increase
        float releaseChange = Math.Abs(releaseProcessor.CurrentGain - 1.0f);

        // Attack should change more in one iteration
        Assert.True(attackChange > releaseChange);
    }

    [Fact]
    public void UpdateAgcGain_RespectsMinGain()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            MinGain = 0.5f,
            MaxGain = 8.0f,
            SilenceThreshold = 200,
            AttackCoeff = 1.0f // Instant for testing
        };
        var processor = new AudioGainProcessor(config);

        // Very loud signal would want gain < MinGain
        processor.UpdateAgcGain(30000);

        Assert.True(processor.CurrentGain >= config.MinGain);
    }

    [Fact]
    public void UpdateAgcGain_RespectsMaxGain()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            MinGain = 1.0f,
            MaxGain = 4.0f,
            SilenceThreshold = 200,
            ReleaseCoeff = 1.0f // Instant for testing
        };
        var processor = new AudioGainProcessor(config);

        // Very quiet signal would want gain > MaxGain
        processor.UpdateAgcGain(300);

        Assert.True(processor.CurrentGain <= config.MaxGain);
    }

    [Fact]
    public void UpdateAgcGain_MultipleIterations_ConvergesToTarget()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            SilenceThreshold = 200,
            ReleaseCoeff = 0.1f
        };
        var processor = new AudioGainProcessor(config);

        // Simulate quiet signal over many iterations
        for (int i = 0; i < 100; i++)
        {
            processor.UpdateAgcGain(1000);
        }

        // Gain should have converged close to 3.0 (3000/1000)
        Assert.InRange(processor.CurrentGain, 2.5f, 3.5f);
    }

    [Fact]
    public void Reset_ResetsGainToOne()
    {
        var processor = new AudioGainProcessor();
        processor.UpdateAgcGain(6000); // Change gain

        processor.Reset();

        Assert.Equal(1.0f, processor.CurrentGain);
    }

    #endregion

    #region Total Gain Calculation Tests

    [Fact]
    public void CalculateTotalGain_DefaultConfig_AppliesBaselineBoost()
    {
        var config = new AudioGainConfig { BaselineBoost = 1.5f };
        var processor = new AudioGainProcessor(config);

        float totalGain = processor.CalculateTotalGain(manualGain: 1.0f);

        Assert.Equal(1.5f, totalGain);
    }

    [Fact]
    public void CalculateTotalGain_WithManualGain_Multiplies()
    {
        var config = new AudioGainConfig { BaselineBoost = 1.5f };
        var processor = new AudioGainProcessor(config);

        float totalGain = processor.CalculateTotalGain(manualGain: 2.0f);

        Assert.Equal(3.0f, totalGain); // 1.5 * 1.0 (AGC) * 2.0
    }

    [Fact]
    public void CalculateTotalGain_AfterAgcUpdate_IncludesAgcGain()
    {
        var config = new AudioGainConfig
        {
            BaselineBoost = 1.0f, // Disable baseline for clarity
            TargetRms = 3000,
            SilenceThreshold = 200,
            ReleaseCoeff = 1.0f
        };
        var processor = new AudioGainProcessor(config);

        processor.UpdateAgcGain(1500); // AGC should want ~2x gain

        float totalGain = processor.CalculateTotalGain(manualGain: 1.0f);

        Assert.InRange(totalGain, 1.5f, 2.5f);
    }

    #endregion

    #region Soft Clipping Tests

    [Fact]
    public void ApplyGainWithSoftClip_BelowThreshold_AppliesFullGain()
    {
        var config = new AudioGainConfig { SoftClipThreshold = 30000 };
        var processor = new AudioGainProcessor(config);

        short result = processor.ApplyGainWithSoftClip(1000, 2.0f);

        Assert.Equal(2000, result);
    }

    [Fact]
    public void ApplyGainWithSoftClip_AboveThreshold_Compresses()
    {
        var config = new AudioGainConfig
        {
            SoftClipThreshold = 30000,
            SoftClipRatio = 0.1f
        };
        var processor = new AudioGainProcessor(config);

        // 20000 * 2 = 40000, which is 10000 above threshold
        // Result should be 30000 + 10000 * 0.1 = 31000
        short result = processor.ApplyGainWithSoftClip(20000, 2.0f);

        Assert.Equal(31000, result);
    }

    [Fact]
    public void ApplyGainWithSoftClip_NegativeAboveThreshold_Compresses()
    {
        var config = new AudioGainConfig
        {
            SoftClipThreshold = 30000,
            SoftClipRatio = 0.1f
        };
        var processor = new AudioGainProcessor(config);

        // -20000 * 2 = -40000
        short result = processor.ApplyGainWithSoftClip(-20000, 2.0f);

        Assert.Equal(-31000, result);
    }

    [Fact]
    public void ApplyGainWithSoftClip_ExtremeValue_ClampsToShortMax()
    {
        var processor = new AudioGainProcessor();

        short result = processor.ApplyGainWithSoftClip(short.MaxValue, 10.0f);

        Assert.Equal(short.MaxValue, result);
    }

    [Fact]
    public void ApplyGainWithSoftClip_ExtremeNegative_ClampsToShortMin()
    {
        var processor = new AudioGainProcessor();

        short result = processor.ApplyGainWithSoftClip(short.MinValue, 10.0f);

        Assert.Equal(short.MinValue, result);
    }

    [Fact]
    public void ApplyGain_ModifiesArrayInPlace()
    {
        var processor = new AudioGainProcessor();
        var samples = new short[] { 1000, 2000, 3000 };

        processor.ApplyGain(samples, 2.0f);

        Assert.Equal(2000, samples[0]);
        Assert.Equal(4000, samples[1]);
        Assert.Equal(6000, samples[2]);
    }

    #endregion

    #region Noise Gate Tests

    [Fact]
    public void IsAboveGateThreshold_GateDisabled_AlwaysTrue()
    {
        bool result = AudioGainProcessor.IsAboveGateThreshold(0.001f, 0.5f, gateEnabled: false);
        Assert.True(result);
    }

    [Fact]
    public void IsAboveGateThreshold_AboveThreshold_ReturnsTrue()
    {
        bool result = AudioGainProcessor.IsAboveGateThreshold(0.1f, 0.05f, gateEnabled: true);
        Assert.True(result);
    }

    [Fact]
    public void IsAboveGateThreshold_BelowThreshold_ReturnsFalse()
    {
        bool result = AudioGainProcessor.IsAboveGateThreshold(0.01f, 0.05f, gateEnabled: true);
        Assert.False(result);
    }

    [Fact]
    public void IsAboveGateThreshold_AtThreshold_ReturnsFalse()
    {
        bool result = AudioGainProcessor.IsAboveGateThreshold(0.05f, 0.05f, gateEnabled: true);
        Assert.False(result);
    }

    [Fact]
    public void ApplyGate_AboveThreshold_DoesNotModify()
    {
        var samples = new short[] { 1000, 2000, 3000 };
        var original = samples.ToArray();

        AudioGainProcessor.ApplyGate(samples, isAboveGate: true, gateEnabled: true);

        Assert.Equal(original, samples);
    }

    [Fact]
    public void ApplyGate_BelowThreshold_Silences()
    {
        var samples = new short[] { 1000, 2000, 3000 };

        AudioGainProcessor.ApplyGate(samples, isAboveGate: false, gateEnabled: true);

        Assert.All(samples, s => Assert.Equal(0, s));
    }

    [Fact]
    public void ApplyGate_GateDisabled_DoesNotModify()
    {
        var samples = new short[] { 1000, 2000, 3000 };
        var original = samples.ToArray();

        AudioGainProcessor.ApplyGate(samples, isAboveGate: false, gateEnabled: false);

        Assert.Equal(original, samples);
    }

    #endregion

    #region Full Pipeline Tests

    [Fact]
    public void Process_SilentInput_ReturnsZeroRms()
    {
        var processor = new AudioGainProcessor();
        var samples = new short[480];

        var result = processor.Process(samples);

        Assert.Equal(0, result.InputRms);
        Assert.Equal(0, result.OutputRms);
    }

    [Fact]
    public void Process_LoudInput_AppliesGainAndReturnsResult()
    {
        var processor = new AudioGainProcessor();
        var samples = new short[480];
        Array.Fill(samples, (short)5000);

        var result = processor.Process(samples, manualGain: 1.0f, gateEnabled: false);

        Assert.True(result.InputRms > 0);
        Assert.True(result.OutputRms > result.InputRms); // Gain was applied
        Assert.True(result.TotalGain > 1.0f); // Baseline boost
    }

    [Fact]
    public void Process_BelowGate_SilencesOutput()
    {
        var config = new AudioGainConfig
        {
            BaselineBoost = 1.0f,
            RmsNormalizationDivisor = 10000
        };
        var processor = new AudioGainProcessor(config);
        var samples = new short[480];
        Array.Fill(samples, (short)100); // Very quiet

        var result = processor.Process(samples, gateEnabled: true, gateThreshold: 0.5f);

        Assert.False(result.IsAboveGate);
        Assert.All(samples, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Process_AboveGate_PreservesOutput()
    {
        var config = new AudioGainConfig
        {
            BaselineBoost = 1.0f,
            RmsNormalizationDivisor = 10000
        };
        var processor = new AudioGainProcessor(config);
        var samples = new short[480];
        Array.Fill(samples, (short)8000); // Loud enough

        var result = processor.Process(samples, gateEnabled: true, gateThreshold: 0.02f);

        Assert.True(result.IsAboveGate);
        Assert.True(samples.Any(s => s != 0));
    }

    [Fact]
    public void Process_MultipleFrames_AgcConverges()
    {
        var config = new AudioGainConfig
        {
            TargetRms = 3000,
            BaselineBoost = 1.0f,
            SilenceThreshold = 200,
            ReleaseCoeff = 0.1f // Faster release for test convergence
        };
        var processor = new AudioGainProcessor(config);

        // Process multiple frames of quiet audio
        for (int i = 0; i < 50; i++)
        {
            var samples = new short[480];
            Array.Fill(samples, (short)1000);
            processor.Process(samples, gateEnabled: false);
        }

        // AGC should have boosted gain
        Assert.True(processor.CurrentGain > 2.0f);
    }

    [Fact]
    public void Process_ReturnsCorrectNormalizedRms()
    {
        var config = new AudioGainConfig
        {
            BaselineBoost = 1.0f,
            RmsNormalizationDivisor = 10000
        };
        var processor = new AudioGainProcessor(config);
        var samples = new short[480];
        Array.Fill(samples, (short)5000);

        var result = processor.Process(samples, gateEnabled: false);

        // With gain ~1, output RMS should be ~5000, normalized ~0.5
        Assert.InRange(result.NormalizedRms, 0.4f, 0.6f);
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void DefaultConfig_HasExpectedValues()
    {
        var config = AudioGainConfig.Default;

        Assert.Equal(3000f, config.TargetRms);
        Assert.Equal(1.0f, config.MinGain);
        Assert.Equal(8.0f, config.MaxGain);
        Assert.Equal(0.1f, config.AttackCoeff);
        Assert.Equal(0.005f, config.ReleaseCoeff);
        Assert.Equal(200f, config.SilenceThreshold);
        Assert.Equal(1.5f, config.BaselineBoost);
    }

    [Fact]
    public void CustomConfig_IsUsed()
    {
        var config = new AudioGainConfig
        {
            BaselineBoost = 3.0f
        };
        var processor = new AudioGainProcessor(config);

        float totalGain = processor.CalculateTotalGain(1.0f);

        Assert.Equal(3.0f, totalGain);
    }

    #endregion
}
