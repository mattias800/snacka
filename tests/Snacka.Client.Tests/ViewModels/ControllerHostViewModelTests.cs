using System.Collections.ObjectModel;
using Moq;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class ControllerHostViewModelTests
{
    private readonly Mock<IControllerHostService> _controllerHostServiceMock;
    private readonly Mock<IVoiceStore> _voiceStoreMock;
    private readonly ObservableCollection<ControllerAccessRequest> _pendingRequests;
    private readonly ObservableCollection<ActiveControllerSession> _activeSessions;

    public ControllerHostViewModelTests()
    {
        _pendingRequests = new ObservableCollection<ControllerAccessRequest>();
        _activeSessions = new ObservableCollection<ActiveControllerSession>();

        _controllerHostServiceMock = new Mock<IControllerHostService>();
        _controllerHostServiceMock.Setup(x => x.PendingRequests).Returns(_pendingRequests);
        _controllerHostServiceMock.Setup(x => x.ActiveSessions).Returns(_activeSessions);

        _voiceStoreMock = new Mock<IVoiceStore>();
        _voiceStoreMock.Setup(s => s.GetCurrentChannelId()).Returns(Guid.NewGuid());
    }

    private ControllerHostViewModel CreateViewModel()
    {
        return new ControllerHostViewModel(
            _controllerHostServiceMock.Object,
            _voiceStoreMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesCommands()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.AcceptRequestCommand);
        Assert.NotNull(vm.DeclineRequestCommand);
        Assert.NotNull(vm.StopSessionCommand);
        Assert.NotNull(vm.ToggleMuteSessionCommand);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void PendingRequests_ReturnsPendingRequestsFromService()
    {
        // Arrange
        var request = new ControllerAccessRequest(Guid.NewGuid(), Guid.NewGuid(), "TestUser", DateTime.UtcNow);
        _pendingRequests.Add(request);
        var vm = CreateViewModel();

        // Assert
        Assert.Single(vm.PendingRequests);
        Assert.Equal(request, vm.PendingRequests[0]);
    }

    [Fact]
    public void ActiveSessions_ReturnsActiveSessionsFromService()
    {
        // Arrange
        var session = new ActiveControllerSession(Guid.NewGuid(), Guid.NewGuid(), "TestUser", 0);
        _activeSessions.Add(session);
        var vm = CreateViewModel();

        // Assert
        Assert.Single(vm.ActiveSessions);
        Assert.Equal(session, vm.ActiveSessions[0]);
    }

    [Fact]
    public void HasPendingRequests_WhenEmpty_ReturnsFalse()
    {
        // Arrange
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.HasPendingRequests);
    }

    [Fact]
    public void HasPendingRequests_WhenHasRequests_ReturnsTrue()
    {
        // Arrange
        _pendingRequests.Add(new ControllerAccessRequest(Guid.NewGuid(), Guid.NewGuid(), "User", DateTime.UtcNow));
        var vm = CreateViewModel();

        // Assert
        Assert.True(vm.HasPendingRequests);
    }

    [Fact]
    public void HasActiveSessions_WhenEmpty_ReturnsFalse()
    {
        // Arrange
        var vm = CreateViewModel();

        // Assert
        Assert.False(vm.HasActiveSessions);
    }

    [Fact]
    public void HasActiveSessions_WhenHasSessions_ReturnsTrue()
    {
        // Arrange
        _activeSessions.Add(new ActiveControllerSession(Guid.NewGuid(), Guid.NewGuid(), "User", 0));
        var vm = CreateViewModel();

        // Assert
        Assert.True(vm.HasActiveSessions);
    }

    [Fact]
    public void SelectedControllerSlot_CanBeSetAndRetrieved()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.SelectedControllerSlot = 2;

        // Assert
        Assert.Equal(2, vm.SelectedControllerSlot);
    }

    [Fact]
    public void AvailableSlots_ReturnsAllFourSlots()
    {
        // Arrange
        var vm = CreateViewModel();

        // Assert
        Assert.Equal(4, vm.AvailableSlots.Length);
        Assert.Equal([0, 1, 2, 3], vm.AvailableSlots);
    }

    #endregion

    #region IsSessionMuted Tests

    [Fact]
    public void IsSessionMuted_DelegatesToService()
    {
        // Arrange
        var guestUserId = Guid.NewGuid();
        _controllerHostServiceMock.Setup(x => x.IsSessionMuted(guestUserId)).Returns(true);
        var vm = CreateViewModel();

        // Act
        var result = vm.IsSessionMuted(guestUserId);

        // Assert
        Assert.True(result);
        _controllerHostServiceMock.Verify(x => x.IsSessionMuted(guestUserId), Times.Once);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_DisposesCommands()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.Dispose();
    }

    #endregion
}
