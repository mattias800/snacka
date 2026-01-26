namespace Snacka.Client.Services;

/// <summary>
/// Information about an available microphone device.
/// Used for native microphone capture enumeration.
/// </summary>
public record MicrophoneInfo(
    string Id,      // Platform-specific device ID
    string Name,    // Human-readable device name
    int Index       // Index in device list
);
