using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.Hubs;

namespace Snacka.Server.Services;

/// <summary>
/// Keeps a user's open connections in step with community membership changed over HTTP: joining adds
/// them to the community's group right away, and leaving takes them out of the community's and its
/// channels' groups, so they stop receiving its messages before they reconnect (#20).
/// </summary>
public interface IRealtimeMembership
{
    Task JoinedCommunityAsync(Guid userId, Guid communityId, CancellationToken cancellationToken = default);
    Task LeftCommunityAsync(Guid userId, Guid communityId, CancellationToken cancellationToken = default);
}

public sealed class RealtimeMembership : IRealtimeMembership
{
    private readonly IHubContext<SnackaHub> _hub;
    private readonly SnackaDbContext _db;

    public RealtimeMembership(IHubContext<SnackaHub> hub, SnackaDbContext db)
    {
        _hub = hub;
        _db = db;
    }

    public async Task JoinedCommunityAsync(Guid userId, Guid communityId, CancellationToken cancellationToken = default)
    {
        foreach (var connection in SnackaHub.ConnectionsOf(userId))
            await _hub.Groups.AddToGroupAsync(connection, $"community:{communityId}", cancellationToken);
    }

    public async Task LeftCommunityAsync(Guid userId, Guid communityId, CancellationToken cancellationToken = default)
    {
        var connections = SnackaHub.ConnectionsOf(userId);
        if (connections.Count == 0) return;
        var groups = (await _db.Channels.Where(c => c.CommunityId == communityId).Select(c => c.Id).ToListAsync(cancellationToken))
            .Select(id => $"channel:{id}")
            .Append($"community:{communityId}")
            .ToList();
        foreach (var connection in connections)
            foreach (var group in groups)
                await _hub.Groups.RemoveFromGroupAsync(connection, group, cancellationToken);
    }
}
