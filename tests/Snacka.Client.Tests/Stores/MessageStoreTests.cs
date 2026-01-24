using System.Collections.Immutable;
using System.Reactive.Linq;
using Snacka.Client.Stores;
using Snacka.Client.Services;

namespace Snacka.Client.Tests.Stores;

public class MessageStoreTests : IDisposable
{
    private readonly MessageStore _store;

    public MessageStoreTests()
    {
        _store = new MessageStore();
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    private static MessageResponse CreateMessage(
        Guid? id = null,
        Guid? channelId = null,
        string content = "Test message",
        Guid? authorId = null,
        bool isPinned = false,
        Guid? threadParentMessageId = null,
        int replyCount = 0)
    {
        return new MessageResponse(
            Id: id ?? Guid.NewGuid(),
            ChannelId: channelId ?? Guid.NewGuid(),
            Content: content,
            AuthorId: authorId ?? Guid.NewGuid(),
            AuthorUsername: "testuser",
            AuthorEffectiveDisplayName: "Test User",
            AuthorAvatar: null,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsEdited: false,
            IsPinned: isPinned,
            PinnedAt: null,
            PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null,
            ReplyToId: null,
            ReplyTo: null,
            Reactions: null,
            Attachments: null,
            ThreadParentMessageId: threadParentMessageId,
            ReplyCount: replyCount,
            LastReplyAt: null
        );
    }

    [Fact]
    public void SetMessages_PopulatesStore()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var messages = new[]
        {
            CreateMessage(channelId: channelId, content: "Message 1"),
            CreateMessage(channelId: channelId, content: "Message 2"),
            CreateMessage(channelId: channelId, content: "Message 3")
        };

        // Act
        _store.SetMessages(channelId, messages);

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void SetMessages_ClearsExistingMessagesForChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var initialMessages = new[] { CreateMessage(channelId: channelId, content: "Old message") };
        _store.SetMessages(channelId, initialMessages);

        var newMessages = new[] { CreateMessage(channelId: channelId, content: "New message") };

        // Act
        _store.SetMessages(channelId, newMessages);

        // Assert
        var items = _store.GetMessagesForChannel(channelId);
        Assert.Single(items);
        Assert.Equal("New message", items.First().Content);
    }

    [Fact]
    public void SetMessages_DoesNotAffectOtherChannels()
    {
        // Arrange
        var channelId1 = Guid.NewGuid();
        var channelId2 = Guid.NewGuid();

        _store.SetMessages(channelId1, new[] { CreateMessage(channelId: channelId1, content: "Channel 1 message") });
        _store.SetMessages(channelId2, new[] { CreateMessage(channelId: channelId2, content: "Channel 2 message") });

        // Act
        _store.SetMessages(channelId1, new[] { CreateMessage(channelId: channelId1, content: "New channel 1 message") });

        // Assert
        var channel2Messages = _store.GetMessagesForChannel(channelId2);
        Assert.Single(channel2Messages);
        Assert.Equal("Channel 2 message", channel2Messages.First().Content);
    }

    [Fact]
    public void AddMessage_AddsToStore()
    {
        // Arrange
        var message = CreateMessage(content: "New message");

        // Act
        _store.AddMessage(message);

        // Assert
        var result = _store.GetMessage(message.Id);
        Assert.NotNull(result);
        Assert.Equal("New message", result.Content);
    }

    [Fact]
    public void UpdateMessage_UpdatesExistingMessage()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var original = CreateMessage(id: messageId, channelId: channelId, content: "Original content");
        _store.AddMessage(original);

        var updated = CreateMessage(id: messageId, channelId: channelId, content: "Updated content");

        // Act
        _store.UpdateMessage(updated);

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Equal("Updated content", result.Content);
    }

    [Fact]
    public void DeleteMessage_RemovesFromStore()
    {
        // Arrange
        var message = CreateMessage();
        _store.AddMessage(message);

        // Act
        _store.DeleteMessage(message.Id);

        // Assert
        var result = _store.GetMessage(message.Id);
        Assert.Null(result);
    }

    [Fact]
    public void SetCurrentChannel_UpdatesCurrentChannelId()
    {
        // Arrange
        var channelId = Guid.NewGuid();

        // Act
        _store.SetCurrentChannel(channelId);

        // Assert
        var currentChannelId = _store.CurrentChannelId.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(channelId, currentChannelId);
    }

    [Fact]
    public void CurrentChannelMessages_ReturnsMessagesForCurrentChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var otherChannelId = Guid.NewGuid();

        _store.AddMessage(CreateMessage(channelId: channelId, content: "Current channel message"));
        _store.AddMessage(CreateMessage(channelId: otherChannelId, content: "Other channel message"));

        // Act
        _store.SetCurrentChannel(channelId);
        var messages = _store.CurrentChannelMessages.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Single(messages);
        Assert.Equal("Current channel message", messages.First().Content);
    }

    [Fact]
    public void CurrentChannelMessages_ExcludesThreadReplies()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var parentMessageId = Guid.NewGuid();

        _store.AddMessage(CreateMessage(id: parentMessageId, channelId: channelId, content: "Parent message"));
        _store.AddMessage(CreateMessage(channelId: channelId, content: "Thread reply", threadParentMessageId: parentMessageId));

        // Act
        _store.SetCurrentChannel(channelId);
        var messages = _store.CurrentChannelMessages.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Single(messages);
        Assert.Equal("Parent message", messages.First().Content);
    }

    [Fact]
    public void CurrentChannelMessages_SortedByCreatedAt()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new MessageResponse(
            Id: Guid.NewGuid(), ChannelId: channelId, Content: "Third",
            AuthorId: Guid.NewGuid(), AuthorUsername: "user", AuthorEffectiveDisplayName: "User",
            AuthorAvatar: null, CreatedAt: now.AddMinutes(2), UpdatedAt: now.AddMinutes(2),
            IsEdited: false, IsPinned: false, PinnedAt: null, PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null, ReplyToId: null, ReplyTo: null,
            Reactions: null, Attachments: null, ThreadParentMessageId: null, ReplyCount: 0, LastReplyAt: null
        );

        var msg2 = new MessageResponse(
            Id: Guid.NewGuid(), ChannelId: channelId, Content: "First",
            AuthorId: Guid.NewGuid(), AuthorUsername: "user", AuthorEffectiveDisplayName: "User",
            AuthorAvatar: null, CreatedAt: now, UpdatedAt: now,
            IsEdited: false, IsPinned: false, PinnedAt: null, PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null, ReplyToId: null, ReplyTo: null,
            Reactions: null, Attachments: null, ThreadParentMessageId: null, ReplyCount: 0, LastReplyAt: null
        );

        var msg3 = new MessageResponse(
            Id: Guid.NewGuid(), ChannelId: channelId, Content: "Second",
            AuthorId: Guid.NewGuid(), AuthorUsername: "user", AuthorEffectiveDisplayName: "User",
            AuthorAvatar: null, CreatedAt: now.AddMinutes(1), UpdatedAt: now.AddMinutes(1),
            IsEdited: false, IsPinned: false, PinnedAt: null, PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null, ReplyToId: null, ReplyTo: null,
            Reactions: null, Attachments: null, ThreadParentMessageId: null, ReplyCount: 0, LastReplyAt: null
        );

        _store.SetMessages(channelId, new[] { msg1, msg2, msg3 });
        _store.SetCurrentChannel(channelId);

        // Act
        var messages = _store.CurrentChannelMessages.FirstAsync().GetAwaiter().GetResult().ToList();

        // Assert
        Assert.Equal(3, messages.Count);
        Assert.Equal("First", messages[0].Content);
        Assert.Equal("Second", messages[1].Content);
        Assert.Equal("Third", messages[2].Content);
    }

    [Fact]
    public void PinnedMessages_ReturnsOnlyPinnedMessagesForCurrentChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();

        _store.AddMessage(CreateMessage(channelId: channelId, content: "Not pinned", isPinned: false));
        _store.AddMessage(CreateMessage(channelId: channelId, content: "Pinned message", isPinned: true));

        // Act
        _store.SetCurrentChannel(channelId);
        var pinnedMessages = _store.PinnedMessages.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Single(pinnedMessages);
        Assert.Equal("Pinned message", pinnedMessages.First().Content);
    }

    [Fact]
    public void UpdatePinState_UpdatesMessagePinStatus()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId, channelId: channelId, isPinned: false));

        // Act
        _store.UpdatePinState(messageId, true, DateTime.UtcNow, "pinner", "Pinner");

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.True(result.IsPinned);
        Assert.Equal("pinner", result.PinnedByUsername);
    }

    [Fact]
    public void AddReaction_AddsReactionToMessage()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId));

        // Act
        _store.AddReaction(messageId, "👍", userId, "user1", "User One");

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Single(result.Reactions);
        Assert.Equal("👍", result.Reactions[0].Emoji);
        Assert.Equal(1, result.Reactions[0].Count);
    }

    [Fact]
    public void AddReaction_IncrementsCountForExistingEmoji()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId));

        // Act
        _store.AddReaction(messageId, "👍", userId1, "user1", "User One");
        _store.AddReaction(messageId, "👍", userId2, "user2", "User Two");

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Single(result.Reactions);
        Assert.Equal(2, result.Reactions[0].Count);
        Assert.Equal(2, result.Reactions[0].Users.Count);
    }

    [Fact]
    public void AddReaction_DoesNotDuplicateSameUserReaction()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId));

        // Act
        _store.AddReaction(messageId, "👍", userId, "user1", "User One");
        _store.AddReaction(messageId, "👍", userId, "user1", "User One"); // Same user again

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Single(result.Reactions);
        Assert.Equal(1, result.Reactions[0].Count); // Should still be 1
    }

    [Fact]
    public void RemoveReaction_RemovesUserFromReaction()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId));
        _store.AddReaction(messageId, "👍", userId1, "user1", "User One");
        _store.AddReaction(messageId, "👍", userId2, "user2", "User Two");

        // Act
        _store.RemoveReaction(messageId, "👍", userId1);

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Single(result.Reactions);
        Assert.Equal(1, result.Reactions[0].Count);
    }

    [Fact]
    public void RemoveReaction_RemovesReactionWhenLastUserRemoves()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId));
        _store.AddReaction(messageId, "👍", userId, "user1", "User One");

        // Act
        _store.RemoveReaction(messageId, "👍", userId);

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Empty(result.Reactions);
    }

    [Fact]
    public void UpdateThreadMetadata_UpdatesReplyCountAndLastReplyAt()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        _store.AddMessage(CreateMessage(id: messageId));
        var lastReplyAt = DateTime.UtcNow;

        // Act
        _store.UpdateThreadMetadata(messageId, 5, lastReplyAt);

        // Assert
        var result = _store.GetMessage(messageId);
        Assert.NotNull(result);
        Assert.Equal(5, result.ReplyCount);
        Assert.Equal(lastReplyAt, result.LastReplyAt);
    }

    [Fact]
    public void ClearChannel_RemovesOnlyMessagesForThatChannel()
    {
        // Arrange
        var channelId1 = Guid.NewGuid();
        var channelId2 = Guid.NewGuid();

        _store.AddMessage(CreateMessage(channelId: channelId1, content: "Channel 1 message"));
        _store.AddMessage(CreateMessage(channelId: channelId2, content: "Channel 2 message"));

        // Act
        _store.ClearChannel(channelId1);

        // Assert
        var channel1Messages = _store.GetMessagesForChannel(channelId1);
        var channel2Messages = _store.GetMessagesForChannel(channelId2);

        Assert.Empty(channel1Messages);
        Assert.Single(channel2Messages);
    }

    [Fact]
    public void Clear_RemovesAllMessagesAndClearsCurrentChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _store.AddMessage(CreateMessage(channelId: channelId));
        _store.SetCurrentChannel(channelId);

        // Act
        _store.Clear();

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        var currentChannelId = _store.CurrentChannelId.FirstAsync().GetAwaiter().GetResult();

        Assert.Empty(items);
        Assert.Null(currentChannelId);
    }

    [Fact]
    public void GetMessage_ReturnsNullForNonExistentMessage()
    {
        // Act
        var result = _store.GetMessage(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetMessagesForChannel_ReturnsSortedMessages()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var msg1 = new MessageResponse(
            Id: Guid.NewGuid(), ChannelId: channelId, Content: "Second",
            AuthorId: Guid.NewGuid(), AuthorUsername: "user", AuthorEffectiveDisplayName: "User",
            AuthorAvatar: null, CreatedAt: now.AddMinutes(1), UpdatedAt: now.AddMinutes(1),
            IsEdited: false, IsPinned: false, PinnedAt: null, PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null, ReplyToId: null, ReplyTo: null,
            Reactions: null, Attachments: null, ThreadParentMessageId: null, ReplyCount: 0, LastReplyAt: null
        );

        var msg2 = new MessageResponse(
            Id: Guid.NewGuid(), ChannelId: channelId, Content: "First",
            AuthorId: Guid.NewGuid(), AuthorUsername: "user", AuthorEffectiveDisplayName: "User",
            AuthorAvatar: null, CreatedAt: now, UpdatedAt: now,
            IsEdited: false, IsPinned: false, PinnedAt: null, PinnedByUsername: null,
            PinnedByEffectiveDisplayName: null, ReplyToId: null, ReplyTo: null,
            Reactions: null, Attachments: null, ThreadParentMessageId: null, ReplyCount: 0, LastReplyAt: null
        );

        _store.SetMessages(channelId, new[] { msg1, msg2 });

        // Act
        var messages = _store.GetMessagesForChannel(channelId).ToList();

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.Equal("First", messages[0].Content);
        Assert.Equal("Second", messages[1].Content);
    }
}
