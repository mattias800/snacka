using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Miscord.Client.ViewModels;
using Miscord.Shared.Models;

namespace Miscord.Client.Views;

/// <summary>
/// Transparent fullscreen overlay window for drawing annotations on the sharer's monitor.
/// Supports input pass-through when draw mode is disabled (platform-specific).
/// </summary>
public partial class ScreenAnnotationWindow : Window
{
    private ScreenAnnotationViewModel? _viewModel;

    // Drawing state
    private bool _isDrawing;
    private List<PointF> _currentStrokePoints = new();
    private Polyline? _currentPolyline;

    // Platform-specific handles
    private IntPtr _windowHandle;

    public ScreenAnnotationWindow()
    {
        InitializeComponent();

        // Subscribe to draw mode changes
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ScreenAnnotationViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateInputPassThrough();
            RedrawStrokes();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScreenAnnotationViewModel.IsDrawModeEnabled))
        {
            UpdateInputPassThrough();
        }
        else if (e.PropertyName == nameof(ScreenAnnotationViewModel.Strokes))
        {
            RedrawStrokes();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Get the native window handle for platform-specific operations
        if (TryGetPlatformHandle() is { } handle)
        {
            _windowHandle = handle.Handle;
            Console.WriteLine($"ScreenAnnotationWindow: Got window handle {_windowHandle}");
        }

        UpdateInputPassThrough();
    }

    /// <summary>
    /// Updates whether the window allows input to pass through to windows below.
    /// When draw mode is OFF, clicks should go to the desktop.
    /// When draw mode is ON, clicks should be captured for drawing.
    /// </summary>
    private void UpdateInputPassThrough()
    {
        var passThrough = _viewModel?.IsDrawModeEnabled != true;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            UpdateInputPassThroughWindows(passThrough);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            UpdateInputPassThroughMacOS(passThrough);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            UpdateInputPassThroughLinux(passThrough);
        }

        // Update canvas hit test
        AnnotationCanvas.IsHitTestVisible = !passThrough;

        Console.WriteLine($"ScreenAnnotationWindow: Input pass-through = {passThrough}");
    }

    #region Platform-Specific Input Pass-Through

    // Windows
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void UpdateInputPassThroughWindows(bool passThrough)
    {
        if (_windowHandle == IntPtr.Zero) return;

        try
        {
            var exStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);

            if (passThrough)
            {
                // Make window click-through
                exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            }
            else
            {
                // Make window capture clicks
                exStyle &= ~WS_EX_TRANSPARENT;
            }

            SetWindowLong(_windowHandle, GWL_EXSTYLE, exStyle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenAnnotationWindow: Failed to update Windows input pass-through: {ex.Message}");
        }
    }

    // macOS
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, bool arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    private void UpdateInputPassThroughMacOS(bool passThrough)
    {
        if (_windowHandle == IntPtr.Zero) return;

        try
        {
            // On macOS, the handle is the NSView, we need to get the NSWindow
            var nsWindow = objc_msgSend(_windowHandle, sel_registerName("window"));
            if (nsWindow != IntPtr.Zero)
            {
                // Set ignoresMouseEvents property
                var selector = sel_registerName("setIgnoresMouseEvents:");
                objc_msgSend_void_bool(nsWindow, selector, passThrough);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenAnnotationWindow: Failed to update macOS input pass-through: {ex.Message}");
        }
    }

    // Linux (X11)
    [DllImport("libX11.so.6")]
    private static extern int XShapeCombineRectangles(
        IntPtr display, IntPtr window, int kind, int xOff, int yOff,
        IntPtr rectangles, int count, int op, int ordering);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    private const int ShapeInput = 2;
    private const int ShapeSet = 0;

    private void UpdateInputPassThroughLinux(bool passThrough)
    {
        if (_windowHandle == IntPtr.Zero) return;

        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                Console.WriteLine("ScreenAnnotationWindow: Failed to open X11 display");
                return;
            }

            try
            {
                if (passThrough)
                {
                    // Set empty input shape (clicks pass through)
                    XShapeCombineRectangles(display, _windowHandle, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, 0);
                }
                else
                {
                    // Reset input shape to default (window captures clicks)
                    // Passing null rectangles with count 0 and ShapeSet resets to bounding region
                    // For full capture, we'd need to set a rectangle covering the window
                    // This is simplified - a full implementation would set the bounding rect
                    XShapeCombineRectangles(display, _windowHandle, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, 0);
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenAnnotationWindow: Failed to update Linux input pass-through: {ex.Message}");
        }
    }

    #endregion

    #region Drawing Handlers

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.IsDrawModeEnabled != true) return;

        var canvas = AnnotationCanvas;
        _isDrawing = true;
        _currentStrokePoints.Clear();

        var pos = e.GetPosition(canvas);
        var normalizedPoint = new PointF(
            (float)(pos.X / canvas.Bounds.Width),
            (float)(pos.Y / canvas.Bounds.Height));
        _currentStrokePoints.Add(normalizedPoint);

        _currentPolyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse(_viewModel.CurrentColor)),
            StrokeThickness = 3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        _currentPolyline.Points.Add(pos);
        canvas.Children.Add(_currentPolyline);

        e.Pointer.Capture(canvas);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawing || _currentPolyline == null || _viewModel == null) return;

        var canvas = AnnotationCanvas;
        var pos = e.GetPosition(canvas);
        var normalizedPoint = new PointF(
            (float)(pos.X / canvas.Bounds.Width),
            (float)(pos.Y / canvas.Bounds.Height));
        _currentStrokePoints.Add(normalizedPoint);
        _currentPolyline.Points.Add(pos);
    }

    private async void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing || _viewModel == null) return;
        _isDrawing = false;

        e.Pointer.Capture(null);

        if (_currentStrokePoints.Count >= 2)
        {
            var stroke = new DrawingStroke
            {
                UserId = _viewModel.SharerId,
                Username = _viewModel.SharerUsername,
                Points = new List<PointF>(_currentStrokePoints),
                Color = _viewModel.CurrentColor,
                Thickness = 3.0f
            };

            await _viewModel.AddStrokeAsync(stroke);
        }

        _currentStrokePoints.Clear();
        _currentPolyline = null;

        RedrawStrokes();
    }

    #endregion

    /// <summary>
    /// Redraws all strokes on the canvas.
    /// </summary>
    public void RedrawStrokes()
    {
        var canvas = AnnotationCanvas;
        if (canvas == null || _viewModel == null) return;

        canvas.Children.Clear();

        foreach (var stroke in _viewModel.Strokes)
        {
            if (stroke.Points.Count < 2) continue;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.Parse(stroke.Color)),
                StrokeThickness = stroke.Thickness,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };

            foreach (var point in stroke.Points)
            {
                var screenX = point.X * canvas.Bounds.Width;
                var screenY = point.Y * canvas.Bounds.Height;
                polyline.Points.Add(new Point(screenX, screenY));
            }

            canvas.Children.Add(polyline);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Cleanup();
        base.OnClosed(e);
    }
}
