using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Moq;
using ReactiveUI;
using Snacka.Client.Models;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;

namespace Snacka.Client.Tests.ViewModels;

public class GamingStationViewModelTests : IDisposable
{
    private readonly Mock<IApiClient> _mockApiClient;
    private readonly Mock<ISignalRService> _mockSignalR;
    private readonly Mock<ISettingsStore> _mockSettingsStore;
    private readonly UserSettings _settings;
    private readonly ObservableCollection<MyGamingStationInfo> _myGamingStations;
    private readonly string _currentMachineId;
    private readonly Guid _currentUserId;
    private Guid? _currentVoiceChannelId;

    public GamingStationViewModelTests()
    {
        _mockApiClient = new Mock<IApiClient>();
        _mockSignalR = new Mock<ISignalRService>();
        _mockSettingsStore = new Mock<ISettingsStore>();
        _settings = new UserSettings();
        _mockSettingsStore.Setup(x => x.Settings).Returns(_settings);
        _myGamingStations = new ObservableCollection<MyGamingStationInfo>();
        _currentMachineId = "TEST-MACHINE-12345678";
        _currentUserId = Guid.NewGuid();
        _currentVoiceChannelId = null;
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    private GamingStationViewModel CreateViewModel()
    {
        return new GamingStationViewModel(
            _mockApiClient.Object,
            _mockSignalR.Object,
            _mockSettingsStore.Object,
            _myGamingStations,
            _currentMachineId,
            () => _currentVoiceChannelId,
            _currentUserId
        );
    }

    private static GamingStationResponse CreateStationResponse(
        Guid? id = null,
        string name = "Test Station",
        bool isOwner = true)
    {
        return new GamingStationResponse(
            Id: id ?? Guid.NewGuid(),
            OwnerId: Guid.NewGuid(),
            OwnerUsername: "owner",
            OwnerEffectiveDisplayName: "Owner",
            Name: name,
            Description: "Test Description",
            Status: Snacka.Client.Services.StationStatus.Online,
            LastSeenAt: DateTime.UtcNow,
            CreatedAt: DateTime.UtcNow,
            ConnectedUserCount: 0,
            IsOwner: isOwner,
            MyPermission: null
        );
    }

    #region Initial State Tests

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsViewingGamingStations);
        Assert.False(vm.IsLoadingStations);
        Assert.False(vm.IsViewingStationStream);
        Assert.False(vm.IsConnectingToStation);
        Assert.Null(vm.ConnectedStationId);
        Assert.Equal("", vm.ConnectedStationName);
        Assert.Equal("Disconnected", vm.StationConnectionStatus);
        Assert.Equal(0, vm.StationConnectedUserCount);
        Assert.Equal(0, vm.StationLatency);
        Assert.Equal("—", vm.StationResolution);
        Assert.Null(vm.StationPlayerSlot);
    }

    [Fact]
    public void Constructor_WithGamingStationDisabled_ShowsCorrectState()
    {
        // Arrange
        _settings.IsGamingStationEnabled = false;

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.IsGamingStationEnabled);
        Assert.False(vm.ShowGamingStationBanner);
    }

    [Fact]
    public void Constructor_WithGamingStationEnabled_ShowsCorrectState()
    {
        // Arrange
        _settings.IsGamingStationEnabled = true;

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.True(vm.IsGamingStationEnabled);
        Assert.True(vm.ShowGamingStationBanner);
    }

    #endregion

    #region Properties Tests

    [Fact]
    public void MyGamingStations_ReturnsInjectedCollection()
    {
        // Arrange
        var station = new MyGamingStationInfo(
            MachineId: "test",
            DisplayName: "Test",
            IsAvailable: true,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false,
            IsCurrentMachine: true);
        _myGamingStations.Add(station);

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Single(vm.MyGamingStations);
        Assert.Equal("test", vm.MyGamingStations[0].MachineId);
    }

    [Fact]
    public void CurrentMachineId_ReturnsInjectedValue()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Equal(_currentMachineId, vm.CurrentMachineId);
    }

    [Fact]
    public void GamingStationChannelStatus_WhenNotInVoice_ReturnsAvailable()
    {
        // Arrange
        var station = new MyGamingStationInfo(
            MachineId: _currentMachineId,
            DisplayName: "Test",
            IsAvailable: true,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false,
            IsCurrentMachine: true);
        _myGamingStations.Add(station);

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Equal("Available", vm.GamingStationChannelStatus);
    }

    [Fact]
    public void GamingStationChannelStatus_WhenInVoice_ReturnsInVoiceChannel()
    {
        // Arrange
        var station = new MyGamingStationInfo(
            MachineId: _currentMachineId,
            DisplayName: "Test",
            IsAvailable: true,
            IsInVoiceChannel: true,
            CurrentChannelId: Guid.NewGuid(),
            IsScreenSharing: false,
            IsCurrentMachine: true);
        _myGamingStations.Add(station);

        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Equal("In voice channel", vm.GamingStationChannelStatus);
    }

    #endregion

    #region OpenAsync Tests

    [Fact]
    public async Task OpenAsync_LoadsStationsAndSetsViewingState()
    {
        // Arrange
        var stations = new List<GamingStationResponse>
        {
            CreateStationResponse(isOwner: true),
            CreateStationResponse(isOwner: false)
        };

        _mockApiClient
            .Setup(x => x.GetStationsAsync())
            .ReturnsAsync(new ApiResult<List<GamingStationResponse>> { Success = true, Data = stations });

        var vm = CreateViewModel();
        var viewOpeningFired = false;
        vm.ViewOpening += () => viewOpeningFired = true;

        // Act
        await vm.OpenAsync();

        // Assert
        Assert.True(viewOpeningFired);
        Assert.True(vm.IsViewingGamingStations);
        Assert.Single(vm.MyStations);
        Assert.Single(vm.SharedStations);
        Assert.False(vm.IsLoadingStations);
    }

    [Fact]
    public async Task OpenAsync_WithNoStations_SetsHasNoStationsTrue()
    {
        // Arrange
        _mockApiClient
            .Setup(x => x.GetStationsAsync())
            .ReturnsAsync(new ApiResult<List<GamingStationResponse>> { Success = true, Data = new List<GamingStationResponse>() });

        var vm = CreateViewModel();

        // Act
        await vm.OpenAsync();

        // Assert
        Assert.True(vm.HasNoStations);
        Assert.False(vm.HasMyStations);
        Assert.False(vm.HasSharedStations);
    }

    #endregion

    #region Close Tests

    [Fact]
    public void Close_ResetsViewingState()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.IsViewingGamingStations = true;
        vm.IsViewingStationStream = true;

        // Act
        vm.Close();

        // Assert
        Assert.False(vm.IsViewingGamingStations);
        Assert.False(vm.IsViewingStationStream);
    }

    #endregion

    #region LoadStationsAsync Tests

    [Fact]
    public async Task LoadStationsAsync_ClearsExistingStations()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.MyStations.Add(CreateStationResponse(isOwner: true));
        vm.SharedStations.Add(CreateStationResponse(isOwner: false));

        var newStations = new List<GamingStationResponse>
        {
            CreateStationResponse(name: "New Station", isOwner: true)
        };

        _mockApiClient
            .Setup(x => x.GetStationsAsync())
            .ReturnsAsync(new ApiResult<List<GamingStationResponse>> { Success = true, Data = newStations });

        // Act
        await vm.LoadStationsAsync();

        // Assert
        Assert.Single(vm.MyStations);
        Assert.Equal("New Station", vm.MyStations[0].Name);
        Assert.Empty(vm.SharedStations);
    }

    [Fact]
    public async Task LoadStationsAsync_SetsIsLoadingDuringOperation()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ApiResult<List<GamingStationResponse>>>();
        _mockApiClient
            .Setup(x => x.GetStationsAsync())
            .Returns(tcs.Task);

        var vm = CreateViewModel();

        // Act - Start loading
        var loadTask = vm.LoadStationsAsync();
        Assert.True(vm.IsLoadingStations);

        // Complete loading
        tcs.SetResult(new ApiResult<List<GamingStationResponse>> { Success = true, Data = new List<GamingStationResponse>() });
        await loadTask;

        // Assert
        Assert.False(vm.IsLoadingStations);
    }

    #endregion

    #region OnGamingStationStatusChanged Tests

    [Fact]
    public void OnGamingStationStatusChanged_ForCurrentUser_AddsStation()
    {
        // Arrange
        var vm = CreateViewModel();
        var e = new GamingStationStatusChangedEvent(
            UserId: _currentUserId,
            Username: "testuser",
            MachineId: "new-machine",
            DisplayName: "New Station",
            IsAvailable: true,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false);

        // Act
        vm.OnGamingStationStatusChanged(e, _currentUserId);

        // Assert
        Assert.Single(_myGamingStations);
        Assert.Equal("new-machine", _myGamingStations[0].MachineId);
        Assert.Equal("New Station", _myGamingStations[0].DisplayName);
    }

    [Fact]
    public void OnGamingStationStatusChanged_ForOtherUser_DoesNotAddStation()
    {
        // Arrange
        var vm = CreateViewModel();
        var otherUserId = Guid.NewGuid();
        var e = new GamingStationStatusChangedEvent(
            UserId: otherUserId,
            Username: "otheruser",
            MachineId: "other-machine",
            DisplayName: "Other Station",
            IsAvailable: true,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false);

        // Act
        vm.OnGamingStationStatusChanged(e, _currentUserId);

        // Assert
        Assert.Empty(_myGamingStations);
    }

    [Fact]
    public void OnGamingStationStatusChanged_WhenNotAvailable_RemovesExistingStation()
    {
        // Arrange
        var existingStation = new MyGamingStationInfo(
            MachineId: "existing-machine",
            DisplayName: "Existing",
            IsAvailable: true,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false,
            IsCurrentMachine: false);
        _myGamingStations.Add(existingStation);

        var vm = CreateViewModel();
        var e = new GamingStationStatusChangedEvent(
            UserId: _currentUserId,
            Username: "testuser",
            MachineId: "existing-machine",
            DisplayName: "Existing",
            IsAvailable: false,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false);

        // Act
        vm.OnGamingStationStatusChanged(e, _currentUserId);

        // Assert
        Assert.Empty(_myGamingStations);
    }

    [Fact]
    public void OnGamingStationStatusChanged_UpdatesExistingStation()
    {
        // Arrange
        var existingStation = new MyGamingStationInfo(
            MachineId: "existing-machine",
            DisplayName: "Existing",
            IsAvailable: true,
            IsInVoiceChannel: false,
            CurrentChannelId: null,
            IsScreenSharing: false,
            IsCurrentMachine: false);
        _myGamingStations.Add(existingStation);

        var vm = CreateViewModel();
        var channelId = Guid.NewGuid();
        var e = new GamingStationStatusChangedEvent(
            UserId: _currentUserId,
            Username: "testuser",
            MachineId: "existing-machine",
            DisplayName: "Updated Name",
            IsAvailable: true,
            IsInVoiceChannel: true,
            CurrentChannelId: channelId,
            IsScreenSharing: true);

        // Act
        vm.OnGamingStationStatusChanged(e, _currentUserId);

        // Assert
        Assert.Single(_myGamingStations);
        Assert.Equal("Updated Name", _myGamingStations[0].DisplayName);
        Assert.True(_myGamingStations[0].IsInVoiceChannel);
        Assert.Equal(channelId, _myGamingStations[0].CurrentChannelId);
        Assert.True(_myGamingStations[0].IsScreenSharing);
    }

    #endregion

    #region DisableGamingStationAsync Tests

    [Fact]
    public async Task DisableCommand_DisablesGamingStation()
    {
        // Arrange
        _settings.IsGamingStationEnabled = true;
        _mockSignalR
            .Setup(x => x.SetGamingStationAvailableAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        Assert.True(vm.IsGamingStationEnabled);

        // Act
        var cmd = (ReactiveCommand<Unit, Unit>)vm.DisableCommand;
        await cmd.Execute();

        // Assert
        Assert.False(vm.IsGamingStationEnabled);
        Assert.False(vm.ShowGamingStationBanner);
        Assert.False(_settings.IsGamingStationEnabled);
        _mockSettingsStore.Verify(x => x.Save(), Times.Once);
    }

    #endregion

    #region SendKeyboardInputAsync Tests

    [Fact]
    public async Task SendKeyboardInputAsync_WhenNotInVoice_DoesNotSend()
    {
        // Arrange
        _currentVoiceChannelId = null;
        var vm = CreateViewModel();
        var input = new StationKeyboardInput(Guid.NewGuid(), "KeyA", true, false, false, false, false);

        // Act
        await vm.SendKeyboardInputAsync(input);

        // Assert
        _mockSignalR.Verify(
            x => x.SendStationKeyboardInputAsync(It.IsAny<Guid>(), It.IsAny<StationKeyboardInput>()),
            Times.Never);
    }

    [Fact]
    public async Task SendKeyboardInputAsync_WhenInVoice_SendsInput()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _currentVoiceChannelId = channelId;
        _mockSignalR
            .Setup(x => x.SendStationKeyboardInputAsync(It.IsAny<Guid>(), It.IsAny<StationKeyboardInput>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        var input = new StationKeyboardInput(Guid.NewGuid(), "KeyA", true, false, false, false, false);

        // Act
        await vm.SendKeyboardInputAsync(input);

        // Assert
        _mockSignalR.Verify(
            x => x.SendStationKeyboardInputAsync(channelId, input),
            Times.Once);
    }

    #endregion

    #region SendMouseInputAsync Tests

    [Fact]
    public async Task SendMouseInputAsync_WhenNotInVoice_DoesNotSend()
    {
        // Arrange
        _currentVoiceChannelId = null;
        var vm = CreateViewModel();
        var input = new StationMouseInput(Guid.NewGuid(), StationMouseInputType.Move, 0.5, 0.5, null, null, null);

        // Act
        await vm.SendMouseInputAsync(input);

        // Assert
        _mockSignalR.Verify(
            x => x.SendStationMouseInputAsync(It.IsAny<Guid>(), It.IsAny<StationMouseInput>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMouseInputAsync_WhenInVoice_SendsInput()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _currentVoiceChannelId = channelId;
        _mockSignalR
            .Setup(x => x.SendStationMouseInputAsync(It.IsAny<Guid>(), It.IsAny<StationMouseInput>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        var input = new StationMouseInput(Guid.NewGuid(), StationMouseInputType.Down, 0.5, 0.5, 0, null, null);

        // Act
        await vm.SendMouseInputAsync(input);

        // Assert
        _mockSignalR.Verify(
            x => x.SendStationMouseInputAsync(channelId, input),
            Times.Once);
    }

    #endregion

    #region ReportStatusAsync Tests

    [Fact]
    public async Task ReportStatusAsync_WhenDisabled_DoesNotReport()
    {
        // Arrange
        _settings.IsGamingStationEnabled = false;
        var vm = CreateViewModel();

        // Act
        await vm.ReportStatusAsync();

        // Assert
        _mockSignalR.Verify(
            x => x.SetGamingStationAvailableAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ReportStatusAsync_WhenEnabled_ReportsStatus()
    {
        // Arrange
        _settings.IsGamingStationEnabled = true;
        _settings.GamingStationDisplayName = "My Station";
        _mockSignalR
            .Setup(x => x.SetGamingStationAvailableAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();

        // Act
        await vm.ReportStatusAsync();

        // Assert
        _mockSignalR.Verify(
            x => x.SetGamingStationAvailableAsync(true, "My Station", _currentMachineId),
            Times.Once);
    }

    #endregion

    #region CommandJoinCurrentChannelAsync Tests

    [Fact]
    public async Task CommandJoinCurrentChannelAsync_WhenNotInVoice_DoesNotSend()
    {
        // Arrange
        _currentVoiceChannelId = null;
        var vm = CreateViewModel();

        // Act
        await vm.CommandJoinCurrentChannelAsync("target-machine");

        // Assert
        _mockSignalR.Verify(
            x => x.CommandStationJoinChannelAsync(It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task CommandJoinCurrentChannelAsync_WhenInVoice_SendsCommand()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _currentVoiceChannelId = channelId;
        _mockSignalR
            .Setup(x => x.CommandStationJoinChannelAsync(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();

        // Act
        await vm.CommandJoinCurrentChannelAsync("target-machine");

        // Assert
        _mockSignalR.Verify(
            x => x.CommandStationJoinChannelAsync("target-machine", channelId),
            Times.Once);
    }

    #endregion

    #region CommandLeaveChannelAsync Tests

    [Fact]
    public async Task CommandLeaveChannelAsync_SendsCommand()
    {
        // Arrange
        _mockSignalR
            .Setup(x => x.CommandStationLeaveChannelAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();

        // Act
        await vm.CommandLeaveChannelAsync("target-machine");

        // Assert
        _mockSignalR.Verify(
            x => x.CommandStationLeaveChannelAsync("target-machine"),
            Times.Once);
    }

    #endregion

    #region Event Tests

    [Fact]
    public void ErrorOccurred_CanBeSubscribed()
    {
        // Arrange
        var vm = CreateViewModel();
        string? errorMessage = null;
        vm.ErrorOccurred += msg => errorMessage = msg;

        // The ErrorOccurred event is raised internally, so we can't easily trigger it
        // This test just verifies the event can be subscribed to
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ViewOpening_CanBeSubscribed()
    {
        // Arrange
        var vm = CreateViewModel();
        var viewOpeningFired = false;
        vm.ViewOpening += () => viewOpeningFired = true;

        // The ViewOpening event is raised when OpenAsync is called, which we test elsewhere
        Assert.False(viewOpeningFired);
    }

    #endregion
}
