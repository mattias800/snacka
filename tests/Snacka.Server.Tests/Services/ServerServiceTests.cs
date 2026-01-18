using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Tests.Services;

[TestClass]
public class CommunityServiceTests
{
    private static async Task<User> CreateTestUserAsync(Data.SnackaDbContext db, string username = "testuser")
    {
        var user = new User
        {
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "hash"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    #region Community Tests

    [TestMethod]
    public async Task CreateCommunityAsync_WithValidData_CreatesCommunityWithDefaultChannel()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);

        // Act
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test Community", "A test community"));

        // Assert
        Assert.IsNotNull(community);
        Assert.AreEqual("Test Community", community.Name);
        Assert.AreEqual("A test community", community.Description);
        Assert.AreEqual(user.Id, community.OwnerId);
        Assert.AreEqual(1, community.MemberCount);

        // Verify default channels were created (text and voice)
        var channels = (await channelService.GetChannelsAsync(community.Id)).ToList();
        Assert.AreEqual(2, channels.Count);
        Assert.IsTrue(channels.Any(c => c.Name == "general" && c.Type == ChannelType.Text));
        Assert.IsTrue(channels.Any(c => c.Name == "general" && c.Type == ChannelType.Voice));
    }

    [TestMethod]
    public async Task GetUserCommunitiesAsync_ReturnsUserCommunities()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var service = new CommunityService(db);

        await service.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Community 1", null));
        await service.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Community 2", null));

        // Act
        var communities = (await service.GetUserCommunitiesAsync(user.Id)).ToList();

        // Assert
        Assert.AreEqual(2, communities.Count);
    }

    [TestMethod]
    public async Task UpdateCommunityAsync_ByOwner_UpdatesCommunity()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var service = new CommunityService(db);
        var community = await service.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Original", null));

        // Act
        var updated = await service.UpdateCommunityAsync(community.Id, user.Id, new UpdateCommunityRequest("Updated", "New description", null));

        // Assert
        Assert.AreEqual("Updated", updated.Name);
        Assert.AreEqual("New description", updated.Description);
    }

    [TestMethod]
    public async Task UpdateCommunityAsync_ByNonMember_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var nonMember = await CreateTestUserAsync(db, "nonmember");
        var service = new CommunityService(db);
        var community = await service.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => service.UpdateCommunityAsync(community.Id, nonMember.Id, new UpdateCommunityRequest("Hacked", null, null)));
    }

    [TestMethod]
    public async Task DeleteCommunityAsync_ByOwner_DeletesCommunity()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var service = new CommunityService(db);
        var community = await service.CreateCommunityAsync(user.Id, new CreateCommunityRequest("To Delete", null));

        // Act
        await service.DeleteCommunityAsync(community.Id, user.Id);

        // Assert
        var communities = await service.GetUserCommunitiesAsync(user.Id);
        Assert.AreEqual(0, communities.Count());
    }

    [TestMethod]
    public async Task DeleteCommunityAsync_ByNonOwner_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var member = await CreateTestUserAsync(db, "member");
        var communityService = new CommunityService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));
        await memberService.JoinCommunityAsync(community.Id, member.Id);

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => communityService.DeleteCommunityAsync(community.Id, member.Id));
    }

    #endregion

    #region Channel Tests

    [TestMethod]
    public async Task CreateChannelAsync_ByAdmin_CreatesChannel()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));

        // Act
        var channel = await channelService.CreateChannelAsync(community.Id, user.Id, new CreateChannelRequest("new-channel", "Topic", ChannelType.Text));

        // Assert
        Assert.AreEqual("new-channel", channel.Name);
        Assert.AreEqual("Topic", channel.Topic);
        Assert.AreEqual(ChannelType.Text, channel.Type);
    }

    [TestMethod]
    public async Task CreateChannelAsync_ByMember_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var member = await CreateTestUserAsync(db, "member");
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));
        await memberService.JoinCommunityAsync(community.Id, member.Id);

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => channelService.CreateChannelAsync(community.Id, member.Id, new CreateChannelRequest("hacked", null, ChannelType.Text)));
    }

    [TestMethod]
    public async Task UpdateChannelAsync_UpdatesChannel()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));
        var channel = await channelService.CreateChannelAsync(community.Id, user.Id, new CreateChannelRequest("original", null, ChannelType.Text));

        // Act
        var updated = await channelService.UpdateChannelAsync(channel.Id, user.Id, new UpdateChannelRequest("updated", "New topic", null));

        // Assert
        Assert.AreEqual("updated", updated.Name);
        Assert.AreEqual("New topic", updated.Topic);
    }

    [TestMethod]
    public async Task DeleteChannelAsync_DeletesChannel()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));
        var channel = await channelService.CreateChannelAsync(community.Id, user.Id, new CreateChannelRequest("to-delete", null, ChannelType.Text));

        // Act
        await channelService.DeleteChannelAsync(channel.Id, user.Id);

        // Assert
        var channels = await channelService.GetChannelsAsync(community.Id);
        Assert.IsFalse(channels.Any(c => c.Name == "to-delete"));
    }

    #endregion

    #region Message Tests

    [TestMethod]
    public async Task SendMessageAsync_CreatesMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var messageService = new MessageService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));
        var channels = (await channelService.GetChannelsAsync(community.Id)).ToList();
        var channelId = channels[0].Id;

        // Act
        var message = await messageService.SendMessageAsync(channelId, user.Id, "Hello world!");

        // Assert
        Assert.AreEqual("Hello world!", message.Content);
        Assert.AreEqual(user.Id, message.AuthorId);
        Assert.AreEqual("testuser", message.AuthorUsername);
    }

    [TestMethod]
    public async Task GetMessagesAsync_ReturnsMessagesInOrder()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var messageService = new MessageService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));
        var channels = (await channelService.GetChannelsAsync(community.Id)).ToList();
        var channelId = channels[0].Id;

        await messageService.SendMessageAsync(channelId, user.Id, "Message 1");
        await messageService.SendMessageAsync(channelId, user.Id, "Message 2");
        await messageService.SendMessageAsync(channelId, user.Id, "Message 3");

        // Act
        var messages = (await messageService.GetMessagesAsync(channelId, user.Id)).ToList();

        // Assert
        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual("Message 1", messages[0].Content);
        Assert.AreEqual("Message 3", messages[2].Content);
    }

    [TestMethod]
    public async Task UpdateMessageAsync_ByAuthor_UpdatesMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var messageService = new MessageService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));
        var channels = (await channelService.GetChannelsAsync(community.Id)).ToList();
        var message = await messageService.SendMessageAsync(channels[0].Id, user.Id, "Original");

        // Act
        var updated = await messageService.UpdateMessageAsync(message.Id, user.Id, "Updated");

        // Assert
        Assert.AreEqual("Updated", updated.Content);
        Assert.IsTrue(updated.IsEdited);
    }

    [TestMethod]
    public async Task UpdateMessageAsync_ByNonAuthor_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var author = await CreateTestUserAsync(db, "author");
        var other = await CreateTestUserAsync(db, "other");
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var messageService = new MessageService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(author.Id, new CreateCommunityRequest("Test", null));
        await memberService.JoinCommunityAsync(community.Id, other.Id);
        var channels = (await channelService.GetChannelsAsync(community.Id)).ToList();
        var message = await messageService.SendMessageAsync(channels[0].Id, author.Id, "Original");

        // Act & Assert
        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => messageService.UpdateMessageAsync(message.Id, other.Id, "Hacked"));
    }

    [TestMethod]
    public async Task DeleteMessageAsync_ByAuthor_DeletesMessage()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var user = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var channelService = new ChannelService(db);
        var messageService = new MessageService(db);
        var community = await communityService.CreateCommunityAsync(user.Id, new CreateCommunityRequest("Test", null));
        var channels = (await channelService.GetChannelsAsync(community.Id)).ToList();
        var message = await messageService.SendMessageAsync(channels[0].Id, user.Id, "To delete");

        // Act
        await messageService.DeleteMessageAsync(message.Id, user.Id);

        // Assert
        var messages = await messageService.GetMessagesAsync(channels[0].Id, user.Id);
        Assert.AreEqual(0, messages.Count());
    }

    #endregion

    #region Member Tests

    [TestMethod]
    public async Task JoinCommunityAsync_AddsMemberToCommunity()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var member = await CreateTestUserAsync(db, "member");
        var communityService = new CommunityService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));

        // Act
        await memberService.JoinCommunityAsync(community.Id, member.Id);

        // Assert
        var members = (await memberService.GetMembersAsync(community.Id)).ToList();
        Assert.AreEqual(2, members.Count);
        Assert.IsTrue(await memberService.IsMemberAsync(community.Id, member.Id));
    }

    [TestMethod]
    public async Task JoinCommunityAsync_WhenAlreadyMember_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var member = await CreateTestUserAsync(db, "member");
        var communityService = new CommunityService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));
        await memberService.JoinCommunityAsync(community.Id, member.Id);

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => memberService.JoinCommunityAsync(community.Id, member.Id));
        Assert.AreEqual("User is already a member of this community.", exception.Message);
    }

    [TestMethod]
    public async Task LeaveCommunityAsync_RemovesMemberFromCommunity()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var member = await CreateTestUserAsync(db, "member");
        var communityService = new CommunityService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));
        await memberService.JoinCommunityAsync(community.Id, member.Id);

        // Act
        await memberService.LeaveCommunityAsync(community.Id, member.Id);

        // Assert
        Assert.IsFalse(await memberService.IsMemberAsync(community.Id, member.Id));
    }

    [TestMethod]
    public async Task LeaveCommunityAsync_ByOwner_ThrowsException()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db);
        var communityService = new CommunityService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));

        // Act & Assert
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => memberService.LeaveCommunityAsync(community.Id, owner.Id));
        Assert.IsTrue(exception.Message.Contains("owner cannot leave"));
    }

    [TestMethod]
    public async Task GetMembersAsync_ReturnsAllMembers()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var owner = await CreateTestUserAsync(db, "owner");
        var member1 = await CreateTestUserAsync(db, "member1");
        var member2 = await CreateTestUserAsync(db, "member2");
        var communityService = new CommunityService(db);
        var memberService = new CommunityMemberService(db, new NullNotificationService());
        var community = await communityService.CreateCommunityAsync(owner.Id, new CreateCommunityRequest("Test", null));
        await memberService.JoinCommunityAsync(community.Id, member1.Id);
        await memberService.JoinCommunityAsync(community.Id, member2.Id);

        // Act
        var members = (await memberService.GetMembersAsync(community.Id)).ToList();

        // Assert
        Assert.AreEqual(3, members.Count);
        Assert.IsTrue(members.Any(m => m.Role == UserRole.Owner));
        Assert.AreEqual(2, members.Count(m => m.Role == UserRole.Member));
    }

    #endregion
}
