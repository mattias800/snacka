using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

/// <summary>LiveKit join tokens for voice channels (#52): members only, voice channels only.</summary>
[TestClass]
public class VoiceTokenTests
{
    private const string ApiKey = "testkey";
    private const string ApiSecret = "a-livekit-test-secret-with-enough-length-0123456789";
    private const string LiveKitUrl = "ws://livekit.test:7880";

    private static readonly Dictionary<string, string?> LiveKitConfig = new()
    {
        ["LiveKit:Url"] = LiveKitUrl,
        ["LiveKit:ApiKey"] = ApiKey,
        ["LiveKit:ApiSecret"] = ApiSecret,
    };

    private sealed record Setup(IntegrationTestBase Test, AuthResponse Alice, AuthResponse Bob, Guid VoiceChannelId, Guid TextChannelId);

    private static async Task<Setup> SetupAsync(Dictionary<string, string?>? config)
    {
        var test = new IntegrationTestBase(config);
        var alice = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var bob = await test.RegisterUserAsync("bob", "bob@test.com", "Password123!");
        test.SetAuthToken(alice.AccessToken);
        var community = await (await test.Client.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Alice's", null)))
            .Content.ReadFromJsonAsync<CommunityResponse>();
        var channels = await test.Client.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{community!.Id}/channels");
        var voice = channels!.First(c => c.Type == Snacka.Shared.Models.ChannelType.Voice);
        var text = channels!.First(c => c.Type == Snacka.Shared.Models.ChannelType.Text);
        return new Setup(test, alice, bob, voice.Id, text.Id);
    }

    private static Task<HttpResponseMessage> RequestTokenAsync(IntegrationTestBase test, Guid channelId) =>
        test.Client.PostAsync($"/api/voice/channels/{channelId}/token", null);

    [TestMethod]
    public async Task Member_GetsLiveKitTokenForVoiceChannel()
    {
        var s = await SetupAsync(LiveKitConfig);
        using var _ = s.Test;

        var response = await RequestTokenAsync(s.Test, s.VoiceChannelId);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VoiceTokenResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(LiveKitUrl, body.Url);
        Assert.AreEqual($"channel-{s.VoiceChannelId}", body.Room);

        // A token LiveKit will accept: HS256 with the API secret, issuer = API key, identity = user id.
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(body.Token, new TokenValidationParameters
        {
            ValidIssuer = ApiKey,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiSecret)),
        }, out var _validated);
        Assert.AreEqual(s.Alice.UserId.ToString(), principal.FindFirst("sub")?.Value);
        Assert.AreEqual("alice", principal.FindFirst("name")?.Value);
        var video = JsonDocument.Parse(principal.FindFirst("video")!.Value).RootElement;
        Assert.AreEqual(body.Room, video.GetProperty("room").GetString());
        Assert.IsTrue(video.GetProperty("roomJoin").GetBoolean());
        Assert.IsTrue(video.GetProperty("canPublish").GetBoolean());
        Assert.IsTrue(video.GetProperty("canSubscribe").GetBoolean());
    }

    [TestMethod]
    public async Task NonMember_IsForbidden()
    {
        var s = await SetupAsync(LiveKitConfig);
        using var _ = s.Test;
        s.Test.SetAuthToken(s.Bob.AccessToken);

        var response = await RequestTokenAsync(s.Test, s.VoiceChannelId);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task TextChannel_IsRejected()
    {
        var s = await SetupAsync(LiveKitConfig);
        using var _ = s.Test;

        var response = await RequestTokenAsync(s.Test, s.TextChannelId);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Unauthenticated_IsRejected()
    {
        var s = await SetupAsync(LiveKitConfig);
        using var _ = s.Test;
        s.Test.ClearAuthToken();

        var response = await RequestTokenAsync(s.Test, s.VoiceChannelId);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task WithoutLiveKitConfigured_ReturnsServiceUnavailable()
    {
        var s = await SetupAsync(config: null);
        using var _ = s.Test;

        var response = await RequestTokenAsync(s.Test, s.VoiceChannelId);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
