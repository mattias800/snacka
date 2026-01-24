using Avalonia.Threading;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;

namespace Snacka.Client.Services;

/// <summary>
/// Handles SignalR events for gaming station commands (when this client is a gaming station).
/// </summary>
public interface IGamingStationCommandHandler : IDisposable
{
    /// <summary>
    /// Initializes the handler with required dependencies.
    /// </summary>
    void Initialize(
        Func<bool> isGamingStationEnabled,
        Func<ChannelResponse, Task> joinVoiceChannel,
        Func<Task> leaveVoiceChannel,
        Func<ScreenShareViewModel?> getScreenShare,
        Func<Task> reportGamingStationStatus);
}

public sealed class GamingStationCommandHandler : IGamingStationCommandHandler
{
    private readonly ISignalRService _signalR;
    private readonly IChannelStore _channelStore;
    private readonly IVoiceStore _voiceStore;
    private readonly ISettingsStore _settingsStore;

    private Func<bool>? _isGamingStationEnabled;
    private Func<ChannelResponse, Task>? _joinVoiceChannel;
    private Func<Task>? _leaveVoiceChannel;
    private Func<ScreenShareViewModel?>? _getScreenShare;
    private Func<Task>? _reportGamingStationStatus;

    public GamingStationCommandHandler(
        ISignalRService signalR,
        IChannelStore channelStore,
        IVoiceStore voiceStore,
        ISettingsStore settingsStore)
    {
        _signalR = signalR;
        _channelStore = channelStore;
        _voiceStore = voiceStore;
        _settingsStore = settingsStore;
    }

    public void Initialize(
        Func<bool> isGamingStationEnabled,
        Func<ChannelResponse, Task> joinVoiceChannel,
        Func<Task> leaveVoiceChannel,
        Func<ScreenShareViewModel?> getScreenShare,
        Func<Task> reportGamingStationStatus)
    {
        _isGamingStationEnabled = isGamingStationEnabled;
        _joinVoiceChannel = joinVoiceChannel;
        _leaveVoiceChannel = leaveVoiceChannel;
        _getScreenShare = getScreenShare;
        _reportGamingStationStatus = reportGamingStationStatus;

        // Subscribe to gaming station command events
        _signalR.StationCommandJoinChannel += OnStationCommandJoinChannel;
        _signalR.StationCommandLeaveChannel += OnStationCommandLeaveChannel;
        _signalR.StationCommandStartScreenShare += OnStationCommandStartScreenShare;
        _signalR.StationCommandStopScreenShare += OnStationCommandStopScreenShare;
        _signalR.StationCommandDisable += OnStationCommandDisable;
        _signalR.StationKeyboardInputReceived += OnStationKeyboardInputReceived;
        _signalR.StationMouseInputReceived += OnStationMouseInputReceived;
    }

    private void OnStationCommandJoinChannel(StationCommandJoinChannelEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_isGamingStationEnabled?.Invoke() != true) return;

            // Try to find the channel in our loaded channels
            var channel = _channelStore.GetChannel(e.ChannelId);
            ChannelResponse channelResponse;

            if (channel is not null)
            {
                // Convert ChannelState to ChannelResponse for the join method
                channelResponse = new ChannelResponse(
                    Id: channel.Id,
                    Name: channel.Name,
                    Topic: channel.Topic,
                    CommunityId: channel.CommunityId,
                    Type: channel.Type,
                    Position: channel.Position,
                    CreatedAt: channel.CreatedAt
                );
            }
            else
            {
                // Channel not in current community, create a minimal ChannelResponse for joining
                channelResponse = new ChannelResponse(
                    Id: e.ChannelId,
                    Name: e.ChannelName,
                    Topic: null,
                    CommunityId: Guid.Empty,
                    Type: ChannelType.Voice,
                    Position: 0,
                    CreatedAt: DateTime.UtcNow
                );
            }

            if (_joinVoiceChannel is not null)
            {
                await _joinVoiceChannel(channelResponse);
            }
        });
    }

    private void OnStationCommandLeaveChannel(StationCommandLeaveChannelEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_isGamingStationEnabled?.Invoke() != true) return;

            if (_leaveVoiceChannel is not null)
            {
                await _leaveVoiceChannel();
            }
        });
    }

    private void OnStationCommandStartScreenShare(StationCommandStartScreenShareEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_isGamingStationEnabled?.Invoke() != true) return;
            if (_voiceStore.GetCurrentChannelId() is null) return;

            var screenShare = _getScreenShare?.Invoke();
            if (screenShare is not null)
            {
                await screenShare.StartFromStationCommandAsync();
            }
        });
    }

    private void OnStationCommandStopScreenShare(StationCommandStopScreenShareEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_isGamingStationEnabled?.Invoke() != true) return;

            var screenShare = _getScreenShare?.Invoke();
            if (screenShare is not null)
            {
                await screenShare.StopScreenShareAsync();
            }
        });
    }

    private void OnStationCommandDisable(StationCommandDisableEvent e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            // Disable gaming station mode in settings
            _settingsStore.Settings.IsGamingStationEnabled = false;
            _settingsStore.Save();

            // Report the status change to the server
            if (_reportGamingStationStatus is not null)
            {
                await _reportGamingStationStatus();
            }
        });
    }

    private void OnStationKeyboardInputReceived(StationKeyboardInputEvent e)
    {
        if (_isGamingStationEnabled?.Invoke() != true) return;
        // TODO: Inject keyboard input using platform-specific APIs
        // Phase 3+ implementation
    }

    private void OnStationMouseInputReceived(StationMouseInputEvent e)
    {
        if (_isGamingStationEnabled?.Invoke() != true) return;
        // TODO: Inject mouse input using platform-specific APIs
        // Phase 3+ implementation
    }

    public void Dispose()
    {
        _signalR.StationCommandJoinChannel -= OnStationCommandJoinChannel;
        _signalR.StationCommandLeaveChannel -= OnStationCommandLeaveChannel;
        _signalR.StationCommandStartScreenShare -= OnStationCommandStartScreenShare;
        _signalR.StationCommandStopScreenShare -= OnStationCommandStopScreenShare;
        _signalR.StationCommandDisable -= OnStationCommandDisable;
        _signalR.StationKeyboardInputReceived -= OnStationKeyboardInputReceived;
        _signalR.StationMouseInputReceived -= OnStationMouseInputReceived;
    }
}
