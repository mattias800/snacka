using System.Reactive.Linq;
using Snacka.Client.Stores;
using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.Tests.Stores;

public class ChannelStoreTests : IDisposable
{
    private readonly ChannelStore _store;

    public ChannelStoreTests()
    {
        _store = new ChannelStore();
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    private static ChannelResponse CreateChannel(
        Guid? id = null,
        string name = "test-channel",
        ChannelType type = ChannelType.Text,
        Guid? communityId = null,
        int position = 0,
        int unreadCount = 0)
    {
        return new ChannelResponse(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            Topic: null,
            Type: type,
            CommunityId: communityId ?? Guid.NewGuid(),
            Position: position,
            UnreadCount: unreadCount,
            CreatedAt: DateTime.UtcNow
        );
    }

    [Fact]
    public void SetChannels_PopulatesStore()
    {
        // Arrange
        var channels = new[]
        {
            CreateChannel(name: "channel-1"),
            CreateChannel(name: "channel-2"),
            CreateChannel(name: "channel-3")
        };

        // Act
        _store.SetChannels(channels);

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void SetChannels_ClearsExistingChannels()
    {
        // Arrange
        var initialChannels = new[] { CreateChannel(name: "old-channel") };
        _store.SetChannels(initialChannels);

        var newChannels = new[] { CreateChannel(name: "new-channel") };

        // Act
        _store.SetChannels(newChannels);

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        Assert.Single(items);
        Assert.Equal("new-channel", items.First().Name);
    }

    [Fact]
    public void AddChannel_AddsToStore()
    {
        // Arrange
        var channel = CreateChannel(name: "new-channel");

        // Act
        _store.AddChannel(channel);

        // Assert
        var result = _store.GetChannel(channel.Id);
        Assert.NotNull(result);
        Assert.Equal("new-channel", result.Name);
    }

    [Fact]
    public void UpdateChannel_UpdatesExistingChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var original = CreateChannel(id: channelId, name: "original-name");
        _store.AddChannel(original);

        var updated = CreateChannel(id: channelId, name: "updated-name");

        // Act
        _store.UpdateChannel(updated);

        // Assert
        var result = _store.GetChannel(channelId);
        Assert.NotNull(result);
        Assert.Equal("updated-name", result.Name);
    }

    [Fact]
    public void UpdateChannel_PreservesUnreadCount()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var original = CreateChannel(id: channelId, name: "channel", unreadCount: 5);
        _store.AddChannel(original);
        _store.UpdateUnreadCount(channelId, 10);

        var updated = CreateChannel(id: channelId, name: "channel-updated", unreadCount: 0);

        // Act
        _store.UpdateChannel(updated);

        // Assert
        var result = _store.GetChannel(channelId);
        Assert.NotNull(result);
        Assert.Equal(10, result.UnreadCount); // Should preserve the 10, not reset to 0
    }

    [Fact]
    public void RemoveChannel_RemovesFromStore()
    {
        // Arrange
        var channel = CreateChannel();
        _store.AddChannel(channel);

        // Act
        _store.RemoveChannel(channel.Id);

        // Assert
        var result = _store.GetChannel(channel.Id);
        Assert.Null(result);
    }

    [Fact]
    public void RemoveChannel_ClearsSelectionIfSelected()
    {
        // Arrange
        var channel = CreateChannel();
        _store.AddChannel(channel);
        _store.SelectChannel(channel.Id);

        // Act
        _store.RemoveChannel(channel.Id);

        // Assert
        var selectedId = _store.SelectedChannelId.FirstAsync().GetAwaiter().GetResult();
        Assert.Null(selectedId);
    }

    [Fact]
    public void SelectChannel_UpdatesSelection()
    {
        // Arrange
        var channel = CreateChannel();
        _store.AddChannel(channel);

        // Act
        _store.SelectChannel(channel.Id);

        // Assert
        var selectedId = _store.SelectedChannelId.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(channel.Id, selectedId);
    }

    [Fact]
    public void SelectedChannel_ReturnsSelectedChannelState()
    {
        // Arrange
        var channel = CreateChannel(name: "selected-channel");
        _store.AddChannel(channel);

        // Act
        _store.SelectChannel(channel.Id);

        // Assert
        var selected = _store.SelectedChannel.FirstAsync().GetAwaiter().GetResult();
        Assert.NotNull(selected);
        Assert.Equal("selected-channel", selected.Name);
    }

    [Fact]
    public void TextChannels_ReturnsOnlyTextChannels()
    {
        // Arrange
        var textChannel1 = CreateChannel(name: "text-1", type: ChannelType.Text, position: 1);
        var textChannel2 = CreateChannel(name: "text-2", type: ChannelType.Text, position: 2);
        var voiceChannel = CreateChannel(name: "voice-1", type: ChannelType.Voice, position: 3);

        _store.SetChannels(new[] { textChannel1, textChannel2, voiceChannel });

        // Act
        var textChannels = _store.TextChannels.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(2, textChannels.Count);
        Assert.All(textChannels, c => Assert.Equal(ChannelType.Text, c.Type));
    }

    [Fact]
    public void VoiceChannels_ReturnsOnlyVoiceChannels()
    {
        // Arrange
        var textChannel = CreateChannel(name: "text-1", type: ChannelType.Text);
        var voiceChannel1 = CreateChannel(name: "voice-1", type: ChannelType.Voice, position: 1);
        var voiceChannel2 = CreateChannel(name: "voice-2", type: ChannelType.Voice, position: 2);

        _store.SetChannels(new[] { textChannel, voiceChannel1, voiceChannel2 });

        // Act
        var voiceChannels = _store.VoiceChannels.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(2, voiceChannels.Count);
        Assert.All(voiceChannels, c => Assert.Equal(ChannelType.Voice, c.Type));
    }

    [Fact]
    public void TextChannels_SortedByPosition()
    {
        // Arrange
        var channel1 = CreateChannel(name: "channel-a", type: ChannelType.Text, position: 3);
        var channel2 = CreateChannel(name: "channel-b", type: ChannelType.Text, position: 1);
        var channel3 = CreateChannel(name: "channel-c", type: ChannelType.Text, position: 2);

        _store.SetChannels(new[] { channel1, channel2, channel3 });

        // Act
        var textChannels = _store.TextChannels.FirstAsync().GetAwaiter().GetResult().ToList();

        // Assert
        Assert.Equal("channel-b", textChannels[0].Name); // position 1
        Assert.Equal("channel-c", textChannels[1].Name); // position 2
        Assert.Equal("channel-a", textChannels[2].Name); // position 3
    }

    [Fact]
    public void UpdateUnreadCount_UpdatesCount()
    {
        // Arrange
        var channel = CreateChannel(unreadCount: 0);
        _store.AddChannel(channel);

        // Act
        _store.UpdateUnreadCount(channel.Id, 5);

        // Assert
        var result = _store.GetChannel(channel.Id);
        Assert.NotNull(result);
        Assert.Equal(5, result.UnreadCount);
    }

    [Fact]
    public void IncrementUnreadCount_IncrementsCount()
    {
        // Arrange
        var channel = CreateChannel(unreadCount: 3);
        _store.AddChannel(channel);

        // Act
        _store.IncrementUnreadCount(channel.Id);

        // Assert
        var result = _store.GetChannel(channel.Id);
        Assert.NotNull(result);
        Assert.Equal(4, result.UnreadCount);
    }

    [Fact]
    public void TotalUnreadCount_SumsAllChannels()
    {
        // Arrange
        var channel1 = CreateChannel(unreadCount: 5);
        var channel2 = CreateChannel(unreadCount: 3);
        var channel3 = CreateChannel(unreadCount: 2);

        _store.SetChannels(new[] { channel1, channel2, channel3 });

        // Act
        var total = _store.TotalUnreadCount.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(10, total);
    }

    [Fact]
    public void ReorderChannels_UpdatesPositions()
    {
        // Arrange
        var channelId1 = Guid.NewGuid();
        var channelId2 = Guid.NewGuid();

        var channel1 = CreateChannel(id: channelId1, name: "channel-1", position: 1);
        var channel2 = CreateChannel(id: channelId2, name: "channel-2", position: 2);

        _store.SetChannels(new[] { channel1, channel2 });

        // Act - swap positions
        var reordered = new[]
        {
            CreateChannel(id: channelId1, name: "channel-1", position: 2),
            CreateChannel(id: channelId2, name: "channel-2", position: 1)
        };
        _store.ReorderChannels(reordered);

        // Assert
        var result1 = _store.GetChannel(channelId1);
        var result2 = _store.GetChannel(channelId2);

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(2, result1.Position);
        Assert.Equal(1, result2.Position);
    }

    [Fact]
    public void Clear_RemovesAllChannelsAndClearsSelection()
    {
        // Arrange
        var channel = CreateChannel();
        _store.AddChannel(channel);
        _store.SelectChannel(channel.Id);

        // Act
        _store.Clear();

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        var selectedId = _store.SelectedChannelId.FirstAsync().GetAwaiter().GetResult();

        Assert.Empty(items);
        Assert.Null(selectedId);
    }

    [Fact]
    public void ClearForCommunity_RemovesOnlyChannelsForThatCommunity()
    {
        // Arrange
        var communityId1 = Guid.NewGuid();
        var communityId2 = Guid.NewGuid();

        var channel1 = CreateChannel(name: "community1-channel", communityId: communityId1);
        var channel2 = CreateChannel(name: "community2-channel", communityId: communityId2);

        _store.SetChannels(new[] { channel1, channel2 });

        // Act
        _store.ClearForCommunity(communityId1);

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        Assert.Single(items);
        Assert.Equal("community2-channel", items.First().Name);
    }

    [Fact]
    public void GetChannel_ReturnsNullForNonExistentChannel()
    {
        // Act
        var result = _store.GetChannel(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }
}
