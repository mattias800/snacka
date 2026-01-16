using System.Collections.ObjectModel;
using ReactiveUI;
using Snacka.Shared.Models;

namespace Snacka.Client.Services;

/// <summary>
/// Represents a pending controller access request from a guest.
/// </summary>
public record ControllerAccessRequest(
    Guid ChannelId,
    Guid RequesterUserId,
    string RequesterUsername,
    DateTime RequestTime
);

/// <summary>
/// Represents an active controller session with a guest.
/// </summary>
public record ActiveControllerSession(
    Guid ChannelId,
    Guid GuestUserId,
    string GuestUsername,
    byte ControllerSlot
);

/// <summary>
/// Manages hosting controller access from guests.
/// On the host side, handles access requests and receives controller state.
/// </summary>
public interface IControllerHostService : IDisposable
{
    /// <summary>
    /// Pending controller access requests.
    /// </summary>
    ObservableCollection<ControllerAccessRequest> PendingRequests { get; }

    /// <summary>
    /// Active controller sessions.
    /// </summary>
    ObservableCollection<ActiveControllerSession> ActiveSessions { get; }

    /// <summary>
    /// Accept a pending controller access request.
    /// </summary>
    Task AcceptRequestAsync(Guid channelId, Guid guestUserId, byte controllerSlot);

    /// <summary>
    /// Decline a pending controller access request.
    /// </summary>
    Task DeclineRequestAsync(Guid channelId, Guid guestUserId);

    /// <summary>
    /// Stop a controller session with a guest.
    /// </summary>
    Task StopSessionAsync(Guid channelId, Guid guestUserId);

    /// <summary>
    /// Stop all controller sessions.
    /// </summary>
    Task StopAllSessionsAsync(Guid channelId);

    /// <summary>
    /// Get the next available controller slot (0-3), or null if all are taken.
    /// </summary>
    byte? GetNextAvailableSlot(Guid channelId);

    /// <summary>
    /// Fired when a new access request is received.
    /// </summary>
    event Action<ControllerAccessRequest>? AccessRequestReceived;

    /// <summary>
    /// Fired when controller state is received from a guest.
    /// </summary>
    event Action<ControllerStateReceivedEvent>? ControllerStateReceived;
}

public class ControllerHostService : ReactiveObject, IControllerHostService
{
    private readonly ISignalRService _signalR;
    private readonly IVirtualControllerService _virtualController;

    public ObservableCollection<ControllerAccessRequest> PendingRequests { get; } = new();
    public ObservableCollection<ActiveControllerSession> ActiveSessions { get; } = new();

    public event Action<ControllerAccessRequest>? AccessRequestReceived;
    public event Action<ControllerStateReceivedEvent>? ControllerStateReceived;

    public ControllerHostService(ISignalRService signalR)
    {
        _signalR = signalR;
        _virtualController = VirtualControllerServiceFactory.Create();

        if (_virtualController.IsSupported)
        {
            Console.WriteLine("ControllerHostService: Virtual controller support available");
        }
        else
        {
            Console.WriteLine($"ControllerHostService: Virtual controller not available - {_virtualController.NotSupportedReason}");
        }

        // Subscribe to SignalR events
        _signalR.ControllerAccessRequested += OnAccessRequested;
        _signalR.ControllerAccessStopped += OnAccessStopped;
        _signalR.ControllerStateReceived += OnControllerStateReceived;
    }

    public async Task AcceptRequestAsync(Guid channelId, Guid guestUserId, byte controllerSlot)
    {
        // Find and remove the pending request
        var request = PendingRequests.FirstOrDefault(r =>
            r.ChannelId == channelId && r.RequesterUserId == guestUserId);

        if (request == null)
        {
            Console.WriteLine($"ControllerHostService: No pending request from {guestUserId}");
            return;
        }

        PendingRequests.Remove(request);

        // Create virtual controller for this slot
        if (_virtualController.IsSupported && !_virtualController.HasController(controllerSlot))
        {
            if (_virtualController.CreateController(controllerSlot))
            {
                Console.WriteLine($"ControllerHostService: Created virtual controller for slot {controllerSlot}");
            }
            else
            {
                Console.WriteLine($"ControllerHostService: Failed to create virtual controller for slot {controllerSlot}");
            }
        }

        // Add to active sessions
        var session = new ActiveControllerSession(
            channelId,
            guestUserId,
            request.RequesterUsername,
            controllerSlot
        );
        ActiveSessions.Add(session);

        Console.WriteLine($"ControllerHostService: Accepting {request.RequesterUsername} as Player {controllerSlot + 1}");
        await _signalR.AcceptControllerAccessAsync(channelId, guestUserId, controllerSlot);
    }

    public async Task DeclineRequestAsync(Guid channelId, Guid guestUserId)
    {
        var request = PendingRequests.FirstOrDefault(r =>
            r.ChannelId == channelId && r.RequesterUserId == guestUserId);

        if (request != null)
        {
            PendingRequests.Remove(request);
        }

        Console.WriteLine($"ControllerHostService: Declining request from {guestUserId}");
        await _signalR.DeclineControllerAccessAsync(channelId, guestUserId);
    }

    public async Task StopSessionAsync(Guid channelId, Guid guestUserId)
    {
        var session = ActiveSessions.FirstOrDefault(s =>
            s.ChannelId == channelId && s.GuestUserId == guestUserId);

        if (session != null)
        {
            ActiveSessions.Remove(session);
            Console.WriteLine($"ControllerHostService: Stopping session with {session.GuestUsername}");
        }

        await _signalR.StopControllerAccessAsync(channelId, guestUserId);
    }

    public async Task StopAllSessionsAsync(Guid channelId)
    {
        var sessionsToRemove = ActiveSessions.Where(s => s.ChannelId == channelId).ToList();

        foreach (var session in sessionsToRemove)
        {
            ActiveSessions.Remove(session);
            await _signalR.StopControllerAccessAsync(channelId, session.GuestUserId);
        }

        // Also clear any pending requests for this channel
        var requestsToRemove = PendingRequests.Where(r => r.ChannelId == channelId).ToList();
        foreach (var request in requestsToRemove)
        {
            PendingRequests.Remove(request);
            await _signalR.DeclineControllerAccessAsync(channelId, request.RequesterUserId);
        }

        Console.WriteLine($"ControllerHostService: Stopped all sessions for channel {channelId}");
    }

    public byte? GetNextAvailableSlot(Guid channelId)
    {
        var usedSlots = ActiveSessions
            .Where(s => s.ChannelId == channelId)
            .Select(s => s.ControllerSlot)
            .ToHashSet();

        for (byte slot = 0; slot < 4; slot++)
        {
            if (!usedSlots.Contains(slot))
            {
                return slot;
            }
        }

        return null; // All slots taken
    }

    private void OnAccessRequested(ControllerAccessRequestedEvent e)
    {
        Console.WriteLine($"ControllerHostService: Access request from {e.RequesterUsername} ({e.RequesterUserId})");

        // Check if we already have a pending request from this user
        var existing = PendingRequests.FirstOrDefault(r =>
            r.ChannelId == e.ChannelId && r.RequesterUserId == e.RequesterUserId);

        if (existing != null)
        {
            Console.WriteLine($"ControllerHostService: Already have pending request from {e.RequesterUsername}");
            return;
        }

        var request = new ControllerAccessRequest(
            e.ChannelId,
            e.RequesterUserId,
            e.RequesterUsername,
            DateTime.UtcNow
        );

        PendingRequests.Add(request);
        AccessRequestReceived?.Invoke(request);
    }

    private void OnAccessStopped(ControllerAccessStoppedEvent e)
    {
        // Remove from active sessions if we're the host
        var session = ActiveSessions.FirstOrDefault(s =>
            s.ChannelId == e.ChannelId && s.GuestUserId == e.GuestUserId);

        if (session != null)
        {
            ActiveSessions.Remove(session);

            // Destroy virtual controller if no other sessions use this slot
            var slotStillInUse = ActiveSessions.Any(s => s.ControllerSlot == session.ControllerSlot);
            if (!slotStillInUse && _virtualController.HasController(session.ControllerSlot))
            {
                _virtualController.DestroyController(session.ControllerSlot);
                Console.WriteLine($"ControllerHostService: Destroyed virtual controller for slot {session.ControllerSlot}");
            }

            Console.WriteLine($"ControllerHostService: Session with {session.GuestUsername} ended ({e.Reason})");
        }
    }

    private void OnControllerStateReceived(ControllerStateReceivedEvent e)
    {
        // Feed to virtual controller if available
        var state = e.State;
        if (_virtualController.IsSupported && _virtualController.HasController(state.ControllerSlot))
        {
            _virtualController.UpdateState(state.ControllerSlot, state);
        }

        // Forward to any listeners
        ControllerStateReceived?.Invoke(e);
    }

    public void Dispose()
    {
        _signalR.ControllerAccessRequested -= OnAccessRequested;
        _signalR.ControllerAccessStopped -= OnAccessStopped;
        _signalR.ControllerStateReceived -= OnControllerStateReceived;
        _virtualController.Dispose();
    }
}
