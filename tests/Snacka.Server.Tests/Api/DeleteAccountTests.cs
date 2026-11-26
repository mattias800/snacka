using System.Net;
using System.Net.Http.Json;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

/// <summary>Deleting an account can't take other people's communities with it, and needs the password.</summary>
[TestClass]
public class DeleteAccountTests
{
    private static Task<HttpResponseMessage> DeleteAsync(IntegrationTestBase test, string password) =>
        test.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/users/me") { Content = JsonContent.Create(new DeleteAccountRequest(password)) });

    [TestMethod]
    public async Task WrongPassword_IsRefused()
    {
        using var test = new IntegrationTestBase();
        test.SetAuthToken((await test.RegisterUserAsync("alice", "alice@test.com", "Password123!")).AccessToken);

        var response = await DeleteAsync(test, "Wrong123!");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await test.Client.GetAsync("/api/users/me")).StatusCode);
    }

    [TestMethod]
    public async Task CommunityOwner_IsRefused_UntilOwnershipIsTransferred()
    {
        using var test = new IntegrationTestBase();
        await test.RegisterUserAsync("admin", "admin@test.com", "Password123!"); // the server admin
        var bob = await test.RegisterUserAsync("bob", "bob@test.com", "Password123!");
        var carol = await test.RegisterUserAsync("carol", "carol@test.com", "Password123!");
        test.SetAuthToken(bob.AccessToken);
        var community = await (await test.Client.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Bob's", null))).Content.ReadFromJsonAsync<CommunityResponse>();
        test.SetAuthToken(carol.AccessToken);
        (await test.Client.PostAsync($"/api/communities/{community!.Id}/join", null)).EnsureSuccessStatusCode();
        test.SetAuthToken(bob.AccessToken);

        var refused = await DeleteAsync(test, "Password123!");
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "Bob's");

        (await test.Client.PostAsJsonAsync($"/api/communities/{community.Id}/transfer-ownership", new TransferOwnershipRequest(carol.UserId))).EnsureSuccessStatusCode();
        Assert.AreEqual(HttpStatusCode.NoContent, (await DeleteAsync(test, "Password123!")).StatusCode);

        // The community lives on with Carol.
        test.SetAuthToken(carol.AccessToken);
        Assert.AreEqual(HttpStatusCode.OK, (await test.Client.GetAsync($"/api/communities/{community.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task OnlyAdmin_IsRefused_WhileOthersRemain()
    {
        using var test = new IntegrationTestBase();
        var admin = await test.RegisterUserAsync("admin", "admin@test.com", "Password123!");
        await test.RegisterUserAsync("bob", "bob@test.com", "Password123!");
        test.SetAuthToken(admin.AccessToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await DeleteAsync(test, "Password123!")).StatusCode);
    }
}
