using System.Net;
using System.Net.Http.Json;
using Snacka.Server.DTOs;
using Snacka.Shared.Models;

namespace Snacka.Server.Tests.Api;

[TestClass]
public class ChannelApiTests
{
    [TestMethod]
    public async Task CreateChannel_WithVoiceType_CreatesVoiceChannel()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createCommunityResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createCommunityResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("voice-channel", null, ChannelType.Voice));

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var channel = await response.Content.ReadFromJsonAsync<ChannelResponse>();
        Assert.IsNotNull(channel);
        Assert.AreEqual("voice-channel", channel.Name);
        Assert.AreEqual(ChannelType.Voice, channel.Type);
    }

    [TestMethod]
    public async Task CreateChannel_DefaultType_CreatesTextChannel()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createCommunityResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createCommunityResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("text-channel", null, ChannelType.Text));

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var channel = await response.Content.ReadFromJsonAsync<ChannelResponse>();
        Assert.IsNotNull(channel);
        Assert.AreEqual(ChannelType.Text, channel.Type);
    }

    [TestMethod]
    public async Task CreateMultipleChannels_Sequentially_AllChannelsCreatedWithUniqueIds()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createCommunityResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createCommunityResponse.Content.ReadFromJsonAsync<CommunityResponse>();
        var createdChannels = new List<ChannelResponse>();

        // Act - Create 5 channels sequentially
        for (int i = 1; i <= 5; i++)
        {
            var response = await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
                new CreateChannelRequest($"channel-{i}", null, ChannelType.Text));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            var channel = await response.Content.ReadFromJsonAsync<ChannelResponse>();
            Assert.IsNotNull(channel);
            createdChannels.Add(channel);
        }

        // Assert - All channels have unique IDs
        var uniqueIds = createdChannels.Select(c => c.Id).Distinct().Count();
        Assert.AreEqual(5, uniqueIds, "All created channels should have unique IDs");

        // Verify all channels are retrievable
        var getResponse = await test.Client.GetAsync($"/api/communities/{community!.Id}/channels");
        var allChannels = await getResponse.Content.ReadFromJsonAsync<List<ChannelResponse>>();
        Assert.IsNotNull(allChannels);
        // 5 new channels + 2 default channels (text 'general' + voice 'general')
        Assert.AreEqual(7, allChannels.Count, "All created channels should be retrievable");
    }

    [TestMethod]
    public async Task CreateMultipleChannels_Concurrently_AllChannelsCreatedSuccessfully()
    {
        // This test verifies that concurrent channel creation doesn't cause issues
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createCommunityResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createCommunityResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act - Create 5 channels concurrently
        var tasks = Enumerable.Range(1, 5).Select(async i =>
        {
            var response = await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
                new CreateChannelRequest($"concurrent-channel-{i}", null, ChannelType.Text));
            return response;
        }).ToArray();

        var responses = await Task.WhenAll(tasks);

        // Assert - All requests succeeded
        foreach (var response in responses)
        {
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }

        // Verify all channels are retrievable
        var getResponse = await test.Client.GetAsync($"/api/communities/{community!.Id}/channels");
        var allChannels = await getResponse.Content.ReadFromJsonAsync<List<ChannelResponse>>();
        Assert.IsNotNull(allChannels);
        // 5 new channels + 2 default channels (text 'general' + voice 'general')
        Assert.AreEqual(7, allChannels.Count, "All concurrently created channels should be retrievable");
    }

    [TestMethod]
    public async Task CreateVoiceAndTextChannels_BothTypesRetrievable()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createCommunityResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createCommunityResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act - Create text and voice channels
        await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("text-1", null, ChannelType.Text));
        await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("voice-1", null, ChannelType.Voice));
        await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("text-2", null, ChannelType.Text));
        await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("voice-2", null, ChannelType.Voice));

        // Assert
        var getResponse = await test.Client.GetAsync($"/api/communities/{community!.Id}/channels");
        var allChannels = await getResponse.Content.ReadFromJsonAsync<List<ChannelResponse>>();
        Assert.IsNotNull(allChannels);

        var textChannels = allChannels.Where(c => c.Type == ChannelType.Text).ToList();
        var voiceChannels = allChannels.Where(c => c.Type == ChannelType.Voice).ToList();

        // 2 new text + 1 default 'general' = 3 text channels
        Assert.AreEqual(3, textChannels.Count, "Should have 3 text channels (2 new + general)");
        // 2 new voice + 1 default 'general' = 3 voice channels
        Assert.AreEqual(3, voiceChannels.Count, "Should have 3 voice channels (2 new + general)");
    }

    [TestMethod]
    public async Task GetChannels_AfterCreation_ReturnsChannelWithCorrectType()
    {
        // This tests that channel type is correctly persisted and retrieved
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createCommunityResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createCommunityResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Create a voice channel
        var createResponse = await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("my-voice-channel", null, ChannelType.Voice));
        var createdChannel = await createResponse.Content.ReadFromJsonAsync<ChannelResponse>();

        // Act - Retrieve channels via GET
        var getResponse = await test.Client.GetAsync($"/api/communities/{community!.Id}/channels");
        var allChannels = await getResponse.Content.ReadFromJsonAsync<List<ChannelResponse>>();

        // Assert
        var retrievedVoiceChannel = allChannels!.FirstOrDefault(c => c.Id == createdChannel!.Id);
        Assert.IsNotNull(retrievedVoiceChannel, "Created voice channel should be retrievable");
        Assert.AreEqual(ChannelType.Voice, retrievedVoiceChannel.Type, "Voice channel type should be preserved after retrieval");
        Assert.AreEqual("my-voice-channel", retrievedVoiceChannel.Name);
    }
}
