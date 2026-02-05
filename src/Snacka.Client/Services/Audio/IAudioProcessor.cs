namespace Snacka.Client.Services.Audio;

/// <summary>
/// Interface for audio processing (AEC, noise suppression, AGC).
/// Allows the audio subsystem to process both capture and playback audio.
/// </summary>
public interface IAudioProcessor : IDisposable
{
    /// <summary>
    /// Feed playback audio for echo cancellation reference.
    /// Call this with audio being sent to speakers.
    /// </summary>
    /// <param name="samples">PCM samples (Int16, interleaved if stereo)</param>
    /// <param name="sampleRate">Sample rate in Hz</param>
    /// <param name="channels">Number of channels</param>
    void FeedPlaybackAudio(ReadOnlySpan<short> samples, int sampleRate, int channels);

    /// <summary>
    /// Process microphone audio through AEC, noise suppression, and AGC.
    /// </summary>
    /// <param name="input">Input PCM samples</param>
    /// <param name="output">Output buffer (same size as input)</param>
    /// <param name="sampleRate">Sample rate in Hz</param>
    /// <param name="channels">Number of channels</param>
    /// <returns>Number of samples written to output</returns>
    int ProcessCaptureAudio(ReadOnlySpan<short> input, Span<short> output, int sampleRate, int channels);

    /// <summary>
    /// Whether echo cancellation is enabled and has a valid reference signal.
    /// </summary>
    bool IsAecActive { get; }

    /// <summary>
    /// Whether noise suppression is enabled.
    /// </summary>
    bool IsNoiseSuppressionEnabled { get; }

    /// <summary>
    /// Whether automatic gain control is enabled.
    /// </summary>
    bool IsAgcEnabled { get; }
}

/// <summary>
/// Null implementation that passes audio through unchanged.
/// Used when WebRTC processing is disabled.
/// </summary>
public class NullAudioProcessor : IAudioProcessor
{
    public static readonly NullAudioProcessor Instance = new();

    public bool IsAecActive => false;
    public bool IsNoiseSuppressionEnabled => false;
    public bool IsAgcEnabled => false;

    public void FeedPlaybackAudio(ReadOnlySpan<short> samples, int sampleRate, int channels) { }

    public int ProcessCaptureAudio(ReadOnlySpan<short> input, Span<short> output, int sampleRate, int channels)
    {
        input.CopyTo(output);
        return input.Length;
    }

    public void Dispose() { }
}
