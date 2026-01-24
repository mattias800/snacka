using System.Reactive.Linq;
using Snacka.Client.Stores;
using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.Tests.Stores;

public class CommunityStoreTests : IDisposable
{
    private readonly CommunityStore _store;

    public CommunityStoreTests()
    {
        _store = new CommunityStore();
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    private static CommunityResponse CreateCommunity(
        Guid? id = null,
        string name = "Test Community",
        string? description = null,
        Guid? ownerId = null,
        int memberCount = 1)
    {
        var ownerGuid = ownerId ?? Guid.NewGuid();
        return new CommunityResponse(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            Description: description,
            Icon: null,
            OwnerId: ownerGuid,
            OwnerUsername: "owner",
            OwnerEffectiveDisplayName: "Owner",
            CreatedAt: DateTime.UtcNow,
            MemberCount: memberCount
        );
    }

    private static CommunityMemberResponse CreateMember(
        Guid? userId = null,
        string username = "testuser",
        UserRole role = UserRole.Member,
        bool isOnline = true)
    {
        return new CommunityMemberResponse(
            UserId: userId ?? Guid.NewGuid(),
            Username: username,
            DisplayName: null,
            DisplayNameOverride: null,
            EffectiveDisplayName: username,
            Avatar: null,
            IsOnline: isOnline,
            Role: role,
            JoinedAt: DateTime.UtcNow
        );
    }

    [Fact]
    public void SetCommunities_PopulatesStore()
    {
        // Arrange
        var communities = new[]
        {
            CreateCommunity(name: "Community 1"),
            CreateCommunity(name: "Community 2"),
            CreateCommunity(name: "Community 3")
        };

        // Act
        _store.SetCommunities(communities);

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void SetCommunities_ClearsExistingCommunities()
    {
        // Arrange
        _store.SetCommunities(new[] { CreateCommunity(name: "Old Community") });

        // Act
        _store.SetCommunities(new[] { CreateCommunity(name: "New Community") });

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        Assert.Single(items);
        Assert.Equal("New Community", items.First().Name);
    }

    [Fact]
    public void AddCommunity_AddsCommunityToStore()
    {
        // Arrange
        var community = CreateCommunity(name: "New Community");

        // Act
        _store.AddCommunity(community);

        // Assert
        var result = _store.GetCommunity(community.Id);
        Assert.NotNull(result);
        Assert.Equal("New Community", result.Name);
    }

    [Fact]
    public void UpdateCommunity_UpdatesExistingCommunity()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var original = CreateCommunity(id: communityId, name: "Original Name");
        _store.AddCommunity(original);

        var updated = CreateCommunity(id: communityId, name: "Updated Name");

        // Act
        _store.UpdateCommunity(updated);

        // Assert
        var result = _store.GetCommunity(communityId);
        Assert.NotNull(result);
        Assert.Equal("Updated Name", result.Name);
    }

    [Fact]
    public void RemoveCommunity_RemovesCommunityFromStore()
    {
        // Arrange
        var community = CreateCommunity();
        _store.AddCommunity(community);

        // Act
        _store.RemoveCommunity(community.Id);

        // Assert
        var result = _store.GetCommunity(community.Id);
        Assert.Null(result);
    }

    [Fact]
    public void RemoveCommunity_ClearsMembersForThatCommunity()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SetMembers(communityId, new[] { CreateMember(username: "member1") });

        // Act
        _store.RemoveCommunity(communityId);

        // Assert
        _store.SelectCommunity(communityId);
        var members = _store.CurrentCommunityMembers.FirstAsync().GetAwaiter().GetResult();
        Assert.Empty(members);
    }

    [Fact]
    public void RemoveCommunity_ClearsSelectionIfSelected()
    {
        // Arrange
        var community = CreateCommunity();
        _store.AddCommunity(community);
        _store.SelectCommunity(community.Id);

        // Act
        _store.RemoveCommunity(community.Id);

        // Assert
        var selectedId = _store.SelectedCommunityId.FirstAsync().GetAwaiter().GetResult();
        Assert.Null(selectedId);
    }

    [Fact]
    public void SelectCommunity_UpdatesSelection()
    {
        // Arrange
        var community = CreateCommunity();
        _store.AddCommunity(community);

        // Act
        _store.SelectCommunity(community.Id);

        // Assert
        var selectedId = _store.SelectedCommunityId.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(community.Id, selectedId);
    }

    [Fact]
    public void SelectedCommunity_ReturnsSelectedCommunityState()
    {
        // Arrange
        var community = CreateCommunity(name: "Selected Community");
        _store.AddCommunity(community);

        // Act
        _store.SelectCommunity(community.Id);

        // Assert
        var selected = _store.SelectedCommunity.FirstAsync().GetAwaiter().GetResult();
        Assert.NotNull(selected);
        Assert.Equal("Selected Community", selected.Name);
    }

    [Fact]
    public void SetMembers_PopulatesMembersForCommunity()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);

        var members = new[]
        {
            CreateMember(username: "member1"),
            CreateMember(username: "member2"),
            CreateMember(username: "member3")
        };

        // Act
        _store.SetMembers(communityId, members);

        // Assert
        var currentMembers = _store.CurrentCommunityMembers.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(3, currentMembers.Count);
    }

    [Fact]
    public void SetMembers_ClearsExistingMembersForCommunity()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);

        _store.SetMembers(communityId, new[] { CreateMember(username: "old-member") });

        // Act
        _store.SetMembers(communityId, new[] { CreateMember(username: "new-member") });

        // Assert
        var currentMembers = _store.CurrentCommunityMembers.FirstAsync().GetAwaiter().GetResult();
        Assert.Single(currentMembers);
        Assert.Equal("new-member", currentMembers.First().Username);
    }

    [Fact]
    public void AddMember_AddsMemberToCommunity()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);

        var member = CreateMember(username: "new-member");

        // Act
        _store.AddMember(communityId, member);

        // Assert
        var result = _store.GetMember(member.UserId);
        Assert.NotNull(result);
        Assert.Equal("new-member", result.Username);
    }

    [Fact]
    public void RemoveMember_RemovesMemberFromCommunity()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SetMembers(communityId, new[] { CreateMember(userId: userId) });

        // Act
        _store.RemoveMember(communityId, userId);

        // Assert
        var result = _store.GetMember(userId);
        Assert.Null(result);
    }

    [Fact]
    public void UpdateMemberOnlineStatus_UpdatesMemberStatus()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SetMembers(communityId, new[] { CreateMember(userId: userId, isOnline: true) });

        // Act
        _store.UpdateMemberOnlineStatus(userId, false);

        // Assert
        var result = _store.GetMember(userId);
        Assert.NotNull(result);
        Assert.False(result.IsOnline);
    }

    [Fact]
    public void UpdateMemberRole_UpdatesMemberRole()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SetMembers(communityId, new[] { CreateMember(userId: userId, role: UserRole.Member) });

        // Act
        _store.UpdateMemberRole(communityId, userId, UserRole.Admin);

        // Assert
        var result = _store.GetMember(userId);
        Assert.NotNull(result);
        Assert.Equal(UserRole.Admin, result.Role);
    }

    [Fact]
    public void UpdateMemberNickname_UpdatesMemberNickname()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SetMembers(communityId, new[] { CreateMember(userId: userId, username: "testuser") });

        // Act
        _store.UpdateMemberNickname(communityId, userId, "Cool Nickname");

        // Assert
        var result = _store.GetMember(userId);
        Assert.NotNull(result);
        Assert.Equal("Cool Nickname", result.DisplayNameOverride);
        Assert.Equal("Cool Nickname", result.EffectiveDisplayName);
    }

    [Fact]
    public void OnlineMembers_ReturnsOnlyOnlineMembers()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);

        var members = new[]
        {
            CreateMember(username: "online-member", isOnline: true),
            CreateMember(username: "offline-member", isOnline: false)
        };
        _store.SetMembers(communityId, members);

        // Act
        var onlineMembers = _store.OnlineMembers.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Single(onlineMembers);
        Assert.Equal("online-member", onlineMembers.First().Username);
    }

    [Fact]
    public void OfflineMembers_ReturnsOnlyOfflineMembers()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);

        var members = new[]
        {
            CreateMember(username: "online-member", isOnline: true),
            CreateMember(username: "offline-member", isOnline: false)
        };
        _store.SetMembers(communityId, members);

        // Act
        var offlineMembers = _store.OfflineMembers.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Single(offlineMembers);
        Assert.Equal("offline-member", offlineMembers.First().Username);
    }

    [Fact]
    public void GetCurrentUserRole_ReturnsUserRole()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);
        _store.SetMembers(communityId, new[] { CreateMember(userId: userId, role: UserRole.Admin) });

        // Act
        var role = _store.GetCurrentUserRole(userId);

        // Assert
        Assert.Equal(UserRole.Admin, role);
    }

    [Fact]
    public void GetCurrentUserRole_ReturnsNullWhenNoCommunitySelected()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        var role = _store.GetCurrentUserRole(userId);

        // Assert
        Assert.Null(role);
    }

    [Fact]
    public void Clear_RemovesAllCommunitiesAndMembers()
    {
        // Arrange
        var communityId = Guid.NewGuid();
        var community = CreateCommunity(id: communityId);
        _store.AddCommunity(community);
        _store.SelectCommunity(communityId);
        _store.SetMembers(communityId, new[] { CreateMember() });

        // Act
        _store.Clear();

        // Assert
        var communities = _store.Items.FirstAsync().GetAwaiter().GetResult();
        var selectedId = _store.SelectedCommunityId.FirstAsync().GetAwaiter().GetResult();

        Assert.Empty(communities);
        Assert.Null(selectedId);
    }

    [Fact]
    public void GetCommunity_ReturnsNullForNonExistentCommunity()
    {
        // Act
        var result = _store.GetCommunity(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetMember_ReturnsNullForNonExistentMember()
    {
        // Act
        var result = _store.GetMember(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }
}
