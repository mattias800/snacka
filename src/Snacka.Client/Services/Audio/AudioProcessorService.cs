namespace Snacka.Client.Services.Audio;

/// <summary>
/// Manages the WebRTC audio processor lifecycle.
/// Provides a central point for audio processing configuration (AEC, noise suppression, AGC).
/// </summary>
public interface IAudioProcessorService : IDisposable
{
    /// <summary>
    /// Gets the current audio processor instance.
    /// Returns NullAudioProcessor if processing is disabled.
    /// </summary>
    IAudioProcessor Processor { get; }

    /// <summary>
    /// Gets whether echo cancellation is enabled in settings.
    /// </summary>
    bool IsEchoCancellationEnabled { get; }

    /// <summary>
    /// Gets whether noise suppression is enabled in settings.
    /// </summary>
    bool IsNoiseSuppressionEnabled { get; }

    /// <summary>
    /// Initializes or reinitializes the processor with current settings.
    /// Call this when joining a voice channel or when settings change.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Resets the processor (disposes current and creates new on next Initialize).
    /// Call this when leaving a voice channel.
    /// </summary>
    void Reset();
}

public class AudioProcessorService : IAudioProcessorService
{
    private readonly ISettingsStore _settingsStore;
    private IAudioProcessor _processor = NullAudioProcessor.Instance;
    private bool _disposed;

    public AudioProcessorService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public IAudioProcessor Processor => _processor;

    public bool IsEchoCancellationEnabled => _settingsStore.Settings.EchoCancellation;
    public bool IsNoiseSuppressionEnabled => _settingsStore.Settings.NoiseSuppression;

    public void Initialize()
    {
        if (_disposed) return;

        // If we have any processing enabled, create the WebRTC processor
        var enableAec = _settingsStore.Settings.EchoCancellation;
        var enableNs = _settingsStore.Settings.NoiseSuppression;

        // Only create processor if some feature is enabled
        if (!enableAec && !enableNs)
        {
            Console.WriteLine("AudioProcessorService: All processing disabled, using passthrough");
            if (_processor != NullAudioProcessor.Instance)
            {
                _processor.Dispose();
                _processor = NullAudioProcessor.Instance;
            }
            return;
        }

        // Dispose existing processor if any
        if (_processor != NullAudioProcessor.Instance)
        {
            _processor.Dispose();
        }

        try
        {
            // Use mono (1 channel) since Opus decoder outputs mono
            // Use Moderate noise suppression to avoid cutting off speech
            _processor = new WebRtcAudioProcessor(
                sampleRate: 48000,
                channels: 1,
                enableAec: enableAec,
                enableNoiseSuppression: enableNs,
                noiseSuppressionLevel: SoundFlow.Extensions.WebRtc.Apm.NoiseSuppressionLevel.Moderate,
                enableAgc: false // AGC is handled separately in AudioInputManager
            );

            Console.WriteLine($"AudioProcessorService: Initialized WebRTC processor (AEC={enableAec}, NS={enableNs})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioProcessorService: Failed to create WebRTC processor: {ex.Message}");
            _processor = NullAudioProcessor.Instance;
        }
    }

    public void Reset()
    {
        if (_processor != NullAudioProcessor.Instance)
        {
            _processor.Dispose();
            _processor = NullAudioProcessor.Instance;
            Console.WriteLine("AudioProcessorService: Reset (processor disposed)");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Reset();
    }
}
