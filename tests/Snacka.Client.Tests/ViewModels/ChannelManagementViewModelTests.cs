using Moq;
using Snacka.Client.Coordinators;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class ChannelManagementViewModelTests
{
    private readonly Mock<IChannelCoordinator> _channelCoordinatorMock;
    private readonly Mock<IChannelStore> _channelStoreMock;
    private readonly Mock<ICommunityStore> _communityStoreMock;
    private readonly Guid _communityId = Guid.NewGuid();
    private readonly Guid _channelId = Guid.NewGuid();

    public ChannelManagementViewModelTests()
    {
        _channelCoordinatorMock = new Mock<IChannelCoordinator>();
        _channelStoreMock = new Mock<IChannelStore>();
        _communityStoreMock = new Mock<ICommunityStore>();
    }

    private CommunityState CreateCommunityState(Guid? id = null, string name = "Test Community")
    {
        return new CommunityState(
            Id: id ?? _communityId,
            Name: name,
            Description: "A test community",
            Icon: null,
            OwnerId: Guid.NewGuid(),
            OwnerUsername: "owner",
            OwnerEffectiveDisplayName: "Owner",
            CreatedAt: DateTime.UtcNow,
            MemberCount: 1);
    }

    private ChannelState CreateChannelState(
        Guid? id = null,
        string name = "test-channel",
        Guid? communityId = null)
    {
        return new ChannelState(
            id ?? _channelId,
            name,
            null,
            ChannelType.Text,
            communityId ?? _communityId,
            0,
            0,
            DateTime.UtcNow);
    }

    private ChannelManagementViewModel CreateViewModel(
        CommunityState? selectedCommunity = null,
        ChannelState? selectedChannel = null,
        List<ChannelState>? allChannels = null,
        List<ChannelState>? textChannels = null)
    {
        var community = selectedCommunity ?? CreateCommunityState();
        var channel = selectedChannel ?? CreateChannelState();
        var allCh = allChannels ?? new List<ChannelState> { channel };
        var textCh = textChannels ?? allCh.Where(c => c.Type == ChannelType.Text).ToList();

        _communityStoreMock.Setup(s => s.GetSelectedCommunity()).Returns(community);
        _channelStoreMock.Setup(s => s.GetSelectedChannel()).Returns(channel);
        _channelStoreMock.Setup(s => s.GetAllChannels()).Returns(allCh);
        _channelStoreMock.Setup(s => s.GetTextChannels()).Returns(textCh);

        return new ChannelManagementViewModel(
            _channelCoordinatorMock.Object,
            _channelStoreMock.Object,
            _communityStoreMock.Object,
            () => null);  // VoiceChannelViewModelManager - not needed for these tests
    }

    #region Initialization Tests

    [Fact]
    public void Constructor_InitializesWithDefaultState()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Null(vm.EditingChannel);
        Assert.Empty(vm.EditingChannelName);
        Assert.Null(vm.ChannelPendingDelete);
        Assert.False(vm.ShowChannelDeleteConfirmation);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void Constructor_InitializesCommands()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.StartEditChannelCommand);
        Assert.NotNull(vm.SaveChannelNameCommand);
        Assert.NotNull(vm.CancelEditChannelCommand);
        Assert.NotNull(vm.DeleteChannelCommand);
        Assert.NotNull(vm.ConfirmDeleteChannelCommand);
        Assert.NotNull(vm.CancelDeleteChannelCommand);
        Assert.NotNull(vm.ReorderChannelsCommand);
        Assert.NotNull(vm.PreviewReorderCommand);
        Assert.NotNull(vm.CancelPreviewCommand);
    }

    #endregion

    #region Edit Channel Tests

    [Fact]
    public void StartEditChannel_SetsEditingState()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState(name: "my-channel");

        // Act
        vm.StartEditChannel(channel);

        // Assert
        Assert.Equal(channel, vm.EditingChannel);
        Assert.Equal("my-channel", vm.EditingChannelName);
    }

    [Fact]
    public void CancelEditChannel_ClearsEditingState()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState();

        vm.StartEditChannel(channel);
        Assert.NotNull(vm.EditingChannel);

        // Act
        vm.CancelEditChannel();

        // Assert
        Assert.Null(vm.EditingChannel);
        Assert.Empty(vm.EditingChannelName);
    }

    [Fact]
    public async Task SaveChannelNameAsync_UpdatesChannel()
    {
        // Arrange
        var vm = CreateViewModel();
        var channelId = Guid.NewGuid();
        var channel = CreateChannelState(id: channelId, name: "old-name");

        var channelState = new ChannelState(
            channelId, "new-name", null, ChannelType.Text, _communityId, 0, 0, DateTime.UtcNow);

        vm.StartEditChannel(channel);
        vm.EditingChannelName = "new-name";

        _channelCoordinatorMock.Setup(x => x.UpdateChannelAsync(_communityId, channelId, "new-name", null))
            .ReturnsAsync(true);
        _channelStoreMock.Setup(x => x.GetChannel(channelId))
            .Returns(channelState);

        // Act
        await vm.SaveChannelNameAsync();

        // Assert
        _channelCoordinatorMock.Verify(x => x.UpdateChannelAsync(_communityId, channelId, "new-name", null), Times.Once);
        Assert.Null(vm.EditingChannel);
        Assert.Empty(vm.EditingChannelName);
    }

    [Fact]
    public async Task SaveChannelNameAsync_WhenNoEditingChannel_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.SaveChannelNameAsync();

        // Assert
        _channelCoordinatorMock.Verify(
            x => x.UpdateChannelAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveChannelNameAsync_OnError_RaisesErrorEvent()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState();

        vm.StartEditChannel(channel);
        vm.EditingChannelName = "new-name";

        string? capturedError = null;
        vm.ErrorOccurred += err => capturedError = err;

        _channelCoordinatorMock.Setup(x => x.UpdateChannelAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(false);

        // Act
        await vm.SaveChannelNameAsync();

        // Assert
        Assert.NotNull(capturedError);
        Assert.Contains("Failed to update channel", capturedError);
    }

    #endregion

    #region Delete Channel Tests

    [Fact]
    public void RequestDeleteChannel_SetsChannelPendingDelete()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState();

        // Act
        vm.RequestDeleteChannel(channel);

        // Assert
        Assert.Equal(channel, vm.ChannelPendingDelete);
        Assert.True(vm.ShowChannelDeleteConfirmation);
    }

    [Fact]
    public void CancelDeleteChannel_ClearsChannelPendingDelete()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState();

        vm.RequestDeleteChannel(channel);
        Assert.NotNull(vm.ChannelPendingDelete);

        // Act
        vm.CancelDeleteChannel();

        // Assert
        Assert.Null(vm.ChannelPendingDelete);
        Assert.False(vm.ShowChannelDeleteConfirmation);
    }

    [Fact]
    public async Task ConfirmDeleteChannelAsync_DeletesChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var channel = CreateChannelState(id: channelId);
        var otherChannel = CreateChannelState(id: Guid.NewGuid(), name: "other-channel");
        var vm = CreateViewModel(
            selectedChannel: channel,
            allChannels: new List<ChannelResponse> { channel, otherChannel },
            textChannels: new List<ChannelResponse> { channel, otherChannel });

        vm.RequestDeleteChannel(channel);

        _channelCoordinatorMock.Setup(x => x.DeleteChannelAsync(channelId))
            .ReturnsAsync(true);

        // Act
        await vm.ConfirmDeleteChannelAsync();

        // Assert
        _channelCoordinatorMock.Verify(x => x.DeleteChannelAsync(channelId), Times.Once);
        Assert.Null(vm.ChannelPendingDelete);
    }

    [Fact]
    public async Task ConfirmDeleteChannelAsync_OnError_RaisesErrorEvent()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState();

        vm.RequestDeleteChannel(channel);

        string? capturedError = null;
        vm.ErrorOccurred += err => capturedError = err;

        _channelCoordinatorMock.Setup(x => x.DeleteChannelAsync(It.IsAny<Guid>()))
            .ReturnsAsync(false);

        // Act
        await vm.ConfirmDeleteChannelAsync();

        // Assert
        Assert.NotNull(capturedError);
        Assert.Contains("Failed to delete channel", capturedError);
    }

    [Fact]
    public async Task ConfirmDeleteChannelAsync_WhenNoChannelPending_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.ConfirmDeleteChannelAsync();

        // Assert
        _channelCoordinatorMock.Verify(x => x.DeleteChannelAsync(It.IsAny<Guid>()), Times.Never);
    }

    #endregion

    #region Reorder Tests

    [Fact]
    public async Task ReorderChannelsAsync_ReordersChannels()
    {
        // Arrange
        var channel1 = CreateChannelState(id: Guid.NewGuid(), name: "channel-1");
        var channel2 = CreateChannelState(id: Guid.NewGuid(), name: "channel-2");
        var vm = CreateViewModel(allChannels: new List<ChannelResponse> { channel1, channel2 });

        var newOrder = new List<Guid> { channel2.Id, channel1.Id };

        _channelCoordinatorMock.Setup(x => x.ReorderChannelsAsync(_communityId, newOrder))
            .ReturnsAsync(true);

        // Act
        await vm.ReorderChannelsAsync(newOrder);

        // Assert
        _channelCoordinatorMock.Verify(x => x.ReorderChannelsAsync(_communityId, newOrder), Times.Once);
    }

    [Fact]
    public async Task ReorderChannelsAsync_OnError_RaisesErrorEvent()
    {
        // Arrange
        var channel = CreateChannelState();
        var vm = CreateViewModel(allChannels: new List<ChannelResponse> { channel });

        var newOrder = new List<Guid> { channel.Id };

        string? capturedError = null;
        vm.ErrorOccurred += err => capturedError = err;

        _channelCoordinatorMock.Setup(x => x.ReorderChannelsAsync(_communityId, newOrder))
            .ReturnsAsync(false);

        // Act
        await vm.ReorderChannelsAsync(newOrder);

        // Assert
        Assert.NotNull(capturedError);
        Assert.Contains("Failed to reorder channels", capturedError);
    }

    [Fact]
    public async Task ReorderChannelsAsync_SetsPendingReorderCommunityId()
    {
        // Arrange
        var channel = CreateChannelState();
        var vm = CreateViewModel(allChannels: new List<ChannelResponse> { channel });

        var newOrder = new List<Guid> { channel.Id };

        _channelCoordinatorMock.Setup(x => x.ReorderChannelsAsync(_communityId, newOrder))
            .ReturnsAsync(true);

        // Act
        await vm.ReorderChannelsAsync(newOrder);

        // Assert
        Assert.Equal(_communityId, vm.PendingReorderCommunityId);
    }

    [Fact]
    public void ClearPendingReorder_ClearsPendingReorderCommunityId()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.PendingReorderCommunityId = _communityId;

        // Act
        vm.ClearPendingReorder();

        // Assert
        Assert.Null(vm.PendingReorderCommunityId);
    }

    #endregion

    #region Loading State Tests

    [Fact]
    public async Task SaveChannelNameAsync_SetsAndClearsLoadingState()
    {
        // Arrange
        var vm = CreateViewModel();
        var channel = CreateChannelState();

        vm.StartEditChannel(channel);
        vm.EditingChannelName = "new-name";

        var loadingStates = new List<bool>();
        vm.LoadingChanged += loading => loadingStates.Add(loading);

        _channelCoordinatorMock.Setup(x => x.UpdateChannelAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        // Act
        await vm.SaveChannelNameAsync();

        // Assert
        Assert.Equal(2, loadingStates.Count);
        Assert.True(loadingStates[0]);  // Loading started
        Assert.False(loadingStates[1]); // Loading ended
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
