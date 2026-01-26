using System.Reactive;
using System.Reactive.Linq;
using Moq;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Tests.ViewModels;

public class AudioSettingsViewModelTests : IDisposable
{
    private readonly Mock<ISettingsStore> _mockSettingsStore;
    private readonly Mock<IAudioDeviceService> _mockAudioDeviceService;
    private readonly UserSettings _settings;

    public AudioSettingsViewModelTests()
    {
        _mockSettingsStore = new Mock<ISettingsStore>();
        _mockAudioDeviceService = new Mock<IAudioDeviceService>();
        _settings = new UserSettings();
        _mockSettingsStore.Setup(x => x.Settings).Returns(_settings);

        // Default device lists
        _mockAudioDeviceService.Setup(x => x.GetInputDevicesAsync())
            .ReturnsAsync(new List<AudioDeviceInfo> { new("0", "Microphone 1"), new("1", "Microphone 2") });
        _mockAudioDeviceService.Setup(x => x.GetOutputDevices())
            .Returns(new List<string> { "Speakers", "Headphones" });
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    private AudioSettingsViewModel CreateViewModel()
    {
        return new AudioSettingsViewModel(
            _mockSettingsStore.Object,
            _mockAudioDeviceService.Object
        );
    }

    #region Initial State Tests

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsTestingMicrophone);
        Assert.False(vm.IsLoopbackEnabled);
        Assert.Equal(0, vm.InputLevel);
        Assert.Equal(1.0f, vm.AgcGain);
        Assert.False(vm.IsLoadingDevices);
    }

    [Fact]
    public void Constructor_LoadsSettingsFromStore()
    {
        // Arrange
        _settings.AudioInputDevice = "Microphone 1";
        _settings.AudioOutputDevice = "Speakers";
        _settings.InputGain = 1.5f;
        _settings.GateThreshold = 0.1f;
        _settings.GateEnabled = true;

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Equal(1.5f, vm.InputGain);
        Assert.Equal(0.1f, vm.GateThreshold);
        Assert.True(vm.GateEnabled);
    }

    [Fact]
    public void Constructor_AddsDefaultDeviceOptions()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.Single(vm.InputDevices);
        Assert.Single(vm.OutputDevices);
        Assert.Equal("System default", vm.InputDevices[0].DisplayName);
        Assert.Equal("System default", vm.OutputDevices[0].DisplayName);
    }

    #endregion

    #region Device Refresh Tests

    [Fact]
    public async Task InitializeAsync_PopulatesDeviceLists()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.InitializeAsync();

        // Assert
        Assert.Equal(3, vm.InputDevices.Count); // Default + 2 devices
        Assert.Equal(3, vm.OutputDevices.Count); // Default + 2 devices
        Assert.Equal("System default", vm.InputDevices[0].DisplayName);
        Assert.Equal("Microphone 1", vm.InputDevices[1].DisplayName);
        Assert.Equal("Microphone 2", vm.InputDevices[2].DisplayName);
    }

    [Fact]
    public async Task RefreshDevicesCommand_RefreshesDeviceLists()
    {
        // Arrange
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        Assert.Equal(3, vm.InputDevices.Count);

        // Update mock to return different devices
        _mockAudioDeviceService.Setup(x => x.GetInputDevicesAsync())
            .ReturnsAsync(new List<AudioDeviceInfo> { new("0", "New Microphone") });
        _mockAudioDeviceService.Setup(x => x.GetOutputDevices())
            .Returns(new List<string> { "New Speaker" });

        // Act
        var cmd = (ReactiveCommand<Unit, Unit>)vm.RefreshDevicesCommand;
        await cmd.Execute();

        // Assert
        Assert.Equal(2, vm.InputDevices.Count); // Default + 1 device
        Assert.Equal("New Microphone", vm.InputDevices[1].DisplayName);
    }

    #endregion

    #region Input Gain Tests

    [Fact]
    public void InputGain_WhenSet_SavesAndNotifies()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.InputGain))
                propertyChangedRaised = true;
        };

        // Act
        vm.InputGain = 2.0f;

        // Assert
        Assert.True(propertyChangedRaised);
        Assert.Equal(2.0f, _settings.InputGain);
        _mockSettingsStore.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public void InputGainPercent_CalculatesCorrectly()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.InputGain = 1.5f;

        // Assert
        Assert.Equal(150, vm.InputGainPercent);
    }

    [Fact]
    public void InputGain_WithSameValue_DoesNotSave()
    {
        // Arrange
        _settings.InputGain = 1.5f;
        var vm = CreateViewModel();

        // Act
        vm.InputGain = 1.5f;

        // Assert
        _mockSettingsStore.Verify(x => x.Save(), Times.Never);
    }

    #endregion

    #region Gate Threshold Tests

    [Fact]
    public void GateThreshold_WhenSet_SavesAndNotifies()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.GateThreshold))
                propertyChangedRaised = true;
        };

        // Act
        vm.GateThreshold = 0.15f;

        // Assert
        Assert.True(propertyChangedRaised);
        Assert.Equal(0.15f, _settings.GateThreshold);
        _mockSettingsStore.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public void GateThresholdPercent_CalculatesCorrectly()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.GateThreshold = 0.25f;

        // Assert
        Assert.Equal(25, vm.GateThresholdPercent);
    }

    #endregion

    #region Gate Enabled Tests

    [Fact]
    public void GateEnabled_WhenSet_SavesAndNotifies()
    {
        // Arrange
        _settings.GateEnabled = false; // Start with false before creating ViewModel
        var vm = CreateViewModel();
        Assert.False(vm.GateEnabled);

        // Act
        vm.GateEnabled = true;

        // Assert
        Assert.True(vm.GateEnabled);
        Assert.True(_settings.GateEnabled);
        _mockSettingsStore.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public void IsAboveGate_WhenGateDisabled_ReturnsTrue()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.GateEnabled = false;
        vm.GateThreshold = 0.5f;
        vm.InputLevel = 0.1f; // Below threshold

        // Act & Assert
        Assert.True(vm.IsAboveGate);
    }

    [Fact]
    public void IsAboveGate_WhenGateEnabled_ReturnsBasedOnLevel()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.GateEnabled = true;
        vm.GateThreshold = 0.5f;

        // Act & Assert - below threshold
        vm.InputLevel = 0.3f;
        Assert.False(vm.IsAboveGate);

        // Act & Assert - above threshold
        vm.InputLevel = 0.6f;
        Assert.True(vm.IsAboveGate);
    }

    #endregion

    #region AGC Tests

    [Fact]
    public void AgcGainDisplay_FormatsCorrectly()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.AgcGain = 2.0f;

        // Assert
        // The display format uses current culture, so we check the value contains "2" and "x"
        Assert.Contains("2", vm.AgcGainDisplay);
        Assert.EndsWith("x", vm.AgcGainDisplay);
    }

    [Fact]
    public void AgcStatus_ReturnsCorrectStatus()
    {
        // Arrange
        var vm = CreateViewModel();

        // Normal
        vm.AgcGain = 1.0f;
        Assert.Equal("Normal", vm.AgcStatus);

        // Boosting
        vm.AgcGain = 2.0f;
        Assert.Equal("Boosting", vm.AgcStatus);

        // High boost
        vm.AgcGain = 4.0f;
        Assert.Equal("High boost", vm.AgcStatus);

        // Max boost
        vm.AgcGain = 7.0f;
        Assert.Equal("Max boost", vm.AgcStatus);
    }

    [Fact]
    public void AgcBoostPercent_CalculatesCorrectly()
    {
        // Arrange
        var vm = CreateViewModel();

        // 1x = 0%
        vm.AgcGain = 1.0f;
        Assert.Equal(0f, vm.AgcBoostPercent);

        // 8x = 100%
        vm.AgcGain = 8.0f;
        Assert.Equal(100f, vm.AgcBoostPercent);

        // 4.5x = 50%
        vm.AgcGain = 4.5f;
        Assert.Equal(50f, vm.AgcBoostPercent);
    }

    #endregion

    #region Device Selection Tests

    [Fact]
    public async Task SelectedInputDeviceItem_WhenSet_SavesAndNotifies()
    {
        // Arrange
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedInputDeviceItem))
                propertyChangedRaised = true;
        };

        // Act
        vm.SelectedInputDeviceItem = vm.InputDevices[1]; // "Microphone 1"

        // Assert
        Assert.True(propertyChangedRaised);
        Assert.Equal("Microphone 1", _settings.AudioInputDevice);
        _mockSettingsStore.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public async Task SelectedOutputDeviceItem_WhenSet_SavesAndNotifies()
    {
        // Arrange
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedOutputDeviceItem))
                propertyChangedRaised = true;
        };

        // Act
        vm.SelectedOutputDeviceItem = vm.OutputDevices[1]; // "Speakers"

        // Assert
        Assert.True(propertyChangedRaised);
        Assert.Equal("Speakers", _settings.AudioOutputDevice);
        _mockSettingsStore.Verify(x => x.Save(), Times.Once);
    }

    [Fact]
    public async Task SelectedOutputDeviceItem_WithLoopbackEnabled_UpdatesLoopback()
    {
        // Arrange
        var vm = CreateViewModel();
        await vm.InitializeAsync();
        vm.IsLoopbackEnabled = true;

        // Act
        vm.SelectedOutputDeviceItem = vm.OutputDevices[2]; // "Headphones"

        // Assert
        _mockAudioDeviceService.Verify(
            x => x.SetLoopbackEnabled(true, "Headphones"),
            Times.Once);
    }

    #endregion

    #region Microphone Test Tests

    [Fact]
    public async Task TestMicrophoneCommand_StartsTest()
    {
        // Arrange
        _mockAudioDeviceService
            .Setup(x => x.StartInputTestAsync(It.IsAny<string?>(), It.IsAny<Action<float>>(), It.IsAny<Action<float>?>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        Assert.False(vm.IsTestingMicrophone);

        // Act
        var cmd = (ReactiveCommand<Unit, Unit>)vm.TestMicrophoneCommand;
        await cmd.Execute();

        // Assert
        Assert.True(vm.IsTestingMicrophone);
        _mockAudioDeviceService.Verify(
            x => x.StartInputTestAsync(It.IsAny<string?>(), It.IsAny<Action<float>>(), It.IsAny<Action<float>?>()),
            Times.Once);
    }

    [Fact]
    public async Task TestMicrophoneCommand_StopsTest_WhenAlreadyTesting()
    {
        // Arrange
        _mockAudioDeviceService
            .Setup(x => x.StartInputTestAsync(It.IsAny<string?>(), It.IsAny<Action<float>>(), It.IsAny<Action<float>?>()))
            .Returns(Task.CompletedTask);
        _mockAudioDeviceService
            .Setup(x => x.StopTestAsync())
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        var cmd = (ReactiveCommand<Unit, Unit>)vm.TestMicrophoneCommand;
        await cmd.Execute(); // Start
        Assert.True(vm.IsTestingMicrophone);

        // Act
        await cmd.Execute(); // Stop

        // Assert
        Assert.False(vm.IsTestingMicrophone);
        Assert.False(vm.IsLoopbackEnabled);
        Assert.Equal(0, vm.InputLevel);
        _mockAudioDeviceService.Verify(x => x.StopTestAsync(), Times.Once);
    }

    [Fact]
    public async Task TestMicrophoneCommand_HandlesException()
    {
        // Arrange
        _mockAudioDeviceService
            .Setup(x => x.StartInputTestAsync(It.IsAny<string?>(), It.IsAny<Action<float>>(), It.IsAny<Action<float>?>()))
            .ThrowsAsync(new Exception("Audio error"));

        var vm = CreateViewModel();

        // Act
        var cmd = (ReactiveCommand<Unit, Unit>)vm.TestMicrophoneCommand;
        await cmd.Execute();

        // Assert
        Assert.False(vm.IsTestingMicrophone);
    }

    #endregion

    #region Loopback Tests

    [Fact]
    public void ToggleLoopbackCommand_DoesNothing_WhenNotTesting()
    {
        // Arrange
        var vm = CreateViewModel();
        Assert.False(vm.IsTestingMicrophone);

        // Act
        vm.ToggleLoopbackCommand.Execute(null);

        // Assert
        Assert.False(vm.IsLoopbackEnabled);
        _mockAudioDeviceService.Verify(
            x => x.SetLoopbackEnabled(It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task ToggleLoopbackCommand_TogglesLoopback_WhenTesting()
    {
        // Arrange
        _mockAudioDeviceService
            .Setup(x => x.StartInputTestAsync(It.IsAny<string?>(), It.IsAny<Action<float>>(), It.IsAny<Action<float>?>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        var testCmd = (ReactiveCommand<Unit, Unit>)vm.TestMicrophoneCommand;
        await testCmd.Execute();
        Assert.False(vm.IsLoopbackEnabled);

        // Act - enable loopback
        vm.ToggleLoopbackCommand.Execute(null);

        // Assert
        Assert.True(vm.IsLoopbackEnabled);
        _mockAudioDeviceService.Verify(
            x => x.SetLoopbackEnabled(true, It.IsAny<string?>()),
            Times.Once);

        // Act - disable loopback
        vm.ToggleLoopbackCommand.Execute(null);

        // Assert
        Assert.False(vm.IsLoopbackEnabled);
        _mockAudioDeviceService.Verify(
            x => x.SetLoopbackEnabled(false, It.IsAny<string?>()),
            Times.Once);
    }

    #endregion
}
