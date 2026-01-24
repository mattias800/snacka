using System.Reactive.Linq;
using Snacka.Client.Stores;
using Snacka.Client.Services;

namespace Snacka.Client.Tests.Stores;

public class PresenceStoreTests : IDisposable
{
    private readonly PresenceStore _store;

    public PresenceStoreTests()
    {
        _store = new PresenceStore();
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public void SetUserOnline_AddsUserAsOnline()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        _store.SetUserOnline(userId);

        // Assert
        Assert.True(_store.IsUserOnline(userId));
    }

    [Fact]
    public void SetUserOffline_SetsUserAsOffline()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _store.SetUserOnline(userId);

        // Act
        _store.SetUserOffline(userId);

        // Assert
        Assert.False(_store.IsUserOnline(userId));
    }

    [Fact]
    public void SetUserOffline_DoesNothingIfUserNotTracked()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act - should not throw
        _store.SetUserOffline(userId);

        // Assert
        Assert.False(_store.IsUserOnline(userId));
    }

    [Fact]
    public void SetOnlineUsers_SetsMultipleUsersOnline()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var userId3 = Guid.NewGuid();

        // Act
        _store.SetOnlineUsers(new[] { userId1, userId2, userId3 });

        // Assert
        Assert.True(_store.IsUserOnline(userId1));
        Assert.True(_store.IsUserOnline(userId2));
        Assert.True(_store.IsUserOnline(userId3));
    }

    [Fact]
    public void SetOnlineUsers_MarksOthersAsOffline()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        _store.SetUserOnline(userId1);
        _store.SetUserOnline(userId2);

        // Act - only userId1 in the new set
        _store.SetOnlineUsers(new[] { userId1 });

        // Assert
        Assert.True(_store.IsUserOnline(userId1));
        Assert.False(_store.IsUserOnline(userId2));
    }

    [Fact]
    public void OnlineUserIds_ReturnsOnlyOnlineUsers()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var userId3 = Guid.NewGuid();

        _store.SetUserOnline(userId1);
        _store.SetUserOnline(userId2);
        _store.SetUserOnline(userId3);
        _store.SetUserOffline(userId2);

        // Act
        var onlineUserIds = _store.OnlineUserIds.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(2, onlineUserIds.Count);
        Assert.Contains(userId1, onlineUserIds);
        Assert.Contains(userId3, onlineUserIds);
        Assert.DoesNotContain(userId2, onlineUserIds);
    }

    [Fact]
    public void SetConnectionStatus_UpdatesStatus()
    {
        // Act
        _store.SetConnectionStatus(ConnectionState.Connected);

        // Assert
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(ConnectionState.Connected, status);
    }

    [Fact]
    public void SetConnectionStatus_ToReconnecting()
    {
        // Act
        _store.SetConnectionStatus(ConnectionState.Reconnecting);

        // Assert
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(ConnectionState.Reconnecting, status);
    }

    [Fact]
    public void SetReconnectCountdown_UpdatesCountdown()
    {
        // Act
        _store.SetReconnectCountdown(10);

        // Assert
        var seconds = _store.ReconnectSecondsRemaining.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(10, seconds);
    }

    [Fact]
    public void IsUserOnline_ReturnsFalseForUnknownUser()
    {
        // Act
        var isOnline = _store.IsUserOnline(Guid.NewGuid());

        // Assert
        Assert.False(isOnline);
    }

    [Fact]
    public void Clear_RemovesAllUsersAndResetsStatus()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _store.SetUserOnline(userId);
        _store.SetConnectionStatus(ConnectionState.Connected);
        _store.SetReconnectCountdown(5);

        // Act
        _store.Clear();

        // Assert
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();
        var reconnectSeconds = _store.ReconnectSecondsRemaining.FirstAsync().GetAwaiter().GetResult();

        Assert.Empty(items);
        Assert.Equal(ConnectionState.Disconnected, status);
        Assert.Equal(0, reconnectSeconds);
    }

    [Fact]
    public void Items_ReturnsAllTrackedUsers()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();

        _store.SetUserOnline(userId1);
        _store.SetUserOnline(userId2);
        _store.SetUserOffline(userId2);

        // Act
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(2, items.Count);
        var user1 = items.First(u => u.UserId == userId1);
        var user2 = items.First(u => u.UserId == userId2);
        Assert.True(user1.IsOnline);
        Assert.False(user2.IsOnline);
    }

    [Fact]
    public void DefaultConnectionStatus_IsDisconnected()
    {
        // Act
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(ConnectionState.Disconnected, status);
    }

    [Fact]
    public void DefaultReconnectCountdown_IsZero()
    {
        // Act
        var seconds = _store.ReconnectSecondsRemaining.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Equal(0, seconds);
    }
}
