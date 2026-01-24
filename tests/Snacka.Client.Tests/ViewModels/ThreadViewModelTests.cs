using System.Reactive;
using System.Reactive.Linq;
using Moq;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Tests.ViewModels;

public class ThreadViewModelTests : IDisposable
{
    private readonly Mock<IApiClient> _mockApiClient;
    private readonly Guid _parentMessageId;
    private readonly MessageResponse _parentMessage;
    private bool _closeCalled;

    public ThreadViewModelTests()
    {
        _mockApiClient = new Mock<IApiClient>();
        _parentMessageId = Guid.NewGuid();
        _closeCalled = false;

        _parentMessage = CreateMessageResponse(_parentMessageId, "Parent message content");
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    private ThreadViewModel CreateViewModel(MessageResponse? parentMessage = null)
    {
        return new ThreadViewModel(
            _mockApiClient.Object,
            parentMessage ?? _parentMessage,
            () => _closeCalled = true
        );
    }

    private static MessageResponse CreateMessageResponse(
        Guid? id = null,
        string content = "Test content",
        Guid? authorId = null,
        Guid? channelId = null,
        List<ReactionSummary>? reactions = null)
    {
        return new MessageResponse(
            Id: id ?? Guid.NewGuid(),
            Content: content,
            AuthorId: authorId ?? Guid.NewGuid(),
            AuthorUsername: "testuser",
            AuthorEffectiveDisplayName: "Test User",
            AuthorAvatar: null,
            ChannelId: channelId ?? Guid.NewGuid(),
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsEdited: false,
            ReplyToId: null,
            ReplyTo: null,
            Reactions: reactions,
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

    private static ThreadResponse CreateThreadResponse(
        MessageResponse parentMessage,
        List<MessageResponse>? replies = null,
        int totalReplyCount = 0)
    {
        return new ThreadResponse(
            ParentMessage: parentMessage,
            Replies: replies ?? new List<MessageResponse>(),
            TotalReplyCount: totalReplyCount,
            Page: 1,
            PageSize: 50
        );
    }

    #region Initial State Tests

    [Fact]
    public void Constructor_InitialState_IsCorrect()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.Equal(_parentMessage, vm.ParentMessage);
        Assert.Empty(vm.Replies);
        Assert.Equal(string.Empty, vm.ReplyInput);
        Assert.False(vm.IsLoading);
        Assert.Equal(0, vm.TotalReplyCount);
        Assert.False(vm.HasMoreReplies);
    }

    [Fact]
    public void Constructor_Commands_AreNotNull()
    {
        // Arrange & Act
        var vm = CreateViewModel();

        // Assert
        Assert.NotNull(vm.SendReplyCommand);
        Assert.NotNull(vm.CloseCommand);
        Assert.NotNull(vm.LoadMoreCommand);
    }

    #endregion

    #region LoadAsync Tests

    [Fact]
    public async Task LoadAsync_Success_PopulatesReplies()
    {
        // Arrange
        var vm = CreateViewModel();
        var replies = new List<MessageResponse>
        {
            CreateMessageResponse(content: "Reply 1"),
            CreateMessageResponse(content: "Reply 2"),
            CreateMessageResponse(content: "Reply 3")
        };
        var threadResponse = CreateThreadResponse(_parentMessage, replies, 3);

        _mockApiClient
            .Setup(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .ReturnsAsync(ApiResult<ThreadResponse>.Ok(threadResponse));

        // Act
        await vm.LoadAsync();

        // Assert
        Assert.Equal(3, vm.Replies.Count);
        Assert.Equal(3, vm.TotalReplyCount);
        Assert.False(vm.HasMoreReplies);
    }

    [Fact]
    public async Task LoadAsync_WithPagination_SetsHasMoreReplies()
    {
        // Arrange
        var vm = CreateViewModel();
        var replies = new List<MessageResponse>
        {
            CreateMessageResponse(content: "Reply 1")
        };
        var threadResponse = CreateThreadResponse(_parentMessage, replies, 100);

        _mockApiClient
            .Setup(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .ReturnsAsync(ApiResult<ThreadResponse>.Ok(threadResponse));

        // Act
        await vm.LoadAsync();

        // Assert
        Assert.Equal(1, vm.Replies.Count);
        Assert.Equal(100, vm.TotalReplyCount);
        Assert.True(vm.HasMoreReplies);
    }

    [Fact]
    public async Task LoadAsync_SetsIsLoadingDuringOperation()
    {
        // Arrange
        var vm = CreateViewModel();
        var loadingStates = new List<bool>();
        var tcs = new TaskCompletionSource<ApiResult<ThreadResponse>>();

        _mockApiClient
            .Setup(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .Returns(tcs.Task);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThreadViewModel.IsLoading))
                loadingStates.Add(vm.IsLoading);
        };

        // Act
        var loadTask = vm.LoadAsync();
        await Task.Delay(50); // Allow time for IsLoading to be set

        // Assert - should be loading
        Assert.True(vm.IsLoading);

        // Complete the API call
        tcs.SetResult(ApiResult<ThreadResponse>.Ok(CreateThreadResponse(_parentMessage)));
        await loadTask;

        // Assert - should not be loading anymore
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_ClearsExistingReplies()
    {
        // Arrange
        var vm = CreateViewModel();
        var firstReplies = new List<MessageResponse>
        {
            CreateMessageResponse(content: "First Reply")
        };
        var secondReplies = new List<MessageResponse>
        {
            CreateMessageResponse(content: "Second Reply")
        };

        _mockApiClient
            .SetupSequence(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .ReturnsAsync(ApiResult<ThreadResponse>.Ok(CreateThreadResponse(_parentMessage, firstReplies, 1)))
            .ReturnsAsync(ApiResult<ThreadResponse>.Ok(CreateThreadResponse(_parentMessage, secondReplies, 1)));

        // Act
        await vm.LoadAsync();
        Assert.Single(vm.Replies);
        Assert.Equal("First Reply", vm.Replies[0].Content);

        await vm.LoadAsync();

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal("Second Reply", vm.Replies[0].Content);
    }

    [Fact]
    public async Task LoadAsync_UpdatesParentMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        var updatedParent = _parentMessage with { Content = "Updated parent content" };
        var threadResponse = CreateThreadResponse(updatedParent, new List<MessageResponse>(), 0);

        _mockApiClient
            .Setup(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .ReturnsAsync(ApiResult<ThreadResponse>.Ok(threadResponse));

        // Act
        await vm.LoadAsync();

        // Assert
        Assert.Equal("Updated parent content", vm.ParentMessage?.Content);
    }

    [Fact]
    public async Task LoadAsync_ApiFailure_KeepsExistingState()
    {
        // Arrange
        var vm = CreateViewModel();

        _mockApiClient
            .Setup(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .ReturnsAsync(ApiResult<ThreadResponse>.Fail("API error"));

        // Act
        await vm.LoadAsync();

        // Assert - should have no replies and no error thrown
        Assert.Empty(vm.Replies);
        Assert.Equal(0, vm.TotalReplyCount);
        Assert.False(vm.IsLoading);
    }

    #endregion

    #region SendReplyCommand Tests

    [Fact]
    public async Task SendReplyCommand_WithValidInput_CreatesReply()
    {
        // Arrange
        var vm = CreateViewModel();
        var newReply = CreateMessageResponse(content: "New reply");

        _mockApiClient
            .Setup(x => x.CreateThreadReplyAsync(_parentMessageId, "New reply", null))
            .ReturnsAsync(ApiResult<MessageResponse>.Ok(newReply));

        vm.ReplyInput = "New reply";

        // Act
        await vm.SendReplyCommand.Execute();

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal("New reply", vm.Replies[0].Content);
        Assert.Equal(string.Empty, vm.ReplyInput);
        Assert.Equal(1, vm.TotalReplyCount);
    }

    [Fact]
    public async Task SendReplyCommand_CannotExecute_WhenInputIsEmpty()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert
        var canExecute = await vm.SendReplyCommand.CanExecute.FirstAsync();
        Assert.False(canExecute);
    }

    [Fact]
    public async Task SendReplyCommand_CannotExecute_WhenInputIsWhitespace()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ReplyInput = "   ";

        // Act & Assert
        var canExecute = await vm.SendReplyCommand.CanExecute.FirstAsync();
        Assert.False(canExecute);
    }

    [Fact]
    public async Task SendReplyCommand_CanExecute_WhenInputHasContent()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ReplyInput = "Valid reply";

        // Act & Assert
        var canExecute = await vm.SendReplyCommand.CanExecute.FirstAsync();
        Assert.True(canExecute);
    }

    [Fact]
    public async Task SendReplyCommand_ApiFailure_DoesNotAddReply()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.ReplyInput = "New reply";

        _mockApiClient
            .Setup(x => x.CreateThreadReplyAsync(_parentMessageId, "New reply", null))
            .ReturnsAsync(ApiResult<MessageResponse>.Fail("API error"));

        // Act
        await vm.SendReplyCommand.Execute();

        // Assert
        Assert.Empty(vm.Replies);
        Assert.Equal("New reply", vm.ReplyInput); // Input should remain
        Assert.Equal(0, vm.TotalReplyCount);
    }

    #endregion

    #region CloseCommand Tests

    [Fact]
    public async Task CloseCommand_InvokesCloseCallback()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.CloseCommand.Execute();

        // Assert
        Assert.True(_closeCalled);
    }

    #endregion

    #region AddReply Tests

    [Fact]
    public void AddReply_AddsReplyToCollection()
    {
        // Arrange
        var vm = CreateViewModel();
        var reply = CreateMessageResponse(content: "New reply");

        // Act
        vm.AddReply(reply);

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal("New reply", vm.Replies[0].Content);
        Assert.Equal(1, vm.TotalReplyCount);
    }

    [Fact]
    public void AddReply_DoesNotAddDuplicate()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var reply = CreateMessageResponse(id: replyId, content: "New reply");

        // Act
        vm.AddReply(reply);
        vm.AddReply(reply); // Try to add again

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal(1, vm.TotalReplyCount);
    }

    [Fact]
    public void AddReply_MultipleReplies_AddsAll()
    {
        // Arrange
        var vm = CreateViewModel();
        var reply1 = CreateMessageResponse(content: "Reply 1");
        var reply2 = CreateMessageResponse(content: "Reply 2");
        var reply3 = CreateMessageResponse(content: "Reply 3");

        // Act
        vm.AddReply(reply1);
        vm.AddReply(reply2);
        vm.AddReply(reply3);

        // Assert
        Assert.Equal(3, vm.Replies.Count);
        Assert.Equal(3, vm.TotalReplyCount);
    }

    #endregion

    #region UpdateReply Tests

    [Fact]
    public void UpdateReply_UpdatesExistingReply()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var originalReply = CreateMessageResponse(id: replyId, content: "Original");
        vm.AddReply(originalReply);

        var updatedReply = originalReply with { Content = "Updated" };

        // Act
        vm.UpdateReply(updatedReply);

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal("Updated", vm.Replies[0].Content);
    }

    [Fact]
    public void UpdateReply_NonExistentReply_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        var existingReply = CreateMessageResponse(content: "Existing");
        vm.AddReply(existingReply);

        var nonExistentReply = CreateMessageResponse(content: "Non-existent");

        // Act
        vm.UpdateReply(nonExistentReply);

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal("Existing", vm.Replies[0].Content);
    }

    [Fact]
    public void UpdateReply_UpdatesCorrectReply_WhenMultipleExist()
    {
        // Arrange
        var vm = CreateViewModel();
        var reply1Id = Guid.NewGuid();
        var reply2Id = Guid.NewGuid();
        var reply1 = CreateMessageResponse(id: reply1Id, content: "Reply 1");
        var reply2 = CreateMessageResponse(id: reply2Id, content: "Reply 2");
        vm.AddReply(reply1);
        vm.AddReply(reply2);

        var updatedReply2 = reply2 with { Content = "Updated Reply 2" };

        // Act
        vm.UpdateReply(updatedReply2);

        // Assert
        Assert.Equal(2, vm.Replies.Count);
        Assert.Equal("Reply 1", vm.Replies[0].Content);
        Assert.Equal("Updated Reply 2", vm.Replies[1].Content);
    }

    #endregion

    #region RemoveReply Tests

    [Fact]
    public void RemoveReply_RemovesExistingReply()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var reply = CreateMessageResponse(id: replyId, content: "To be removed");
        vm.AddReply(reply);

        // Act
        vm.RemoveReply(replyId);

        // Assert
        Assert.Empty(vm.Replies);
        Assert.Equal(0, vm.TotalReplyCount);
    }

    [Fact]
    public void RemoveReply_NonExistentId_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        var reply = CreateMessageResponse(content: "Existing");
        vm.AddReply(reply);

        // Act
        vm.RemoveReply(Guid.NewGuid());

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal(1, vm.TotalReplyCount);
    }

    [Fact]
    public void RemoveReply_RemovesCorrectReply_WhenMultipleExist()
    {
        // Arrange
        var vm = CreateViewModel();
        var reply1Id = Guid.NewGuid();
        var reply2Id = Guid.NewGuid();
        var reply1 = CreateMessageResponse(id: reply1Id, content: "Reply 1");
        var reply2 = CreateMessageResponse(id: reply2Id, content: "Reply 2");
        vm.AddReply(reply1);
        vm.AddReply(reply2);

        // Act
        vm.RemoveReply(reply1Id);

        // Assert
        Assert.Single(vm.Replies);
        Assert.Equal("Reply 2", vm.Replies[0].Content);
        Assert.Equal(1, vm.TotalReplyCount);
    }

    [Fact]
    public void RemoveReply_TotalReplyCount_DoesNotGoBelowZero()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var reply = CreateMessageResponse(id: replyId, content: "Reply");
        vm.AddReply(reply);

        // Act
        vm.RemoveReply(replyId);
        vm.RemoveReply(replyId); // Try to remove again

        // Assert
        Assert.Equal(0, vm.TotalReplyCount);
    }

    #endregion

    #region UpdateReplyReaction Tests

    [Fact]
    public void UpdateReplyReaction_AddReaction_ToReplyWithoutReactions()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var reply = CreateMessageResponse(id: replyId, content: "Reply");
        vm.AddReply(reply);

        var userId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();

        // Act
        vm.UpdateReplyReaction(replyId, "👍", 1, true, userId, "user1", "User 1", currentUserId);

        // Assert
        Assert.NotNull(vm.Replies[0].Reactions);
        Assert.Single(vm.Replies[0].Reactions!);
        Assert.Equal("👍", vm.Replies[0].Reactions![0].Emoji);
        Assert.Equal(1, vm.Replies[0].Reactions![0].Count);
    }

    [Fact]
    public void UpdateReplyReaction_AddReaction_SetsHasReacted_ForCurrentUser()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var reply = CreateMessageResponse(id: replyId, content: "Reply");
        vm.AddReply(reply);

        var currentUserId = Guid.NewGuid();

        // Act
        vm.UpdateReplyReaction(replyId, "👍", 1, true, currentUserId, "me", "Me", currentUserId);

        // Assert
        Assert.True(vm.Replies[0].Reactions![0].HasReacted);
    }

    [Fact]
    public void UpdateReplyReaction_AddReaction_DoesNotSetHasReacted_ForOtherUser()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var reply = CreateMessageResponse(id: replyId, content: "Reply");
        vm.AddReply(reply);

        var otherUserId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();

        // Act
        vm.UpdateReplyReaction(replyId, "👍", 1, true, otherUserId, "other", "Other", currentUserId);

        // Assert
        Assert.False(vm.Replies[0].Reactions![0].HasReacted);
    }

    [Fact]
    public void UpdateReplyReaction_AddReaction_ToExistingReaction_IncrementsCount()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var existingReactions = new List<ReactionSummary>
        {
            new("👍", 1, false, new List<ReactionUser> { new(Guid.NewGuid(), "user1", "User 1") })
        };
        var reply = CreateMessageResponse(id: replyId, content: "Reply", reactions: existingReactions);
        vm.AddReply(reply);

        var newUserId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();

        // Act
        vm.UpdateReplyReaction(replyId, "👍", 2, true, newUserId, "user2", "User 2", currentUserId);

        // Assert
        Assert.Single(vm.Replies[0].Reactions!);
        Assert.Equal(2, vm.Replies[0].Reactions![0].Count);
        Assert.Equal(2, vm.Replies[0].Reactions![0].Users.Count);
    }

    [Fact]
    public void UpdateReplyReaction_RemoveReaction_DecrementsCount()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var existingReactions = new List<ReactionSummary>
        {
            new("👍", 2, false, new List<ReactionUser>
            {
                new(user1Id, "user1", "User 1"),
                new(user2Id, "user2", "User 2")
            })
        };
        var reply = CreateMessageResponse(id: replyId, content: "Reply", reactions: existingReactions);
        vm.AddReply(reply);

        var currentUserId = Guid.NewGuid();

        // Act
        vm.UpdateReplyReaction(replyId, "👍", 1, false, user1Id, "user1", "User 1", currentUserId);

        // Assert
        Assert.Single(vm.Replies[0].Reactions!);
        Assert.Equal(1, vm.Replies[0].Reactions![0].Count);
        Assert.Single(vm.Replies[0].Reactions![0].Users);
    }

    [Fact]
    public void UpdateReplyReaction_RemoveReaction_RemovesEmoji_WhenCountIsZero()
    {
        // Arrange
        var vm = CreateViewModel();
        var replyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var existingReactions = new List<ReactionSummary>
        {
            new("👍", 1, false, new List<ReactionUser> { new(userId, "user1", "User 1") })
        };
        var reply = CreateMessageResponse(id: replyId, content: "Reply", reactions: existingReactions);
        vm.AddReply(reply);

        var currentUserId = Guid.NewGuid();

        // Act
        vm.UpdateReplyReaction(replyId, "👍", 0, false, userId, "user1", "User 1", currentUserId);

        // Assert
        Assert.Null(vm.Replies[0].Reactions);
    }

    [Fact]
    public void UpdateReplyReaction_NonExistentReply_DoesNothing()
    {
        // Arrange
        var vm = CreateViewModel();
        var reply = CreateMessageResponse(content: "Reply");
        vm.AddReply(reply);

        var currentUserId = Guid.NewGuid();

        // Act - should not throw
        vm.UpdateReplyReaction(Guid.NewGuid(), "👍", 1, true, Guid.NewGuid(), "user", "User", currentUserId);

        // Assert
        Assert.Null(vm.Replies[0].Reactions);
    }

    #endregion

    #region Property Change Notifications

    [Fact]
    public void ReplyInput_PropertyChanged_IsRaised()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChanged = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThreadViewModel.ReplyInput))
                propertyChanged = true;
        };

        // Act
        vm.ReplyInput = "New input";

        // Assert
        Assert.True(propertyChanged);
    }

    [Fact]
    public async Task TotalReplyCount_PropertyChanged_IsRaised()
    {
        // Arrange
        var vm = CreateViewModel();
        var propertyChanged = false;
        var replies = new List<MessageResponse> { CreateMessageResponse(content: "Reply") };

        _mockApiClient
            .Setup(x => x.GetThreadAsync(_parentMessageId, 1, 50))
            .ReturnsAsync(ApiResult<ThreadResponse>.Ok(CreateThreadResponse(_parentMessage, replies, 5)));

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThreadViewModel.TotalReplyCount))
                propertyChanged = true;
        };

        // Act
        await vm.LoadAsync();

        // Assert
        Assert.True(propertyChanged);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act & Assert - should not throw
        vm.Dispose();
        vm.Dispose();
    }

    #endregion
}
