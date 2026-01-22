namespace Snacka.Client.Models;

/// <summary>
/// Represents a gaming station owned by the current user.
/// </summary>
public record MyGamingStationInfo(
    string MachineId,
    string DisplayName,
    bool IsAvailable,
    bool IsInVoiceChannel,
    Guid? CurrentChannelId,
    bool IsScreenSharing,
    bool IsCurrentMachine  // True if this is the machine we're running on
);
