using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Miscord.Client.Services.GpuVideo;

namespace Miscord.Client.Controls;

/// <summary>
/// Avalonia control for GPU-accelerated video rendering.
/// Uses NativeControlHost to embed a platform-specific GPU surface.
/// </summary>
public class GpuVideoView : NativeControlHost
{
    private IGpuVideoRenderer? _renderer;
    private int _videoWidth;
    private int _videoHeight;
    private bool _isInitialized;

    public static readonly StyledProperty<int> VideoWidthProperty =
        AvaloniaProperty.Register<GpuVideoView, int>(nameof(VideoWidth), 1920);

    public static readonly StyledProperty<int> VideoHeightProperty =
        AvaloniaProperty.Register<GpuVideoView, int>(nameof(VideoHeight), 1080);

    public GpuVideoView()
    {
        // Try to create GPU renderer
        _renderer = GpuVideoRendererFactory.Create();
    }

    /// <summary>
    /// Gets or sets the video width.
    /// </summary>
    public int VideoWidth
    {
        get => GetValue(VideoWidthProperty);
        set => SetValue(VideoWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the video height.
    /// </summary>
    public int VideoHeight
    {
        get => GetValue(VideoHeightProperty);
        set => SetValue(VideoHeightProperty, value);
    }

    /// <summary>
    /// Gets whether GPU rendering is being used.
    /// </summary>
    public bool IsGpuRendering => _renderer != null && _isInitialized;

    /// <summary>
    /// Gets the current video dimensions.
    /// </summary>
    public (int Width, int Height) VideoDimensions => (_videoWidth, _videoHeight);

    /// <summary>
    /// Initializes the renderer for the specified video dimensions.
    /// </summary>
    public bool InitializeRenderer(int width, int height)
    {
        if (_renderer == null)
            return false;

        _videoWidth = width;
        _videoHeight = height;

        if (_renderer.Initialize(width, height))
        {
            _isInitialized = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders an NV12 frame.
    /// </summary>
    public void RenderFrame(ReadOnlySpan<byte> nv12Data)
    {
        if (_renderer == null || !_isInitialized)
            return;

        _renderer.RenderFrame(nv12Data);
    }

    /// <summary>
    /// Renders an NV12 frame from a byte array.
    /// </summary>
    public void RenderFrame(byte[] nv12Data)
    {
        RenderFrame(nv12Data.AsSpan());
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var renderer = _renderer;
        if (renderer != null && renderer.NativeHandle != nint.Zero)
        {
            // Update display size to match control bounds
            var bounds = Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                renderer.SetDisplaySize((int)bounds.Width, (int)bounds.Height);
            }

            var handle = renderer.NativeHandle;

            // Return the native view handle
            if (OperatingSystem.IsMacOS())
            {
                return new PlatformHandle(handle, "NSView");
            }
            else if (OperatingSystem.IsWindows())
            {
                return new PlatformHandle(handle, "HWND");
            }
            else if (OperatingSystem.IsLinux())
            {
                return new PlatformHandle(handle, "XID");
            }
        }

        // Fallback to base implementation (empty control)
        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Don't destroy the handle here - the renderer owns it
        // base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty && _renderer != null)
        {
            var bounds = (Rect)change.NewValue!;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _renderer.SetDisplaySize((int)bounds.Width, (int)bounds.Height);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _renderer?.Dispose();
        _renderer = null;
        _isInitialized = false;
    }
}

/// <summary>
/// Simple platform handle implementation.
/// </summary>
internal class PlatformHandle : IPlatformHandle
{
    public PlatformHandle(nint handle, string descriptor)
    {
        Handle = handle;
        HandleDescriptor = descriptor;
    }

    public nint Handle { get; }
    public string HandleDescriptor { get; }
}
