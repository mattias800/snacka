using Moq;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class ThreadPanelViewModelTests
{
    private readonly Mock<IApiClient> _apiClientMock;
    private readonly Mock<IMessageStore> _messageStoreMock;
    private readonly Mock<ISignalRService> _signalRMock;
    private readonly Guid _currentUserId = Guid.NewGuid();

    public ThreadPanelViewModelTests()
    {
        _apiClientMock = new Mock<IApiClient>();
        _messageStoreMock = new Mock<IMessageStore>();
        _signalRMock = new Mock<ISignalRService>();
    }

    private ThreadPanelViewModel CreateViewModel()
    {
        return new ThreadPanelViewModel(
            _apiClientMock.Object,
            _messageStoreMock.Object,
            _signalRMock.Object,
            _currentUserId);
    }

    private static MessageResponse CreateTestMessage(Guid? id = null)
    {
        return new MessageResponse(
            Id: id ?? Guid.NewGuid(),
            Content: "Test message",
            AuthorId: Guid.NewGuid(),
            AuthorUsername: "TestUser",
            AuthorEffectiveDisplayName: "Test User",
            AuthorAvatar: null,
            ChannelId: Guid.NewGuid(),
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsEdited: false,
            ReplyToId: null,
            ReplyTo: null,
            Reactions: null,
            IsPinned: false,
            PinnedAt: null,
            PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null,
            Attachments: null,
            ThreadParentMessageId: null,
            ReplyCount: 0,
            LastReplyAt: null);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesCommands()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.OpenCommand);
        Assert.NotNull(vm.CloseCommand);
    }

    [Fact]
    public void Constructor_InitializesWithNoThread()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Null(vm.CurrentThread);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Constructor_InitializesDefaultPanelWidth()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Equal(400, vm.PanelWidth);
    }

    #endregion

    #region PanelWidth Tests

    [Fact]
    public void PanelWidth_CanBeSetAndRetrieved()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.PanelWidth = 500;

        // Assert
        Assert.Equal(500, vm.PanelWidth);
    }

    [Fact]
    public void PanelWidth_RaisesPropertyChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThreadPanelViewModel.PanelWidth))
                propertyChangedRaised = true;
        };

        // Act
        vm.PanelWidth = 450;

        // Assert
        Assert.True(propertyChangedRaised);
    }

    #endregion

    #region Close Tests

    [Fact]
    public void Close_WhenNoThreadOpen_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.Close();
    }

    [Fact]
    public void Close_UpdatesIsOpen()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.Close();

        // Assert
        Assert.False(vm.IsOpen);
    }

    #endregion

    #region UpdateThreadMetadata Tests

    [Fact]
    public void UpdateThreadMetadata_DelegatesToMessageStore()
    {
        // Arrange
        var vm = CreateViewModel();
        var parentMessageId = Guid.NewGuid();
        var replyCount = 5;
        var lastReplyAt = DateTime.UtcNow;

        // Act
        vm.UpdateThreadMetadata(parentMessageId, replyCount, lastReplyAt);

        // Assert
        _messageStoreMock.Verify(
            x => x.UpdateThreadMetadata(parentMessageId, replyCount, lastReplyAt),
            Times.Once);
    }

    #endregion

    #region SignalR Handler Tests

    [Fact]
    public void Constructor_SubscribesToSignalREvents()
    {
        // Act
        var vm = CreateViewModel();

        // Assert - verify event handlers were registered
        _signalRMock.VerifyAdd(x => x.MessageEdited += It.IsAny<Action<MessageResponse>>(), Times.Once);
        _signalRMock.VerifyAdd(x => x.MessageDeleted += It.IsAny<Action<MessageDeletedEvent>>(), Times.Once);
        _signalRMock.VerifyAdd(x => x.ThreadReplyReceived += It.IsAny<Action<ThreadReplyEvent>>(), Times.Once);
        _signalRMock.VerifyAdd(x => x.ReactionUpdated += It.IsAny<Action<ReactionUpdatedEvent>>(), Times.Once);
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

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.Dispose();
        vm.Dispose();
    }

    #endregion
}
