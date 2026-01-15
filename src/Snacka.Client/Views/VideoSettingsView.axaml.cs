using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Snacka.Client.Controls;
using Snacka.Client.Services.GpuVideo;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Views;

public partial class VideoSettingsView : UserControl
{
    private GpuVideoView? _previewView;
    private Image? _previewFallback;

    private int _videoWidth;
    private int _videoHeight;

    // Track whether GPU rendering is working
    private bool _usingGpu = true;

    public VideoSettingsView()
    {
        InitializeComponent();

        // Get references to controls
        _previewView = this.FindControl<GpuVideoView>("PreviewView");
        _previewFallback = this.FindControl<Image>("PreviewFallback");

        // Check if GPU rendering is available
        _usingGpu = GpuVideoRendererFactory.IsAvailable();

        // Show fallback control if GPU not available
        if (!_usingGpu && _previewFallback != null)
        {
            _previewFallback.IsVisible = true;
            Console.WriteLine("VideoSettingsView: GPU not available, using software fallback");
        }

        // Subscribe to DataContext changes to wire up frame events
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is VideoSettingsViewModel viewModel)
        {
            // Subscribe to NV12 frame event
            viewModel.OnPreviewFrame += OnPreviewFrame;
        }
    }

    private void OnPreviewFrame(int width, int height, byte[] nv12Data)
    {
        if (_usingGpu && _previewView != null)
        {
            // Try GPU rendering
            if (width != _videoWidth || height != _videoHeight)
            {
                _videoWidth = width;
                _videoHeight = height;

                if (!_previewView.InitializeRenderer(width, height))
                {
                    Console.WriteLine($"VideoSettingsView: GPU init failed, falling back to software");
                    _usingGpu = false;
                    if (_previewFallback != null) _previewFallback.IsVisible = true;
                }
                else
                {
                    Console.WriteLine($"VideoSettingsView: Initialized GPU renderer for {width}x{height}");
                }
            }

            if (_usingGpu)
            {
                _previewView.RenderFrame(nv12Data);
                return;
            }
        }

        // Software fallback: convert NV12 to bitmap
        if (_previewFallback != null)
        {
            var bitmap = CreateBitmapFromNv12(nv12Data, width, height);
            _previewFallback.Source = bitmap;
        }
    }

    /// <summary>
    /// Software fallback: Convert NV12 to WriteableBitmap.
    /// This is slower than GPU rendering but works on all systems.
    /// </summary>
    private static WriteableBitmap? CreateBitmapFromNv12(byte[] nv12Data, int width, int height)
    {
        var expectedSize = width * height * 3 / 2;
        if (nv12Data.Length < expectedSize)
        {
            Console.WriteLine($"VideoSettingsView: Invalid NV12 data length {nv12Data.Length}, expected {expectedSize}");
            return null;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using (var lockedBitmap = bitmap.Lock())
        {
            var destPtr = lockedBitmap.Address;
            var bgraData = new byte[width * height * 4];
            var yPlaneSize = width * height;

            for (int j = 0; j < height; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    var yIndex = j * width + i;
                    var uvIndex = yPlaneSize + (j / 2) * width + (i / 2) * 2;

                    var y = nv12Data[yIndex];
                    var u = nv12Data[uvIndex];
                    var v = nv12Data[uvIndex + 1];

                    // YUV to RGB conversion (BT.601)
                    var c = y - 16;
                    var d = u - 128;
                    var e = v - 128;

                    var r = (298 * c + 409 * e + 128) >> 8;
                    var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                    var b = (298 * c + 516 * d + 128) >> 8;

                    var bgraIndex = (j * width + i) * 4;
                    bgraData[bgraIndex + 0] = (byte)Math.Clamp(b, 0, 255); // B
                    bgraData[bgraIndex + 1] = (byte)Math.Clamp(g, 0, 255); // G
                    bgraData[bgraIndex + 2] = (byte)Math.Clamp(r, 0, 255); // R
                    bgraData[bgraIndex + 3] = 255;                          // A
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(bgraData, 0, destPtr, bgraData.Length);
        }

        return bitmap;
    }
}
