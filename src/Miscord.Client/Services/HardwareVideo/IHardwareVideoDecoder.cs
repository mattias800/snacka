namespace Miscord.Client.Services.HardwareVideo;

/// <summary>
/// Interface for hardware-accelerated video decoders.
/// Implementations use platform-specific APIs (VideoToolbox, Media Foundation, VA-API)
/// to decode H264 on the GPU and output frames directly to GPU textures.
/// </summary>
public interface IHardwareVideoDecoder : IDisposable
{
    /// <summary>
    /// Gets whether the decoder is initialized and ready.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets the video dimensions.
    /// </summary>
    (int Width, int Height) VideoDimensions { get; }

    /// <summary>
    /// Initializes the decoder for the specified video dimensions.
    /// </summary>
    /// <param name="width">Video width in pixels</param>
    /// <param name="height">Video height in pixels</param>
    /// <param name="sps">H264 Sequence Parameter Set (NAL unit without start code)</param>
    /// <param name="pps">H264 Picture Parameter Set (NAL unit without start code)</param>
    /// <returns>True if initialization succeeded</returns>
    bool Initialize(int width, int height, ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps);

    /// <summary>
    /// Decodes an H264 NAL unit and renders directly to the associated GPU surface.
    /// This is a zero-copy operation - the decoded frame stays in GPU memory.
    /// </summary>
    /// <param name="nalUnit">H264 NAL unit (without Annex B start code)</param>
    /// <param name="isKeyframe">Whether this is a keyframe (IDR)</param>
    /// <returns>True if decode succeeded and frame was rendered</returns>
    bool DecodeAndRender(ReadOnlySpan<byte> nalUnit, bool isKeyframe);

    /// <summary>
    /// Gets the native renderer handle for embedding in UI.
    /// On macOS: NSView with CAMetalLayer
    /// On Windows: HWND with D3D11 swap chain
    /// On Linux: X11 Window with VA-API surface
    /// </summary>
    nint NativeViewHandle { get; }

    /// <summary>
    /// Sets the display size for the renderer (for scaling).
    /// </summary>
    void SetDisplaySize(int width, int height);
}

/// <summary>
/// Factory for creating platform-specific hardware video decoders.
/// </summary>
public static class HardwareVideoDecoderFactory
{
    /// <summary>
    /// Creates a hardware video decoder appropriate for the current platform.
    /// </summary>
    /// <returns>Hardware video decoder, or null if not available</returns>
    public static IHardwareVideoDecoder? Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return VideoToolboxDecoder.TryCreate();
        }

        if (OperatingSystem.IsWindows())
        {
            return MediaFoundationDecoder.TryCreate();
        }

        if (OperatingSystem.IsLinux())
        {
            return VaapiDecoder.TryCreate();
        }

        return null;
    }

    /// <summary>
    /// Checks if hardware video decoding is available on the current platform.
    /// </summary>
    public static bool IsAvailable()
    {
        if (OperatingSystem.IsMacOS())
        {
            return VideoToolboxDecoder.IsAvailable();
        }

        if (OperatingSystem.IsWindows())
        {
            return MediaFoundationDecoder.IsAvailable();
        }

        if (OperatingSystem.IsLinux())
        {
            return VaapiDecoder.IsAvailable();
        }

        return false;
    }
}
