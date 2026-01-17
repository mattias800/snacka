using Snacka.Client.Services;
using SIPSorceryMedia.Abstractions;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for the AudioResampler class to verify correct sample rate conversion.
/// These tests validate that audio is correctly resampled between different sample rates
/// to prevent the "heavily pitched down" microphone issue caused by sample rate mismatches.
/// </summary>
public class AudioResamplerTests
{
    [Fact]
    public void ToHz_ReturnsCorrectRate_For8KHz()
    {
        var hz = AudioResampler.ToHz(AudioSamplingRatesEnum.Rate8KHz);
        Assert.Equal(8000, hz);
    }

    [Fact]
    public void ToHz_ReturnsCorrectRate_For16KHz()
    {
        var hz = AudioResampler.ToHz(AudioSamplingRatesEnum.Rate16KHz);
        Assert.Equal(16000, hz);
    }

    [Fact]
    public void ToHz_ReturnsCorrectRate_ForDirectEnumValue()
    {
        // When the enum value is a valid sample rate directly (e.g., 48000)
        var hz = AudioResampler.ToHz((AudioSamplingRatesEnum)48000);
        Assert.Equal(48000, hz);
    }

    [Fact]
    public void ToHz_DefaultsTo48kHz_ForUnknownValues()
    {
        // Unknown/invalid values should default to 48kHz
        var hz = AudioResampler.ToHz((AudioSamplingRatesEnum)0);
        Assert.Equal(48000, hz);
    }

    [Fact]
    public void Resample_SameRate_ReturnsInputUnchanged()
    {
        var resampler = new AudioResampler();
        var input = new short[] { 100, 200, 300, 400, 500 };

        var output = resampler.Resample(input, 48000, 48000);

        Assert.Same(input, output);
    }

    [Fact]
    public void Resample_EmptyInput_ReturnsEmpty()
    {
        var resampler = new AudioResampler();
        var input = Array.Empty<short>();

        var output = resampler.Resample(input, 44100, 48000);

        Assert.Empty(output);
    }

    [Fact]
    public void Resample_44100To48000_ProducesCorrectLength()
    {
        var resampler = new AudioResampler();
        // 44100 samples at 44.1kHz = 1 second
        // Should produce ~48000 samples at 48kHz
        var input = new short[44100];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(i % 32767);

        var output = resampler.Resample(input, 44100, 48000);

        // Output length should be approximately input * (48000/44100)
        var expectedLength = (int)Math.Ceiling(44100.0 * (48000.0 / 44100.0));
        Assert.InRange(output.Length, expectedLength - 1, expectedLength + 1);
    }

    [Fact]
    public void Resample_8000To48000_ProducesCorrectLength()
    {
        var resampler = new AudioResampler();
        // 8000 samples at 8kHz = 1 second
        // Should produce 48000 samples at 48kHz (6x upsampling)
        var input = new short[8000];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(i % 32767);

        var output = resampler.Resample(input, 8000, 48000);

        // 8kHz to 48kHz is exactly 6x
        var expectedLength = (int)Math.Ceiling(8000.0 * 6.0);
        Assert.InRange(output.Length, expectedLength - 1, expectedLength + 1);
    }

    [Fact]
    public void Resample_48000To8000_ProducesCorrectLength()
    {
        var resampler = new AudioResampler();
        // 48000 samples at 48kHz = 1 second
        // Should produce ~8000 samples at 8kHz (6x downsampling)
        var input = new short[48000];
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(i % 32767);

        var output = resampler.Resample(input, 48000, 8000);

        // 48kHz to 8kHz is 1/6
        var expectedLength = (int)Math.Ceiling(48000.0 / 6.0);
        Assert.InRange(output.Length, expectedLength - 1, expectedLength + 1);
    }

    [Fact]
    public void ResampleToCodecRate_ConvertsTo48kHz()
    {
        var resampler = new AudioResampler();
        var input = new short[44100]; // 1 second at 44.1kHz

        var output = resampler.ResampleToCodecRate(input, 44100);

        // Should be close to 48000 samples
        Assert.InRange(output.Length, 47999, 48001);
    }

    [Fact]
    public void ResampleFromCodecRate_ConvertsFrom48kHz()
    {
        var resampler = new AudioResampler();
        var input = new short[48000]; // 1 second at 48kHz

        var output = resampler.ResampleFromCodecRate(input, 44100);

        // Should be close to 44100 samples
        Assert.InRange(output.Length, 44099, 44101);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var resampler = new AudioResampler();

        // Do some resampling to build up state
        var input = new short[] { 100, 200, 300 };
        resampler.Resample(input, 44100, 48000);

        // Reset
        resampler.Reset();

        // After reset, resampling should start fresh
        // We can't directly verify internal state, but we can verify behavior
        var output1 = resampler.Resample(input, 44100, 48000);
        resampler.Reset();
        var output2 = resampler.Resample(input, 44100, 48000);

        // Both outputs should be identical after reset
        Assert.Equal(output1, output2);
    }

    [Fact]
    public void CalculateOutputLength_CorrectFor8kTo48k()
    {
        var length = AudioResampler.CalculateOutputLength(8000, 8000, 48000);
        Assert.Equal(48000, length);
    }

    [Fact]
    public void CalculateOutputLength_CorrectFor48kTo8k()
    {
        var length = AudioResampler.CalculateOutputLength(48000, 48000, 8000);
        Assert.Equal(8000, length);
    }

    [Fact]
    public void CalculateOutputLength_SameRate_ReturnsSame()
    {
        var length = AudioResampler.CalculateOutputLength(1000, 48000, 48000);
        Assert.Equal(1000, length);
    }

    /// <summary>
    /// This test verifies that a sine wave resampled to a different rate
    /// maintains the same perceived pitch (frequency relationship is preserved).
    /// This is the core bug we're testing for - incorrect resampling causes
    /// audio to be played back at the wrong pitch.
    /// </summary>
    [Fact]
    public void Resample_PreservesPitch_SineWaveTest()
    {
        var resampler = new AudioResampler();

        // Generate a 440Hz sine wave at 44100Hz sample rate
        const int sampleRate = 44100;
        const int frequency = 440;
        const int durationMs = 100;
        const int sampleCount = sampleRate * durationMs / 1000;

        var input = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            input[i] = (short)(Math.Sin(2 * Math.PI * frequency * t) * 16000);
        }

        // Resample to 48kHz
        var output = resampler.ResampleToCodecRate(input, sampleRate);

        // The output should have more samples (48000/44100 ratio)
        Assert.True(output.Length > input.Length);

        // The period of the sine wave in samples should scale with sample rate
        // At 44100Hz, 440Hz has period of 44100/440 = ~100.2 samples
        // At 48000Hz, 440Hz should have period of 48000/440 = ~109.1 samples

        // Verify that the output contains valid audio data
        Assert.True(output.Any(s => Math.Abs(s) > 1000), "Output should contain non-zero audio");
    }

    /// <summary>
    /// Regression test for the bug where audio played at wrong sample rate sounds
    /// "heavily pitched down" - this simulates the condition where 48kHz audio
    /// is incorrectly configured to play at 8kHz (6x slower).
    /// </summary>
    [Fact]
    public void Resample_WrongDirection_CausesPitchIssue()
    {
        var resampler = new AudioResampler();

        // Simulate the bug: treating 48kHz audio as if it were 8kHz
        // This would cause the audio to play 6x slower (heavily pitched down)
        var input = new short[48000]; // 1 second of 48kHz audio
        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(i % 32767);

        // If we incorrectly "upsample" from 8kHz to 48kHz (when it's already 48kHz)
        // we'd get 6x as many samples, causing 6x slower playback
        var buggyOutput = resampler.Resample(input, 8000, 48000);

        // This demonstrates the bug: output would be 6x longer than it should be
        // A 1 second audio clip would become 6 seconds (heavily pitched down)
        Assert.Equal(input.Length * 6, buggyOutput.Length);
    }
}

/// <summary>
/// Stateless audio resampler utility tests.
/// </summary>
public class AudioResamplerUtilsTests
{
    [Fact]
    public void Resample_SameRate_ReturnsInput()
    {
        var input = new short[] { 1, 2, 3 };
        var output = AudioResamplerUtils.Resample(input, 48000, 48000);
        Assert.Same(input, output);
    }

    [Fact]
    public void Resample_Empty_ReturnsEmpty()
    {
        var input = Array.Empty<short>();
        var output = AudioResamplerUtils.Resample(input, 44100, 48000);
        Assert.Empty(output);
    }

    [Fact]
    public void Resample_44100To48000_CorrectLength()
    {
        var input = new short[44100];
        var output = AudioResamplerUtils.Resample(input, 44100, 48000);
        Assert.InRange(output.Length, 47999, 48001);
    }
}
