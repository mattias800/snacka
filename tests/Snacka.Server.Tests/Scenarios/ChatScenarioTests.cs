using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SkiaSharp;
using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Tests.Scenarios;

/// <summary>
/// Multi-client chat scenarios against a real server (#23): what used to be checked by hand with
/// Alice and Bob side by side. Every message must reach the right people exactly once, and nobody else.
/// </summary>
[TestClass]
public class ChatScenarioTests
{
    private sealed class World : IAsyncDisposable
    {
        public required IntegrationTestBase Server { get; init; }
        public required SimulatedUser Alice { get; init; }
        public required SimulatedUser Bob { get; init; }
        /// <summary>Registered, but not a member of Alice's community.</summary>
        public required SimulatedUser Carol { get; init; }
        public required Guid CommunityId { get; init; }
        public required Guid General { get; init; }
        public required Guid Random { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Alice.DisposeAsync();
            await Bob.DisposeAsync();
            await Carol.DisposeAsync();
            Server.Dispose();
        }
    }

    /// <summary>Alice owns a community with #general and #random; Bob is a member; Carol isn't.</summary>
    private static async Task<World> CreateWorldAsync()
    {
        var server = new IntegrationTestBase();
        var alice = await SimulatedUser.CreateAsync(server, "alice");
        var bob = await SimulatedUser.CreateAsync(server, "bob");
        var carol = await SimulatedUser.CreateAsync(server, "carol");

        var community = await Ok<CommunityResponse>(await alice.Http.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Friends", null)));
        var general = (await alice.Http.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{community.Id}/channels"))!
            .First(c => c.Type == ChannelType.Text);
        var random = await Ok<ChannelResponse>(await alice.Http.PostAsJsonAsync($"/api/communities/{community.Id}/channels",
            new CreateChannelRequest("random", null, ChannelType.Text)));
        (await bob.Http.PostAsync($"/api/communities/{community.Id}/join", null)).EnsureSuccessStatusCode();

        return new World { Server = server, Alice = alice, Bob = bob, Carol = carol, CommunityId = community.Id, General = general.Id, Random = random.Id };
    }

    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<MessageResponse> SendAsync(SimulatedUser user, Guid channelId, string content) =>
        user.Http.PostAsJsonAsync($"/api/channels/{channelId}/messages", new SendMessageRequest(content)).ContinueWith(t => Ok<MessageResponse>(t.Result)).Unwrap();

    [TestMethod]
    public async Task ChannelMessage_ReachesEveryoneInTheChannel_ExactlyOnce()
    {
        await using var w = await CreateWorldAsync();
        await w.Alice.Hub().InvokeAsync("JoinChannel", w.General);
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        var bobPhone = await w.Bob.ConnectAsync();
        await w.Bob.Hub(bobPhone).InvokeAsync("JoinChannel", w.General);

        var sent = await SendAsync(w.Alice, w.General, "hello bob");

        await w.Bob.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == sent.Id, connection: 0);
        await w.Bob.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == sent.Id, connection: bobPhone);
        await w.Alice.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == sent.Id);
        await Task.WhenAll(w.Alice.SettleAsync(), w.Bob.SettleAsync());
        Assert.AreEqual(1, w.Bob.Received<MessageResponse>("ReceiveChannelMessage", connection: 0).Count(m => m.Id == sent.Id), "Bob's desktop");
        Assert.AreEqual(1, w.Bob.Received<MessageResponse>("ReceiveChannelMessage", connection: bobPhone).Count(m => m.Id == sent.Id), "Bob's phone");
        Assert.AreEqual(1, w.Alice.Received<MessageResponse>("ReceiveChannelMessage").Count(m => m.Id == sent.Id), "Alice's own echo");
        Assert.AreEqual("hello bob", w.Bob.Received<MessageResponse>("ReceiveChannelMessage").First().Content);
    }

    [TestMethod]
    public async Task ChannelMessage_DoesNotLeakToOtherChannelsOrNonMembers()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.Random);
        // Carol tries to listen in; the hub must ignore a non-member.
        await w.Carol.Hub().InvokeAsync("JoinChannel", w.General);
        await w.Carol.Hub().InvokeAsync("JoinServer", w.CommunityId);

        await SendAsync(w.Alice, w.General, "only for #general");

        await Task.WhenAll(w.Bob.SettleAsync(), w.Carol.SettleAsync());
        Assert.AreEqual(0, w.Bob.Received<MessageResponse>("ReceiveChannelMessage").Count, "Bob is looking at #random");
        Assert.AreEqual(0, w.Carol.Received<MessageResponse>("ReceiveChannelMessage").Count, "Carol isn't a member");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await w.Carol.Http.GetAsync($"/api/channels/{w.General}/messages")).StatusCode);
    }

    [TestMethod]
    public async Task EditAndDelete_ReachOtherMembers()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        var sent = await SendAsync(w.Alice, w.General, "typo");

        await Ok<MessageResponse>(await w.Alice.Http.PutAsJsonAsync($"/api/channels/{w.General}/messages/{sent.Id}", new UpdateMessageRequest("fixed")));
        var edited = await w.Bob.WaitForAsync<MessageResponse>("ChannelMessageEdited", m => m.Id == sent.Id);
        Assert.AreEqual("fixed", edited.Content);

        (await w.Alice.Http.DeleteAsync($"/api/channels/{w.General}/messages/{sent.Id}")).EnsureSuccessStatusCode();
        await w.Bob.WaitForAsync<MessageDeletedEvent>("ChannelMessageDeleted", e => e.MessageId == sent.Id);
        var history = await w.Bob.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{w.General}/messages");
        Assert.IsFalse(history!.Any(m => m.Id == sent.Id));
    }

    [TestMethod]
    public async Task OthersCannotEditYourMessage_NorMembersDeleteIt()
    {
        await using var w = await CreateWorldAsync();
        var sent = await SendAsync(w.Bob, w.General, "bob's words");

        var edit = await w.Carol.Http.PutAsJsonAsync($"/api/channels/{w.General}/messages/{sent.Id}", new UpdateMessageRequest("carol was here"));
        Assert.AreEqual(HttpStatusCode.Forbidden, edit.StatusCode, "non-member edit");
        var memberEdit = await w.Alice.Http.PutAsJsonAsync($"/api/channels/{w.General}/messages/{sent.Id}", new UpdateMessageRequest("alice was here"));
        Assert.AreEqual(HttpStatusCode.Forbidden, memberEdit.StatusCode, "another member's edit");
        // A regular member can't delete someone else's message (the owner can: moderation).
        var alices = await SendAsync(w.Alice, w.General, "alice's words");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await w.Bob.Http.DeleteAsync($"/api/channels/{w.General}/messages/{alices.Id}")).StatusCode, "a member deleting another's message");

        var history = await w.Bob.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{w.General}/messages");
        Assert.AreEqual("bob's words", history!.Single(m => m.Id == sent.Id).Content);
    }

    [TestMethod]
    public async Task Reconnect_CatchesUpOnMissedMessages_InOrder_WithoutDuplicates()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        var before = await SendAsync(w.Alice, w.General, "before");
        await w.Bob.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == before.Id);

        await w.Bob.DisconnectAsync();
        var missed = new List<MessageResponse>();
        for (var i = 1; i <= 3; i++) missed.Add(await SendAsync(w.Alice, w.General, $"missed {i}"));

        // Bob comes back: a new connection, then the history fills the gap.
        var again = await w.Bob.ConnectAsync();
        await w.Bob.Hub(again).InvokeAsync("JoinChannel", w.General);
        var history = (await w.Bob.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{w.General}/messages"))!;
        CollectionAssert.AreEqual(
            new[] { before.Id }.Concat(missed.Select(m => m.Id)).ToList(),
            history.Select(m => m.Id).ToList());

        var after = await SendAsync(w.Alice, w.General, "after");
        await w.Bob.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == after.Id, connection: again);
    }

    [TestMethod]
    public async Task NewMember_GetsLiveMessagesWithoutReconnecting()
    {
        await using var w = await CreateWorldAsync();
        // Carol joins the community while connected (after her hub connection was set up).
        (await w.Carol.Http.PostAsync($"/api/communities/{w.CommunityId}/join", null)).EnsureSuccessStatusCode();
        await w.Carol.Hub().InvokeAsync("JoinChannel", w.General);

        var sent = await SendAsync(w.Alice, w.General, "welcome carol");

        await w.Carol.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == sent.Id);
    }

    [TestMethod]
    public async Task DirectMessage_FirstMessageOfANewConversation_ArrivesLive()
    {
        await using var w = await CreateWorldAsync();

        // Alice starts a DM and immediately says something; Bob is online but has never seen
        // this conversation. The first message must not be lost.
        var conversation = await Ok<ConversationResponse>(await w.Alice.Http.PostAsJsonAsync("/api/conversations", new CreateConversationRequest([w.Bob.Id], null)));
        var first = await Ok<ConversationMessageResponse>(await w.Alice.Http.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new SendConversationMessageRequest("hi bob, it's alice")));

        await w.Bob.WaitForAsync<ConversationResponse>("ConversationCreated", c => c.Id == conversation.Id);
        var received = await w.Bob.WaitForAsync<ConversationMessageResponse>("ConversationMessageReceived", m => m.Id == first.Id);
        Assert.AreEqual("hi bob, it's alice", received.Content);

        // And nobody else hears it.
        await w.Carol.SettleAsync();
        Assert.AreEqual(0, w.Carol.Received<ConversationMessageResponse>("ConversationMessageReceived").Count);
    }

    [TestMethod]
    public async Task DirectMessage_ReachesEachParticipantConnectionExactlyOnce()
    {
        await using var w = await CreateWorldAsync();
        var conversation = await Ok<ConversationResponse>(await w.Alice.Http.PostAsJsonAsync("/api/conversations", new CreateConversationRequest([w.Bob.Id], null)));
        var bobPhone = await w.Bob.ConnectAsync(); // connects after the conversation exists

        var reply = await Ok<ConversationMessageResponse>(await w.Bob.Http.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new SendConversationMessageRequest("hey alice")));

        await w.Alice.WaitForAsync<ConversationMessageResponse>("ConversationMessageReceived", m => m.Id == reply.Id);
        await w.Bob.WaitForAsync<ConversationMessageResponse>("ConversationMessageReceived", m => m.Id == reply.Id, connection: bobPhone);
        await Task.WhenAll(w.Alice.SettleAsync(), w.Bob.SettleAsync());
        Assert.AreEqual(1, w.Alice.Received<ConversationMessageResponse>("ConversationMessageReceived").Count(m => m.Id == reply.Id));
        Assert.AreEqual(1, w.Bob.Received<ConversationMessageResponse>("ConversationMessageReceived", connection: bobPhone).Count(m => m.Id == reply.Id));
    }

    [TestMethod]
    public async Task NonParticipant_CannotReadOrJoinAConversation()
    {
        await using var w = await CreateWorldAsync();
        var conversation = await Ok<ConversationResponse>(await w.Alice.Http.PostAsJsonAsync("/api/conversations", new CreateConversationRequest([w.Bob.Id], null)));
        await w.Carol.Hub().InvokeAsync("JoinConversationGroup", conversation.Id);

        await w.Alice.Http.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new SendConversationMessageRequest("secret"));

        await w.Carol.SettleAsync();
        Assert.AreEqual(0, w.Carol.Received<ConversationMessageResponse>("ConversationMessageReceived").Count);
        var read = await w.Carol.Http.GetAsync($"/api/conversations/{conversation.Id}/messages");
        Assert.IsFalse(read.IsSuccessStatusCode, $"Carol read the DM: {read.StatusCode}");
        var send = await w.Carol.Http.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new SendConversationMessageRequest("let me in"));
        Assert.IsFalse(send.IsSuccessStatusCode, $"Carol posted in the DM: {send.StatusCode}");
    }

    [TestMethod]
    public async Task RemovedParticipant_StopsReceivingTheGroupConversation()
    {
        await using var w = await CreateWorldAsync();
        var group = await Ok<ConversationResponse>(await w.Alice.Http.PostAsJsonAsync("/api/conversations", new CreateConversationRequest([w.Bob.Id, w.Carol.Id], "plans")));
        // Carol's client joins the conversation group, as clients do on ConversationCreated.
        await w.Carol.Hub().InvokeAsync("JoinConversationGroup", group.Id);
        var hello = await Ok<ConversationMessageResponse>(await w.Alice.Http.PostAsJsonAsync($"/api/conversations/{group.Id}/messages", new SendConversationMessageRequest("hello both")));
        await w.Carol.WaitForAsync<ConversationMessageResponse>("ConversationMessageReceived", m => m.Id == hello.Id);

        (await w.Alice.Http.DeleteAsync($"/api/conversations/{group.Id}/participants/{w.Carol.Id}")).EnsureSuccessStatusCode();
        var secret = await Ok<ConversationMessageResponse>(await w.Alice.Http.PostAsJsonAsync($"/api/conversations/{group.Id}/messages", new SendConversationMessageRequest("surprise party for carol")));

        await w.Bob.WaitForAsync<ConversationMessageResponse>("ConversationMessageReceived", m => m.Id == secret.Id);
        await w.Carol.SettleAsync();
        Assert.IsFalse(w.Carol.Received<ConversationMessageResponse>("ConversationMessageReceived").Any(m => m.Id == secret.Id), "Carol still receives after removal");
    }

    [TestMethod]
    public async Task Reactions_ReachOtherMembersOnce_WithTheRightCount()
    {
        await using var w = await CreateWorldAsync();
        await w.Alice.Hub().InvokeAsync("JoinChannel", w.General);
        var sent = await SendAsync(w.Alice, w.General, "pizza tonight?");

        await Ok<ReactionUpdatedEvent>(await w.Bob.Http.PostAsJsonAsync($"/api/channels/{w.General}/messages/{sent.Id}/reactions", new AddReactionRequest("🍕")));
        var added = await w.Alice.WaitForAsync<ReactionUpdatedEvent>("ReactionUpdated", e => e.MessageId == sent.Id && e.Added);
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(w.Bob.Id, added.UserId);

        // Reacting twice with the same emoji doesn't count twice.
        await w.Bob.Http.PostAsJsonAsync($"/api/channels/{w.General}/messages/{sent.Id}/reactions", new AddReactionRequest("🍕"));
        var history = await w.Alice.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{w.General}/messages");
        Assert.AreEqual(1, history!.Single(m => m.Id == sent.Id).Reactions!.Single(r => r.Emoji == "🍕").Count);

        (await w.Bob.Http.DeleteAsync($"/api/channels/{w.General}/messages/{sent.Id}/reactions/{Uri.EscapeDataString("🍕")}")).EnsureSuccessStatusCode();
        await w.Alice.WaitForAsync<ReactionUpdatedEvent>("ReactionUpdated", e => e.MessageId == sent.Id && !e.Added && e.Count == 0);
    }

    [TestMethod]
    public async Task MessageActions_ThroughAnotherChannelsUrl_AreRejected()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        var sent = await SendAsync(w.Alice, w.General, "in #general");

        // Same message id, but the URL says #random.
        var react = await w.Alice.Http.PostAsJsonAsync($"/api/channels/{w.Random}/messages/{sent.Id}/reactions", new AddReactionRequest("👍"));
        var edit = await w.Alice.Http.PutAsJsonAsync($"/api/channels/{w.Random}/messages/{sent.Id}", new UpdateMessageRequest("edited"));

        Assert.AreEqual(HttpStatusCode.NotFound, react.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, edit.StatusCode);
        var history = await w.Bob.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{w.General}/messages");
        Assert.AreEqual("in #general", history!.Single(m => m.Id == sent.Id).Content);
    }

    [TestMethod]
    public async Task Pins_AuthorOrAdminOnly_AndOthersSeeThem()
    {
        await using var w = await CreateWorldAsync();
        await w.Alice.Hub().InvokeAsync("JoinChannel", w.General);
        var alices = await SendAsync(w.Alice, w.General, "house rules");

        var bobPins = await w.Bob.Http.PostAsync($"/api/channels/{w.General}/messages/{alices.Id}/pin", null);
        Assert.AreEqual(HttpStatusCode.Forbidden, bobPins.StatusCode, "a regular member pinning someone else's message");

        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        (await w.Alice.Http.PostAsync($"/api/channels/{w.General}/messages/{alices.Id}/pin", null)).EnsureSuccessStatusCode();
        var pinned = await w.Bob.WaitForAsync<MessagePinnedEvent>("MessagePinned", e => e.MessageId == alices.Id);
        Assert.IsTrue(pinned.IsPinned);
    }

    [TestMethod]
    public async Task ThreadReply_ReachesTheChannel_WithReplyCount()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        var parent = await SendAsync(w.Bob, w.General, "who's in for saturday?");

        for (var i = 1; i <= 2; i++)
            await Ok<MessageResponse>(await w.Alice.Http.PostAsJsonAsync($"/api/messages/{parent.Id}/replies", new SendMessageRequest($"me {i}")));

        await w.Bob.WaitForAsync<System.Text.Json.JsonElement>("ThreadMetadataUpdated", e => e.GetProperty("replyCount").GetInt32() == 2);
        var thread = await w.Bob.Http.GetFromJsonAsync<ThreadResponse>($"/api/messages/{parent.Id}/thread");
        Assert.AreEqual(2, thread!.TotalReplyCount);
        // Thread replies stay out of the main channel history.
        var history = await w.Bob.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{w.General}/messages");
        Assert.AreEqual(1, history!.Count);
    }

    [TestMethod]
    public async Task ConversationTyping_ReachesTheOtherParticipantOnly()
    {
        await using var w = await CreateWorldAsync();
        var dm = await Ok<ConversationResponse>(await w.Alice.Http.PostAsJsonAsync("/api/conversations", new CreateConversationRequest([w.Bob.Id], null)));

        await w.Alice.Hub().InvokeAsync("SendConversationTyping", dm.Id);

        await w.Bob.WaitForAsync<ConversationTypingEvent>("ConversationUserTyping", e => e.ConversationId == dm.Id && e.UserId == w.Alice.Id);
        await Task.WhenAll(w.Alice.SettleAsync(), w.Carol.SettleAsync());
        Assert.AreEqual(0, w.Alice.Received<ConversationTypingEvent>("ConversationUserTyping").Count, "own typing echoed back");
        Assert.AreEqual(0, w.Carol.Received<ConversationTypingEvent>("ConversationUserTyping").Count);
    }

    private static MultipartFormDataContent Upload(string content, Guid? replyToId, string fileName = "note.txt", string body = "hello")
    {
        var form = new MultipartFormDataContent { { new StringContent(content), "content" } };
        if (replyToId is { } id) form.Add(new StringContent(id.ToString()), "replyToId");
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(file, "files", fileName);
        return form;
    }

    [TestMethod]
    public async Task Attachment_ReachesOthers_AndOnlyMembersCanDownloadIt()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);

        var sent = await Ok<MessageResponse>(await w.Alice.Http.PostAsync($"/api/channels/{w.General}/messages/with-attachments", Upload("see attached", null, body: "the plan")));

        var received = await w.Bob.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == sent.Id);
        var url = new Uri(received.Attachments!.Single().Url).PathAndQuery;
        Assert.AreEqual("the plan", await w.Bob.Http.GetStringAsync(url));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await w.Carol.Http.GetAsync(url)).StatusCode);
    }

    [TestMethod]
    public async Task Attachment_ReplyToAMessageElsewhere_DoesNotLeakItsContent()
    {
        await using var w = await CreateWorldAsync();
        var secret = await SendAsync(w.Alice, w.General, "alice's private plans");

        // Carol, in her own community, "replies" to Alice's message id with an attachment.
        var own = await Ok<CommunityResponse>(await w.Carol.Http.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Carol's", null)));
        var ownChannel = (await w.Carol.Http.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{own.Id}/channels"))!.First(c => c.Type == ChannelType.Text);
        var posted = await Ok<MessageResponse>(await w.Carol.Http.PostAsync($"/api/channels/{ownChannel.Id}/messages/with-attachments", Upload("hmm", secret.Id)));

        Assert.IsNull(posted.ReplyTo?.Content);
        var history = await w.Carol.Http.GetFromJsonAsync<List<MessageResponse>>($"/api/channels/{ownChannel.Id}/messages");
        Assert.IsNull(history!.Single(m => m.Id == posted.Id).ReplyTo, "reply preview leaked another community's message");
    }

    [TestMethod]
    public async Task Attachment_ReplyInTheSameChannel_ShowsTheQuote()
    {
        await using var w = await CreateWorldAsync();
        var question = await SendAsync(w.Alice, w.General, "photo of the setup?");

        var reply = await Ok<MessageResponse>(await w.Bob.Http.PostAsync($"/api/channels/{w.General}/messages/with-attachments", Upload("here", question.Id, "setup.txt")));

        Assert.AreEqual("photo of the setup?", reply.ReplyTo?.Content);
    }

    // ---- Profiles ----

    [TestMethod]
    public async Task ProfileChange_ReachesPeopleWhoShareACommunity_AndNobodyElse()
    {
        await using var w = await CreateWorldAsync();

        var profile = await Ok<UserProfileResponse>(await w.Alice.Http.PutAsJsonAsync("/api/users/me", new UpdateProfileRequest(null, "  Alice 🎮 ", null)));
        Assert.AreEqual("Alice 🎮", profile.EffectiveDisplayName);

        var seen = await w.Bob.WaitForAsync<UserProfileUpdatedEvent>("UserProfileUpdated", e => e.UserId == w.Alice.Id);
        Assert.AreEqual("Alice 🎮", seen.EffectiveDisplayName);
        await w.Alice.WaitForAsync<UserProfileUpdatedEvent>("UserProfileUpdated", e => e.UserId == w.Alice.Id); // her other sessions
        await w.Carol.SettleAsync();
        Assert.AreEqual(0, w.Carol.Received<UserProfileUpdatedEvent>("UserProfileUpdated").Count, "Carol shares nothing with Alice");

        // New messages carry the new name.
        var m = await SendAsync(w.Alice, w.General, "new name");
        Assert.AreEqual("Alice 🎮", m.AuthorEffectiveDisplayName);
    }

    [TestMethod]
    public async Task ProfileChange_ReachesDmPartners()
    {
        await using var w = await CreateWorldAsync();
        await Ok<ConversationResponse>(await w.Carol.Http.PostAsJsonAsync("/api/conversations", new CreateConversationRequest([w.Alice.Id], null)));

        await Ok<UserProfileResponse>(await w.Alice.Http.PutAsJsonAsync("/api/users/me", new UpdateProfileRequest(null, "Ali", null)));

        await w.Carol.WaitForAsync<UserProfileUpdatedEvent>("UserProfileUpdated", e => e.UserId == w.Alice.Id && e.EffectiveDisplayName == "Ali");
    }

    [TestMethod]
    public async Task BlankDisplayName_FallsBackToUsername()
    {
        await using var w = await CreateWorldAsync();
        await Ok<UserProfileResponse>(await w.Alice.Http.PutAsJsonAsync("/api/users/me", new UpdateProfileRequest(null, "Temp", null)));

        var cleared = await Ok<UserProfileResponse>(await w.Alice.Http.PutAsJsonAsync("/api/users/me", new UpdateProfileRequest(null, "   ", null)));

        Assert.IsNull(cleared.DisplayName);
        Assert.AreEqual("alice", cleared.EffectiveDisplayName);
    }

    [TestMethod]
    public async Task Avatar_UploadReplaceAndRemove_AreSeenByOthers()
    {
        await using var w = await CreateWorldAsync();

        var first = await Ok<AvatarUploadResponse>(await w.Alice.Http.PutAsync("/api/users/me/avatar", AvatarForm(SKColors.Red)));
        await w.Bob.WaitForAsync<UserProfileUpdatedEvent>("UserProfileUpdated", e => e.UserId == w.Alice.Id && e.Avatar == first.Avatar);
        var png = await w.Bob.Http.GetByteArrayAsync($"/api/users/{w.Alice.Id}/avatar");
        using (var image = SKBitmap.Decode(png))
            Assert.AreEqual(SKColors.Red, image.GetPixel(image.Width / 2, image.Height / 2));

        var second = await Ok<AvatarUploadResponse>(await w.Alice.Http.PutAsync("/api/users/me/avatar", AvatarForm(SKColors.Blue)));
        Assert.AreNotEqual(first.Avatar, second.Avatar, "a new file name lets clients drop their cached image");
        await w.Bob.WaitForAsync<UserProfileUpdatedEvent>("UserProfileUpdated", e => e.UserId == w.Alice.Id && e.Avatar == second.Avatar);

        (await w.Alice.Http.DeleteAsync("/api/users/me/avatar")).EnsureSuccessStatusCode();
        await w.Bob.WaitForAsync<UserProfileUpdatedEvent>("UserProfileUpdated", e => e.UserId == w.Alice.Id && e.Avatar == null);
        Assert.AreEqual(HttpStatusCode.NotFound, (await w.Bob.Http.GetAsync($"/api/users/{w.Alice.Id}/avatar")).StatusCode);
    }

    private static MultipartFormDataContent AvatarForm(SKColor color)
    {
        using var bitmap = new SKBitmap(64, 48);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var file = new ByteArrayContent(data.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "me.png" } };
    }

    // ---- Unread counts ----

    private async Task<int> UnreadAsync(SimulatedUser user, Guid communityId, Guid channelId) =>
        (await user.Http.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{communityId}/channels"))!.Single(c => c.Id == channelId).UnreadCount;

    [TestMethod]
    public async Task UnreadCount_CountsOthersMessagesInTheChannelList_UntilRead()
    {
        await using var w = await CreateWorldAsync();
        var first = await SendAsync(w.Alice, w.Random, "one");
        await SendAsync(w.Alice, w.Random, "two");
        await SendAsync(w.Bob, w.Random, "bob's own");
        await Ok<MessageResponse>(await w.Alice.Http.PostAsJsonAsync($"/api/messages/{first.Id}/replies", new SendMessageRequest("in a thread")));

        Assert.AreEqual(2, await UnreadAsync(w.Bob, w.CommunityId, w.Random), "own messages and thread replies don't count");
        Assert.AreEqual(1, await UnreadAsync(w.Alice, w.CommunityId, w.Random), "Bob's message is unread for Alice");

        (await w.Bob.Http.PostAsync($"/api/communities/{w.CommunityId}/channels/{w.Random}/read", null)).EnsureSuccessStatusCode();
        Assert.AreEqual(0, await UnreadAsync(w.Bob, w.CommunityId, w.Random));

        await SendAsync(w.Alice, w.Random, "three");
        Assert.AreEqual(1, await UnreadAsync(w.Bob, w.CommunityId, w.Random));
    }

    [TestMethod]
    public async Task ChannelActivity_ReachesCommunityMembersWithoutTheChannelOpen()
    {
        await using var w = await CreateWorldAsync();
        // Bob has the community open (as the client does: JoinServer) but no channel: he gets the
        // community-wide activity event, not the message.
        await w.Bob.Hub().InvokeAsync("JoinServer", w.CommunityId);
        var m = await SendAsync(w.Alice, w.Random, "anyone?");

        var e = await w.Bob.WaitForAsync<ChannelActivityEvent>("ChannelActivity", e => e.Message.Id == m.Id);
        Assert.AreEqual(w.Random, e.ChannelId);
        Assert.AreEqual("random", e.ChannelName);
        Assert.AreEqual(w.Alice.Id, e.Message.AuthorId);
        Assert.AreEqual("anyone?", e.Message.Content);
        Assert.AreEqual(0, e.MentionedUserIds.Count);
        await w.Carol.SettleAsync();
        Assert.AreEqual(0, w.Carol.Received<ChannelActivityEvent>("ChannelActivity").Count, "Carol isn't a member");
        Assert.AreEqual(0, w.Bob.Received<MessageResponse>("ReceiveChannelMessage").Count);
    }

    [TestMethod]
    public async Task MarkRead_ChannelOfAnotherCommunity_IsNotFound()
    {
        await using var w = await CreateWorldAsync();
        var own = await Ok<CommunityResponse>(await w.Carol.Http.PostAsJsonAsync("/api/communities", new CreateCommunityRequest("Carol's", null)));
        var carols = (await w.Carol.Http.GetFromJsonAsync<List<ChannelResponse>>($"/api/communities/{own.Id}/channels"))!.First();

        var res = await w.Bob.Http.PostAsync($"/api/communities/{w.CommunityId}/channels/{carols.Id}/read", null);

        Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
    }

    [TestMethod]
    public async Task Mentions_NameOnlyMembers_AnyCase_NeverTheAuthor()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinServer", w.CommunityId);
        // Carol exists but isn't a member; an email address isn't a mention.
        var m = await SendAsync(w.Alice, w.General, "@BOB look, and @carol @alice @nobody mail alice@test.com");

        var e = await w.Bob.WaitForAsync<ChannelActivityEvent>("ChannelActivity", e => e.Message.Id == m.Id);

        CollectionAssert.AreEquivalent(new[] { w.Bob.Id }, e.MentionedUserIds.ToArray());
    }

    [TestMethod]
    public async Task EveryoneMention_NamesAllMembersButTheAuthor()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinServer", w.CommunityId);

        var m = await SendAsync(w.Alice, w.General, "dinner? @everyone");

        var e = await w.Bob.WaitForAsync<ChannelActivityEvent>("ChannelActivity", e => e.Message.Id == m.Id);
        CollectionAssert.AreEquivalent(new[] { w.Bob.Id }, e.MentionedUserIds.ToArray());
    }

    [TestMethod]
    public async Task ThreadReply_TellsTheCommunityWhoseThreadItIs()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinServer", w.CommunityId);
        var parent = await SendAsync(w.Bob, w.General, "who's up for a game?");

        var reply = await Ok<MessageResponse>(await w.Alice.Http.PostAsJsonAsync($"/api/messages/{parent.Id}/replies", new SendMessageRequest("me!")));

        var e = await w.Bob.WaitForAsync<ChannelActivityEvent>("ChannelActivity", e => e.Message.Id == reply.Id);
        Assert.AreEqual(parent.Id, e.Message.ThreadParentMessageId);
        Assert.AreEqual(w.Bob.Id, e.ThreadParentAuthorId);
    }

    [TestMethod]
    public async Task UnreadByCommunity_SumsItsChannels_UntilRead()
    {
        await using var w = await CreateWorldAsync();
        var parent = await SendAsync(w.Alice, w.General, "one");
        await SendAsync(w.Alice, w.General, "two");
        await SendAsync(w.Alice, w.Random, "three");
        await SendAsync(w.Bob, w.Random, "bob's own");
        await w.Alice.Http.PostAsJsonAsync($"/api/messages/{parent.Id}/replies", new SendMessageRequest("thread replies don't count"));

        Task<List<CommunityUnreadResponse>> Unread() => w.Bob.Http.GetFromJsonAsync<List<CommunityUnreadResponse>>("/api/communities/unread")!;

        var before = await Unread();
        Assert.AreEqual(1, before.Count);
        Assert.AreEqual(w.CommunityId, before[0].CommunityId);
        Assert.AreEqual(3, before[0].UnreadCount);

        (await w.Bob.Http.PostAsync($"/api/communities/{w.CommunityId}/channels/{w.General}/read", null)).EnsureSuccessStatusCode();
        Assert.AreEqual(1, (await Unread()).Single().UnreadCount);

        (await w.Bob.Http.PostAsync($"/api/communities/{w.CommunityId}/channels/{w.Random}/read", null)).EnsureSuccessStatusCode();
        Assert.AreEqual(0, (await Unread()).Count);
        Assert.AreEqual(0, (await w.Carol.Http.GetFromJsonAsync<List<CommunityUnreadResponse>>("/api/communities/unread"))!.Count, "Carol isn't a member");
    }

    [TestMethod]
    [DataRow("hi @bob", "bob")]
    [DataRow("(@Bob), @bob.", "bob")]
    [DataRow("@bob_the-builder!", "bob_the-builder")]
    [DataRow("bob@test.com @@bob", "")]
    [DataRow("@everyone", "everyone")]
    public void MentionedNames_FindsAtNamesStartingAWord(string content, string expected)
    {
        var names = ChannelActivityService.MentionedNames(content);
        Assert.AreEqual(expected, string.Join(",", names.Order()));
    }

    private sealed record Presence(Guid UserId, string Username, bool IsOnline);

    [TestMethod]
    public async Task Presence_ReachesOnlyPeopleWhoShareACommunityOrConversation()
    {
        await using var w = await CreateWorldAsync();
        await using var dan = await SimulatedUser.CreateAsync(w.Server, "dan", connect: false);
        (await dan.Http.PostAsync($"/api/communities/{w.CommunityId}/join", null)).EnsureSuccessStatusCode();
        // Erin shares nothing with Dan but a DM.
        await using var erin = await SimulatedUser.CreateAsync(w.Server, "erin");
        await Ok<ConversationResponse>(await erin.Http.GetAsync($"/api/conversations/direct/{dan.Id}"));

        await dan.ConnectAsync();
        await w.Alice.WaitForAsync<Presence>("UserOnline", p => p.UserId == dan.Id && p.IsOnline);
        await erin.WaitForAsync<Presence>("UserOnline", p => p.UserId == dan.Id);
        await dan.DisconnectAsync();
        await w.Bob.WaitForAsync<Presence>("UserOffline", p => p.UserId == dan.Id && !p.IsOnline);

        await w.Carol.SettleAsync();
        Assert.AreEqual(0, w.Carol.Received<Presence>("UserOnline").Count(p => p.UserId == dan.Id), "Carol shares nothing with Dan");
        Assert.AreEqual(0, w.Carol.Received<Presence>("UserOffline").Count(p => p.UserId == dan.Id));
    }

    [TestMethod]
    public async Task LeavingACommunity_StopsItsEventsAtOnce()
    {
        await using var w = await CreateWorldAsync();
        await w.Bob.Hub().InvokeAsync("JoinServer", w.CommunityId);
        await w.Bob.Hub().InvokeAsync("JoinChannel", w.General);
        var before = await SendAsync(w.Alice, w.General, "still here?");
        await w.Bob.WaitForAsync<MessageResponse>("ReceiveChannelMessage", m => m.Id == before.Id);

        (await w.Bob.Http.PostAsync($"/api/communities/{w.CommunityId}/leave", null)).EnsureSuccessStatusCode();
        var after = await SendAsync(w.Alice, w.General, "bob left");

        await w.Alice.WaitForAsync<ChannelActivityEvent>("ChannelActivity", e => e.Message.Id == after.Id);
        await w.Bob.SettleAsync();
        Assert.AreEqual(0, w.Bob.Received<MessageResponse>("ReceiveChannelMessage").Count(m => m.Id == after.Id), "channel group");
        Assert.AreEqual(0, w.Bob.Received<ChannelActivityEvent>("ChannelActivity").Count(e => e.Message.Id == after.Id), "community group");
    }

    [TestMethod]
    public async Task JoiningACommunity_DeliversItsActivityWithoutReconnecting()
    {
        await using var w = await CreateWorldAsync();
        // Carol is connected already; joining over HTTP is enough (no JoinServer call).
        (await w.Carol.Http.PostAsync($"/api/communities/{w.CommunityId}/join", null)).EnsureSuccessStatusCode();

        var m = await SendAsync(w.Alice, w.Random, "welcome carol");

        await w.Carol.WaitForAsync<ChannelActivityEvent>("ChannelActivity", e => e.Message.Id == m.Id);
    }
}
