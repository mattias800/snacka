using System.Reactive.Linq;
using System.Reactive.Subjects;
using Moq;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class VoiceControlViewModelTests
{
    private readonly Mock<IVoiceStore> _voiceStoreMock;
    private readonly Mock<ISettingsStore> _settingsStoreMock;
    private readonly Mock<IWebRtcService> _webRtcMock;
    private readonly Mock<ISignalRService> _signalRMock;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly UserSettings _settings;

    // BehaviorSubjects to simulate store state
    private readonly BehaviorSubject<bool> _isMutedSubject = new(false);
    private readonly BehaviorSubject<bool> _isDeafenedSubject = new(false);
    private readonly BehaviorSubject<bool> _isSpeakingSubject = new(false);
    private readonly BehaviorSubject<bool> _isCameraOnSubject = new(false);
    private readonly BehaviorSubject<bool> _isScreenSharingSubject = new(false);
    private readonly BehaviorSubject<VoiceConnectionStatus> _connectionStatusSubject = new(VoiceConnectionStatus.Disconnected);

    public VoiceControlViewModelTests()
    {
        _voiceStoreMock = new Mock<IVoiceStore>();
        _settingsStoreMock = new Mock<ISettingsStore>();
        _webRtcMock = new Mock<IWebRtcService>();
        _signalRMock = new Mock<ISignalRService>();

        _settings = new UserSettings();
        _settingsStoreMock.Setup(x => x.Settings).Returns(_settings);

        // Setup voice store observables
        _voiceStoreMock.Setup(x => x.IsMuted).Returns(_isMutedSubject);
        _voiceStoreMock.Setup(x => x.IsDeafened).Returns(_isDeafenedSubject);
        _voiceStoreMock.Setup(x => x.IsSpeaking).Returns(_isSpeakingSubject);
        _voiceStoreMock.Setup(x => x.IsCameraOn).Returns(_isCameraOnSubject);
        _voiceStoreMock.Setup(x => x.IsScreenSharing).Returns(_isScreenSharingSubject);
        _voiceStoreMock.Setup(x => x.ConnectionStatus).Returns(_connectionStatusSubject);
    }

    private VoiceControlViewModel CreateViewModel(Guid? channelId = null)
    {
        _voiceStoreMock.Setup(s => s.GetCurrentChannelId()).Returns(channelId);
        return new VoiceControlViewModel(
            _voiceStoreMock.Object,
            _settingsStoreMock.Object,
            _webRtcMock.Object,
            _signalRMock.Object,
            _userId);
    }

    #region Initialization Tests

    [Fact]
    public void Constructor_InitializesWithSettingsState()
    {
        // Arrange - set both settings and initial store state
        _settings.IsMuted = true;
        _settings.IsDeafened = true;
        _isMutedSubject.OnNext(true);
        _isDeafenedSubject.OnNext(true);

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.True(vm.IsMuted);
        Assert.True(vm.IsDeafened);
    }

    [Fact]
    public void Constructor_InitializesCommands()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.ToggleMuteCommand);
        Assert.NotNull(vm.ToggleDeafenCommand);
        Assert.NotNull(vm.ToggleCameraCommand);
    }

    [Fact]
    public void Constructor_SubscribesToStoreState()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        _isMutedSubject.OnNext(true);
        _isDeafenedSubject.OnNext(true);
        _isSpeakingSubject.OnNext(true);
        _isCameraOnSubject.OnNext(true);
        _isScreenSharingSubject.OnNext(true);
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Connected);

        // Assert
        Assert.True(vm.IsMuted);
        Assert.True(vm.IsDeafened);
        Assert.True(vm.IsSpeaking);
        Assert.True(vm.IsCameraOn);
        Assert.True(vm.IsScreenSharing);
        Assert.Equal(VoiceConnectionStatus.Connected, vm.VoiceConnectionStatus);
    }

    #endregion

    #region ToggleMute Tests

    [Fact]
    public async Task ToggleMuteAsync_WhenNotInVoiceChannel_OnlyUpdatesLocalState()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.ToggleMuteAsync();

        // Assert
        Assert.True(vm.IsMuted);
        Assert.True(_settings.IsMuted);
        _settingsStoreMock.Verify(x => x.Save(), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalMuted(true), Times.Once);
        _webRtcMock.Verify(x => x.SetMuted(It.IsAny<bool>()), Times.Never);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(It.IsAny<Guid>(), It.IsAny<VoiceStateUpdate>()), Times.Never);
    }

    [Fact]
    public async Task ToggleMuteAsync_WhenInVoiceChannel_UpdatesWebRtcAndSignalR()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.ToggleMuteAsync();

        // Assert
        Assert.True(vm.IsMuted);
        _webRtcMock.Verify(x => x.SetMuted(true), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(channelId, It.Is<VoiceStateUpdate>(u => u.IsMuted == true)), Times.Once);
    }

    [Fact]
    public async Task ToggleMuteAsync_UnmuteWhenServerMuted_DoesNothing()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _isMutedSubject.OnNext(false); // Start unmuted

        var participant = new VoiceParticipantState(
            Id: Guid.NewGuid(),
            UserId: _userId,
            Username: "Test",
            ChannelId: channelId,
            IsMuted: false,
            IsDeafened: false,
            IsServerMuted: true, // Server muted
            IsServerDeafened: false,
            IsSpeaking: false,
            IsScreenSharing: false,
            ScreenShareHasAudio: false,
            IsCameraOn: false,
            JoinedAt: DateTime.UtcNow);

        _voiceStoreMock.Setup(x => x.GetLocalParticipant(_userId)).Returns(participant);

        // Act
        await vm.ToggleMuteAsync();

        // Assert - should not have changed state
        _voiceStoreMock.Verify(x => x.SetLocalMuted(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ToggleMuteAsync_Toggle_SwitchesState()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act - toggle to muted
        await vm.ToggleMuteAsync();

        // Assert
        Assert.True(vm.IsMuted);

        // Act - toggle back to unmuted
        await vm.ToggleMuteAsync();

        // Assert
        Assert.False(vm.IsMuted);
    }

    [Fact]
    public async Task ToggleMuteAsync_InVoiceChannel_UpdatesVoiceStore()
    {
        // Arrange
        var channelId = Guid.NewGuid();

        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel(channelId);

        // Act
        await vm.ToggleMuteAsync();

        // Assert
        _voiceStoreMock.Verify(x => x.SetLocalMuted(true), Times.Once);
    }

    #endregion

    #region ToggleDeafen Tests

    [Fact]
    public async Task ToggleDeafenAsync_WhenNotInVoiceChannel_OnlyUpdatesLocalState()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.ToggleDeafenAsync();

        // Assert
        Assert.True(vm.IsDeafened);
        Assert.True(vm.IsMuted); // Deafening also mutes
        Assert.True(_settings.IsDeafened);
        Assert.True(_settings.IsMuted);
        _settingsStoreMock.Verify(x => x.Save(), Times.AtLeast(1));
        _voiceStoreMock.Verify(x => x.SetLocalDeafened(true), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalMuted(true), Times.Once);
    }

    [Fact]
    public async Task ToggleDeafenAsync_WhenInVoiceChannel_UpdatesWebRtcAndSignalR()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.ToggleDeafenAsync();

        // Assert
        Assert.True(vm.IsDeafened);
        Assert.True(vm.IsMuted);
        _webRtcMock.Verify(x => x.SetMuted(true), Times.Once);
        _webRtcMock.Verify(x => x.SetDeafened(true), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(channelId,
            It.Is<VoiceStateUpdate>(u => u.IsMuted == true && u.IsDeafened == true)), Times.Once);
    }

    [Fact]
    public async Task ToggleDeafenAsync_AlsoMutesWhenDeafening()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.ToggleDeafenAsync();

        // Assert
        Assert.True(vm.IsDeafened);
        Assert.True(vm.IsMuted); // Should auto-mute
    }

    [Fact]
    public async Task ToggleDeafenAsync_UndeafenWhenServerDeafened_DoesNothing()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _isDeafenedSubject.OnNext(false); // Start undeafened

        var participant = new VoiceParticipantState(
            Id: Guid.NewGuid(),
            UserId: _userId,
            Username: "Test",
            ChannelId: channelId,
            IsMuted: false,
            IsDeafened: false,
            IsServerMuted: false,
            IsServerDeafened: true, // Server deafened
            IsSpeaking: false,
            IsScreenSharing: false,
            ScreenShareHasAudio: false,
            IsCameraOn: false,
            JoinedAt: DateTime.UtcNow);

        _voiceStoreMock.Setup(x => x.GetLocalParticipant(_userId)).Returns(participant);

        // Act
        await vm.ToggleDeafenAsync();

        // Assert - should not have changed state
        _voiceStoreMock.Verify(x => x.SetLocalDeafened(It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region ToggleCamera Tests

    [Fact]
    public async Task ToggleCameraAsync_WhenNotInVoiceChannel_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.ToggleCameraAsync();

        // Assert
        Assert.False(vm.IsCameraOn);
        _webRtcMock.Verify(x => x.SetCameraAsync(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ToggleCameraAsync_WhenInVoiceChannel_UpdatesWebRtcAndSignalR()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _webRtcMock.Setup(x => x.SetCameraAsync(true)).Returns(Task.CompletedTask);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.ToggleCameraAsync();

        // Assert
        Assert.True(vm.IsCameraOn);
        _webRtcMock.Verify(x => x.SetCameraAsync(true), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalCameraOn(true), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(channelId,
            It.Is<VoiceStateUpdate>(u => u.IsCameraOn == true)), Times.Once);
    }

    [Fact]
    public async Task ToggleCameraAsync_WhenWebRtcFails_DoesNotUpdateState()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _webRtcMock.Setup(x => x.SetCameraAsync(true)).ThrowsAsync(new Exception("Camera error"));

        // Act
        await vm.ToggleCameraAsync();

        // Assert - state should not change on failure
        Assert.False(vm.IsCameraOn);
        _voiceStoreMock.Verify(x => x.SetLocalCameraOn(It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region SetMutedAsync Tests

    [Fact]
    public async Task SetMutedAsync_WhenAlreadyMuted_DoesNothing()
    {
        // Arrange - set both settings and initial store state
        _settings.IsMuted = true;
        _isMutedSubject.OnNext(true);
        var vm = CreateViewModel();

        // Act
        await vm.SetMutedAsync(true);

        // Assert
        _settingsStoreMock.Verify(x => x.Save(), Times.Never);
    }

    [Fact]
    public async Task SetMutedAsync_UpdatesStateAndPersists()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.SetMutedAsync(true);

        // Assert
        Assert.True(vm.IsMuted);
        Assert.True(_settings.IsMuted);
        _settingsStoreMock.Verify(x => x.Save(), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalMuted(true), Times.Once);
    }

    [Fact]
    public async Task SetMutedAsync_WhenInVoiceChannel_UpdatesWebRtcAndSignalR()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.SetMutedAsync(true);

        // Assert
        _webRtcMock.Verify(x => x.SetMuted(true), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(channelId,
            It.Is<VoiceStateUpdate>(u => u.IsMuted == true)), Times.Once);
    }

    #endregion

    #region UpdateSpeakingState Tests

    [Fact]
    public void UpdateSpeakingState_UpdatesLocalStateAndStore()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.UpdateSpeakingState(true);

        // Assert
        Assert.True(vm.IsSpeaking);
        _voiceStoreMock.Verify(x => x.SetLocalSpeaking(true), Times.Once);
    }

    #endregion

    #region HandleServerVoiceStateUpdate Tests

    [Fact]
    public void HandleServerVoiceStateUpdate_ServerMuted_ForcesMute()
    {
        // Arrange
        var vm = CreateViewModel();
        Assert.False(vm.IsMuted);

        // Act
        vm.HandleServerVoiceStateUpdate(isServerMuted: true, isServerDeafened: null);

        // Assert
        Assert.True(vm.IsMuted);
        Assert.True(_settings.IsMuted);
        _settingsStoreMock.Verify(x => x.Save(), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalMuted(true), Times.Once);
    }

    [Fact]
    public void HandleServerVoiceStateUpdate_ServerDeafened_ForcesMuteAndDeafen()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.HandleServerVoiceStateUpdate(isServerMuted: null, isServerDeafened: true);

        // Assert
        Assert.True(vm.IsDeafened);
        Assert.True(vm.IsMuted);
        Assert.True(_settings.IsDeafened);
        Assert.True(_settings.IsMuted);
        _voiceStoreMock.Verify(x => x.SetLocalMuted(true), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalDeafened(true), Times.Once);
    }

    [Fact]
    public void HandleServerVoiceStateUpdate_WhenAlreadyMuted_DoesNothing()
    {
        // Arrange - set both settings and initial store state
        _settings.IsMuted = true;
        _isMutedSubject.OnNext(true);
        var vm = CreateViewModel();

        // Act
        vm.HandleServerVoiceStateUpdate(isServerMuted: true, isServerDeafened: null);

        // Assert
        _settingsStoreMock.Verify(x => x.Save(), Times.Never);
    }

    #endregion

    #region ApplyPersistedStateAsync Tests

    [Fact]
    public async Task ApplyPersistedStateAsync_AppliesStateToWebRtc()
    {
        // Arrange - set both settings and initial store state
        _settings.IsMuted = true;
        _settings.IsDeafened = true;
        _isMutedSubject.OnNext(true);
        _isDeafenedSubject.OnNext(true);
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel();
        _signalRMock.Setup(x => x.UpdateVoiceStateAsync(channelId, It.IsAny<VoiceStateUpdate>()))
            .Returns(Task.CompletedTask);

        // Act
        await vm.ApplyPersistedStateAsync(channelId);

        // Assert
        _webRtcMock.Verify(x => x.SetMuted(true), Times.Once);
        _webRtcMock.Verify(x => x.SetDeafened(true), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(channelId,
            It.Is<VoiceStateUpdate>(u => u.IsMuted == true && u.IsDeafened == true)), Times.Once);
    }

    [Fact]
    public async Task ApplyPersistedStateAsync_WhenNotMutedOrDeafened_SkipsSignalR()
    {
        // Arrange
        _settings.IsMuted = false;
        _settings.IsDeafened = false;
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel();

        // Act
        await vm.ApplyPersistedStateAsync(channelId);

        // Assert
        _webRtcMock.Verify(x => x.SetMuted(false), Times.Once);
        _webRtcMock.Verify(x => x.SetDeafened(false), Times.Once);
        _signalRMock.Verify(x => x.UpdateVoiceStateAsync(It.IsAny<Guid>(), It.IsAny<VoiceStateUpdate>()), Times.Never);
    }

    #endregion

    #region ResetTransientState Tests

    [Fact]
    public void ResetTransientState_ResetsTransientStateOnly()
    {
        // Arrange - set both settings and initial store state for mute/deafen
        _settings.IsMuted = true;
        _settings.IsDeafened = true;
        _isMutedSubject.OnNext(true);
        _isDeafenedSubject.OnNext(true);
        var vm = CreateViewModel();

        // Set transient state after creating VM
        _isSpeakingSubject.OnNext(true);
        _isCameraOnSubject.OnNext(true);
        _isScreenSharingSubject.OnNext(true);

        // Act
        vm.ResetTransientState();

        // Assert - mute/deafen should remain, transient should reset
        Assert.True(vm.IsMuted); // Persisted - not reset
        Assert.True(vm.IsDeafened); // Persisted - not reset
        Assert.False(vm.IsSpeaking); // Transient - reset
        Assert.False(vm.IsCameraOn); // Transient - reset
        Assert.False(vm.IsScreenSharing); // Transient - reset

        _voiceStoreMock.Verify(x => x.SetLocalSpeaking(false), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalCameraOn(false), Times.Once);
        _voiceStoreMock.Verify(x => x.SetLocalScreenSharing(false), Times.Once);
    }

    #endregion

    #region Connection Status Tests

    [Fact]
    public void VoiceConnectionStatusText_Connected_ReturnsCorrectText()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Connected);

        // Assert
        Assert.Equal("Voice Connected", vm.VoiceConnectionStatusText);
    }

    [Fact]
    public void VoiceConnectionStatusText_Connecting_ReturnsCorrectText()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Connecting);

        // Assert
        Assert.Equal("Connecting...", vm.VoiceConnectionStatusText);
    }

    [Fact]
    public void VoiceConnectionStatusText_Disconnected_ReturnsEmpty()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Disconnected);

        // Assert
        Assert.Equal("", vm.VoiceConnectionStatusText);
    }

    [Fact]
    public void IsVoiceConnecting_ReturnsTrue_WhenConnecting()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Connecting);

        // Assert
        Assert.True(vm.IsVoiceConnecting);
        Assert.False(vm.IsVoiceConnected);
    }

    [Fact]
    public void IsVoiceConnected_ReturnsTrue_WhenConnected()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Connected);

        // Assert
        Assert.True(vm.IsVoiceConnected);
        Assert.False(vm.IsVoiceConnecting);
    }

    #endregion

    #region IsInVoiceChannel Tests

    [Fact]
    public void IsInVoiceChannel_ReturnsTrue_WhenChannelIdProvided()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var vm = CreateViewModel(channelId);

        // Act & Assert
        Assert.True(vm.IsInVoiceChannel);
    }

    [Fact]
    public void IsInVoiceChannel_ReturnsFalse_WhenNoChannelId()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert
        Assert.False(vm.IsInVoiceChannel);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_DisposesCommands()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.Dispose();

        // Assert - no exceptions should be thrown
        Assert.True(true);
    }

    #endregion

    #region Property Change Notification Tests

    [Fact]
    public void IsMuted_RaisesPropertyChanged_WhenStoreUpdates()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChanged = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceControlViewModel.IsMuted))
                propertyChanged = true;
        };

        // Act
        _isMutedSubject.OnNext(true);

        // Assert
        Assert.True(propertyChanged);
    }

    [Fact]
    public void IsDeafened_RaisesPropertyChanged_WhenStoreUpdates()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChanged = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceControlViewModel.IsDeafened))
                propertyChanged = true;
        };

        // Act
        _isDeafenedSubject.OnNext(true);

        // Assert
        Assert.True(propertyChanged);
    }

    [Fact]
    public void VoiceConnectionStatus_RaisesMultiplePropertyChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertiesChanged = new List<string>();
        vm.PropertyChanged += (_, e) => propertiesChanged.Add(e.PropertyName!);

        // Act
        _connectionStatusSubject.OnNext(VoiceConnectionStatus.Connected);

        // Assert
        Assert.Contains(nameof(VoiceControlViewModel.VoiceConnectionStatus), propertiesChanged);
        Assert.Contains(nameof(VoiceControlViewModel.VoiceConnectionStatusText), propertiesChanged);
        Assert.Contains(nameof(VoiceControlViewModel.IsVoiceConnecting), propertiesChanged);
        Assert.Contains(nameof(VoiceControlViewModel.IsVoiceConnected), propertiesChanged);
        Assert.Contains(nameof(VoiceControlViewModel.IsInVoiceChannel), propertiesChanged);
    }

    #endregion
}
