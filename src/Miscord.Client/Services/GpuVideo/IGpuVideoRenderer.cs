namespace Miscord.Client.Services.GpuVideo;

/// <summary>
/// Interface for GPU-accelerated video rendering.
/// Implementations handle NV12 (YUV 4:2:0) to RGB conversion on the GPU
/// and render directly to a native surface.
/// </summary>
public interface IGpuVideoRenderer : IDisposable
{
    /// <summary>
    /// Gets the native view handle for embedding in UI.
    /// On macOS: NSView pointer
    /// On Windows: HWND
    /// On Linux: X11 Window or Wayland surface
    /// </summary>
    nint NativeHandle { get; }

    /// <summary>
    /// Gets whether the renderer is initialized and ready.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Gets the current video dimensions.
    /// </summary>
    (int Width, int Height) VideoDimensions { get; }

    /// <summary>
    /// Initializes the renderer with the specified video dimensions.
    /// </summary>
    /// <param name="width">Video width in pixels</param>
    /// <param name="height">Video height in pixels</param>
    /// <returns>True if initialization succeeded</returns>
    bool Initialize(int width, int height);

    /// <summary>
    /// Updates the video frame with new NV12 data and renders it.
    /// NV12 format: Y plane (width * height bytes) followed by interleaved UV plane (width * height / 2 bytes).
    /// </summary>
    /// <param name="nv12Data">NV12 frame data</param>
    void RenderFrame(ReadOnlySpan<byte> nv12Data);

    /// <summary>
    /// Resizes the renderer for new video dimensions.
    /// </summary>
    /// <param name="width">New video width</param>
    /// <param name="height">New video height</param>
    void Resize(int width, int height);

    /// <summary>
    /// Sets the display size (for scaling).
    /// </summary>
    /// <param name="width">Display width</param>
    /// <param name="height">Display height</param>
    void SetDisplaySize(int width, int height);
}

/// <summary>
/// Factory for creating platform-specific GPU video renderers.
/// </summary>
public static class GpuVideoRendererFactory
{
    /// <summary>
    /// Creates a GPU video renderer appropriate for the current platform.
    /// </summary>
    /// <returns>GPU video renderer, or null if GPU rendering is not available</returns>
    public static IGpuVideoRenderer? Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MetalVideoRenderer.TryCreate();
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            if (OpenGLVideoRenderer.IsAvailable())
            {
                return new OpenGLVideoRenderer();
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if GPU video rendering is available on the current platform.
    /// </summary>
    public static bool IsAvailable()
    {
        if (OperatingSystem.IsMacOS())
        {
            return MetalVideoRenderer.IsAvailable();
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            return OpenGLVideoRenderer.IsAvailable();
        }

        return false;
    }
}
