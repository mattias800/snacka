using Moq;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;
using Xunit;

namespace Snacka.Client.Tests.ViewModels;

public class MessageInputViewModelTests
{
    private readonly Mock<IApiClient> _apiClientMock;
    private readonly Mock<ISignalRService> _signalRMock;
    private readonly Mock<IMessageStore> _messageStoreMock;
    private readonly Mock<IChannelStore> _channelStoreMock;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _channelId = Guid.NewGuid();

    public MessageInputViewModelTests()
    {
        _apiClientMock = new Mock<IApiClient>();
        _signalRMock = new Mock<ISignalRService>();
        _messageStoreMock = new Mock<IMessageStore>();
        _channelStoreMock = new Mock<IChannelStore>();
        _channelStoreMock.Setup(s => s.GetSelectedChannelId()).Returns(_channelId);
    }

    private MessageResponse CreateMessageResponse(
        string content,
        Guid? id = null,
        Guid? authorId = null,
        Guid? channelId = null,
        Guid? replyToId = null)
    {
        return new MessageResponse(
            Id: id ?? Guid.NewGuid(),
            Content: content,
            AuthorId: authorId ?? _userId,
            AuthorUsername: "testuser",
            AuthorEffectiveDisplayName: "Test User",
            AuthorAvatar: null,
            ChannelId: channelId ?? _channelId,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsEdited: false,
            ReplyToId: replyToId,
            ReplyTo: null,
            Reactions: null,
            IsPinned: false,
            PinnedAt: null,
            PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null,
            Attachments: null,
            ThreadParentMessageId: null,
            ReplyCount: 0,
            LastReplyAt: null
        );
    }

    private MessageInputViewModel CreateViewModel(
        Func<IEnumerable<CommunityMemberResponse>>? getMembers = null,
        bool gifsEnabled = false)
    {
        return new MessageInputViewModel(
            _apiClientMock.Object,
            _signalRMock.Object,
            _messageStoreMock.Object,
            _channelStoreMock.Object,
            _userId,
            getMembers ?? (() => Array.Empty<CommunityMemberResponse>()),
            gifsEnabled);
    }

    #region Initialization Tests

    [Fact]
    public void Constructor_InitializesWithDefaultState()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.Empty(vm.MessageInput);
        Assert.Null(vm.EditingMessage);
        Assert.Empty(vm.EditingMessageContent);
        Assert.Null(vm.ReplyingToMessage);
        Assert.False(vm.IsReplying);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasPendingAttachments);
    }

    [Fact]
    public void Constructor_InitializesCommands()
    {
        // Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.SendMessageCommand);
        Assert.NotNull(vm.StartEditMessageCommand);
        Assert.NotNull(vm.SaveMessageEditCommand);
        Assert.NotNull(vm.CancelEditMessageCommand);
        Assert.NotNull(vm.DeleteMessageCommand);
        Assert.NotNull(vm.ReplyToMessageCommand);
        Assert.NotNull(vm.CancelReplyCommand);
    }

    #endregion

    #region MessageInput Tests

    [Fact]
    public void MessageInput_WhenSet_RaisesPropertyChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChangedRaised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MessageInputViewModel.MessageInput))
                propertyChangedRaised = true;
        };

        // Act
        vm.MessageInput = "Hello";

        // Assert
        Assert.True(propertyChangedRaised);
        Assert.Equal("Hello", vm.MessageInput);
    }

    [Fact]
    public void MessageInput_WhenSetWithContent_SendsTypingIndicator()
    {
        // Arrange
        var vm = CreateViewModel();
        _signalRMock.Setup(x => x.SendTypingAsync(_channelId))
            .Returns(Task.CompletedTask);

        // Act
        vm.MessageInput = "Hello";

        // Assert - typing indicator should be sent
        _signalRMock.Verify(x => x.SendTypingAsync(_channelId), Times.Once);
    }

    [Fact]
    public void MessageInput_WhenNotInChannel_DoesNotSendTypingIndicator()
    {
        // Arrange
        var vm = CreateViewModel(getCurrentChannelId: () => null);

        // Act
        vm.MessageInput = "Hello";

        // Assert
        _signalRMock.Verify(x => x.SendTypingAsync(It.IsAny<Guid>()), Times.Never);
    }

    #endregion

    #region SendMessageAsync Tests

    [Fact]
    public async Task SendMessageAsync_WhenNoChannel_DoesNotSend()
    {
        // Arrange
        var vm = CreateViewModel(getSelectedChannel: () => null);
        vm.MessageInput = "Hello";

        // Act
        await vm.SendMessageAsync();

        // Assert
        _apiClientMock.Verify(x => x.SendMessageAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task SendMessageAsync_WhenEmptyMessage_DoesNotSend()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.MessageInput = "   ";

        // Act
        await vm.SendMessageAsync();

        // Assert
        _apiClientMock.Verify(x => x.SendMessageAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task SendMessageAsync_SendsMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.MessageInput = "Hello world";

        var messageResponse = CreateMessageResponse("Hello world");

        _apiClientMock.Setup(x => x.SendMessageAsync(_channelId, "Hello world", null))
            .ReturnsAsync(ApiResult<MessageResponse>.Ok(messageResponse));

        // Act
        await vm.SendMessageAsync();

        // Assert
        _apiClientMock.Verify(x => x.SendMessageAsync(_channelId, "Hello world", null), Times.Once);
        _messageStoreMock.Verify(x => x.AddMessage(messageResponse), Times.Once);
        Assert.Empty(vm.MessageInput);
    }

    [Fact]
    public async Task SendMessageAsync_WithReply_SendsWithReplyId()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyToMessage = CreateMessageResponse("Original");

        vm.StartReplyToMessage(replyToMessage);
        vm.MessageInput = "Reply text";

        var messageResponse = CreateMessageResponse("Reply text", replyToId: replyToMessage.Id);

        _apiClientMock.Setup(x => x.SendMessageAsync(_channelId, "Reply text", replyToMessage.Id))
            .ReturnsAsync(ApiResult<MessageResponse>.Ok(messageResponse));

        // Act
        await vm.SendMessageAsync();

        // Assert
        _apiClientMock.Verify(x => x.SendMessageAsync(_channelId, "Reply text", replyToMessage.Id), Times.Once);
        Assert.Null(vm.ReplyingToMessage);
        Assert.False(vm.IsReplying);
    }

    [Fact]
    public async Task SendMessageAsync_OnError_RestoresMessageAndRaisesError()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.MessageInput = "Hello world";

        string? capturedError = null;
        vm.ErrorOccurred += err => capturedError = err;

        _apiClientMock.Setup(x => x.SendMessageAsync(_channelId, "Hello world", null))
            .ReturnsAsync(ApiResult<MessageResponse>.Fail("Send failed"));

        // Act
        await vm.SendMessageAsync();

        // Assert
        Assert.Equal("Hello world", vm.MessageInput); // Message restored
        Assert.NotNull(capturedError);
        Assert.Contains("Send failed", capturedError);
    }

    [Fact]
    public async Task SendMessageAsync_GifCommand_RaisesGifPreviewRequested()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.MessageInput = "/gif cats";

        string? capturedQuery = null;
        vm.GifPreviewRequested += async query =>
        {
            capturedQuery = query;
            await Task.CompletedTask;
        };

        // Act
        await vm.SendMessageAsync();

        // Assert
        Assert.Equal("cats", capturedQuery);
        Assert.Empty(vm.MessageInput);
        _apiClientMock.Verify(x => x.SendMessageAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()), Times.Never);
    }

    #endregion

    #region Reply Tests

    [Fact]
    public void StartReplyToMessage_SetsReplyingToMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var message = CreateMessageResponse("Test");

        // Act
        vm.StartReplyToMessage(message);

        // Assert
        Assert.Equal(message, vm.ReplyingToMessage);
        Assert.True(vm.IsReplying);
    }

    [Fact]
    public void CancelReply_ClearsReplyingToMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var message = CreateMessageResponse("Test");

        vm.StartReplyToMessage(message);
        Assert.True(vm.IsReplying);

        // Act
        vm.CancelReply();

        // Assert
        Assert.Null(vm.ReplyingToMessage);
        Assert.False(vm.IsReplying);
    }

    #endregion

    #region Edit Tests

    [Fact]
    public void StartEditMessage_OwnMessage_SetsEditingState()
    {
        // Arrange
        var vm = CreateViewModel();
        var message = CreateMessageResponse("Original content");

        // Act
        vm.StartEditMessage(message);

        // Assert
        Assert.Equal(message, vm.EditingMessage);
        Assert.Equal("Original content", vm.EditingMessageContent);
    }

    [Fact]
    public void StartEditMessage_OtherUserMessage_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        var otherUserId = Guid.NewGuid();
        var message = CreateMessageResponse("Other's message", authorId: otherUserId);

        // Act
        vm.StartEditMessage(message);

        // Assert
        Assert.Null(vm.EditingMessage);
        Assert.Empty(vm.EditingMessageContent);
    }

    [Fact]
    public void CancelEditMessage_ClearsEditingState()
    {
        // Arrange
        var vm = CreateViewModel();
        var message = CreateMessageResponse("Test");

        vm.StartEditMessage(message);

        // Act
        vm.CancelEditMessage();

        // Assert
        Assert.Null(vm.EditingMessage);
        Assert.Empty(vm.EditingMessageContent);
    }

    [Fact]
    public async Task SaveMessageEditAsync_UpdatesMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var messageId = Guid.NewGuid();
        var message = CreateMessageResponse("Original", id: messageId);

        vm.StartEditMessage(message);
        vm.EditingMessageContent = "Updated content";

        var updatedMessage = CreateMessageResponse("Updated content", id: messageId);

        _apiClientMock.Setup(x => x.UpdateMessageAsync(_channelId, messageId, "Updated content"))
            .ReturnsAsync(ApiResult<MessageResponse>.Ok(updatedMessage));

        // Act
        await vm.SaveMessageEditAsync();

        // Assert
        _messageStoreMock.Verify(x => x.UpdateMessage(It.Is<MessageResponse>(m => m.Id == messageId)), Times.Once);
        Assert.Null(vm.EditingMessage);
        Assert.Empty(vm.EditingMessageContent);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task DeleteMessageAsync_DeletesMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var messageId = Guid.NewGuid();
        var message = CreateMessageResponse("Test", id: messageId);

        _apiClientMock.Setup(x => x.DeleteMessageAsync(_channelId, messageId))
            .ReturnsAsync(ApiResult<bool>.Ok(true));

        // Act
        await vm.DeleteMessageAsync(message);

        // Assert
        _messageStoreMock.Verify(x => x.DeleteMessage(messageId), Times.Once);
    }

    [Fact]
    public async Task DeleteMessageAsync_OnError_RaisesError()
    {
        // Arrange
        var vm = CreateViewModel();
        var message = CreateMessageResponse("Test");

        string? capturedError = null;
        vm.ErrorOccurred += err => capturedError = err;

        _apiClientMock.Setup(x => x.DeleteMessageAsync(_channelId, message.Id))
            .ReturnsAsync(ApiResult<bool>.Fail("Delete failed"));

        // Act
        await vm.DeleteMessageAsync(message);

        // Assert
        Assert.NotNull(capturedError);
        Assert.Contains("Delete failed", capturedError);
    }

    #endregion

    #region Attachment Tests

    [Fact]
    public void AddPendingAttachment_AddsToCollection()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = new MemoryStream();

        // Act
        vm.AddPendingAttachment("test.txt", stream, 100, "text/plain");

        // Assert
        Assert.Single(vm.PendingAttachments);
        Assert.True(vm.HasPendingAttachments);
    }

    [Fact]
    public void RemovePendingAttachment_RemovesFromCollection()
    {
        // Arrange
        var vm = CreateViewModel();
        var stream = new MemoryStream();
        vm.AddPendingAttachment("test.txt", stream, 100, "text/plain");
        var attachment = vm.PendingAttachments[0];

        // Act
        vm.RemovePendingAttachment(attachment);

        // Assert
        Assert.Empty(vm.PendingAttachments);
        Assert.False(vm.HasPendingAttachments);
    }

    [Fact]
    public void ClearPendingAttachments_ClearsAll()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.AddPendingAttachment("test1.txt", new MemoryStream(), 100, "text/plain");
        vm.AddPendingAttachment("test2.txt", new MemoryStream(), 200, "text/plain");

        // Act
        vm.ClearPendingAttachments();

        // Assert
        Assert.Empty(vm.PendingAttachments);
        Assert.False(vm.HasPendingAttachments);
    }

    #endregion

    #region Autocomplete Tests

    [Fact]
    public void CloseAutocompletePopup_ClosesPopup()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act - should not throw
        vm.CloseAutocompletePopup();

        // Assert - popup should be closed (no exception)
        Assert.False(vm.IsAutocompletePopupOpen);
    }

    [Fact]
    public void NavigateAutocompleteUp_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.NavigateAutocompleteUp();
    }

    [Fact]
    public void NavigateAutocompleteDown_DoesNotThrow()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.NavigateAutocompleteDown();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CleansUpResources()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.AddPendingAttachment("test.txt", new MemoryStream(), 100, "text/plain");

        // Act
        vm.Dispose();

        // Assert
        Assert.Empty(vm.PendingAttachments);
    }

    #endregion
}
