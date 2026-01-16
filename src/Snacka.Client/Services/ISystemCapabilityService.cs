namespace Snacka.Client.Services;

/// <summary>
/// Service for detecting and reporting system capabilities at startup.
/// Checks hardware acceleration support for video encoding/decoding and
/// outputs diagnostics to stdout for testing and debugging.
/// </summary>
public interface ISystemCapabilityService
{
    /// <summary>
    /// Gets whether a hardware video encoder is available.
    /// Hardware encoders: VideoToolbox (macOS), NVENC/AMF/QuickSync (Windows), VAAPI (Linux).
    /// </summary>
    bool IsHardwareEncoderAvailable { get; }

    /// <summary>
    /// Gets the name of the detected hardware encoder, or null if none available.
    /// Examples: "VideoToolbox", "NVENC", "AMF", "QuickSync", "VAAPI"
    /// </summary>
    string? HardwareEncoderName { get; }

    /// <summary>
    /// Gets whether a hardware video decoder is available.
    /// Hardware decoders: VideoToolbox (macOS), Media Foundation (Windows), VAAPI (Linux).
    /// </summary>
    bool IsHardwareDecoderAvailable { get; }

    /// <summary>
    /// Gets the name of the detected hardware decoder, or null if none available.
    /// Examples: "VideoToolbox", "MediaFoundation", "VAAPI"
    /// </summary>
    string? HardwareDecoderName { get; }

    /// <summary>
    /// Gets whether native capture tools are available (for screen share/camera with hardware encoding).
    /// </summary>
    bool IsNativeCaptureAvailable { get; }

    /// <summary>
    /// Gets the native capture tool name, or null if none available.
    /// Examples: "SnackaCaptureVideoToolbox", "SnackaCaptureWindows", "SnackaCaptureLinux"
    /// </summary>
    string? NativeCaptureName { get; }

    /// <summary>
    /// Gets whether full hardware acceleration is available (encoder + decoder + native capture).
    /// </summary>
    bool IsFullHardwareAccelerationAvailable { get; }

    /// <summary>
    /// Gets any warning messages that should be displayed to the user.
    /// Empty if all capabilities are available.
    /// </summary>
    IReadOnlyList<string> Warnings { get; }
}
