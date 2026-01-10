using System.Runtime.InteropServices;

namespace Miscord.Client.Services.GpuVideo;

/// <summary>
/// macOS Metal-based GPU video renderer.
/// Performs NV12 (YUV 4:2:0) to RGB conversion on the GPU using Metal shaders.
/// </summary>
public class MetalVideoRenderer : IGpuVideoRenderer
{
    private nint _handle;
    private bool _disposed;
    private int _videoWidth;
    private int _videoHeight;

    // Path to the native library
    private const string LibraryName = "libMiscordMetalRenderer";

    #region Native Methods

    [DllImport(LibraryName)]
    private static extern nint metal_renderer_create();

    [DllImport(LibraryName)]
    private static extern void metal_renderer_destroy(nint handle);

    [DllImport(LibraryName)]
    private static extern nint metal_renderer_get_view(nint handle);

    [DllImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool metal_renderer_initialize(nint handle, int width, int height);

    [DllImport(LibraryName)]
    private static extern void metal_renderer_render_frame(nint handle, nint nv12Data, int length);

    [DllImport(LibraryName)]
    private static extern void metal_renderer_set_display_size(nint handle, int width, int height);

    [DllImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool metal_renderer_is_available();

    #endregion

    private MetalVideoRenderer(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Tries to create a Metal video renderer.
    /// Returns null if Metal is not available.
    /// </summary>
    public static MetalVideoRenderer? TryCreate()
    {
        if (!IsAvailable())
        {
            Console.WriteLine("MetalVideoRenderer: Metal not available");
            return null;
        }

        try
        {
            var handle = metal_renderer_create();
            if (handle == nint.Zero)
            {
                Console.WriteLine("MetalVideoRenderer: Failed to create native renderer");
                return null;
            }

            Console.WriteLine("MetalVideoRenderer: Created successfully");
            return new MetalVideoRenderer(handle);
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"MetalVideoRenderer: Native library not found: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MetalVideoRenderer: Error creating renderer: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if Metal rendering is available.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            return metal_renderer_is_available();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public nint NativeHandle
    {
        get
        {
            if (_handle == nint.Zero)
                return nint.Zero;

            return metal_renderer_get_view(_handle);
        }
    }

    /// <inheritdoc />
    public bool IsInitialized => _videoWidth > 0 && _videoHeight > 0;

    /// <inheritdoc />
    public (int Width, int Height) VideoDimensions => (_videoWidth, _videoHeight);

    /// <inheritdoc />
    public bool Initialize(int width, int height)
    {
        if (_handle == nint.Zero)
            return false;

        var result = metal_renderer_initialize(_handle, width, height);
        if (result)
        {
            _videoWidth = width;
            _videoHeight = height;
            Console.WriteLine($"MetalVideoRenderer: Initialized for {width}x{height}");
        }
        return result;
    }

    /// <inheritdoc />
    public void RenderFrame(ReadOnlySpan<byte> nv12Data)
    {
        if (_handle == nint.Zero || nv12Data.IsEmpty)
            return;

        unsafe
        {
            fixed (byte* dataPtr = nv12Data)
            {
                metal_renderer_render_frame(_handle, (nint)dataPtr, nv12Data.Length);
            }
        }
    }

    /// <inheritdoc />
    public void Resize(int width, int height)
    {
        if (_handle == nint.Zero)
            return;

        // Reinitialize textures for new size
        if (metal_renderer_initialize(_handle, width, height))
        {
            _videoWidth = width;
            _videoHeight = height;
        }
    }

    /// <inheritdoc />
    public void SetDisplaySize(int width, int height)
    {
        if (_handle == nint.Zero)
            return;

        metal_renderer_set_display_size(_handle, width, height);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_handle != nint.Zero)
        {
            metal_renderer_destroy(_handle);
            _handle = nint.Zero;
            Console.WriteLine("MetalVideoRenderer: Disposed");
        }

        GC.SuppressFinalize(this);
    }

    ~MetalVideoRenderer()
    {
        Dispose();
    }
}
