using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Api;

/// <summary>
/// Voice presence from LiveKit webhooks (#52): LiveKit tells the server who joined or left a
/// room, and every community member sees it, also those who aren't in voice themselves.
/// </summary>
[TestClass]
public class VoicePresenceTests
{
    private const string ApiKey = "testkey";
    private const string ApiSecret = "a-livekit-test-secret-with-enough-length-0123456789";

    private static readonly Dictionary<string, string?> LiveKitConfig = new()
    {
        ["LiveKit:Url"] = "ws://livekit.test:7880",
        ["LiveKit:ApiKey"] = ApiKey,
        ["LiveKit:ApiSecret"] = ApiSecret,
    };

    private sealed record Setup(IntegrationTestBase Test, AuthResponse Alice, AuthResponse Bob, Guid CommunityId, Guid VoiceChannelId) : IDisposable
    {
        public void Dispose() => Test.Dispose();
    }

    /// <summary>Alice owns a community that Bob has joined.</summary>
    private static async Task<Setup> SetupAsync()
    {
        var test = new IntegrationTestBase(LiveKitConfig);
        var alice = await test.RegisterUserAsync("alice", "alice@test.com", "Password123!");
        var bob = await test.RegisterUserAsync("bob", "bob@test.com", "Password123!");
        test.SetAuthToken(alice.AccessToken);
        var community = await (await test.Client.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Alice's", null)))
            .Content.ReadFromJsonAsync<CommunityResponse>();
        var channels = await test.Client.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{community!.Id}/channels");
        var voice = channels!.First(c => c.Type == Snacka.Shared.Models.ChannelType.Voice);
        test.SetAuthToken(bob.AccessToken);
        (await test.Client.PostAsync($"/api/communities/{community.Id}/join", null)).EnsureSuccessStatusCode();
        return new Setup(test, alice, bob, community.Id, voice.Id);
    }

    private static string Event(string name, Guid channelId, Guid? userId = null, long? joinedAt = null) =>
        JsonSerializer.Serialize(new
        {
            @event = name,
            room = new { sid = "RM_test", name = $"channel-{channelId}" },
            participant = userId is null ? null : new { sid = "PA_test", identity = userId.ToString(), joinedAt = (joinedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString() },
            id = Guid.NewGuid().ToString(),
            createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        });

    /// <summary>What LiveKit sends: Authorization is a JWT (iss = API key) with the body's SHA-256.</summary>
    private static string Sign(string body, string secret = ApiSecret, string issuer = ApiKey)
    {
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer,
            nbf = now,
            exp = now + 300,
            sha256 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
        }));
        var sig = B64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes($"{header}.{claims}")));
        return $"{header}.{claims}.{sig}";
    }

    private static async Task<HttpResponseMessage> PostWebhookAsync(IntegrationTestBase test, string body, string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/livekit/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/webhook+json"),
        };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await test.Client.SendAsync(request);
    }

    private static async Task<List<VoiceParticipantResponse>> ParticipantsAsync(Setup s) =>
        (await s.Test.Client.GetFromJsonAsync<List<VoiceParticipantResponse>>($"/api/voice/communities/{s.CommunityId}/participants"))!;

    [TestMethod]
    public async Task Joined_IsVisibleToOtherMembers_AndLeftRemovesIt()
    {
        using var s = await SetupAsync();

        var joined = Event("participant_joined", s.VoiceChannelId, s.Alice.UserId);
        Assert.AreEqual(HttpStatusCode.OK, (await PostWebhookAsync(s.Test, joined, Sign(joined))).StatusCode);

        s.Test.SetAuthToken(s.Bob.AccessToken);
        var participants = await ParticipantsAsync(s);
        Assert.AreEqual(1, participants.Count);
        Assert.AreEqual(s.Alice.UserId, participants[0].UserId);
        Assert.AreEqual(s.VoiceChannelId, participants[0].ChannelId);

        var left = Event("participant_left", s.VoiceChannelId, s.Alice.UserId);
        Assert.AreEqual(HttpStatusCode.OK, (await PostWebhookAsync(s.Test, left, Sign(left))).StatusCode);
        Assert.AreEqual(0, (await ParticipantsAsync(s)).Count);
    }

    [TestMethod]
    public async Task OtherMember_GetsRealtimeJoinAndLeaveEvents()
    {
        using var s = await SetupAsync();
        await using var bobHub = await s.Test.ConnectHubAsync(s.Bob.AccessToken);
        var joinedTcs = new TaskCompletionSource<VoiceParticipantJoinedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftTcs = new TaskCompletionSource<VoiceParticipantLeftEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        bobHub.On<VoiceParticipantJoinedEvent>("VoiceParticipantJoined", e => joinedTcs.TrySetResult(e));
        bobHub.On<VoiceParticipantLeftEvent>("VoiceParticipantLeft", e => leftTcs.TrySetResult(e));

        var joined = Event("participant_joined", s.VoiceChannelId, s.Alice.UserId);
        await PostWebhookAsync(s.Test, joined, Sign(joined));
        var joinedEvent = await joinedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(s.VoiceChannelId, joinedEvent.ChannelId);
        Assert.AreEqual(s.Alice.UserId, joinedEvent.Participant.UserId);
        Assert.AreEqual("alice", joinedEvent.Participant.Username);

        var left = Event("participant_left", s.VoiceChannelId, s.Alice.UserId);
        await PostWebhookAsync(s.Test, left, Sign(left));
        var leftEvent = await leftTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(s.Alice.UserId, leftEvent.UserId);
    }

    [TestMethod]
    public async Task StaleLeaveFromOlderSession_IsIgnored()
    {
        using var s = await SetupAsync();
        var joined = Event("participant_joined", s.VoiceChannelId, s.Alice.UserId);
        await PostWebhookAsync(s.Test, joined, Sign(joined));

        // Alice's previous device, connected a minute ago, is kicked after the new one joined.
        var staleLeft = Event("participant_left", s.VoiceChannelId, s.Alice.UserId, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        await PostWebhookAsync(s.Test, staleLeft, Sign(staleLeft));

        s.Test.SetAuthToken(s.Bob.AccessToken);
        Assert.AreEqual(1, (await ParticipantsAsync(s)).Count);
    }

    [TestMethod]
    public async Task RoomFinished_ClearsEveryone()
    {
        using var s = await SetupAsync();
        foreach (var user in new[] { s.Alice.UserId, s.Bob.UserId })
        {
            var joined = Event("participant_joined", s.VoiceChannelId, user);
            await PostWebhookAsync(s.Test, joined, Sign(joined));
        }
        Assert.AreEqual(2, (await ParticipantsAsync(s)).Count);

        var finished = Event("room_finished", s.VoiceChannelId);
        await PostWebhookAsync(s.Test, finished, Sign(finished));

        Assert.AreEqual(0, (await ParticipantsAsync(s)).Count);
    }

    [TestMethod]
    public async Task ForgedOrTamperedWebhooks_AreRejected()
    {
        using var s = await SetupAsync();
        var joined = Event("participant_joined", s.VoiceChannelId, s.Alice.UserId);
        var otherBody = Event("participant_joined", s.VoiceChannelId, s.Bob.UserId);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await PostWebhookAsync(s.Test, joined, null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await PostWebhookAsync(s.Test, joined, Sign(joined, secret: "wrong-secret-wrong-secret-wrong-secret"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await PostWebhookAsync(s.Test, joined, Sign(joined, issuer: "other-key"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await PostWebhookAsync(s.Test, joined, Sign(otherBody))).StatusCode);

        s.Test.SetAuthToken(s.Bob.AccessToken);
        Assert.AreEqual(0, (await ParticipantsAsync(s)).Count);
    }

    [TestMethod]
    public async Task NonMember_CannotListParticipants()
    {
        using var s = await SetupAsync();
        var carol = await s.Test.RegisterUserAsync("carol", "carol@test.com", "Password123!");
        s.Test.SetAuthToken(carol.AccessToken);

        var response = await s.Test.Client.GetAsync($"/api/voice/communities/{s.CommunityId}/participants");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
