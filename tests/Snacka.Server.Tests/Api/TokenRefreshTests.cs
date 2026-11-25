using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

/// <summary>Refresh tokens must be unexpired, of the refresh type, and die with a password change (#15).</summary>
[TestClass]
public class TokenRefreshTests
{
    private static async Task<HttpResponseMessage> RefreshAsync(IntegrationTestBase test, string token) =>
        await test.Client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(token));

    [TestMethod]
    public async Task Refresh_WithValidRefreshToken_ReturnsNewTokens()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");

        var response = await RefreshAsync(test, auth.RefreshToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var refreshed = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsFalse(string.IsNullOrEmpty(refreshed?.AccessToken));
    }

    [TestMethod]
    public async Task Refresh_WithAccessToken_IsRejected()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");

        var response = await RefreshAsync(test, auth.AccessToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Refresh_WithExpiredRefreshToken_IsRejected()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var expired = CreateToken(auth.UserId, tokenType: "refresh", expires: DateTime.UtcNow.AddMinutes(-5));

        var response = await RefreshAsync(test, expired);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task RefreshToken_CannotBeUsedAsBearerToken()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        test.SetAuthToken(auth.RefreshToken);

        var response = await test.Client.GetAsync("/api/communities");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Refresh_AfterPasswordChange_IsRejected()
    {
        using var test = new IntegrationTestBase();
        var auth = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        test.SetAuthToken(auth.AccessToken);
        var change = await test.Client.PutAsJsonAsync("/api/users/me/password", new ChangePasswordRequest("Password123!", "NewPassword456!"));
        change.EnsureSuccessStatusCode();

        var response = await RefreshAsync(test, auth.RefreshToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string CreateToken(Guid userId, string tokenType, DateTime expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(IntegrationTestBase.TestSecretKey));
        var token = new JwtSecurityToken(
            issuer: IntegrationTestBase.TestIssuer,
            audience: IntegrationTestBase.TestAudience,
            claims: [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("token_type", tokenType)],
            notBefore: expires.AddDays(-1),
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
