namespace Snacka.Server.DTOs;

// ============================================================================
// Port Forwarding / Tunnel DTOs
// ============================================================================

/// <summary>
/// Request to share a local port with voice channel members.
/// </summary>
public record SharePortRequest(
    int Port,
    string? Label
);

/// <summary>
/// Information about an active shared port.
/// </summary>
public record SharedPortInfo(
    string TunnelId,
    Guid OwnerId,
    string OwnerUsername,
    int Port,
    string? Label,
    DateTime SharedAt
);

/// <summary>
/// Event broadcast when a port is shared in a voice channel.
/// </summary>
public record PortSharedEvent(
    Guid ChannelId,
    string TunnelId,
    Guid OwnerId,
    string OwnerUsername,
    int Port,
    string? Label
);

/// <summary>
/// Event broadcast when a port share is stopped.
/// </summary>
public record PortShareStoppedEvent(
    Guid ChannelId,
    string TunnelId,
    Guid OwnerId
);

/// <summary>
/// Response containing the authenticated URL to access a tunnel.
/// </summary>
public record TunnelAccessResponse(
    string Url
);
