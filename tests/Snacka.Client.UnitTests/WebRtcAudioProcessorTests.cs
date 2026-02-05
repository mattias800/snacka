using Snacka.Client.Services.Audio;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for the WebRtcAudioProcessor class to verify correct audio processing behavior.
/// These tests validate channel conversion, frame buffering, and AEC state management
/// to prevent regressions in the audio processing pipeline.
/// </summary>
public class WebRtcAudioProcessorTests : IDisposable
{
    private WebRtcAudioProcessor? _processor;

    public void Dispose()
    {
        _processor?.Dispose();
    }

    #region Constructor Tests

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(32000)]
    [InlineData(48000)]
    public void Constructor_ValidSampleRates_Succeeds(int sampleRate)
    {
        _processor = new WebRtcAudioProcessor(sampleRate: sampleRate, channels: 1);
        Assert.NotNull(_processor);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(22050)]
    [InlineData(96000)]
    [InlineData(0)]
    public void Constructor_InvalidSampleRates_ThrowsArgumentException(int sampleRate)
    {
        Assert.Throws<ArgumentException>(() => new WebRtcAudioProcessor(sampleRate: sampleRate));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Constructor_ValidChannelCounts_Succeeds(int channels)
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: channels);
        Assert.NotNull(_processor);
    }

    [Fact]
    public void Constructor_DefaultSettings_HasCorrectState()
    {
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: true,
            enableNoiseSuppression: true,
            enableAgc: true);

        // AEC not active until playback is fed
        Assert.False(_processor.IsAecActive);
        Assert.True(_processor.IsNoiseSuppressionEnabled);
        Assert.True(_processor.IsAgcEnabled);
    }

    [Fact]
    public void Constructor_DisabledFeatures_HasCorrectState()
    {
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: false,
            enableNoiseSuppression: false,
            enableAgc: false);

        Assert.False(_processor.IsAecActive);
        Assert.False(_processor.IsNoiseSuppressionEnabled);
        Assert.False(_processor.IsAgcEnabled);
    }

    #endregion

    #region AEC State Tests

    [Fact]
    public void IsAecActive_NoPlaybackFed_ReturnsFalse()
    {
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: true);

        Assert.False(_processor.IsAecActive);
    }

    [Fact]
    public void IsAecActive_AfterPlaybackFed_ReturnsTrue()
    {
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: true);

        // Feed some playback audio
        var playback = new short[480]; // 10ms at 48kHz mono
        _processor.FeedPlaybackAudio(playback, 48000, 1);

        Assert.True(_processor.IsAecActive);
    }

    [Fact]
    public void IsAecActive_AecDisabled_AlwaysFalse()
    {
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: false);

        // Feed playback audio
        var playback = new short[480];
        _processor.FeedPlaybackAudio(playback, 48000, 1);

        // Still false because AEC is disabled
        Assert.False(_processor.IsAecActive);
    }

    #endregion

    #region Playback Feed Tests

    [Fact]
    public void FeedPlaybackAudio_MatchingSampleRate_Succeeds()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        var playback = new short[480];
        // Should not throw
        _processor.FeedPlaybackAudio(playback, 48000, 1);

        Assert.True(_processor.IsAecActive);
    }

    [Fact]
    public void FeedPlaybackAudio_MismatchedSampleRate_SilentlyIgnored()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        var playback = new short[160]; // 10ms at 16kHz
        _processor.FeedPlaybackAudio(playback, 16000, 1);

        // Mismatched sample rate is silently ignored - AEC should not be active
        Assert.False(_processor.IsAecActive);
    }

    [Fact]
    public void FeedPlaybackAudio_MonoToStereoProcessor_ConvertsCorrectly()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 2);

        // Feed mono playback to stereo processor
        var monoPlayback = new short[480];
        for (int i = 0; i < monoPlayback.Length; i++)
            monoPlayback[i] = (short)(i * 10);

        _processor.FeedPlaybackAudio(monoPlayback, 48000, 1);

        Assert.True(_processor.IsAecActive);
    }

    [Fact]
    public void FeedPlaybackAudio_StereoToMonoProcessor_ConvertsCorrectly()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        // Feed stereo playback to mono processor
        var stereoPlayback = new short[960]; // 480 stereo frames
        for (int i = 0; i < stereoPlayback.Length; i++)
            stereoPlayback[i] = (short)(i * 5);

        _processor.FeedPlaybackAudio(stereoPlayback, 48000, 2);

        Assert.True(_processor.IsAecActive);
    }

    #endregion

    #region Capture Processing Tests

    [Fact]
    public void ProcessCaptureAudio_MatchingFormat_ReturnsProcessedAudio()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        // 10ms of mono audio at 48kHz = 480 samples
        var input = new short[480];
        var output = new short[480];

        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(Math.Sin(i * 0.1) * 10000);

        int processed = _processor.ProcessCaptureAudio(input, output, 48000, 1);

        Assert.True(processed > 0);
    }

    [Fact]
    public void ProcessCaptureAudio_MismatchedSampleRate_PassesThrough()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        var input = new short[160]; // 10ms at 16kHz
        var output = new short[160];

        for (int i = 0; i < input.Length; i++)
            input[i] = (short)(i * 100);

        int processed = _processor.ProcessCaptureAudio(input, output, 16000, 1);

        // Should pass through unchanged
        Assert.Equal(input.Length, processed);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[i]);
    }

    [Fact]
    public void ProcessCaptureAudio_MonoInputStereoProcessor_ConvertsAndProcesses()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 2);

        // Feed mono input to stereo processor
        var monoInput = new short[480];
        var monoOutput = new short[480];

        for (int i = 0; i < monoInput.Length; i++)
            monoInput[i] = (short)(Math.Sin(i * 0.1) * 10000);

        int processed = _processor.ProcessCaptureAudio(monoInput, monoOutput, 48000, 1);

        // Should return mono output (same as input channel count)
        Assert.True(processed > 0);
        Assert.True(processed <= monoInput.Length);
    }

    [Fact]
    public void ProcessCaptureAudio_StereoInputMonoProcessor_ConvertsAndProcesses()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        // Feed stereo input to mono processor
        var stereoInput = new short[960]; // 480 stereo frames
        var stereoOutput = new short[960];

        for (int i = 0; i < stereoInput.Length; i++)
            stereoInput[i] = (short)(Math.Sin(i * 0.05) * 10000);

        int processed = _processor.ProcessCaptureAudio(stereoInput, stereoOutput, 48000, 2);

        // Should return stereo output (same as input channel count)
        Assert.True(processed > 0);
        Assert.True(processed <= stereoInput.Length);
    }

    [Fact]
    public void ProcessCaptureAudio_MultipleFrames_AccumulatesCorrectly()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        // Process multiple frames
        int totalProcessed = 0;
        var input = new short[480];
        var output = new short[480];

        for (int frame = 0; frame < 10; frame++)
        {
            for (int i = 0; i < input.Length; i++)
                input[i] = (short)(Math.Sin((frame * 480 + i) * 0.1) * 10000);

            totalProcessed += _processor.ProcessCaptureAudio(input, output, 48000, 1);
        }

        // Should have processed all frames
        Assert.True(totalProcessed > 0);
    }

    [Fact]
    public void ProcessCaptureAudio_SmallBuffer_AccumulatesUntilFrame()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        // Feed audio smaller than frame size (480 samples)
        var smallInput = new short[100];
        var output = new short[100];

        for (int i = 0; i < smallInput.Length; i++)
            smallInput[i] = (short)(i * 100);

        int processed = _processor.ProcessCaptureAudio(smallInput, output, 48000, 1);

        // May return 0 while accumulating, or partial output from previous accumulated frames
        Assert.True(processed >= 0);
    }

    [Fact]
    public void ProcessCaptureAudio_LargeBuffer_ProcessesMultipleFrames()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        // Feed audio larger than frame size (multiple 480-sample frames)
        var largeInput = new short[2400]; // 5 frames
        var largeOutput = new short[2400];

        for (int i = 0; i < largeInput.Length; i++)
            largeInput[i] = (short)(Math.Sin(i * 0.1) * 10000);

        int processed = _processor.ProcessCaptureAudio(largeInput, largeOutput, 48000, 1);

        // Should process most or all of the input
        Assert.True(processed >= 480 * 4); // At least 4 complete frames
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        _processor.Dispose();
        _processor.Dispose(); // Should not throw
    }

    [Fact]
    public void ProcessCaptureAudio_AfterDispose_ReturnsZero()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);
        _processor.Dispose();

        var input = new short[480];
        var output = new short[480];

        int processed = _processor.ProcessCaptureAudio(input, output, 48000, 1);

        Assert.Equal(0, processed);
    }

    [Fact]
    public void FeedPlaybackAudio_AfterDispose_DoesNotThrow()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);
        _processor.Dispose();

        var playback = new short[480];
        // Should not throw, just silently return
        _processor.FeedPlaybackAudio(playback, 48000, 1);
    }

    #endregion

    #region Stream Delay Tests

    [Fact]
    public void SetStreamDelay_ValidValue_CanBeRetrieved()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);

        _processor.SetStreamDelay(50);
        int delay = _processor.GetStreamDelay();

        Assert.Equal(50, delay);
    }

    [Fact]
    public void GetStreamDelay_AfterDispose_ReturnsZero()
    {
        _processor = new WebRtcAudioProcessor(sampleRate: 48000, channels: 1);
        _processor.SetStreamDelay(100);
        _processor.Dispose();

        int delay = _processor.GetStreamDelay();

        Assert.Equal(0, delay);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void FullPipeline_FeedPlaybackThenProcessCapture_Works()
    {
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: true,
            enableNoiseSuppression: true);

        // Simulate typical usage: feed playback, then process capture
        var playback = new short[480];
        var capture = new short[480];
        var output = new short[480];

        // Fill with test data
        for (int i = 0; i < 480; i++)
        {
            playback[i] = (short)(Math.Sin(i * 0.2) * 8000);
            capture[i] = (short)(Math.Sin(i * 0.1) * 10000 + Math.Sin(i * 0.2) * 4000); // Capture includes echo
        }

        // Feed playback first (AEC reference)
        _processor.FeedPlaybackAudio(playback, 48000, 1);
        Assert.True(_processor.IsAecActive);

        // Process capture
        int processed = _processor.ProcessCaptureAudio(capture, output, 48000, 1);

        Assert.True(processed > 0);
    }

    [Fact]
    public void FullPipeline_MonoCaptureWithStereoPlayback_Works()
    {
        // This tests a common real-world scenario:
        // - Playback is stereo (speakers)
        // - Capture is mono (microphone)
        // - Processor is mono (to match Opus encoder)
        _processor = new WebRtcAudioProcessor(
            sampleRate: 48000,
            channels: 1,
            enableAec: true);

        // Stereo playback
        var stereoPlayback = new short[960]; // 480 stereo frames
        for (int i = 0; i < stereoPlayback.Length; i++)
            stereoPlayback[i] = (short)(Math.Sin(i * 0.1) * 8000);

        _processor.FeedPlaybackAudio(stereoPlayback, 48000, 2);

        // Mono capture
        var monoCapture = new short[480];
        var monoOutput = new short[480];
        for (int i = 0; i < monoCapture.Length; i++)
            monoCapture[i] = (short)(Math.Sin(i * 0.15) * 10000);

        int processed = _processor.ProcessCaptureAudio(monoCapture, monoOutput, 48000, 1);

        Assert.True(processed > 0);
    }

    #endregion
}

/// <summary>
/// Tests for NullAudioProcessor to verify passthrough behavior.
/// </summary>
public class NullAudioProcessorTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        var instance1 = NullAudioProcessor.Instance;
        var instance2 = NullAudioProcessor.Instance;

        Assert.Same(instance1, instance2);
    }

    [Fact]
    public void IsAecActive_AlwaysFalse()
    {
        Assert.False(NullAudioProcessor.Instance.IsAecActive);
    }

    [Fact]
    public void IsNoiseSuppressionEnabled_AlwaysFalse()
    {
        Assert.False(NullAudioProcessor.Instance.IsNoiseSuppressionEnabled);
    }

    [Fact]
    public void IsAgcEnabled_AlwaysFalse()
    {
        Assert.False(NullAudioProcessor.Instance.IsAgcEnabled);
    }

    [Fact]
    public void ProcessCaptureAudio_PassesThrough()
    {
        var input = new short[] { 100, 200, 300, 400, 500 };
        var output = new short[5];

        int processed = NullAudioProcessor.Instance.ProcessCaptureAudio(input, output, 48000, 1);

        Assert.Equal(input.Length, processed);
        for (int i = 0; i < input.Length; i++)
            Assert.Equal(input[i], output[i]);
    }

    [Fact]
    public void FeedPlaybackAudio_DoesNothing()
    {
        var playback = new short[] { 100, 200, 300 };

        // Should not throw
        NullAudioProcessor.Instance.FeedPlaybackAudio(playback, 48000, 1);

        // Still not active
        Assert.False(NullAudioProcessor.Instance.IsAecActive);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Dispose should be a no-op for singleton
        NullAudioProcessor.Instance.Dispose();

        // Instance should still work
        Assert.False(NullAudioProcessor.Instance.IsAecActive);
    }
}
