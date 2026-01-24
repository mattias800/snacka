using Moq;
using Snacka.Client.Services;
using Snacka.Client.Services.WebRtc;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;

namespace Snacka.Client.Tests.ViewModels;

public class VideoFullscreenViewModelTests : IDisposable
{
    private readonly Mock<IWebRtcService> _mockWebRtc;
    private readonly Mock<IControllerStreamingService> _mockControllerStreaming;
    private readonly AnnotationService _annotationService;
    private readonly Mock<ISignalRService> _mockSignalR;
    private readonly Mock<IVoiceStore> _mockVoiceStore;
    private readonly Guid _currentUserId;

    public VideoFullscreenViewModelTests()
    {
        _mockWebRtc = new Mock<IWebRtcService>();
        _mockControllerStreaming = new Mock<IControllerStreamingService>();
        _mockSignalR = new Mock<ISignalRService>();
        _mockVoiceStore = new Mock<IVoiceStore>();
        _annotationService = new AnnotationService(_mockSignalR.Object);
        _currentUserId = Guid.NewGuid();
    }

    public void Dispose()
    {
        // Cleanup
    }

    private VideoFullscreenViewModel CreateViewModel()
    {
        return new VideoFullscreenViewModel(
            _mockWebRtc.Object,
            _annotationService,
            _mockControllerStreaming.Object,
            _mockVoiceStore.Object,
            _currentUserId
        );
    }

    private VideoStreamViewModel CreateVideoStream(
        Guid? userId = null,
        string username = "testuser",
        VideoStreamType streamType = VideoStreamType.ScreenShare)
    {
        return new VideoStreamViewModel(
            userId ?? Guid.NewGuid(),
            username,
            streamType,
            _currentUserId
        );
    }

    #region Initial State Tests

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsOpen);
        Assert.Null(vm.Stream);
        Assert.False(vm.IsGpuFullscreenActive);
        Assert.Null(vm.HardwareDecoder);
        Assert.False(vm.IsAnnotationEnabled);
        Assert.False(vm.IsDrawingAllowedByHost);
        Assert.Equal("#FF0000", vm.AnnotationColor);
        Assert.False(vm.IsKeyboardCaptureEnabled);
        Assert.False(vm.IsMouseCaptureEnabled);
    }

    [Fact]
    public void Constructor_AvailableAnnotationColors_IsNotEmpty()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotEmpty(vm.AvailableAnnotationColors);
    }

    [Fact]
    public void Constructor_AnnotationService_IsExposed()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.AnnotationService);
        Assert.Same(_annotationService, vm.AnnotationService);
    }

    #endregion

    #region Open Tests

    [Fact]
    public void Open_SetsStreamAndIsOpen()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = CreateVideoStream();

        // Act
        vm.Open(stream);

        // Assert
        Assert.True(vm.IsOpen);
        Assert.Same(stream, vm.Stream);
    }

    [Fact]
    public void Open_WithGpuRenderingAvailable_SetsGpuFullscreenActive()
    {
        // Arrange
        _mockWebRtc.Setup(x => x.IsGpuRenderingAvailable).Returns(true);
        var vm = CreateViewModel();
        var stream = CreateVideoStream();

        // Act
        vm.Open(stream);

        // Assert
        Assert.True(vm.IsGpuFullscreenActive);
    }

    [Fact]
    public void Open_WithoutGpuRendering_DoesNotSetGpuFullscreenActive()
    {
        // Arrange
        _mockWebRtc.Setup(x => x.IsGpuRenderingAvailable).Returns(false);
        var vm = CreateViewModel();
        var stream = CreateVideoStream();

        // Act
        vm.Open(stream);

        // Assert
        Assert.False(vm.IsGpuFullscreenActive);
    }

    [Fact]
    public void Open_LoadsExistingAnnotationStrokes()
    {
        // Arrange
        var vm = CreateViewModel();
        var sharerId = Guid.NewGuid();
        var stream = CreateVideoStream(userId: sharerId);

        // Act
        vm.Open(stream);

        // Assert
        Assert.NotNull(vm.CurrentAnnotationStrokes);
    }

    #endregion

    #region Close Tests

    [Fact]
    public void Close_ResetsState()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        vm.Open(stream);
        vm.IsAnnotationEnabled = true;
        vm.IsKeyboardCaptureEnabled = true;
        vm.IsMouseCaptureEnabled = true;

        // Act
        vm.Close();

        // Assert
        Assert.False(vm.IsOpen);
        Assert.Null(vm.Stream);
        Assert.False(vm.IsAnnotationEnabled);
        Assert.False(vm.IsKeyboardCaptureEnabled);
        Assert.False(vm.IsMouseCaptureEnabled);
    }

    [Fact]
    public void Close_WhenGpuFullscreenActive_ResetsGpuState()
    {
        // Arrange
        _mockWebRtc.Setup(x => x.IsGpuRenderingAvailable).Returns(true);
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        vm.Open(stream);
        Assert.True(vm.IsGpuFullscreenActive);

        // Act
        vm.Close();

        // Assert
        Assert.False(vm.IsGpuFullscreenActive);
    }

    #endregion

    #region OnUserLeftVoice Tests

    [Fact]
    public void OnUserLeftVoice_WhenViewingThatUser_ClosesFullscreen()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: userId);
        vm.Open(stream);
        Assert.True(vm.IsOpen);

        // Act
        vm.OnUserLeftVoice(userId);

        // Assert
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void OnUserLeftVoice_WhenViewingDifferentUser_DoesNotClose()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: userId1);
        vm.Open(stream);
        Assert.True(vm.IsOpen);

        // Act
        vm.OnUserLeftVoice(userId2);

        // Assert
        Assert.True(vm.IsOpen);
    }

    [Fact]
    public void OnUserLeftVoice_WhenNotOpen_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        Assert.False(vm.IsOpen);

        // Act & Assert - Should not throw
        vm.OnUserLeftVoice(Guid.NewGuid());
        Assert.False(vm.IsOpen);
    }

    #endregion

    #region OnScreenShareEnded Tests

    [Fact]
    public void OnScreenShareEnded_WhenViewingThatUser_ClosesFullscreen()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: userId);
        vm.Open(stream);
        Assert.True(vm.IsOpen);

        // Act
        vm.OnScreenShareEnded(userId);

        // Assert
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void OnScreenShareEnded_WhenViewingDifferentUser_DoesNotClose()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: userId1);
        vm.Open(stream);
        Assert.True(vm.IsOpen);

        // Act
        vm.OnScreenShareEnded(userId2);

        // Assert
        Assert.True(vm.IsOpen);
    }

    #endregion

    #region Gaming Station Properties Tests

    [Fact]
    public void IsGamingStation_WhenStreamIsGamingStation_ReturnsTrue()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        stream.IsGamingStation = true;
        vm.Open(stream);

        // Assert
        Assert.True(vm.IsGamingStation);
    }

    [Fact]
    public void IsGamingStation_WhenStreamIsNotGamingStation_ReturnsFalse()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        stream.IsGamingStation = false;
        vm.Open(stream);

        // Assert
        Assert.False(vm.IsGamingStation);
    }

    [Fact]
    public void IsGamingStation_WhenNoStream_ReturnsFalse()
    {
        // Arrange
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsGamingStation);
    }

    [Fact]
    public void GamingStationMachineId_WhenStreamHasMachineId_ReturnsId()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        stream.GamingStationMachineId = "test-machine-id";
        vm.Open(stream);

        // Assert
        Assert.Equal("test-machine-id", vm.GamingStationMachineId);
    }

    [Fact]
    public void GamingStationMachineId_WhenNoStream_ReturnsNull()
    {
        // Arrange
        var vm = CreateViewModel();

        // Assert
        Assert.Null(vm.GamingStationMachineId);
    }

    #endregion

    #region Controller Streaming Tests

    [Fact]
    public void IsControllerStreaming_DelegatesToService()
    {
        // Arrange
        _mockControllerStreaming.Setup(x => x.IsStreaming).Returns(true);
        var vm = CreateViewModel();

        // Assert
        Assert.True(vm.IsControllerStreaming);
    }

    [Fact]
    public void ControllerStreamingHostUserId_DelegatesToService()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        _mockControllerStreaming.Setup(x => x.StreamingHostUserId).Returns(hostId);
        var vm = CreateViewModel();

        // Assert
        Assert.Equal(hostId, vm.ControllerStreamingHostUserId);
    }

    [Fact]
    public void IsStreamingControllerTo_WhenStreamingToHost_ReturnsTrue()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        _mockControllerStreaming.Setup(x => x.IsStreaming).Returns(true);
        _mockControllerStreaming.Setup(x => x.StreamingHostUserId).Returns(hostId);
        var vm = CreateViewModel();

        // Assert
        Assert.True(vm.IsStreamingControllerTo(hostId));
    }

    [Fact]
    public void IsStreamingControllerTo_WhenNotStreaming_ReturnsFalse()
    {
        // Arrange
        _mockControllerStreaming.Setup(x => x.IsStreaming).Returns(false);
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsStreamingControllerTo(Guid.NewGuid()));
    }

    [Fact]
    public void IsStreamingControllerTo_WhenStreamingToDifferentHost_ReturnsFalse()
    {
        // Arrange
        var hostId1 = Guid.NewGuid();
        var hostId2 = Guid.NewGuid();
        _mockControllerStreaming.Setup(x => x.IsStreaming).Returns(true);
        _mockControllerStreaming.Setup(x => x.StreamingHostUserId).Returns(hostId1);
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsStreamingControllerTo(hostId2));
    }

    #endregion

    #region ToggleControllerAccessAsync Tests

    [Fact]
    public async Task ToggleControllerAccessAsync_WithNoStream_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.ToggleControllerAccessAsync();

        // Assert
        _mockControllerStreaming.Verify(
            x => x.RequestAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never);
        _mockControllerStreaming.Verify(
            x => x.StopStreamingAsync(),
            Times.Never);
    }

    [Fact]
    public async Task ToggleControllerAccessAsync_WithCameraStream_DoesNothing()
    {
        // Arrange
        _mockVoiceStore.Setup(s => s.GetCurrentChannelId()).Returns(Guid.NewGuid());
        var vm = CreateViewModel();
        var stream = CreateVideoStream(streamType: VideoStreamType.Camera);
        vm.Open(stream);

        // Act
        await vm.ToggleControllerAccessAsync(stream);

        // Assert
        _mockControllerStreaming.Verify(
            x => x.RequestAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task ToggleControllerAccessAsync_WithOwnScreenShare_DoesNothing()
    {
        // Arrange
        _mockVoiceStore.Setup(s => s.GetCurrentChannelId()).Returns(Guid.NewGuid());
        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: _currentUserId, streamType: VideoStreamType.ScreenShare);
        vm.Open(stream);

        // Act
        await vm.ToggleControllerAccessAsync(stream);

        // Assert
        _mockControllerStreaming.Verify(
            x => x.RequestAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task ToggleControllerAccessAsync_WhenAlreadyStreaming_StopsStreaming()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        _mockVoiceStore.Setup(s => s.GetCurrentChannelId()).Returns(channelId);
        _mockControllerStreaming.Setup(x => x.IsStreaming).Returns(true);
        _mockControllerStreaming.Setup(x => x.StreamingHostUserId).Returns(hostId);
        _mockControllerStreaming.Setup(x => x.StopStreamingAsync()).Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: hostId, streamType: VideoStreamType.ScreenShare);
        vm.Open(stream);

        // Act
        await vm.ToggleControllerAccessAsync(stream);

        // Assert
        _mockControllerStreaming.Verify(x => x.StopStreamingAsync(), Times.Once);
    }

    [Fact]
    public async Task ToggleControllerAccessAsync_WhenNotStreaming_RequestsAccess()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        _mockVoiceStore.Setup(s => s.GetCurrentChannelId()).Returns(channelId);
        _mockControllerStreaming.Setup(x => x.IsStreaming).Returns(false);
        _mockControllerStreaming.Setup(x => x.RequestAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        var stream = CreateVideoStream(userId: hostId, streamType: VideoStreamType.ScreenShare);
        vm.Open(stream);

        // Act
        await vm.ToggleControllerAccessAsync(stream);

        // Assert
        _mockControllerStreaming.Verify(
            x => x.RequestAccessAsync(channelId, hostId),
            Times.Once);
    }

    #endregion

    #region Annotation Tests

    [Fact]
    public void AnnotationColor_WhenSet_UpdatesAnnotationService()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.AnnotationColor = "#00FF00";

        // Assert
        Assert.Equal("#00FF00", vm.AnnotationColor);
        Assert.Equal("#00FF00", _annotationService.CurrentColor);
    }

    [Fact]
    public async Task AddAnnotationStrokeAsync_WhenNotInVoiceChannel_DoesNothing()
    {
        // Arrange
        _mockVoiceStore.Setup(s => s.GetCurrentChannelId()).Returns((Guid?)null);
        var vm = CreateViewModel();
        var sharerId = Guid.NewGuid();
        var stream = CreateVideoStream(userId: sharerId);
        vm.Open(stream);

        var stroke = new DrawingStroke
        {
            UserId = _currentUserId,
            Username = "test",
            Points = new List<PointF> { new(0, 0), new(1, 1) },
            Color = "#FF0000"
        };

        // Act
        await vm.AddAnnotationStrokeAsync(stroke);

        // Assert
        _mockSignalR.Verify(
            x => x.SendAnnotationAsync(It.IsAny<AnnotationMessage>()),
            Times.Never);
    }

    [Fact]
    public async Task ClearAnnotationsAsync_WhenNotInVoiceChannel_DoesNothing()
    {
        // Arrange
        _mockVoiceStore.Setup(s => s.GetCurrentChannelId()).Returns((Guid?)null);
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        vm.Open(stream);

        // Act
        await vm.ClearAnnotationsAsync();

        // Assert
        _mockSignalR.Verify(
            x => x.SendAnnotationAsync(It.IsAny<AnnotationMessage>()),
            Times.Never);
    }

    #endregion

    #region Input Capture Tests

    [Fact]
    public void IsKeyboardCaptureEnabled_CanBeSetAndRetrieved()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.IsKeyboardCaptureEnabled = true;

        // Assert
        Assert.True(vm.IsKeyboardCaptureEnabled);
    }

    [Fact]
    public void IsMouseCaptureEnabled_CanBeSetAndRetrieved()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.IsMouseCaptureEnabled = true;

        // Assert
        Assert.True(vm.IsMouseCaptureEnabled);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_UnsubscribesFromAnnotationEvents()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - Should not throw
        vm.Dispose();
    }

    [Fact]
    public void Dispose_WhenGpuFullscreenActive_UnsubscribesFromWebRtc()
    {
        // Arrange
        _mockWebRtc.Setup(x => x.IsGpuRenderingAvailable).Returns(true);
        var vm = CreateViewModel();
        var stream = CreateVideoStream();
        vm.Open(stream);
        Assert.True(vm.IsGpuFullscreenActive);

        // Act & Assert - Should not throw
        vm.Dispose();
    }

    #endregion

    #region Property Change Notifications Tests

    [Fact]
    public void IsOpen_WhenChanged_RaisesPropertyChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsOpen))
                propertyChangedRaised = true;
        };

        // Act
        var stream = CreateVideoStream();
        vm.Open(stream);

        // Assert
        Assert.True(propertyChangedRaised);
    }

    [Fact]
    public void Stream_WhenChanged_RaisesPropertyChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Stream))
                propertyChangedRaised = true;
        };

        // Act
        var stream = CreateVideoStream();
        vm.Open(stream);

        // Assert
        Assert.True(propertyChangedRaised);
    }

    [Fact]
    public void IsAnnotationEnabled_WhenChanged_RaisesPropertyChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsAnnotationEnabled))
                propertyChangedRaised = true;
        };

        // Act
        vm.IsAnnotationEnabled = true;

        // Assert
        Assert.True(propertyChangedRaised);
    }

    #endregion
}
