using System.Net;
using System.Net.Http.Json;
using Miscord.Server.DTOs;

namespace Miscord.Server.Tests.Api;

[TestClass]
public class CommunitiesApiTests
{
    [TestMethod]
    public async Task CreateCommunity_WithValidData_CreatesCommunity()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);

        // Act
        var response = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", "A test community"));

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var community = await response.Content.ReadFromJsonAsync<CommunityResponse>();
        Assert.IsNotNull(community);
        Assert.AreEqual("Test Community", community.Name);
        Assert.AreEqual(auth.UserId, community.OwnerId);
    }

    [TestMethod]
    public async Task CreateCommunity_WithoutAuth_ReturnsUnauthorized()
    {
        // Arrange
        using var test = new IntegrationTestBase();

        // Act
        var response = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetCommunities_ReturnsUserCommunities()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        await test.Client.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Community 1", null));
        await test.Client.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Community 2", null));

        // Act
        var response = await test.Client.GetAsync("/api/communities");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var communities = await response.Content.ReadFromJsonAsync<List<CommunityResponse>>();
        Assert.IsNotNull(communities);
        Assert.AreEqual(2, communities.Count);
    }

    [TestMethod]
    public async Task GetCommunity_WithValidId_ReturnsCommunity()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.GetAsync($"/api/communities/{community!.Id}");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var fetchedCommunity = await response.Content.ReadFromJsonAsync<CommunityResponse>();
        Assert.IsNotNull(fetchedCommunity);
        Assert.AreEqual("Test Community", fetchedCommunity.Name);
    }

    [TestMethod]
    public async Task UpdateCommunity_ByOwner_UpdatesCommunity()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Original", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.PutAsJsonAsync($"/api/communities/{community!.Id}",
            new UpdateCommunityRequest("Updated", "New description", null));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updatedCommunity = await response.Content.ReadFromJsonAsync<CommunityResponse>();
        Assert.IsNotNull(updatedCommunity);
        Assert.AreEqual("Updated", updatedCommunity.Name);
    }

    [TestMethod]
    public async Task DeleteCommunity_ByOwner_DeletesCommunity()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("To Delete", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.DeleteAsync($"/api/communities/{community!.Id}");

        // Assert
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify deletion
        var getResponse = await test.Client.GetAsync("/api/communities");
        var communities = await getResponse.Content.ReadFromJsonAsync<List<CommunityResponse>>();
        Assert.AreEqual(0, communities!.Count);
    }

    [TestMethod]
    public async Task GetChannels_ReturnsChannels()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.GetAsync($"/api/communities/{community!.Id}/channels");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var channels = await response.Content.ReadFromJsonAsync<List<ChannelResponse>>();
        Assert.IsNotNull(channels);
        Assert.AreEqual(1, channels.Count); // Default 'general' channel
        Assert.AreEqual("general", channels[0].Name);
    }

    [TestMethod]
    public async Task CreateChannel_WithValidData_CreatesChannel()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.PostAsJsonAsync($"/api/communities/{community!.Id}/channels",
            new CreateChannelRequest("new-channel", "Topic", Miscord.Shared.Models.ChannelType.Text));

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var channel = await response.Content.ReadFromJsonAsync<ChannelResponse>();
        Assert.IsNotNull(channel);
        Assert.AreEqual("new-channel", channel.Name);
    }

    [TestMethod]
    public async Task GetMembers_ReturnsMembers()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.GetAsync($"/api/communities/{community!.Id}/members");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var members = await response.Content.ReadFromJsonAsync<List<CommunityMemberResponse>>();
        Assert.IsNotNull(members);
        Assert.AreEqual(1, members.Count);
        Assert.AreEqual("testuser", members[0].Username);
    }

    [TestMethod]
    public async Task UpdateMyNickname_WithValidData_UpdatesNickname()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Act
        var response = await test.Client.PutAsJsonAsync(
            $"/api/communities/{community!.Id}/members/me/nickname",
            new UpdateNicknameRequest("My Nickname 🎮"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<CommunityMemberResponse>();
        Assert.IsNotNull(member);
        Assert.AreEqual("My Nickname 🎮", member.DisplayNameOverride);
        Assert.AreEqual("My Nickname 🎮", member.EffectiveDisplayName);
    }

    [TestMethod]
    public async Task UpdateMyNickname_WithNull_ClearsNickname()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // First set a nickname
        await test.Client.PutAsJsonAsync(
            $"/api/communities/{community!.Id}/members/me/nickname",
            new UpdateNicknameRequest("Temporary Nick"));

        // Act - Clear nickname by setting it to null
        var response = await test.Client.PutAsJsonAsync(
            $"/api/communities/{community.Id}/members/me/nickname",
            new UpdateNicknameRequest(null));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<CommunityMemberResponse>();
        Assert.IsNotNull(member);
        Assert.IsNull(member.DisplayNameOverride);
        // EffectiveDisplayName should fall back to user's DisplayName or Username
        Assert.AreEqual("testuser", member.EffectiveDisplayName);
    }

    [TestMethod]
    public async Task UpdateMemberNickname_ByAdmin_UpdatesNickname()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var ownerAuth = await test.RegisterUserAsync("owner", "owner@example.com", "Password123!");
        test.SetAuthToken(ownerAuth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Register second user (RegisterUserAsync handles invite internally)
        var memberAuth = await test.RegisterUserAsync("member", "member@example.com", "Password123!");

        // Add member to community
        test.SetAuthToken(memberAuth.AccessToken);
        await test.Client.PostAsync($"/api/communities/{community!.Id}/join", null);

        // Switch to owner to update member's nickname
        test.SetAuthToken(ownerAuth.AccessToken);

        // Act
        var response = await test.Client.PutAsJsonAsync(
            $"/api/communities/{community.Id}/members/{memberAuth.UserId}/nickname",
            new UpdateNicknameRequest("Admin-Set Nickname"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<CommunityMemberResponse>();
        Assert.IsNotNull(member);
        Assert.AreEqual("Admin-Set Nickname", member.DisplayNameOverride);
    }

    [TestMethod]
    public async Task UpdateMemberNickname_ByNonAdmin_ReturnsForbidden()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var ownerAuth = await test.RegisterUserAsync("owner", "owner@example.com", "Password123!");
        test.SetAuthToken(ownerAuth.AccessToken);
        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Register second user
        var memberAuth = await test.RegisterUserAsync("member", "member@example.com", "Password123!");

        // Add member to community
        test.SetAuthToken(memberAuth.AccessToken);
        await test.Client.PostAsync($"/api/communities/{community!.Id}/join", null);

        // Try to update owner's nickname as regular member
        var response = await test.Client.PutAsJsonAsync(
            $"/api/communities/{community.Id}/members/{ownerAuth.UserId}/nickname",
            new UpdateNicknameRequest("Hacker Nickname"));

        // Assert - should be forbidden
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task GetMembers_ReturnsEffectiveDisplayName()
    {
        // Arrange
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("testuser", "test@example.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);

        // Set display name
        await test.Client.PutAsJsonAsync("/api/users/me",
            new UpdateProfileRequest(null, "Global Display Name", null));

        var createResponse = await test.Client.PostAsJsonAsync("/api/communities",
            new CreateCommunityRequest("Test Community", null));
        var community = await createResponse.Content.ReadFromJsonAsync<CommunityResponse>();

        // Set nickname that should override display name
        await test.Client.PutAsJsonAsync(
            $"/api/communities/{community!.Id}/members/me/nickname",
            new UpdateNicknameRequest("Community Nickname"));

        // Act
        var response = await test.Client.GetAsync($"/api/communities/{community.Id}/members");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var members = await response.Content.ReadFromJsonAsync<List<CommunityMemberResponse>>();
        Assert.IsNotNull(members);
        Assert.AreEqual(1, members.Count);
        Assert.AreEqual("Global Display Name", members[0].DisplayName);
        Assert.AreEqual("Community Nickname", members[0].DisplayNameOverride);
        Assert.AreEqual("Community Nickname", members[0].EffectiveDisplayName);
    }
}
