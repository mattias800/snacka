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
public class DirectMessagesApiTests
{
    [TestMethod]
    public async Task SendDirectMessage_WithValidData_CreatesMessage()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var sender = await test.RegisterUserAsync("sender", "sender@example.com", "Password123!");
        var recipient = await test.RegisterUserAsync("recipient", "recipient@example.com", "Password123!");
        test.SetAuthToken(sender.AccessToken);

        // Act
        var response = await test.Client.PostAsJsonAsync($"/api/direct-messages/conversations/{recipient.UserId}",
            new SendDirectMessageRequest("Hello!"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<DirectMessageResponse>();
        Assert.IsNotNull(message);
        Assert.AreEqual("Hello!", message.Content);
        Assert.AreEqual(sender.UserId, message.SenderId);
        Assert.AreEqual(recipient.UserId, message.RecipientId);
    }

    [TestMethod]
    public async Task GetConversation_ReturnsMessages()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var sender = await test.RegisterUserAsync("sender", "sender@example.com", "Password123!");
        var recipient = await test.RegisterUserAsync("recipient", "recipient@example.com", "Password123!");
        test.SetAuthToken(sender.AccessToken);

        await test.Client.PostAsJsonAsync($"/api/direct-messages/conversations/{recipient.UserId}",
            new SendDirectMessageRequest("Message 1"));
        await test.Client.PostAsJsonAsync($"/api/direct-messages/conversations/{recipient.UserId}",
            new SendDirectMessageRequest("Message 2"));

        // Act
        var response = await test.Client.GetAsync($"/api/direct-messages/conversations/{recipient.UserId}");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var messages = await response.Content.ReadFromJsonAsync<List<DirectMessageResponse>>();
        Assert.IsNotNull(messages);
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public async Task GetConversations_ReturnsConversationList()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var sender = await test.RegisterUserAsync("sender", "sender@example.com", "Password123!");
        var recipient = await test.RegisterUserAsync("recipient", "recipient@example.com", "Password123!");
        test.SetAuthToken(sender.AccessToken);

        await test.Client.PostAsJsonAsync($"/api/direct-messages/conversations/{recipient.UserId}",
            new SendDirectMessageRequest("Hello!"));

        // Act
        var response = await test.Client.GetAsync("/api/direct-messages");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var conversations = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>();
        Assert.IsNotNull(conversations);
        Assert.AreEqual(1, conversations.Count);
        Assert.AreEqual(recipient.UserId, conversations[0].UserId);
    }

    [TestMethod]
    public async Task MarkAsRead_MarksMessagesAsRead()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var sender = await test.RegisterUserAsync("sender", "sender@example.com", "Password123!");
        var recipient = await test.RegisterUserAsync("recipient", "recipient@example.com", "Password123!");
        test.SetAuthToken(sender.AccessToken);

        await test.Client.PostAsJsonAsync($"/api/direct-messages/conversations/{recipient.UserId}",
            new SendDirectMessageRequest("Hello!"));

        // Switch to recipient
        test.SetAuthToken(recipient.AccessToken);

        // Act
        var response = await test.Client.PostAsync($"/api/direct-messages/conversations/{sender.UserId}/read", null);

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify messages are marked as read
        var getResponse = await test.Client.GetAsync($"/api/direct-messages/conversations/{sender.UserId}");
        var messages = await getResponse.Content.ReadFromJsonAsync<List<DirectMessageResponse>>();
        Assert.IsTrue(messages!.All(m => m.IsRead));
    }
}
