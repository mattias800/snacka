using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Miscord.Client.Services.HardwareVideo;

namespace Miscord.Client.Controls;

/// <summary>
/// Container control that hosts a HardwareVideoView and recreates it when the decoder changes.
/// This is necessary because NativeControlHost can only set its native handle once at creation.
/// </summary>
public class HardwareVideoViewHost : ContentControl
{
    private HardwareVideoView? _currentView;

    public static readonly StyledProperty<IHardwareVideoDecoder?> DecoderProperty =
        AvaloniaProperty.Register<HardwareVideoViewHost, IHardwareVideoDecoder?>(nameof(Decoder));

    public IHardwareVideoDecoder? Decoder
    {
        get => GetValue(DecoderProperty);
        set => SetValue(DecoderProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DecoderProperty)
        {
            var decoder = change.NewValue as IHardwareVideoDecoder;

            if (decoder != null && decoder.NativeViewHandle != nint.Zero)
            {
                // Create new HardwareVideoView with the decoder
                _currentView = new HardwareVideoView();
                _currentView.SetDecoder(decoder);
                Content = _currentView;
            }
            else
            {
                // No decoder - clear content
                _currentView = null;
                Content = null;
            }
        }
        else if (change.Property == BoundsProperty && _currentView != null)
        {
            var bounds = (Rect)change.NewValue!;
            if (bounds.Width > 0 && bounds.Height > 0 && Decoder != null)
            {
                Decoder.SetDisplaySize((int)bounds.Width, (int)bounds.Height);
            }
        }
    }
}

/// <summary>
/// Avalonia control for embedding hardware-accelerated video decoder output.
/// Uses NativeControlHost to embed the decoder's native view (NSView on macOS, HWND on Windows).
/// Zero-copy pipeline: H264 NAL → Hardware Decode → GPU Texture → Display
/// </summary>
public class HardwareVideoView : NativeControlHost
{
    private IHardwareVideoDecoder? _decoder;

    public HardwareVideoView()
    {
    }

    /// <summary>
    /// Sets the decoder before the control is attached to the visual tree.
    /// Must be called before the control is displayed.
    /// </summary>
    public void SetDecoder(IHardwareVideoDecoder decoder)
    {
        _decoder = decoder;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Take all available space, with reasonable defaults for infinite constraints
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 1920 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 1080 : availableSize.Height
        );
        return size;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_decoder != null && _decoder.NativeViewHandle != nint.Zero)
        {
            // Update display size to match control bounds
            var bounds = Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _decoder.SetDisplaySize((int)bounds.Width, (int)bounds.Height);
            }

            var handle = _decoder.NativeViewHandle;

            // Return the native view handle with platform-specific descriptor
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

        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // Don't destroy the handle - the WebRtcService owns the decoder and its view
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty && _decoder != null)
        {
            var bounds = (Rect)change.NewValue!;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _decoder.SetDisplaySize((int)bounds.Width, (int)bounds.Height);
            }
        }
    }
}
