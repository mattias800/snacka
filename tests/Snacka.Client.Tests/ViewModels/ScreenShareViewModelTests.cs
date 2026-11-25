using Moq;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class ScreenShareViewModelTests
{
    private readonly Mock<IScreenCaptureService> _screenCaptureServiceMock;
    private readonly Mock<ISignalRService> _signalRMock;
    private readonly Mock<IWebRtcService> _webRtcMock;
    private readonly AnnotationService _annotationService;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _username = "TestUser";

    public ScreenShareViewModelTests()
    {
        _screenCaptureServiceMock = new Mock<IScreenCaptureService>();
        _signalRMock = new Mock<ISignalRService>();
        _webRtcMock = new Mock<IWebRtcService>();
        _annotationService = new AnnotationService(_signalRMock.Object);

        // Setup default return values for screen capture service
        _screenCaptureServiceMock.Setup(x => x.GetDisplays())
            .Returns(new List<ScreenCaptureSource>());
        _screenCaptureServiceMock.Setup(x => x.GetWindows())
            .Returns(new List<ScreenCaptureSource>());
        _screenCaptureServiceMock.Setup(x => x.GetApplications())
            .Returns(new List<ScreenCaptureSource>());
    }

    private (ScreenShareViewModel vm, Mock<IVoiceStore> voiceStoreMock) CreateViewModel(Guid? currentChannelId = null)
    {
        var voiceStoreMock = new Mock<IVoiceStore>();
        voiceStoreMock.Setup(x => x.GetCurrentChannelId()).Returns(currentChannelId);

        var vm = new ScreenShareViewModel(
            _screenCaptureServiceMock.Object,
            _signalRMock.Object,
            _webRtcMock.Object,
            _annotationService,
            voiceStoreMock.Object,
            _userId,
            _username);

        return (vm, voiceStoreMock);
    }

    #region Initialization Tests

    [Fact]
    public void Constructor_InitializesWithDefaultState()
    {
        // Act
        var (vm, _) = CreateViewModel();

        // Assert
        Assert.False(vm.IsScreenSharing);
        Assert.False(vm.IsScreenSharePickerOpen);
        Assert.Null(vm.ScreenSharePicker);
        Assert.Null(vm.CurrentSettings);
        Assert.False(vm.IsDrawingAllowedForViewers);
    }

    [Fact]
    public void Constructor_InitializesCommand()
    {
        // Act
        var (vm, _) = CreateViewModel();

        // Assert
        Assert.NotNull(vm.ToggleScreenShareCommand);
    }

    #endregion

    #region ToggleScreenShareAsync Tests

    [Fact]
    public async Task ToggleScreenShareAsync_WhenNotInChannel_DoesNothing()
    {
        // Arrange
        var (vm, _) = CreateViewModel(currentChannelId: null);

        // Act
        await vm.ToggleScreenShareAsync();

        // Assert
        Assert.False(vm.IsScreenSharePickerOpen);
        Assert.Null(vm.ScreenSharePicker);
    }

    [Fact]
    public async Task ToggleScreenShareAsync_WhenNotSharing_OpensPicker()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Act
        await vm.ToggleScreenShareAsync();

        // Assert
        Assert.True(vm.IsScreenSharePickerOpen);
        Assert.NotNull(vm.ScreenSharePicker);
    }

    [Fact]
    public async Task ToggleScreenShareAsync_WhenSharing_StopsSharing()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Start sharing first
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.True(vm.IsScreenSharing);

        // Setup for stop
        _webRtcMock.Setup(x => x.SetScreenSharingAsync(false, null))
            .Returns(Task.CompletedTask);

        // Act - toggle again to stop
        await vm.ToggleScreenShareAsync();

        // Assert
        Assert.False(vm.IsScreenSharing);
        Assert.Null(vm.CurrentSettings);
    }

    #endregion

    #region StartScreenShareWithSettingsAsync Tests

    [Fact]
    public async Task StartScreenShareWithSettingsAsync_WhenNotInChannel_DoesNothing()
    {
        // Arrange
        var (vm, _) = CreateViewModel(currentChannelId: null);
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        // Act
        await vm.StartScreenShareWithSettingsAsync(settings);

        // Assert
        Assert.False(vm.IsScreenSharing);
        _webRtcMock.Verify(x => x.SetScreenSharingAsync(It.IsAny<bool>(), It.IsAny<ScreenShareSettings>()), Times.Never);
    }

    [Fact]
    public async Task StartScreenShareWithSettingsAsync_StartsSharing()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            true);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.StartScreenShareWithSettingsAsync(settings);

        // Assert
        Assert.True(vm.IsScreenSharing);
        Assert.Equal(settings, vm.CurrentSettings);
        _webRtcMock.Verify(x => x.SetScreenSharingAsync(true, settings), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(channelId,
            It.Is<VoiceStateUpdate>(u => u.IsScreenSharing == true && u.ScreenShareHasAudio == true)), Times.Once);
    }

    [Fact]
    public async Task StartScreenShareWithSettingsAsync_UpdatesVoiceStore()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, voiceStoreMock) = CreateViewModel(currentChannelId: channelId);

        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Window, "1", "TestWindow");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.StartScreenShareWithSettingsAsync(settings);

        // Assert - VoiceStore should be updated for immediate UI feedback
        voiceStoreMock.Verify(x => x.SetLocalScreenSharing(true), Times.Once);
        voiceStoreMock.Verify(x => x.UpdateVoiceState(channelId, _userId,
            It.Is<VoiceStateUpdate>(u => u.IsScreenSharing == true)), Times.Once);
    }

    [Fact]
    public async Task StartScreenShareWithSettingsAsync_DisplaySharing_RaisesShowAnnotationOverlayRequested()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        ScreenShareSettings? capturedSettings = null;
        ScreenAnnotationViewModel? capturedAnnotationVm = null;
        vm.ShowAnnotationOverlayRequested += (s, a) =>
        {
            capturedSettings = s;
            capturedAnnotationVm = a;
        };

        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD1080,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.StartScreenShareWithSettingsAsync(settings);

        // Assert
        Assert.NotNull(capturedSettings);
        Assert.Equal(settings, capturedSettings);
        Assert.NotNull(capturedAnnotationVm);
        Assert.NotNull(vm.AnnotationViewModel);
    }

    [Fact]
    public async Task StartScreenShareWithSettingsAsync_WindowSharing_DoesNotRaiseShowAnnotationOverlayRequested()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        var wasEventRaised = false;
        vm.ShowAnnotationOverlayRequested += (_, _) => wasEventRaised = true;

        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Window, "1", "TestWindow");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.StartScreenShareWithSettingsAsync(settings);

        // Assert
        Assert.False(wasEventRaised);
        Assert.Null(vm.AnnotationViewModel);
    }

    [Fact]
    public async Task StartScreenShareWithSettingsAsync_OnError_ResetsStateAndRaisesErrorOccurred()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        string? capturedError = null;
        vm.ErrorOccurred += err => capturedError = err;

        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .ThrowsAsync(new Exception("WebRTC failed"));

        // Act
        await vm.StartScreenShareWithSettingsAsync(settings);

        // Assert
        Assert.False(vm.IsScreenSharing);
        Assert.Null(vm.CurrentSettings);
        Assert.NotNull(capturedError);
        Assert.Contains("WebRTC failed", capturedError);
    }

    #endregion

    #region StopScreenShareAsync Tests

    [Fact]
    public async Task StopScreenShareAsync_WhenNotInChannel_DoesNothing()
    {
        // Arrange
        var (vm, _) = CreateViewModel(currentChannelId: null);

        // Act
        await vm.StopScreenShareAsync();

        // Assert
        _webRtcMock.Verify(x => x.SetScreenSharingAsync(It.IsAny<bool>(), It.IsAny<ScreenShareSettings>()), Times.Never);
    }

    [Fact]
    public async Task StopScreenShareAsync_StopsSharing()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Start sharing first
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.True(vm.IsScreenSharing);

        // Setup for stop
        _webRtcMock.Setup(x => x.SetScreenSharingAsync(false, null))
            .Returns(Task.CompletedTask);

        // Act
        await vm.StopScreenShareAsync();

        // Assert
        Assert.False(vm.IsScreenSharing);
        Assert.Null(vm.CurrentSettings);
        _webRtcMock.Verify(x => x.SetScreenSharingAsync(false, null), Times.Once);
    }

    [Fact]
    public async Task StopScreenShareAsync_RaisesHideAnnotationOverlayRequested()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        var wasEventRaised = false;
        vm.HideAnnotationOverlayRequested += () => wasEventRaised = true;

        // Start sharing first
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _webRtcMock.Setup(x => x.SetScreenSharingAsync(false, null))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);

        // Act
        await vm.StopScreenShareAsync();

        // Assert
        Assert.True(wasEventRaised);
    }

    [Fact]
    public async Task StopScreenShareAsync_ClearsAnnotationViewModel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Start display sharing to create annotation VM
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _webRtcMock.Setup(x => x.SetScreenSharingAsync(false, null))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.NotNull(vm.AnnotationViewModel);

        // Act
        await vm.StopScreenShareAsync();

        // Assert
        Assert.Null(vm.AnnotationViewModel);
    }

    [Fact]
    public async Task StopScreenShareAsync_UpdatesVoiceStore()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, voiceStoreMock) = CreateViewModel(currentChannelId: channelId);

        // Start sharing first
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _webRtcMock.Setup(x => x.SetScreenSharingAsync(false, null))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);

        // Act
        await vm.StopScreenShareAsync();

        // Assert - VoiceStore should be updated for immediate UI feedback
        voiceStoreMock.Verify(x => x.SetLocalScreenSharing(false), Times.Once);
        voiceStoreMock.Verify(x => x.UpdateVoiceState(channelId, _userId,
            It.Is<VoiceStateUpdate>(u => u.IsScreenSharing == false)), Times.Once);
    }

    #endregion

    #region ForceStop Tests

    [Fact]
    public async Task ForceStop_WhenSharing_StopsAndRaisesEvent()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        var wasEventRaised = false;
        vm.HideAnnotationOverlayRequested += () => wasEventRaised = true;

        // Manually set sharing state (simulating started state)
        // We need to start sharing properly first
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.True(vm.IsScreenSharing);

        // Act
        vm.ForceStop();

        // Assert
        Assert.False(vm.IsScreenSharing);
        Assert.True(wasEventRaised);
    }

    [Fact]
    public void ForceStop_WhenNotSharing_DoesNotRaiseEvent()
    {
        // Arrange
        var (vm, _) = CreateViewModel();

        var wasEventRaised = false;
        vm.HideAnnotationOverlayRequested += () => wasEventRaised = true;

        // Act
        vm.ForceStop();

        // Assert
        Assert.False(wasEventRaised);
    }

    #endregion

    #region OnAnnotationToolbarCloseRequested Tests

    [Fact]
    public async Task OnAnnotationToolbarCloseRequested_CallsStopScreenShareAsync()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Start sharing
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _webRtcMock.Setup(x => x.SetScreenSharingAsync(false, null))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.True(vm.IsScreenSharing);

        // Act
        await vm.OnAnnotationToolbarCloseRequested();

        // Assert
        Assert.False(vm.IsScreenSharing);
    }

    #endregion

    #region StartFromStationCommandAsync Tests

    [Fact]
    public async Task StartFromStationCommandAsync_WhenNotInChannel_DoesNothing()
    {
        // Arrange
        var (vm, _) = CreateViewModel(currentChannelId: null);

        // Act
        await vm.StartFromStationCommandAsync();

        // Assert
        Assert.False(vm.IsScreenSharing);
        _screenCaptureServiceMock.Verify(x => x.GetDisplays(), Times.Never);
    }

    [Fact]
    public async Task StartFromStationCommandAsync_WhenNoDisplays_DoesNothing()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);
        _screenCaptureServiceMock.Setup(x => x.GetDisplays())
            .Returns(new List<ScreenCaptureSource>());

        // Act
        await vm.StartFromStationCommandAsync();

        // Assert
        Assert.False(vm.IsScreenSharing);
    }

    [Fact]
    public async Task StartFromStationCommandAsync_StartsWithGamingSettings()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        var display = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "Main Display");
        _screenCaptureServiceMock.Setup(x => x.GetDisplays())
            .Returns(new List<ScreenCaptureSource> { display });

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, It.IsAny<ScreenShareSettings>()))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.StartFromStationCommandAsync();

        // Assert
        Assert.True(vm.IsScreenSharing);
        Assert.NotNull(vm.CurrentSettings);
        Assert.Equal(ScreenShareResolution.HD1080, vm.CurrentSettings!.Resolution);
        Assert.Equal(ScreenShareFramerate.Fps60, vm.CurrentSettings.Framerate);
        Assert.Equal(ScreenShareQuality.Gaming, vm.CurrentSettings.Quality);
        Assert.False(vm.CurrentSettings.IncludeAudio);
    }

    #endregion

    #region IsDrawingAllowedForViewers Tests

    [Fact]
    public void IsDrawingAllowedForViewers_WhenNoAnnotationViewModel_ReturnsFalse()
    {
        // Arrange
        var (vm, _) = CreateViewModel();

        // Assert
        Assert.False(vm.IsDrawingAllowedForViewers);
    }

    [Fact]
    public async Task IsDrawingAllowedForViewers_WhenAnnotationViewModelExists_DelegatesToIt()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Start display sharing to create annotation VM
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.NotNull(vm.AnnotationViewModel);

        // Act
        vm.IsDrawingAllowedForViewers = true;

        // Assert
        Assert.True(vm.IsDrawingAllowedForViewers);
        Assert.True(vm.AnnotationViewModel!.IsDrawingAllowedForViewers);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CleansUpResources()
    {
        // Arrange
        var (vm, _) = CreateViewModel();

        // Act - should not throw
        vm.Dispose();

        // Assert - command should be disposed (this is tested by ensuring no exception)
        Assert.True(true);
    }

    [Fact]
    public async Task Dispose_CleansUpAnnotationViewModel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var (vm, _) = CreateViewModel(currentChannelId: channelId);

        // Start display sharing to create annotation VM
        var source = new ScreenCaptureSource(ScreenCaptureSourceType.Display, "1", "TestDisplay");
        var settings = new ScreenShareSettings(
            source,
            ScreenShareResolution.HD720,
            ScreenShareFramerate.Fps30,
            ScreenShareQuality.Balanced,
            false);

        _webRtcMock.Setup(x => x.SetScreenSharingAsync(true, settings))
            .Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        await vm.StartScreenShareWithSettingsAsync(settings);
        Assert.NotNull(vm.AnnotationViewModel);

        // Act
        vm.Dispose();

        // Assert
        Assert.Null(vm.AnnotationViewModel);
    }

    #endregion
}
