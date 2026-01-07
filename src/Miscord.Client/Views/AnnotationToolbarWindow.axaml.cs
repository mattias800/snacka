using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Views;

/// <summary>
/// Floating toolbar window for controlling screen share annotations.
/// Always stays on top and captures input (never passes through).
/// Can be dragged to reposition.
/// </summary>
public partial class AnnotationToolbarWindow : Window
{
    private ScreenAnnotationViewModel? _viewModel;
    private ScreenAnnotationWindow? _overlayWindow;
    private bool _isDragging;
    private Point _dragStartPosition;

    /// <summary>
    /// Event raised when the user closes the toolbar (stops screen annotation).
    /// </summary>
    public event Action? CloseRequested;

    public AnnotationToolbarWindow()
    {
        InitializeComponent();
        this.DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Sets the reference to the overlay window so we can close it when toolbar closes.
    /// </summary>
    public void SetOverlayWindow(ScreenAnnotationWindow overlayWindow)
    {
        _overlayWindow = overlayWindow;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel = DataContext as ScreenAnnotationViewModel;
    }

    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Enable dragging the toolbar
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStartPosition = e.GetPosition(this);
            e.Pointer.Capture((IInputElement?)sender);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isDragging)
        {
            var currentPosition = e.GetPosition(this);
            var delta = currentPosition - _dragStartPosition;
            Position = new PixelPoint(
                Position.X + (int)delta.X,
                Position.Y + (int)delta.Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    private void OnColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string color && _viewModel != null)
        {
            _viewModel.CurrentColor = color;
        }
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.ClearStrokesAsync();
            _overlayWindow?.RedrawStrokes();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        _overlayWindow?.Close();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Cleanup();
        base.OnClosed(e);
    }
}
