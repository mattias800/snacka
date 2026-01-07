using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Miscord.Client.ViewModels;
using Miscord.Shared.Models;

namespace Miscord.Client.Views;

public partial class MainAppView : ReactiveUserControl<MainAppViewModel>
{
    // Drawing state
    private bool _isDrawing;
    private List<PointF> _currentStrokePoints = new();
    private Polyline? _currentPolyline;
    private Guid _currentStrokeId;
    private int _lastSentPointCount;
    private const int LiveUpdateThreshold = 30; // Send update every N points

    // Auto-scroll state - track if user is at bottom of message lists
    private bool _isMessagesAtBottom = true;
    private bool _isDMMessagesAtBottom = true;
    private const double ScrollBottomThreshold = 50; // pixels from bottom to consider "at bottom"

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

        // Subscribe to ViewModel changes for annotation redraw
        this.DataContextChanged += OnDataContextChanged;

        // Track scroll position for smart auto-scrolling
        MessagesScrollViewer.ScrollChanged += OnMessagesScrollChanged;
        DMMessagesScrollViewer.ScrollChanged += OnDMMessagesScrollChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Subscribe to collection changes for auto-scrolling
            ViewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            ViewModel.DMMessages.CollectionChanged += OnDMMessagesCollectionChanged;
        }
    }

    // Track if user is scrolled to the bottom of messages
    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = MessagesScrollViewer;
        if (scrollViewer == null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;
        _isMessagesAtBottom = distanceFromBottom <= ScrollBottomThreshold;
    }

    // Track if user is scrolled to the bottom of DM messages
    private void OnDMMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = DMMessagesScrollViewer;
        if (scrollViewer == null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;
        _isDMMessagesAtBottom = distanceFromBottom <= ScrollBottomThreshold;
    }

    // Auto-scroll to bottom when new messages arrive (if already at bottom)
    private void OnMessagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Reset scroll state when collection is cleared (channel changed)
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _isMessagesAtBottom = true;
            return;
        }

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && _isMessagesAtBottom)
        {
            // Delay scroll to allow layout to update
            Dispatcher.UIThread.Post(() =>
            {
                MessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    // Auto-scroll to bottom when new DM messages arrive (if already at bottom)
    private void OnDMMessagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Reset scroll state when collection is cleared (conversation changed)
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _isDMMessagesAtBottom = true;
            return;
        }

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && _isDMMessagesAtBottom)
        {
            // Delay scroll to allow layout to update
            Dispatcher.UIThread.Post(() =>
            {
                DMMessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainAppViewModel.CurrentAnnotationStrokes))
        {
            // Don't redraw while actively drawing - it would clear our current polyline
            // The redraw will happen when drawing completes
            if (_isDrawing) return;

            // Redraw annotations when strokes change (from host or other guests)
            RedrawAnnotations();
        }
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

    // Formatting toolbar handlers
    private void OnBoldClick(object? sender, RoutedEventArgs e) => WrapSelectionWith("**");
    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelectionWith("*");
    private void OnCodeClick(object? sender, RoutedEventArgs e) => WrapSelectionWith("`");

    private void WrapSelectionWith(string wrapper)
    {
        var textBox = MessageInputBox;
        if (textBox == null || ViewModel == null) return;

        var text = ViewModel.MessageInput ?? "";
        var selStart = textBox.SelectionStart;
        var selEnd = textBox.SelectionEnd;

        if (selStart > selEnd)
            (selStart, selEnd) = (selEnd, selStart);

        var selectedText = selEnd > selStart ? text.Substring(selStart, selEnd - selStart) : "";

        if (string.IsNullOrEmpty(selectedText))
        {
            // No selection - insert wrapper pair and place cursor between them
            var newText = text.Insert(selStart, wrapper + wrapper);
            ViewModel.MessageInput = newText;
            textBox.SelectionStart = selStart + wrapper.Length;
            textBox.SelectionEnd = selStart + wrapper.Length;
        }
        else
        {
            // Wrap the selected text
            var newText = text.Substring(0, selStart) + wrapper + selectedText + wrapper + text.Substring(selEnd);
            ViewModel.MessageInput = newText;
            textBox.SelectionStart = selStart;
            textBox.SelectionEnd = selEnd + wrapper.Length * 2;
        }

        textBox.Focus();
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
        _currentStrokeId = Guid.NewGuid(); // New stroke ID for this drawing
        _lastSentPointCount = 0;

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
        if (!_isDrawing || _currentPolyline == null || ViewModel == null) return;

        var canvas = AnnotationCanvas;
        if (canvas == null) return;

        var pos = e.GetPosition(canvas);
        var normalizedPoint = new PointF((float)(pos.X / canvas.Bounds.Width), (float)(pos.Y / canvas.Bounds.Height));
        _currentStrokePoints.Add(normalizedPoint);
        _currentPolyline.Points.Add(pos);

        // Send live update every N points (fire-and-forget to avoid blocking drawing)
        if (_currentStrokePoints.Count - _lastSentPointCount >= LiveUpdateThreshold)
        {
            _lastSentPointCount = _currentStrokePoints.Count;
            var stroke = new DrawingStroke
            {
                Id = _currentStrokeId,
                UserId = ViewModel.UserId,
                Username = ViewModel.Username,
                Points = new List<PointF>(_currentStrokePoints),
                Color = ViewModel.AnnotationColor,
                Thickness = 3.0f
            };
            // Fire-and-forget - don't await to keep drawing smooth
            _ = ViewModel.UpdateAnnotationStrokeAsync(stroke);
        }
    }

    private async void OnAnnotationCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
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
                    Id = _currentStrokeId, // Use the same ID from the live updates
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

            // Schedule a redraw after the stroke is added to _currentStrokes
            // This catches up on any strokes received during drawing (which were skipped)
            Dispatcher.UIThread.Post(RedrawAnnotations, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MainAppView: ERROR in OnAnnotationCanvasPointerReleased: {ex.Message}");
            Console.WriteLine($"MainAppView: Stack trace: {ex.StackTrace}");

            // Clean up state to prevent further issues
            _isDrawing = false;
            _currentStrokePoints.Clear();
            _currentPolyline = null;
        }
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
        try
        {
            var canvas = AnnotationCanvas;
            if (canvas == null || ViewModel == null) return;

            canvas.Children.Clear();

            // Take a snapshot of strokes to avoid collection modification during iteration
            var strokes = ViewModel.CurrentAnnotationStrokes.ToList();

            foreach (var stroke in strokes)
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
                // Take a snapshot of points too
                var points = stroke.Points.ToList();
                foreach (var point in points)
                {
                    var screenX = point.X * canvas.Bounds.Width;
                    var screenY = point.Y * canvas.Bounds.Height;
                    polyline.Points.Add(new Point(screenX, screenY));
                }

                canvas.Children.Add(polyline);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MainAppView: ERROR in RedrawAnnotations: {ex.Message}");
            Console.WriteLine($"MainAppView: Stack trace: {ex.StackTrace}");
        }
    }
}
