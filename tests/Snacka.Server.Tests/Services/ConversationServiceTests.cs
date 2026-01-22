using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Tests.Services;

[TestClass]
public class ConversationServiceTests
{
    private static async Task<(User user1, User user2, User user3)> CreateTestUsersAsync(Data.SnackaDbContext db)
    {
        var user1 = new User
        {
            Username = "user1",
            Email = "user1@example.com",
            PasswordHash = "hash"
        };
        var user2 = new User
        {
            Username = "user2",
            Email = "user2@example.com",
            PasswordHash = "hash"
        };
        var user3 = new User
        {
            Username = "user3",
            Email = "user3@example.com",
            PasswordHash = "hash"
        };

        db.Users.AddRange(user1, user2, user3);
        await db.SaveChangesAsync();
        return (user1, user2, user3);
    }

    [TestMethod]
    public async Task CreateConversation_With2Participants_CreatesDirectConversation()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);

        // Act
        var conversation = await service.CreateConversationAsync(
            user1.Id, [user2.Id], null);

        // Assert
        Assert.IsNotNull(conversation);
        Assert.IsFalse(conversation.IsGroup);
        Assert.IsNull(conversation.Name);
        Assert.AreEqual(2, conversation.Participants.Count);
    }

    [TestMethod]
    public async Task CreateConversation_With3Participants_CreatesGroupConversation()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);

        // Act - explicitly include creator in the participant list
        var participantIds = new List<Guid> { user1.Id, user2.Id, user3.Id };
        var conversation = await service.CreateConversationAsync(
            user1.Id, participantIds, "Test Group");

        // Assert
        Assert.IsNotNull(conversation);
        Assert.IsTrue(conversation.IsGroup);
        Assert.AreEqual("Test Group", conversation.Name);
        Assert.AreEqual(3, conversation.Participants.Count);
    }

    [TestMethod]
    public async Task CreateConversation_WithSameParticipants_ReturnsExistingConversation()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);

        // Act
        var conversation1 = await service.CreateConversationAsync(user1.Id, [user2.Id], null);
        var conversation2 = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Assert
        Assert.AreEqual(conversation1.Id, conversation2.Id);
    }

    [TestMethod]
    public async Task SendMessageAsync_WithValidParticipant_CreatesMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Act
        var message = await service.SendMessageAsync(conversation.Id, user1.Id, "Hello!");

        // Assert
        Assert.IsNotNull(message);
        Assert.AreEqual("Hello!", message.Content);
        Assert.AreEqual(user1.Id, message.SenderId);
        Assert.AreEqual(conversation.Id, message.ConversationId);
    }

    [TestMethod]
    public async Task SendMessageAsync_WithNonParticipant_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => service.SendMessageAsync(conversation.Id, user3.Id, "Hello!"));
    }

    [TestMethod]
    public async Task GetMessagesAsync_ReturnsMessagesInOrder()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        await service.SendMessageAsync(conversation.Id, user1.Id, "Message 1");
        await service.SendMessageAsync(conversation.Id, user2.Id, "Message 2");
        await service.SendMessageAsync(conversation.Id, user1.Id, "Message 3");

        // Act
        var messages = await service.GetMessagesAsync(conversation.Id, user1.Id, 0, 50);

        // Assert
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual("Message 1", messages[0].Content);
        Assert.AreEqual("Message 2", messages[1].Content);
        Assert.AreEqual("Message 3", messages[2].Content);
    }

    [TestMethod]
    public async Task GetMessagesAsync_WithPagination_ReturnsCorrectSubset()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        for (int i = 1; i <= 10; i++)
        {
            await service.SendMessageAsync(conversation.Id, user1.Id, $"Message {i}");
        }

        // Act
        var messages = await service.GetMessagesAsync(conversation.Id, user1.Id, 0, 3);

        // Assert
        Assert.AreEqual(3, messages.Count);
    }

    [TestMethod]
    public async Task UpdateMessageAsync_ByAuthor_UpdatesContent()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);
        var message = await service.SendMessageAsync(conversation.Id, user1.Id, "Original");

        // Act
        var updated = await service.UpdateMessageAsync(
            conversation.Id, message.Id, user1.Id, "Updated");

        // Assert
        Assert.AreEqual("Updated", updated.Content);
        Assert.IsNotNull(updated.UpdatedAt);
    }

    [TestMethod]
    public async Task UpdateMessageAsync_ByNonAuthor_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);
        var message = await service.SendMessageAsync(conversation.Id, user1.Id, "Original");

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => service.UpdateMessageAsync(conversation.Id, message.Id, user2.Id, "Updated"));
    }

    [TestMethod]
    public async Task DeleteMessageAsync_ByAuthor_DeletesMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);
        var message = await service.SendMessageAsync(conversation.Id, user1.Id, "To delete");

        // Act
        await service.DeleteMessageAsync(conversation.Id, message.Id, user1.Id);

        // Assert
        var messages = await service.GetMessagesAsync(conversation.Id, user1.Id, 0, 50);
        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public async Task DeleteMessageAsync_ByNonAuthor_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);
        var message = await service.SendMessageAsync(conversation.Id, user1.Id, "To delete");

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => service.DeleteMessageAsync(conversation.Id, message.Id, user2.Id));
    }

    [TestMethod]
    public async Task GetUserConversationsAsync_ReturnsConversations()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);

        var conv1 = await service.CreateConversationAsync(user1.Id, [user2.Id], null);
        var conv2 = await service.CreateConversationAsync(user1.Id, [user3.Id], null);

        await service.SendMessageAsync(conv1.Id, user1.Id, "Hello user2!");
        await service.SendMessageAsync(conv2.Id, user1.Id, "Hello user3!");

        // Act
        var conversations = await service.GetUserConversationsAsync(user1.Id);

        // Assert
        Assert.AreEqual(2, conversations.Count);
    }

    [TestMethod]
    public async Task MarkAsReadAsync_UpdatesReadState()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        await service.SendMessageAsync(conversation.Id, user1.Id, "Message 1");
        await service.SendMessageAsync(conversation.Id, user1.Id, "Message 2");

        // Get unread count before marking as read
        var unreadBefore = await service.GetUnreadCountAsync(conversation.Id, user2.Id);

        // Act
        await service.MarkAsReadAsync(conversation.Id, user2.Id);

        // Assert
        var unreadAfter = await service.GetUnreadCountAsync(conversation.Id, user2.Id);
        Assert.AreEqual(2, unreadBefore);
        Assert.AreEqual(0, unreadAfter);
    }

    [TestMethod]
    public async Task GetOrCreateDirectConversationAsync_WithExisting_ReturnsExisting()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);

        var existing = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Act
        var result = await service.GetOrCreateDirectConversationAsync(user1.Id, user2.Id);

        // Assert
        Assert.AreEqual(existing.Id, result.Id);
    }

    [TestMethod]
    public async Task GetOrCreateDirectConversationAsync_WithoutExisting_CreatesNew()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);

        // Act
        var result = await service.GetOrCreateDirectConversationAsync(user1.Id, user2.Id);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsGroup);
        Assert.AreEqual(2, result.Participants.Count);
    }

    [TestMethod]
    public async Task AddParticipantAsync_ToGroup_AddsParticipant()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        // Create a 4th user to add later
        var user4 = new User
        {
            Username = "user4",
            Email = "user4@example.com",
            PasswordHash = "hash"
        };
        db.Users.Add(user4);
        await db.SaveChangesAsync();

        var service = new ConversationService(db);
        // Create a group with 3 participants (more than 2 = group)
        var participantIds = new List<Guid> { user1.Id, user2.Id, user3.Id };
        var conversation = await service.CreateConversationAsync(
            user1.Id, participantIds, "Group");

        // Verify it's actually a group
        Assert.IsTrue(conversation.IsGroup);

        // Act
        var participant = await service.AddParticipantAsync(conversation.Id, user4.Id, user1.Id);

        // Assert
        Assert.IsNotNull(participant);
        Assert.AreEqual(user4.Id, participant.UserId);

        var updatedConv = await service.GetConversationAsync(conversation.Id, user1.Id);
        Assert.AreEqual(4, updatedConv!.Participants.Count);
    }

    [TestMethod]
    public async Task AddParticipantAsync_ToDirectConversation_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Act & Assert
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.AddParticipantAsync(conversation.Id, user3.Id, user1.Id));
    }

    [TestMethod]
    public async Task RemoveParticipantAsync_FromGroup_RemovesParticipant()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(
            user1.Id, [user2.Id, user3.Id], "Group");

        // Act
        await service.RemoveParticipantAsync(conversation.Id, user3.Id, user1.Id);

        // Assert
        var updatedConv = await service.GetConversationAsync(conversation.Id, user1.Id);
        Assert.AreEqual(2, updatedConv!.Participants.Count);
        Assert.IsFalse(updatedConv.Participants.Any(p => p.UserId == user3.Id));
    }

    [TestMethod]
    public async Task IsParticipantAsync_WithParticipant_ReturnsTrue()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, _) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Act
        var result = await service.IsParticipantAsync(conversation.Id, user1.Id);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task IsParticipantAsync_WithNonParticipant_ReturnsFalse()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var (user1, user2, user3) = await CreateTestUsersAsync(db);
        var service = new ConversationService(db);
        var conversation = await service.CreateConversationAsync(user1.Id, [user2.Id], null);

        // Act
        var result = await service.IsParticipantAsync(conversation.Id, user3.Id);

        // Assert
        Assert.IsFalse(result);
    }
}
