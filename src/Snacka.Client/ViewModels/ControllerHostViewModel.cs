using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for managing controller access requests and active sessions.
/// Handles accepting/declining requests, stopping sessions, and muting.
/// Reads current voice channel from VoiceStore (Redux-style).
/// </summary>
public class ControllerHostViewModel : ReactiveObject, IDisposable
{
    private readonly IControllerHostService _controllerHostService;
    private readonly IVoiceStore _voiceStore;
    private byte _selectedControllerSlot;

    public ControllerHostViewModel(
        IControllerHostService controllerHostService,
        IVoiceStore voiceStore)
    {
        _controllerHostService = controllerHostService;
        _voiceStore = voiceStore;

        // Create commands
        AcceptRequestCommand = ReactiveCommand.CreateFromTask<ControllerAccessRequest>(AcceptRequestAsync);
        DeclineRequestCommand = ReactiveCommand.CreateFromTask<ControllerAccessRequest>(DeclineRequestAsync);
        StopSessionCommand = ReactiveCommand.CreateFromTask<ActiveControllerSession>(StopSessionAsync);
        ToggleMuteSessionCommand = ReactiveCommand.Create<ActiveControllerSession>(ToggleMuteSession);

        // Subscribe to collection changes for property notifications
        _controllerHostService.PendingRequests.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(HasPendingRequests)));

        _controllerHostService.ActiveSessions.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(HasActiveSessions)));

        _controllerHostService.MutedSessionsChanged += () =>
            Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(ActiveSessions)));
    }

    #region Properties

    /// <summary>
    /// Pending controller access requests from other users.
    /// </summary>
    public ObservableCollection<ControllerAccessRequest> PendingRequests =>
        _controllerHostService.PendingRequests;

    /// <summary>
    /// Currently active controller sessions.
    /// </summary>
    public ObservableCollection<ActiveControllerSession> ActiveSessions =>
        _controllerHostService.ActiveSessions;

    /// <summary>
    /// Whether there are any pending controller requests.
    /// </summary>
    public bool HasPendingRequests => PendingRequests.Count > 0;

    /// <summary>
    /// Whether there are any active controller sessions.
    /// </summary>
    public bool HasActiveSessions => ActiveSessions.Count > 0;

    /// <summary>
    /// The currently selected controller slot for new sessions.
    /// </summary>
    public byte SelectedControllerSlot
    {
        get => _selectedControllerSlot;
        set => this.RaiseAndSetIfChanged(ref _selectedControllerSlot, value);
    }

    /// <summary>
    /// Available controller slots (0-3).
    /// </summary>
    public byte[] AvailableSlots => [0, 1, 2, 3];

    #endregion

    #region Commands

    /// <summary>
    /// Command to accept a controller access request.
    /// </summary>
    public ReactiveCommand<ControllerAccessRequest, Unit> AcceptRequestCommand { get; }

    /// <summary>
    /// Command to decline a controller access request.
    /// </summary>
    public ReactiveCommand<ControllerAccessRequest, Unit> DeclineRequestCommand { get; }

    /// <summary>
    /// Command to stop an active controller session.
    /// </summary>
    public ReactiveCommand<ActiveControllerSession, Unit> StopSessionCommand { get; }

    /// <summary>
    /// Command to toggle mute on a controller session.
    /// </summary>
    public ReactiveCommand<ActiveControllerSession, Unit> ToggleMuteSessionCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Check if a controller session is muted.
    /// </summary>
    public bool IsSessionMuted(Guid guestUserId)
    {
        return _controllerHostService.IsSessionMuted(guestUserId);
    }

    private async Task AcceptRequestAsync(ControllerAccessRequest request)
    {
        var channelId = _voiceStore.GetCurrentChannelId();
        if (channelId == null) return;

        try
        {
            var slot = _controllerHostService.GetNextAvailableSlot(channelId.Value) ?? SelectedControllerSlot;
            await _controllerHostService.AcceptRequestAsync(request.ChannelId, request.RequesterUserId, slot);
        }
        catch
        {
            // Controller request acceptance failure - ignore
        }
    }

    private async Task DeclineRequestAsync(ControllerAccessRequest request)
    {
        try
        {
            await _controllerHostService.DeclineRequestAsync(request.ChannelId, request.RequesterUserId);
        }
        catch
        {
            // Controller request decline failure - ignore
        }
    }

    private async Task StopSessionAsync(ActiveControllerSession session)
    {
        try
        {
            await _controllerHostService.StopSessionAsync(session.ChannelId, session.GuestUserId);
        }
        catch
        {
            // Controller session stop failure - ignore
        }
    }

    private void ToggleMuteSession(ActiveControllerSession session)
    {
        _controllerHostService.ToggleMuteSession(session.GuestUserId);
    }

    #endregion

    public void Dispose()
    {
        AcceptRequestCommand.Dispose();
        DeclineRequestCommand.Dispose();
        StopSessionCommand.Dispose();
        ToggleMuteSessionCommand.Dispose();
    }
}
