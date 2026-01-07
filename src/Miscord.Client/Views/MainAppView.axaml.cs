using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Miscord.Client.ViewModels;
using Miscord.Shared.Models;

namespace Miscord.Client.Views;

public partial class MainAppView : ReactiveUserControl<MainAppViewModel>
{
    // Drawing state
    private bool _isDrawing;
    private List<PointF> _currentStrokePoints = new();
    private Polyline? _currentPolyline;

    public MainAppView()
    {
        InitializeComponent();

        // Use tunneling (Preview) events to intercept Enter before AcceptsReturn processes it
        MessageInputBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        EditMessageInputBox.AddHandler(KeyDownEvent, OnEditMessageKeyDown, RoutingStrategies.Tunnel);
        DMMessageInputBox.AddHandler(KeyDownEvent, OnDMMessageKeyDown, RoutingStrategies.Tunnel);
        EditDMMessageInputBox.AddHandler(KeyDownEvent, OnEditDMMessageKeyDown, RoutingStrategies.Tunnel);

        // ESC key to exit fullscreen video
        this.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
    }

    // Global key handler for ESC to exit fullscreen
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel?.IsVideoFullscreen == true)
        {
            ViewModel.CloseFullscreen();
            e.Handled = true;
        }
    }

    // Called for message input TextBox (tunneling event)
    // Enter sends message, Shift+Enter inserts newline
    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Enter only = send message (mark handled to prevent newline)
            e.Handled = true;

            if (ViewModel?.SendMessageCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SendMessageCommand.Execute().Subscribe();
            }
        }
        // Shift+Enter = let AcceptsReturn handle it (inserts newline)
    }

    // Called from XAML for channel rename TextBox
    public void OnChannelRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel?.SaveChannelNameCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
        {
            ViewModel.SaveChannelNameCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelEditChannelCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    // Called for message edit TextBox (tunneling event)
    // Enter saves edit, Shift+Enter inserts newline
    private void OnEditMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Enter only = save edit (mark handled to prevent newline)
            e.Handled = true;

            if (ViewModel?.SaveMessageEditCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SaveMessageEditCommand.Execute().Subscribe();
            }
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelEditMessageCommand.Execute().Subscribe();
            e.Handled = true;
        }
        // Shift+Enter = let AcceptsReturn handle it (inserts newline)
    }

    // Called when clicking a voice channel
    private void VoiceChannel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is Services.ChannelResponse channel)
        {
            // Visual feedback - darken on press
            border.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3f4248"));

            // Check if already in this voice channel
            if (ViewModel?.CurrentVoiceChannel?.Id == channel.Id)
            {
                // Already in this channel - just view it (don't rejoin)
                ViewModel.SelectedVoiceChannelForViewing = channel;
            }
            else
            {
                // Join the channel
                ViewModel?.JoinVoiceChannelCommand.Execute(channel).Subscribe();
            }
        }
    }

    // Reset background when pointer released
    private void VoiceChannel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = Avalonia.Media.Brushes.Transparent;
        }
    }

    // Called when clicking a member in the members list - opens DMs
    private void Member_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is Services.CommunityMemberResponse member)
        {
            ViewModel?.StartDMCommand.Execute(member).Subscribe();
        }
    }

    // Called for DM message input TextBox (tunneling event)
    // Enter sends message, Shift+Enter inserts newline
    private void OnDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (ViewModel?.SendDMMessageCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SendDMMessageCommand.Execute().Subscribe();
            }
        }
    }

    // Called for DM message edit TextBox (tunneling event)
    // Enter saves edit, Shift+Enter inserts newline
    private void OnEditDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (ViewModel?.SaveDMMessageEditCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SaveDMMessageEditCommand.Execute().Subscribe();
            }
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelEditDMMessageCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    // Called when clicking the Watch button on a screen share
    private async void OnWatchScreenShareClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is VideoStreamViewModel stream &&
            ViewModel?.VoiceChannelContent != null)
        {
            await ViewModel.VoiceChannelContent.WatchScreenShareAsync(stream);
        }
    }

    // Called when clicking the close button on fullscreen video overlay
    private void OnCloseFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseFullscreen();
    }

    // Called when clicking the fullscreen button on a video tile
    private void OnFullscreenButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VideoStreamViewModel stream)
        {
            ViewModel?.OpenFullscreen(stream);
        }
    }

    // Called when double-clicking a screen share video tile
    private void OnVideoTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && border.Tag is VideoStreamViewModel stream)
        {
            // Only allow fullscreen for screen shares (not camera streams)
            if (!string.IsNullOrEmpty(stream.StreamLabel))
            {
                ViewModel?.OpenFullscreen(stream);
            }
        }
    }

    // ==================== Drawing Annotation Handlers ====================

    private void OnAnnotationCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.IsAnnotationEnabled != true) return;

        var canvas = AnnotationCanvas;
        if (canvas == null) return;

        _isDrawing = true;
        _currentStrokePoints.Clear();

        // Get position and normalize to 0-1 range
        var pos = e.GetPosition(canvas);
        var normalizedPoint = new PointF((float)(pos.X / canvas.Bounds.Width), (float)(pos.Y / canvas.Bounds.Height));
        _currentStrokePoints.Add(normalizedPoint);

        // Create a new polyline for visual feedback
        _currentPolyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.Parse(ViewModel.AnnotationColor)),
            StrokeThickness = 3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
        _currentPolyline.Points.Add(pos);
        canvas.Children.Add(_currentPolyline);

        e.Pointer.Capture(canvas);
    }

    private void OnAnnotationCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawing || _currentPolyline == null) return;

        var canvas = AnnotationCanvas;
        if (canvas == null) return;

        var pos = e.GetPosition(canvas);
        var normalizedPoint = new PointF((float)(pos.X / canvas.Bounds.Width), (float)(pos.Y / canvas.Bounds.Height));
        _currentStrokePoints.Add(normalizedPoint);
        _currentPolyline.Points.Add(pos);
    }

    private async void OnAnnotationCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;

        var canvas = AnnotationCanvas;
        if (canvas == null) return;

        e.Pointer.Capture(null);

        // Only save stroke if we have at least 2 points
        if (_currentStrokePoints.Count >= 2 && ViewModel != null)
        {
            var stroke = new DrawingStroke
            {
                UserId = ViewModel.UserId,
                Username = ViewModel.Username,
                Points = new List<PointF>(_currentStrokePoints),
                Color = ViewModel.AnnotationColor,
                Thickness = 3.0f
            };

            await ViewModel.AddAnnotationStrokeAsync(stroke);
        }

        _currentStrokePoints.Clear();
        _currentPolyline = null;

        // Redraw all strokes to ensure consistency
        RedrawAnnotations();
    }

    private void OnAnnotationColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string color && ViewModel != null)
        {
            ViewModel.AnnotationColor = color;
        }
    }

    private async void OnClearAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.ClearAnnotationsAsync();
            AnnotationCanvas?.Children.Clear();
        }
    }

    /// <summary>
    /// Redraws all annotation strokes on the canvas.
    /// Called when strokes are received from other users or after completing a local stroke.
    /// </summary>
    private void RedrawAnnotations()
    {
        var canvas = AnnotationCanvas;
        if (canvas == null || ViewModel == null) return;

        canvas.Children.Clear();

        foreach (var stroke in ViewModel.CurrentAnnotationStrokes)
        {
            if (stroke.Points.Count < 2) continue;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.Parse(stroke.Color)),
                StrokeThickness = stroke.Thickness,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            };

            // Convert normalized coordinates back to screen coordinates
            foreach (var point in stroke.Points)
            {
                var screenX = point.X * canvas.Bounds.Width;
                var screenY = point.Y * canvas.Bounds.Height;
                polyline.Points.Add(new Point(screenX, screenY));
            }

            canvas.Children.Add(polyline);
        }
    }
}
