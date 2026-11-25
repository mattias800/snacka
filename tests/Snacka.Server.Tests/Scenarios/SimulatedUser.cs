using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Snacka.Server.DTOs;

namespace Snacka.Server.Tests.Scenarios;

/// <summary>
/// One user of a multi-client scenario ("Alice &amp; Bob as code", #23): their own HTTP client and
/// realtime connection(s) to the test server, recording every hub event they receive, so tests
/// can assert what each user saw, and how many times.
/// </summary>
public sealed class SimulatedUser : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Hub events this user listens for (add more as scenarios need them).</summary>
    private static readonly string[] Events =
    [
        "ReceiveChannelMessage", "ChannelMessageEdited", "ChannelMessageDeleted", "ReactionUpdated", "MessagePinned",
        "ReceiveThreadReply", "ThreadMetadataUpdated", "ConversationUserTyping",
        "ConversationCreated", "ConversationMessageReceived", "ConversationMessageUpdated", "ConversationMessageDeleted",
        "AddedToConversation", "VoiceParticipantJoined", "VoiceParticipantLeft", "CommunityMemberAdded",
    ];

    private readonly IntegrationTestBase _server;
    private readonly List<HubConnection> _connections = [];
    private readonly ConcurrentQueue<(int Connection, string Event, JsonElement Payload)> _received = new();
    private readonly SemaphoreSlim _signal = new(0);

    public AuthResponse Auth { get; }
    public HttpClient Http { get; }
    public Guid Id => Auth.UserId;
    public string Name => Auth.Username;

    private SimulatedUser(IntegrationTestBase server, AuthResponse auth)
    {
        _server = server;
        Auth = auth;
        Http = server.Factory.CreateClient();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
    }

    /// <summary>Registers the user and opens one realtime connection (one "device").</summary>
    public static async Task<SimulatedUser> CreateAsync(IntegrationTestBase server, string name)
    {
        var auth = await server.RegisterUserAsync(name, $"{name}@test.com", "Password123!");
        var user = new SimulatedUser(server, auth);
        await user.ConnectAsync();
        return user;
    }

    /// <summary>Opens another realtime connection, like the same user on a second device. Returns its index.</summary>
    public async Task<int> ConnectAsync()
    {
        var connection = await _server.ConnectHubAsync(Auth.AccessToken);
        var index = _connections.Count;
        foreach (var name in Events)
            connection.On<JsonElement>(name, payload =>
            {
                _received.Enqueue((index, name, payload));
                _signal.Release();
            });
        _connections.Add(connection);
        return index;
    }

    public HubConnection Hub(int connection = 0) => _connections[connection];

    public async Task DisconnectAsync(int connection = 0) => await _connections[connection].StopAsync();

    /// <summary>Events received so far, deserialized as <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> Received<T>(string eventName, int? connection = null) =>
        _received.Where(r => r.Event == eventName && (connection is null || r.Connection == connection))
            .Select(r => r.Payload.Deserialize<T>(Json)!)
            .ToList();

    /// <summary>Waits until an event matching <paramref name="match"/> arrives (or was already received).</summary>
    public async Task<T> WaitForAsync<T>(string eventName, Func<T, bool> match, int? connection = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var hit = Received<T>(eventName, connection).FirstOrDefault(match);
            if (hit is not null) return hit;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left))
                throw new TimeoutException($"{Name} did not receive {eventName} within the timeout. Received: {string.Join(", ", _received.Select(r => r.Event))}");
        }
    }

    /// <summary>
    /// Lets in-flight events arrive before asserting that something did NOT happen (or happened
    /// only once). Long polling delivers in batches, so this round-trips every connection.
    /// </summary>
    public async Task SettleAsync()
    {
        await Task.Delay(300);
        foreach (var c in _connections.Where(c => c.State == HubConnectionState.Connected))
            await c.InvokeAsync<JsonElement>("GetOnlineUsers");
        await Task.Delay(300);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        Http.Dispose();
    }
}
