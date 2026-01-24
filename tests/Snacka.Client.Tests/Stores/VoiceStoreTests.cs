using System.Reactive.Linq;
using Snacka.Client.Stores;
using Snacka.Client.Services;

namespace Snacka.Client.Tests.Stores;

public class VoiceStoreTests : IDisposable
{
    private readonly VoiceStore _store;

    public VoiceStoreTests()
    {
        _store = new VoiceStore();
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    private static VoiceParticipantResponse CreateParticipant(
        Guid? id = null,
        Guid? userId = null,
        Guid? channelId = null,
        string username = "testuser",
        bool isMuted = false,
        bool isDeafened = false,
        bool isScreenSharing = false,
        bool isCameraOn = false)
    {
        return new VoiceParticipantResponse(
            Id: id ?? Guid.NewGuid(),
            UserId: userId ?? Guid.NewGuid(),
            Username: username,
            ChannelId: channelId ?? Guid.NewGuid(),
            IsMuted: isMuted,
            IsDeafened: isDeafened,
            IsServerMuted: false,
            IsServerDeafened: false,
            IsScreenSharing: isScreenSharing,
            ScreenShareHasAudio: false,
            IsCameraOn: isCameraOn,
            JoinedAt: DateTime.UtcNow,
            IsGamingStation: false,
            GamingStationMachineId: null
        );
    }

    [Fact]
    public void SetParticipants_PopulatesStore()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var participants = new[]
        {
            CreateParticipant(channelId: channelId, username: "user1"),
            CreateParticipant(channelId: channelId, username: "user2"),
            CreateParticipant(channelId: channelId, username: "user3")
        };

        // Act
        _store.SetParticipants(channelId, participants);

        // Assert
        var items = _store.GetParticipantsForChannel(channelId);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void SetParticipants_ClearsExistingParticipantsForChannel()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _store.SetParticipants(channelId, new[] { CreateParticipant(channelId: channelId, username: "old-user") });

        // Act
        _store.SetParticipants(channelId, new[] { CreateParticipant(channelId: channelId, username: "new-user") });

        // Assert
        var items = _store.GetParticipantsForChannel(channelId);
        Assert.Single(items);
        Assert.Equal("new-user", items.First().Username);
    }

    [Fact]
    public void AddParticipant_AddsToStore()
    {
        // Arrange
        var participant = CreateParticipant(username: "new-participant");

        // Act
        _store.AddParticipant(participant);

        // Assert
        var items = _store.GetParticipantsForChannel(participant.ChannelId);
        Assert.Single(items);
        Assert.Equal("new-participant", items.First().Username);
    }

    [Fact]
    public void RemoveParticipant_RemovesFromStore()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = CreateParticipant(userId: userId, channelId: channelId);
        _store.AddParticipant(participant);

        // Act
        _store.RemoveParticipant(channelId, userId);

        // Assert
        var items = _store.GetParticipantsForChannel(channelId);
        Assert.Empty(items);
    }

    [Fact]
    public void SetCurrentChannel_UpdatesCurrentChannelId()
    {
        // Arrange
        var channelId = Guid.NewGuid();

        // Act
        _store.SetCurrentChannel(channelId);

        // Assert
        var currentChannelId = _store.CurrentChannelId.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(channelId, currentChannelId);
    }

    [Fact]
    public void SetCurrentChannel_ToNull_SetsConnectionStatusToDisconnected()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _store.SetCurrentChannel(channelId);
        _store.SetConnectionStatus(VoiceConnectionStatus.Connected);

        // Act
        _store.SetCurrentChannel(null);

        // Assert
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(VoiceConnectionStatus.Disconnected, status);
    }

    [Fact]
    public void SetConnectionStatus_UpdatesStatus()
    {
        // Act
        _store.SetConnectionStatus(VoiceConnectionStatus.Connected);

        // Assert
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(VoiceConnectionStatus.Connected, status);
    }

    [Fact]
    public void CurrentChannelParticipants_ReturnsParticipantsForCurrentChannel()
    {
        // Arrange
        var channelId1 = Guid.NewGuid();
        var channelId2 = Guid.NewGuid();

        _store.AddParticipant(CreateParticipant(channelId: channelId1, username: "user1"));
        _store.AddParticipant(CreateParticipant(channelId: channelId2, username: "user2"));

        // Act
        _store.SetCurrentChannel(channelId1);
        var participants = _store.CurrentChannelParticipants.FirstAsync().GetAwaiter().GetResult();

        // Assert
        Assert.Single(participants);
        Assert.Equal("user1", participants.First().Username);
    }

    [Fact]
    public void UpdateVoiceState_UpdatesParticipantState()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = CreateParticipant(userId: userId, channelId: channelId, isMuted: false);
        _store.AddParticipant(participant);

        // Act
        _store.UpdateVoiceState(channelId, userId, new VoiceStateUpdate(IsMuted: true));

        // Assert
        var items = _store.GetParticipantsForChannel(channelId);
        Assert.Single(items);
        Assert.True(items.First().IsMuted);
    }

    [Fact]
    public void UpdateSpeakingState_UpdatesParticipantSpeakingState()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = CreateParticipant(userId: userId, channelId: channelId);
        _store.AddParticipant(participant);

        // Act
        _store.UpdateSpeakingState(channelId, userId, true);

        // Assert
        var items = _store.GetParticipantsForChannel(channelId);
        Assert.Single(items);
        Assert.True(items.First().IsSpeaking);
    }

    [Fact]
    public void UpdateServerVoiceState_UpdatesServerMutedAndDeafened()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participant = CreateParticipant(userId: userId, channelId: channelId);
        _store.AddParticipant(participant);

        // Act
        _store.UpdateServerVoiceState(channelId, userId, isServerMuted: true, isServerDeafened: true);

        // Assert
        var items = _store.GetParticipantsForChannel(channelId);
        Assert.Single(items);
        Assert.True(items.First().IsServerMuted);
        Assert.True(items.First().IsServerDeafened);
    }

    [Fact]
    public void SetLocalMuted_UpdatesLocalMutedState()
    {
        // Act
        _store.SetLocalMuted(true);

        // Assert
        var isMuted = _store.IsMuted.FirstAsync().GetAwaiter().GetResult();
        Assert.True(isMuted);
    }

    [Fact]
    public void SetLocalDeafened_UpdatesLocalDeafenedState()
    {
        // Act
        _store.SetLocalDeafened(true);

        // Assert
        var isDeafened = _store.IsDeafened.FirstAsync().GetAwaiter().GetResult();
        Assert.True(isDeafened);
    }

    [Fact]
    public void SetLocalCameraOn_UpdatesLocalCameraState()
    {
        // Act
        _store.SetLocalCameraOn(true);

        // Assert
        var isCameraOn = _store.IsCameraOn.FirstAsync().GetAwaiter().GetResult();
        Assert.True(isCameraOn);
    }

    [Fact]
    public void SetLocalScreenSharing_UpdatesLocalScreenSharingState()
    {
        // Act
        _store.SetLocalScreenSharing(true);

        // Assert
        var isScreenSharing = _store.IsScreenSharing.FirstAsync().GetAwaiter().GetResult();
        Assert.True(isScreenSharing);
    }

    [Fact]
    public void SetLocalSpeaking_UpdatesLocalSpeakingState()
    {
        // Act
        _store.SetLocalSpeaking(true);

        // Assert
        var isSpeaking = _store.IsSpeaking.FirstAsync().GetAwaiter().GetResult();
        Assert.True(isSpeaking);
    }

    [Fact]
    public void SetVoiceOnOtherDevice_UpdatesOtherDeviceState()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var channelName = "test-voice-channel";

        // Act
        _store.SetVoiceOnOtherDevice(channelId, channelName);

        // Assert
        var (resultChannelId, resultChannelName) = _store.VoiceOnOtherDevice.FirstAsync().GetAwaiter().GetResult();
        Assert.Equal(channelId, resultChannelId);
        Assert.Equal(channelName, resultChannelName);
    }

    [Fact]
    public void GetLocalParticipant_ReturnsLocalUserParticipant()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var localUserId = Guid.NewGuid();
        var participant = CreateParticipant(userId: localUserId, channelId: channelId, username: "local-user");

        _store.SetCurrentChannel(channelId);
        _store.AddParticipant(participant);

        // Act
        var localParticipant = _store.GetLocalParticipant(localUserId);

        // Assert
        Assert.NotNull(localParticipant);
        Assert.Equal("local-user", localParticipant.Username);
    }

    [Fact]
    public void GetLocalParticipant_ReturnsNullWhenNotInChannel()
    {
        // Arrange
        var localUserId = Guid.NewGuid();

        // Act
        var localParticipant = _store.GetLocalParticipant(localUserId);

        // Assert
        Assert.Null(localParticipant);
    }

    [Fact]
    public void ClearChannel_RemovesOnlyParticipantsForThatChannel()
    {
        // Arrange
        var channelId1 = Guid.NewGuid();
        var channelId2 = Guid.NewGuid();

        _store.AddParticipant(CreateParticipant(channelId: channelId1, username: "user1"));
        _store.AddParticipant(CreateParticipant(channelId: channelId2, username: "user2"));

        // Act
        _store.ClearChannel(channelId1);

        // Assert
        var channel1Participants = _store.GetParticipantsForChannel(channelId1);
        var channel2Participants = _store.GetParticipantsForChannel(channelId2);

        Assert.Empty(channel1Participants);
        Assert.Single(channel2Participants);
    }

    [Fact]
    public void Clear_RemovesAllStateAndResetsToDefaults()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        _store.SetCurrentChannel(channelId);
        _store.SetConnectionStatus(VoiceConnectionStatus.Connected);
        _store.SetLocalMuted(true);
        _store.SetLocalDeafened(true);
        _store.SetLocalCameraOn(true);
        _store.SetLocalScreenSharing(true);
        _store.SetLocalSpeaking(true);
        _store.SetVoiceOnOtherDevice(Guid.NewGuid(), "channel");
        _store.AddParticipant(CreateParticipant(channelId: channelId));

        // Act
        _store.Clear();

        // Assert
        var currentChannelId = _store.CurrentChannelId.FirstAsync().GetAwaiter().GetResult();
        var status = _store.ConnectionStatus.FirstAsync().GetAwaiter().GetResult();
        var isMuted = _store.IsMuted.FirstAsync().GetAwaiter().GetResult();
        var isDeafened = _store.IsDeafened.FirstAsync().GetAwaiter().GetResult();
        var isCameraOn = _store.IsCameraOn.FirstAsync().GetAwaiter().GetResult();
        var isScreenSharing = _store.IsScreenSharing.FirstAsync().GetAwaiter().GetResult();
        var isSpeaking = _store.IsSpeaking.FirstAsync().GetAwaiter().GetResult();
        var (otherChannelId, otherChannelName) = _store.VoiceOnOtherDevice.FirstAsync().GetAwaiter().GetResult();
        var items = _store.Items.FirstAsync().GetAwaiter().GetResult();

        Assert.Null(currentChannelId);
        Assert.Equal(VoiceConnectionStatus.Disconnected, status);
        Assert.False(isMuted);
        Assert.False(isDeafened);
        Assert.False(isCameraOn);
        Assert.False(isScreenSharing);
        Assert.False(isSpeaking);
        Assert.Null(otherChannelId);
        Assert.Null(otherChannelName);
        Assert.Empty(items);
    }

    [Fact]
    public void GetParticipantsForChannel_SortedByJoinedAt()
    {
        // Arrange
        var channelId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var p1 = new VoiceParticipantResponse(
            Id: Guid.NewGuid(), UserId: Guid.NewGuid(), Username: "third",
            ChannelId: channelId, IsMuted: false, IsDeafened: false,
            IsServerMuted: false, IsServerDeafened: false, IsScreenSharing: false,
            ScreenShareHasAudio: false, IsCameraOn: false,
            JoinedAt: now.AddMinutes(2), IsGamingStation: false, GamingStationMachineId: null
        );

        var p2 = new VoiceParticipantResponse(
            Id: Guid.NewGuid(), UserId: Guid.NewGuid(), Username: "first",
            ChannelId: channelId, IsMuted: false, IsDeafened: false,
            IsServerMuted: false, IsServerDeafened: false, IsScreenSharing: false,
            ScreenShareHasAudio: false, IsCameraOn: false,
            JoinedAt: now, IsGamingStation: false, GamingStationMachineId: null
        );

        var p3 = new VoiceParticipantResponse(
            Id: Guid.NewGuid(), UserId: Guid.NewGuid(), Username: "second",
            ChannelId: channelId, IsMuted: false, IsDeafened: false,
            IsServerMuted: false, IsServerDeafened: false, IsScreenSharing: false,
            ScreenShareHasAudio: false, IsCameraOn: false,
            JoinedAt: now.AddMinutes(1), IsGamingStation: false, GamingStationMachineId: null
        );

        _store.SetParticipants(channelId, new[] { p1, p2, p3 });

        // Act
        var participants = _store.GetParticipantsForChannel(channelId).ToList();

        // Assert
        Assert.Equal(3, participants.Count);
        Assert.Equal("first", participants[0].Username);
        Assert.Equal("second", participants[1].Username);
        Assert.Equal("third", participants[2].Username);
    }
}
