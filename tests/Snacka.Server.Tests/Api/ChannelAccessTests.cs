using System.Net;
using System.Net.Http.Json;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

/// <summary>
/// Channel-scoped endpoints must only be usable by members of the channel's community (#14).
/// Bob is a logged-in user who is NOT a member of Alice's community.
/// </summary>
[TestClass]
public class ChannelAccessTests
{
    private sealed record Setup(IntegrationTestBase Test, string AliceToken, string BobToken, Guid CommunityId, Guid ChannelId, Guid MessageId);

    private static async Task<Setup> SetupAsync()
    {
        var test = new IntegrationTestBase();
        var alice = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var bob = await test.RegisterUserAsync("bob", "bob@test.com", "Password123!");

        test.SetAuthToken(alice.AccessToken);
        var community = await (await test.Client.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Alice's", null)))
            .Content.ReadFromJsonAsync<CommunityResponse>();
        var channels = await test.Client.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{community!.Id}/channels");
        var channel = channels!.First();
        var message = await (await test.Client.PostAsJsonAsync($"/api/channels/{channel.Id}/messages", new SendMessageRequest("secret plans")))
            .Content.ReadFromJsonAsync<MessageResponse>();

        test.SetAuthToken(bob.AccessToken);
        return new Setup(test, alice.AccessToken, bob.AccessToken, community.Id, channel.Id, message!.Id);
    }

    [TestMethod]
    public async Task NonMember_CannotReadMessages()
    {
        var s = await SetupAsync();
        using var _ = s.Test;
        var response = await s.Test.Client.GetAsync($"/api/channels/{s.ChannelId}/messages");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task NonMember_CannotSendMessage()
    {
        var s = await SetupAsync();
        using var _ = s.Test;
        var response = await s.Test.Client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/messages", new SendMessageRequest("hi"));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        s.Test.SetAuthToken(s.AliceToken);
        var messages = await s.Test.Client.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{s.ChannelId}/messages");
        Assert.AreEqual(1, messages!.Count, "Bob's message must not have been stored");
    }

    [TestMethod]
    public async Task NonMember_CannotReadPinnedMessages()
    {
        var s = await SetupAsync();
        using var _ = s.Test;
        var response = await s.Test.Client.GetAsync($"/api/channels/{s.ChannelId}/messages/pinned");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task NonMember_CannotReact()
    {
        var s = await SetupAsync();
        using var _ = s.Test;
        var response = await s.Test.Client.PostAsJsonAsync(
            $"/api/channels/{s.ChannelId}/messages/{s.MessageId}/reactions", new AddReactionRequest("👍"));
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task NonMember_CannotReadOrReplyToThread()
    {
        var s = await SetupAsync();
        using var _ = s.Test;
        var read = await s.Test.Client.GetAsync($"/api/messages/{s.MessageId}/thread");
        Assert.AreEqual(HttpStatusCode.Forbidden, read.StatusCode);

        var reply = await s.Test.Client.PostAsJsonAsync($"/api/messages/{s.MessageId}/replies", new SendMessageRequest("me too"));
        Assert.AreEqual(HttpStatusCode.Forbidden, reply.StatusCode);
    }

    [TestMethod]
    public async Task Member_CanReadMessagesAfterJoining()
    {
        var s = await SetupAsync();
        using var _ = s.Test;
        var join = await s.Test.Client.PostAsync($"/api/communities/{s.CommunityId}/join", null);
        join.EnsureSuccessStatusCode();

        var messages = await s.Test.Client.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{s.ChannelId}/messages");
        Assert.AreEqual(1, messages!.Count);
    }
}
