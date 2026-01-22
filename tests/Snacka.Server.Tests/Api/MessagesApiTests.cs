using System.Net;
using System.Net.Http.Json;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

[TestClass]
public class MessagesApiTests
{
    private static async Task<(IntegrationTestBase test, AuthResponse auth, CommunityResponse community, ChannelResponse channel)> SetupAsync()
    {
        var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        var channelsResponse = await test.Client.GetAsync($"/api/communities/{community!.Id}/channels");
        var channels = await channelsResponse.Content.ReadFromJsonAsync<List<ChannelResponse>>();
        var channel = channels!.First();

        return (test, auth, community!, channel);
    }

    [TestMethod]
    public async Task SendMessage_WithValidData_CreatesMessage()
    {
        // Arrange
        var (test, auth, community, channel) = await SetupAsync();

        try
        {
            // Act
            var response = await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages",
                new SendMessageRequest("Hello world!"));

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var message = await response.Content.ReadFromJsonAsync<MessageResponse>();
            Assert.IsNotNull(message);
            Assert.AreEqual("Hello world!", message.Content);
            Assert.AreEqual(auth.UserId, message.AuthorId);
        }
        finally
        {
            test.Dispose();
        }
    }

    [TestMethod]
    public async Task GetMessages_ReturnsMessages()
    {
        // Arrange
        var (test, auth, community, channel) = await SetupAsync();

        try
        {
            await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new SendMessageRequest("Message 1"));
            await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new SendMessageRequest("Message 2"));
            await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new SendMessageRequest("Message 3"));

            // Act
            var response = await test.Client.GetAsync($"/api/channels/{channel.Id}/messages");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var messages = await response.Content.ReadFromJsonAsync<List<MessageResponse>>();
            Assert.IsNotNull(messages);
            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual("Message 1", messages[0].Content);
            Assert.AreEqual("Message 3", messages[2].Content);
        }
        finally
        {
            test.Dispose();
        }
    }

    [TestMethod]
    public async Task UpdateMessage_ByAuthor_UpdatesMessage()
    {
        // Arrange
        var (test, auth, community, channel) = await SetupAsync();

        try
        {
            var createResponse = await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages",
                new SendMessageRequest("Original"));
            var message = await createResponse.Content.ReadFromJsonAsync<MessageResponse>();

            // Act
            var response = await test.Client.PutAsJsonAsync($"/api/channels/{channel.Id}/messages/{message!.Id}",
                new UpdateMessageRequest("Updated"));

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var updated = await response.Content.ReadFromJsonAsync<MessageResponse>();
            Assert.IsNotNull(updated);
            Assert.AreEqual("Updated", updated.Content);
            Assert.IsTrue(updated.IsEdited);
        }
        finally
        {
            test.Dispose();
        }
    }

    [TestMethod]
    public async Task DeleteMessage_ByAuthor_DeletesMessage()
    {
        // Arrange
        var (test, auth, community, channel) = await SetupAsync();

        try
        {
            var createResponse = await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages",
                new SendMessageRequest("To delete"));
            var message = await createResponse.Content.ReadFromJsonAsync<MessageResponse>();

            // Act
            var response = await test.Client.DeleteAsync($"/api/channels/{channel.Id}/messages/{message!.Id}");

            // Assert
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

            // Verify deletion
            var getResponse = await test.Client.GetAsync($"/api/channels/{channel.Id}/messages");
            var messages = await getResponse.Content.ReadFromJsonAsync<List<MessageResponse>>();
            Assert.AreEqual(0, messages!.Count);
        }
        finally
        {
            test.Dispose();
        }
    }

    [TestMethod]
    public async Task SendMessage_WithoutAuth_ReturnsUnauthorized()
    {
        // Arrange
        using var test = new IntegrationTestBase();

        // Act
        var response = await test.Client.PostAsJsonAsync($"/api/channels/{Guid.NewGuid()}/messages",
            new SendMessageRequest("Hello!"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

[TestClass]
public class ConversationsApiTests
{
    [TestMethod]
    public async Task CreateConversation_WithValidData_CreatesConversation()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        // Act
        var response = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.IsNotNull(conversation);
        Assert.IsFalse(conversation.IsGroup);
        Assert.AreEqual(2, conversation.Participants.Count);
    }

    [TestMethod]
    public async Task SendMessage_ToConversation_CreatesMessage()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var conversation = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        // Act
        var response = await test.Client.PostAsJsonAsync($"/api/conversations/{conversation!.Id}/messages",
            new SendConversationMessageRequest("Hello!"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<ConversationMessageResponse>();
        Assert.IsNotNull(message);
        Assert.AreEqual("Hello!", message.Content);
        Assert.AreEqual(user1.UserId, message.SenderId);
        Assert.AreEqual(conversation.Id, message.ConversationId);
    }

    [TestMethod]
    public async Task GetMessages_ReturnsMessages()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var conversation = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        await test.Client.PostAsJsonAsync($"/api/conversations/{conversation!.Id}/messages",
            new SendConversationMessageRequest("Message 1"));
        await test.Client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages",
            new SendConversationMessageRequest("Message 2"));

        // Act
        var response = await test.Client.GetAsync($"/api/conversations/{conversation.Id}/messages");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var messages = await response.Content.ReadFromJsonAsync<List<ConversationMessageResponse>>();
        Assert.IsNotNull(messages);
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public async Task GetConversations_ReturnsConversationList()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var conversation = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        await test.Client.PostAsJsonAsync($"/api/conversations/{conversation!.Id}/messages",
            new SendConversationMessageRequest("Hello!"));

        // Act
        var response = await test.Client.GetAsync("/api/conversations");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var conversations = await response.Content.ReadFromJsonAsync<List<ConversationResponse>>();
        Assert.IsNotNull(conversations);
        Assert.AreEqual(1, conversations.Count);
        Assert.AreEqual(conversation.Id, conversations[0].Id);
    }

    [TestMethod]
    public async Task MarkAsRead_MarksConversationAsRead()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var conversation = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        await test.Client.PostAsJsonAsync($"/api/conversations/{conversation!.Id}/messages",
            new SendConversationMessageRequest("Hello!"));

        // Switch to user2
        test.SetAuthToken(user2.AccessToken);

        // Get unread count before
        var beforeResponse = await test.Client.GetAsync($"/api/conversations/{conversation.Id}");
        var beforeConv = await beforeResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        // Act
        var response = await test.Client.PostAsync($"/api/conversations/{conversation.Id}/read", null);

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify unread count is now 0
        var afterResponse = await test.Client.GetAsync($"/api/conversations/{conversation.Id}");
        var afterConv = await afterResponse.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.AreEqual(1, beforeConv!.UnreadCount);
        Assert.AreEqual(0, afterConv!.UnreadCount);
    }

    [TestMethod]
    public async Task GetOrCreateDirectConversation_WithExisting_ReturnsExisting()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        // Create conversation first
        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var created = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        // Act - Get or create using direct endpoint
        var response = await test.Client.GetAsync($"/api/conversations/direct/{user2.UserId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>();
        Assert.IsNotNull(conversation);
        Assert.AreEqual(created!.Id, conversation.Id);
    }

    [TestMethod]
    public async Task UpdateMessage_ByAuthor_UpdatesMessage()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var conversation = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        var msgResponse = await test.Client.PostAsJsonAsync($"/api/conversations/{conversation!.Id}/messages",
            new SendConversationMessageRequest("Original"));
        var message = await msgResponse.Content.ReadFromJsonAsync<ConversationMessageResponse>();

        // Act
        var response = await test.Client.PutAsJsonAsync(
            $"/api/conversations/{conversation.Id}/messages/{message!.Id}",
            new SendConversationMessageRequest("Updated"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ConversationMessageResponse>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Updated", updated.Content);
        Assert.IsNotNull(updated.UpdatedAt);
    }

    [TestMethod]
    public async Task DeleteMessage_ByAuthor_DeletesMessage()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var user1 = await test.RegisterUserAsync("user1", "user1@example.com", "Password123!");
        var user2 = await test.RegisterUserAsync("user2", "user2@example.com", "Password123!");
        test.SetAuthToken(user1.AccessToken);

        var createResponse = await test.Client.PostAsJsonAsync("/api/conversations",
            new CreateConversationRequest([user2.UserId], null));
        var conversation = await createResponse.Content.ReadFromJsonAsync<ConversationResponse>();

        var msgResponse = await test.Client.PostAsJsonAsync($"/api/conversations/{conversation!.Id}/messages",
            new SendConversationMessageRequest("To delete"));
        var message = await msgResponse.Content.ReadFromJsonAsync<ConversationMessageResponse>();

        // Act
        var response = await test.Client.DeleteAsync(
            $"/api/conversations/{conversation.Id}/messages/{message!.Id}");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify deletion
        var getResponse = await test.Client.GetAsync($"/api/conversations/{conversation.Id}/messages");
        var messages = await getResponse.Content.ReadFromJsonAsync<List<ConversationMessageResponse>>();
        Assert.AreEqual(0, messages!.Count);
    }
}
