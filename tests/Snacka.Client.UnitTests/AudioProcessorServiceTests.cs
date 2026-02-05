using Moq;
using Snacka.Client.Services;
using Snacka.Client.Services.Audio;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for AudioProcessorService to verify correct processor lifecycle management.
/// These tests validate that the processor is created/disposed based on settings,
/// preventing issues like "no audio when NS is disabled but AEC is enabled".
/// </summary>
public class AudioProcessorServiceTests : IDisposable
{
    private readonly Mock<ISettingsStore> _mockSettingsStore;
    private readonly UserSettings _settings;
    private AudioProcessorService? _service;

    public AudioProcessorServiceTests()
    {
        _settings = new UserSettings();
        _mockSettingsStore = new Mock<ISettingsStore>();
        _mockSettingsStore.Setup(s => s.Settings).Returns(_settings);
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithSettingsStore_DoesNotThrow()
    {
        _service = new AudioProcessorService(_mockSettingsStore.Object);
        Assert.NotNull(_service);
    }

    [Fact]
    public void Constructor_ProcessorIsNullProcessor()
    {
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    #endregion

    #region Initialize Tests - Processor Creation

    [Fact]
    public void Initialize_BothAecAndNsEnabled_CreatesWebRtcProcessor()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        Assert.NotSame(NullAudioProcessor.Instance, _service.Processor);
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);
    }

    [Fact]
    public void Initialize_OnlyAecEnabled_CreatesWebRtcProcessor()
    {
        // This is the scenario that caused "no audio when NS is disabled"
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = false;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        Assert.NotSame(NullAudioProcessor.Instance, _service.Processor);
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);
    }

    [Fact]
    public void Initialize_OnlyNsEnabled_CreatesWebRtcProcessor()
    {
        _settings.EchoCancellation = false;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        Assert.NotSame(NullAudioProcessor.Instance, _service.Processor);
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);
    }

    [Fact]
    public void Initialize_BothDisabled_UsesNullProcessor()
    {
        _settings.EchoCancellation = false;
        _settings.NoiseSuppression = false;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    [Fact]
    public void Initialize_CalledTwice_DisposesOldProcessor()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        var firstProcessor = _service.Processor;

        _service.Initialize();
        var secondProcessor = _service.Processor;

        // Should be different instances
        Assert.NotSame(firstProcessor, secondProcessor);
    }

    [Fact]
    public void Initialize_SettingsChangedToDisabled_SwitchesToNullProcessor()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);

        // Change settings
        _settings.EchoCancellation = false;
        _settings.NoiseSuppression = false;

        _service.Initialize();
        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    [Fact]
    public void Initialize_SettingsChangedToEnabled_SwitchesToWebRtcProcessor()
    {
        _settings.EchoCancellation = false;
        _settings.NoiseSuppression = false;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        Assert.Same(NullAudioProcessor.Instance, _service.Processor);

        // Change settings
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = false;

        _service.Initialize();
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);
    }

    #endregion

    #region Initialize Tests - Processor Configuration

    [Fact]
    public void Initialize_WithAecEnabled_ProcessorHasAecCapability()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = false;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        // Feed playback to activate AEC
        var playback = new short[480];
        _service.Processor.FeedPlaybackAudio(playback, 48000, 1);

        Assert.True(_service.Processor.IsAecActive);
    }

    [Fact]
    public void Initialize_WithNsEnabled_ProcessorHasNsEnabled()
    {
        _settings.EchoCancellation = false;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        Assert.True(_service.Processor.IsNoiseSuppressionEnabled);
    }

    [Fact]
    public void Initialize_WithAecDisabled_ProcessorAecNotActive()
    {
        _settings.EchoCancellation = false;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        // Even after feeding playback, AEC should not be active
        var playback = new short[480];
        _service.Processor.FeedPlaybackAudio(playback, 48000, 1);

        Assert.False(_service.Processor.IsAecActive);
    }

    [Fact]
    public void Initialize_WithNsDisabled_ProcessorNsNotEnabled()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = false;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();

        Assert.False(_service.Processor.IsNoiseSuppressionEnabled);
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_AfterInitialize_SwitchesToNullProcessor()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);

        _service.Reset();

        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    [Fact]
    public void Reset_WhenAlreadyNullProcessor_DoesNotThrow()
    {
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        // Should not throw
        _service.Reset();
        _service.Reset();

        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    [Fact]
    public void Reset_ThenInitialize_CreatesNewProcessor()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        _service.Reset();
        _service.Initialize();

        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_AfterInitialize_SwitchesToNullProcessor()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        _service.Dispose();

        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        _service = new AudioProcessorService(_mockSettingsStore.Object);
        _service.Initialize();

        _service.Dispose();
        _service.Dispose();
        _service.Dispose();
    }

    [Fact]
    public void Initialize_AfterDispose_DoesNothing()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Dispose();
        _service.Initialize();

        // Should still be NullProcessor because service is disposed
        Assert.Same(NullAudioProcessor.Instance, _service.Processor);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void IsEchoCancellationEnabled_ReflectsSettings()
    {
        _settings.EchoCancellation = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        Assert.True(_service.IsEchoCancellationEnabled);

        _settings.EchoCancellation = false;
        Assert.False(_service.IsEchoCancellationEnabled);
    }

    [Fact]
    public void IsNoiseSuppressionEnabled_ReflectsSettings()
    {
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        Assert.True(_service.IsNoiseSuppressionEnabled);

        _settings.NoiseSuppression = false;
        Assert.False(_service.IsNoiseSuppressionEnabled);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void FullLifecycle_JoinLeaveRejoin_WorksCorrectly()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        // Join voice channel
        _service.Initialize();
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);

        // Use processor
        var playback = new short[480];
        _service.Processor.FeedPlaybackAudio(playback, 48000, 1);
        Assert.True(_service.Processor.IsAecActive);

        // Leave voice channel
        _service.Reset();
        Assert.Same(NullAudioProcessor.Instance, _service.Processor);

        // Rejoin voice channel
        _service.Initialize();
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor);
    }

    [Fact]
    public void SettingsToggle_DuringSession_WorksCorrectly()
    {
        _settings.EchoCancellation = true;
        _settings.NoiseSuppression = true;
        _service = new AudioProcessorService(_mockSettingsStore.Object);

        _service.Initialize();
        Assert.True(_service.Processor.IsNoiseSuppressionEnabled);

        // User disables NS mid-session
        _settings.NoiseSuppression = false;
        _service.Initialize(); // Re-initialize to pick up changes

        Assert.False(_service.Processor.IsNoiseSuppressionEnabled);
        Assert.IsType<WebRtcAudioProcessor>(_service.Processor); // Still has processor for AEC
    }

    #endregion
}
