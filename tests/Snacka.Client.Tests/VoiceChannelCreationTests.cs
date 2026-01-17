using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;
using Snacka.Client.Views;
using Snacka.Shared.Models;
using Moq;

namespace Snacka.Client.Tests;

/// <summary>
/// E2E headless tests for voice channel creation functionality.
/// These tests verify that the UI correctly creates and displays voice channels.
/// </summary>
public class VoiceChannelCreationTests
{
    private Mock<IApiClient> CreateMockApiClient()
    {
        var mock = new Mock<IApiClient>();

        // Setup default community
        var community = new CommunityResponse(
            Id: Guid.NewGuid(),
            Name: "Test Community",
            Description: null,
            Icon: null,
            OwnerId: Guid.NewGuid(),
            OwnerUsername: "owner",
            OwnerEffectiveDisplayName: "owner",
            CreatedAt: DateTime.UtcNow,
            MemberCount: 1
        );

        // Setup default text channel
        var generalChannel = new ChannelResponse(
            Id: Guid.NewGuid(),
            Name: "general",
            Topic: null,
            CommunityId: community.Id,
            Type: ChannelType.Text,
            Position: 0,
            CreatedAt: DateTime.UtcNow
        );

        var channels = new List<ChannelResponse> { generalChannel };

        mock.Setup(x => x.GetCommunitiesAsync())
            .ReturnsAsync(ApiResult<List<CommunityResponse>>.Ok(new List<CommunityResponse> { community }));

        mock.Setup(x => x.GetChannelsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => ApiResult<List<ChannelResponse>>.Ok(channels.ToList()));

        mock.Setup(x => x.GetMembersAsync(It.IsAny<Guid>()))
            .ReturnsAsync(ApiResult<List<CommunityMemberResponse>>.Ok(new List<CommunityMemberResponse>()));

        mock.Setup(x => x.GetMessagesAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(ApiResult<List<MessageResponse>>.Ok(new List<MessageResponse>()));

        // Create voice channel - generates unique names (with small delay to simulate network)
        mock.Setup(x => x.CreateChannelAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), ChannelType.Voice))
            .Returns(async (Guid communityId, string name, string? topic, ChannelType type) =>
            {
                await Task.Delay(50); // Simulate network latency
                var newChannel = new ChannelResponse(
                    Id: Guid.NewGuid(),
                    Name: name,
                    Topic: topic,
                    CommunityId: communityId,
                    Type: ChannelType.Voice,
                    Position: channels.Count,
                    CreatedAt: DateTime.UtcNow
                );
                channels.Add(newChannel);
                return ApiResult<ChannelResponse>.Ok(newChannel);
            });

        // Create text channel
        mock.Setup(x => x.CreateChannelAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), ChannelType.Text))
            .ReturnsAsync((Guid communityId, string name, string? topic, ChannelType type) =>
            {
                var newChannel = new ChannelResponse(
                    Id: Guid.NewGuid(),
                    Name: name,
                    Topic: topic,
                    CommunityId: communityId,
                    Type: ChannelType.Text,
                    Position: channels.Count,
                    CreatedAt: DateTime.UtcNow
                );
                channels.Add(newChannel);
                return ApiResult<ChannelResponse>.Ok(newChannel);
            });

        return mock;
    }

    private Mock<ISignalRService> CreateMockSignalR()
    {
        var mock = new Mock<ISignalRService>();
        mock.Setup(x => x.ConnectAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.JoinServerAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.JoinChannelAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.LeaveChannelAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.GetVoiceParticipantsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<VoiceParticipantResponse>());
        return mock;
    }

    private Mock<IWebRtcService> CreateMockWebRtc()
    {
        var mock = new Mock<IWebRtcService>();
        mock.Setup(x => x.JoinVoiceChannelAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<VoiceParticipantResponse>>()))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.LeaveVoiceChannelAsync())
            .Returns(Task.CompletedTask);
        return mock;
    }

    private Mock<IScreenCaptureService> CreateMockScreenCapture()
    {
        var mock = new Mock<IScreenCaptureService>();
        return mock;
    }

    private Mock<ISettingsStore> CreateMockSettingsStore()
    {
        var mock = new Mock<ISettingsStore>();
        mock.Setup(x => x.Settings).Returns(new UserSettings());
        return mock;
    }

    private Mock<IAudioDeviceService> CreateMockAudioDeviceService()
    {
        var mock = new Mock<IAudioDeviceService>();
        mock.Setup(x => x.GetInputDevices()).Returns(new List<string>());
        mock.Setup(x => x.GetOutputDevices()).Returns(new List<string>());
        return mock;
    }

    private Mock<IControllerStreamingService> CreateMockControllerStreamingService()
    {
        var mock = new Mock<IControllerStreamingService>();
        return mock;
    }

    private Mock<IControllerHostService> CreateMockControllerHostService()
    {
        var mock = new Mock<IControllerHostService>();
        return mock;
    }

    private AuthResponse CreateTestAuth()
    {
        return new AuthResponse(
            UserId: Guid.NewGuid(),
            Username: "testuser",
            Email: "test@example.com",
            IsServerAdmin: false,
            AccessToken: "test-token",
            RefreshToken: "test-refresh",
            ExpiresAt: DateTime.UtcNow.AddHours(1)
        );
    }

    private MainAppViewModel CreateViewModel(
        Mock<IApiClient> mockApi,
        Mock<ISignalRService> mockSignalR,
        AuthResponse auth)
    {
        return new MainAppViewModel(
            mockApi.Object,
            mockSignalR.Object,
            CreateMockWebRtc().Object,
            CreateMockScreenCapture().Object,
            CreateMockSettingsStore().Object,
            CreateMockAudioDeviceService().Object,
            CreateMockControllerStreamingService().Object,
            CreateMockControllerHostService().Object,
            "http://localhost:5000",
            auth,
            onLogout: () => { }
        );
    }

    [AvaloniaFact]
    public async Task CreateVoiceChannel_WhenButtonClicked_ChannelAppearsInList()
    {
        // Arrange
        var mockApi = CreateMockApiClient();
        var mockSignalR = CreateMockSignalR();
        var auth = CreateTestAuth();

        var viewModel = CreateViewModel(mockApi, mockSignalR, auth);

        // Wait for initialization
        await Task.Delay(200);

        // Create a window with the MainAppView
        var mainAppView = new MainAppView { DataContext = viewModel };
        var window = new Window { Content = mainAppView };
        window.Show();

        // Wait for communities and channels to load
        await Task.Delay(300);

        // Assert - Should have one community selected
        Assert.NotNull(viewModel.SelectedCommunity);
        Assert.Equal("Test Community", viewModel.SelectedCommunity.Name);

        // Act - Get initial voice channel count
        var initialVoiceChannelCount = viewModel.VoiceChannelViewModels.Count();
        Assert.Equal(0, initialVoiceChannelCount);

        // Execute the create voice channel command
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe();

        // Wait for the channel to be created
        await Task.Delay(300);

        // Assert - Voice channel count should have increased by 1
        var finalVoiceChannelCount = viewModel.VoiceChannelViewModels.Count();
        Assert.Equal(1, finalVoiceChannelCount);

        // Assert - The new channel should have the correct name
        var newChannel = viewModel.VoiceChannelViewModels.FirstOrDefault(c => c.Name == "Voice 1");
        Assert.NotNull(newChannel);

        // Cleanup
        window.Close();
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task CreateMultipleVoiceChannels_SequentialClicks_AllChannelsHaveUniqueNames()
    {
        // Arrange
        var mockApi = CreateMockApiClient();
        var mockSignalR = CreateMockSignalR();
        var auth = CreateTestAuth();

        var viewModel = CreateViewModel(mockApi, mockSignalR, auth);

        // Wait for initialization
        await Task.Delay(200);

        // Create a window with the MainAppView
        var mainAppView = new MainAppView { DataContext = viewModel };
        var window = new Window { Content = mainAppView };
        window.Show();

        // Wait for communities and channels to load
        await Task.Delay(300);

        // Act - Create 3 voice channels sequentially
        for (int i = 0; i < 3; i++)
        {
            viewModel.CreateVoiceChannelCommand.Execute().Subscribe();
            // Wait for each channel creation to complete (IsLoading guard)
            await Task.Delay(300);
        }

        // Wait a bit more for all updates to propagate
        await Task.Delay(200);

        // Assert - Should have 3 voice channels
        var voiceChannels = viewModel.VoiceChannelViewModels.ToList();
        Assert.Equal(3, voiceChannels.Count);

        // Assert - All channel names should be unique
        var uniqueNames = voiceChannels.Select(c => c.Name).Distinct().Count();
        Assert.Equal(3, uniqueNames);

        // Assert - Channel names should follow the naming pattern
        Assert.Contains(voiceChannels, c => c.Name == "Voice 1");
        Assert.Contains(voiceChannels, c => c.Name == "Voice 2");
        Assert.Contains(voiceChannels, c => c.Name == "Voice 3");

        // Cleanup
        window.Close();
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task CreateVoiceChannel_AfterCreation_ChannelHasCorrectType()
    {
        // Arrange
        var mockApi = CreateMockApiClient();
        var mockSignalR = CreateMockSignalR();
        var auth = CreateTestAuth();

        var viewModel = CreateViewModel(mockApi, mockSignalR, auth);

        // Wait for initialization
        await Task.Delay(200);

        var mainAppView = new MainAppView { DataContext = viewModel };
        var window = new Window { Content = mainAppView };
        window.Show();

        await Task.Delay(300);

        // Act - Create a voice channel
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe();
        await Task.Delay(300);

        // Assert - Channel should be in VoiceChannelViewModels
        Assert.Single(viewModel.VoiceChannelViewModels);
        Assert.Equal("Voice 1", viewModel.VoiceChannelViewModels.First().Name);

        // TextChannels should only have the default 'general' channel
        Assert.Single(viewModel.TextChannels);
        Assert.Equal("general", viewModel.TextChannels.First().Name);

        // Cleanup
        window.Close();
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task CreateVoiceChannel_RapidClicks_OnlyCreatesOneAtATime()
    {
        // This test verifies the IsLoading guard prevents duplicate creation
        // Arrange
        var mockApi = CreateMockApiClient();
        var mockSignalR = CreateMockSignalR();
        var auth = CreateTestAuth();

        var viewModel = CreateViewModel(mockApi, mockSignalR, auth);

        await Task.Delay(200);

        var mainAppView = new MainAppView { DataContext = viewModel };
        var window = new Window { Content = mainAppView };
        window.Show();

        await Task.Delay(300);

        // Act - Try to create multiple voice channels rapidly (without waiting)
        // Due to IsLoading guard, only the first should execute
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe();
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe(); // Should be blocked
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe(); // Should be blocked

        // Wait for first creation to complete
        await Task.Delay(300);

        // Now create two more (these should work since IsLoading is false)
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe();
        await Task.Delay(300);
        viewModel.CreateVoiceChannelCommand.Execute().Subscribe();
        await Task.Delay(300);

        // Assert - Should have exactly 3 voice channels (not 5)
        var voiceChannels = viewModel.VoiceChannelViewModels.ToList();
        Assert.Equal(3, voiceChannels.Count);

        // Cleanup
        window.Close();
        viewModel.Dispose();
    }
}

/// <summary>
/// Extension methods for finding controls in the visual tree
/// </summary>
public static class VisualTreeExtensions
{
    public static T? FindDescendantOfType<T>(this Control control) where T : Control
    {
        return control.GetVisualDescendants().OfType<T>().FirstOrDefault();
    }
}
