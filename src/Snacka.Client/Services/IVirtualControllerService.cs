using Snacka.Shared.Models;

namespace Snacka.Client.Services;

/// <summary>
/// Service for creating and controlling virtual game controllers.
/// Implemented per-platform: ViGEm on Windows, uinput on Linux.
/// </summary>
public interface IVirtualControllerService : IDisposable
{
    /// <summary>
    /// Whether virtual controller creation is supported on this platform.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Error message if not supported (e.g., driver not installed).
    /// </summary>
    string? NotSupportedReason { get; }

    /// <summary>
    /// Creates a virtual Xbox 360 controller at the specified slot.
    /// </summary>
    /// <param name="slot">Controller slot (0-3 for players 1-4)</param>
    /// <returns>True if successful</returns>
    bool CreateController(byte slot);

    /// <summary>
    /// Destroys the virtual controller at the specified slot.
    /// </summary>
    bool DestroyController(byte slot);

    /// <summary>
    /// Updates the state of a virtual controller.
    /// </summary>
    void UpdateState(byte slot, ControllerStateMessage state);

    /// <summary>
    /// Checks if a controller exists at the specified slot.
    /// </summary>
    bool HasController(byte slot);

    /// <summary>
    /// Gets the number of active virtual controllers.
    /// </summary>
    int ActiveControllerCount { get; }
}

/// <summary>
/// Null implementation for unsupported platforms.
/// </summary>
public class NullVirtualControllerService : IVirtualControllerService
{
    public bool IsSupported => false;
    public string? NotSupportedReason => "Virtual controller not supported on this platform";
    public int ActiveControllerCount => 0;

    public bool CreateController(byte slot) => false;
    public bool DestroyController(byte slot) => false;
    public void UpdateState(byte slot, ControllerStateMessage state) { }
    public bool HasController(byte slot) => false;
    public void Dispose() { }
}
