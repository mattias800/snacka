using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Snacka.Server.Data;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

/// <summary>
/// Refresh tokens are stored, rotate on every use, and die with reuse, sign-out and password changes (#15).
/// </summary>
[TestClass]
public class TokenRefreshTests
{
    private static async Task<HttpResponseMessage> RefreshAsync(IntegrationTestBase test, string token) =>
        await test.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token));

    private static async Task<AuthResponse> RefreshOkAsync(IntegrationTestBase test, string token)
    {
        var response = await RefreshAsync(test, token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>Moves the token's use further into the past than the reuse grace period.</summary>
    private static void AgeUse(IntegrationTestBase test)
    {
        using var scope = test.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SnackaDbContext>();
        foreach (var t in db.RefreshTokens.Where(t => t.UsedAt != null))
            t.UsedAt = t.UsedAt!.Value.AddMinutes(-5);
        db.SaveChanges();
    }

    [TestMethod]
    public async Task Refresh_RotatesTheToken_AndTheNewAccessTokenWorks()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");

        var refreshed = await RefreshOkAsync(test, auth.RefreshToken);

        Assert.AreNotEqual(auth.RefreshToken, refreshed.RefreshToken);
        test.SetAuthToken(refreshed.AccessToken);
        Assert.AreEqual(HttpStatusCode.OK, (await test.Client.GetAsync("/api/communities")).StatusCode);
        // And the chain continues.
        await RefreshOkAsync(test, refreshed.RefreshToken);
    }

    [TestMethod]
    public async Task ReusingARotatedToken_SignsOutThatSignIn_ButNotOtherDevices()
    {
        using var test = new IntegrationTestBase();
        var laptop = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var phone = await test.LoginUserAsync("alice@test.com", "Password123!");
        var rotated = await RefreshOkAsync(test, laptop.RefreshToken);
        AgeUse(test);

        // Someone replays the old laptop token: refused, and the laptop's current token dies with it.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, laptop.RefreshToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, rotated.RefreshToken)).StatusCode);
        await RefreshOkAsync(test, phone.RefreshToken);
    }

    [TestMethod]
    public async Task ConcurrentRefreshWithTheSameToken_BothSucceed()
    {
        // Two tabs (or the hub and a request) waking up at once must not sign the user out.
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");

        var first = await RefreshOkAsync(test, auth.RefreshToken);
        var second = await RefreshOkAsync(test, auth.RefreshToken);

        await RefreshOkAsync(test, first.RefreshToken);
        await RefreshOkAsync(test, second.RefreshToken);
    }

    [TestMethod]
    public async Task Refresh_WithAccessToken_IsRejected()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, auth.AccessToken)).StatusCode);
    }

    [TestMethod]
    public async Task Refresh_WithExpiredToken_IsRejected()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        using (var scope = test.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SnackaDbContext>();
            foreach (var t in db.RefreshTokens) t.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            db.SaveChanges();
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, auth.RefreshToken)).StatusCode);
    }

    [TestMethod]
    public async Task RefreshToken_CannotBeUsedAsBearerToken()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        test.SetAuthToken(auth.RefreshToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await test.Client.GetAsync("/api/communities")).StatusCode);
    }

    [TestMethod]
    public async Task Logout_EndsThatSignInOnly()
    {
        using var test = new IntegrationTestBase();
        var laptop = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var phone = await test.LoginUserAsync("alice@test.com", "Password123!");

        var logout = await test.Client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest(laptop.RefreshToken));

        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, laptop.RefreshToken)).StatusCode);
        await RefreshOkAsync(test, phone.RefreshToken);
    }

    [TestMethod]
    public async Task PasswordChange_SignsOutEveryDevice_AndTheNewSignInWorks()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var phone = await test.LoginUserAsync("alice@test.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var change = await test.Client.PutAsJsonAsync("/api/users/me/password", new ChangePasswordRequest("Password123!", "NewPassword456!"));
        change.EnsureSuccessStatusCode();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, auth.RefreshToken)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RefreshAsync(test, phone.RefreshToken)).StatusCode);
        var again = await test.LoginUserAsync("alice@test.com", "NewPassword456!");
        await RefreshOkAsync(test, again.RefreshToken);
    }
}
