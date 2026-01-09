using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Miscord.Client.Controls;
using Miscord.Client.Services;
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

        // Push-to-talk: Space key handling
        this.AddHandler(KeyDownEvent, OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        this.AddHandler(KeyUpEvent, OnPushToTalkKeyUp, RoutingStrategies.Tunnel);

        // Subscribe to ViewModel changes for annotation redraw
        this.DataContextChanged += OnDataContextChanged;

        // Track scroll position for smart auto-scrolling
        MessagesScrollViewer.ScrollChanged += OnMessagesScrollChanged;
        DMMessagesScrollViewer.ScrollChanged += OnDMMessagesScrollChanged;

        // Drag-drop handlers for file attachments
        MessageInputBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        MessageInputBox.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Subscribe to collection changes for auto-scrolling
            ViewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            ViewModel.DMMessages.CollectionChanged += OnDMMessagesCollectionChanged;

            // Scroll to bottom if messages are already loaded (we missed the events)
            // Also scroll after a short delay to handle async loading
            ScrollToBottomAfterDelay();
        }
    }

    // Scroll to bottom after a delay to ensure content is loaded and laid out
    private async void ScrollToBottomAfterDelay()
    {
        // Wait for messages to load and layout to complete
        await Task.Delay(100);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            MessagesScrollViewer?.ScrollToEnd();
        });
    }

    // Track if user is scrolled to the bottom of messages
    private double _lastMessagesExtentHeight;
    private double _lastDMMessagesExtentHeight;

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = MessagesScrollViewer;
        if (scrollViewer == null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;

        // If content grew (e.g., link preview loaded) and we were at bottom, scroll to bottom again
        if (_isMessagesAtBottom && scrollViewer.Extent.Height > _lastMessagesExtentHeight && _lastMessagesExtentHeight > 0)
        {
            Dispatcher.UIThread.Post(() => scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }

        _lastMessagesExtentHeight = scrollViewer.Extent.Height;
        _isMessagesAtBottom = distanceFromBottom <= ScrollBottomThreshold;
        scrollViewer.VerticalScrollBarVisibility = _isMessagesAtBottom
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    // Track if user is scrolled to the bottom of DM messages
    private void OnDMMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = DMMessagesScrollViewer;
        if (scrollViewer == null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;

        // If content grew (e.g., link preview loaded) and we were at bottom, scroll to bottom again
        if (_isDMMessagesAtBottom && scrollViewer.Extent.Height > _lastDMMessagesExtentHeight && _lastDMMessagesExtentHeight > 0)
        {
            Dispatcher.UIThread.Post(() => scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }

        _lastDMMessagesExtentHeight = scrollViewer.Extent.Height;
        _isDMMessagesAtBottom = distanceFromBottom <= ScrollBottomThreshold;
        scrollViewer.VerticalScrollBarVisibility = _isDMMessagesAtBottom
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    // Auto-scroll to bottom when new messages arrive (if already at bottom)
    private void OnMessagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Reset scroll state when collection is cleared (channel changed)
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _isMessagesAtBottom = true;
            // Scroll to bottom after messages are loaded (delay to allow layout)
            Dispatcher.UIThread.Post(() =>
            {
                MessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
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
            // Scroll to bottom after messages are loaded (delay to allow layout)
            Dispatcher.UIThread.Post(() =>
            {
                DMMessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
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
        else if (e.PropertyName == nameof(MainAppViewModel.IsLoading))
        {
            // Scroll to bottom when loading completes
            if (ViewModel?.IsLoading == false && _isMessagesAtBottom)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    MessagesScrollViewer?.ScrollToEnd();
                }, DispatcherPriority.Background);
            }
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
        // Handle mention popup navigation
        if (ViewModel?.IsMentionPopupOpen == true)
        {
            switch (e.Key)
            {
                case Key.Up:
                    ViewModel.NavigateMentionUp();
                    e.Handled = true;
                    return;
                case Key.Down:
                    ViewModel.NavigateMentionDown();
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    var cursorPos = ViewModel.SelectCurrentMention();
                    if (cursorPos >= 0 && MessageInputBox != null)
                    {
                        MessageInputBox.SelectionStart = cursorPos;
                        MessageInputBox.SelectionEnd = cursorPos;
                    }
                    e.Handled = true;
                    return;
                case Key.Escape:
                    ViewModel.CloseMentionPopup();
                    e.Handled = true;
                    return;
            }
        }

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

    // Called when clicking a member in the members list - opens DMs
    private void OnMemberClicked(object? sender, Services.CommunityMemberResponse member)
    {
        ViewModel?.StartDMCommand.Execute(member).Subscribe();
    }

    // Called when clicking a voice channel to join it
    private void OnVoiceChannelClicked(object? sender, Services.ChannelResponse channel)
    {
        ViewModel?.JoinVoiceChannelCommand.Execute(channel).Subscribe();
    }

    // Called when clicking a voice channel we're already in (just view it)
    private void OnVoiceChannelViewRequested(object? sender, Services.ChannelResponse channel)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedVoiceChannelForViewing = channel;
        }
    }

    // Called when clicking a mention suggestion
    private void MentionSuggestion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is Services.CommunityMemberResponse member && ViewModel != null)
        {
            var cursorPos = ViewModel.SelectMention(member);
            // Keep focus on the message input and set cursor position
            if (MessageInputBox != null)
            {
                MessageInputBox.Focus();
                if (cursorPos >= 0)
                {
                    MessageInputBox.SelectionStart = cursorPos;
                    MessageInputBox.SelectionEnd = cursorPos;
                }
            }
        }
    }

    // Thread panel resize state
    private bool _isResizingThreadPanel;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private void ThreadPanelResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && ViewModel != null)
        {
            _isResizingThreadPanel = true;
            _resizeStartX = e.GetPosition(this).X;
            _resizeStartWidth = ViewModel.ThreadPanelWidth;
            e.Pointer.Capture(border);
            e.Handled = true;
        }
    }

    private void ThreadPanelResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isResizingThreadPanel && ViewModel != null)
        {
            var currentX = e.GetPosition(this).X;
            var delta = _resizeStartX - currentX;
            ViewModel.ThreadPanelWidth = _resizeStartWidth + delta;
            e.Handled = true;
        }
    }

    private void ThreadPanelResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizingThreadPanel)
        {
            _isResizingThreadPanel = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // Store the current message for the emoji picker
    private Services.MessageResponse? _emojiPickerMessage;

    // MessageItemView event handlers
    private void OnMessageAddReactionRequested(object? sender, object message)
    {
        if (ViewModel == null || message is not Services.MessageResponse msgResponse) return;

        _emojiPickerMessage = msgResponse;
        var popup = this.FindControl<Popup>("EmojiPickerPopup");
        if (popup != null && sender is Control control)
        {
            popup.PlacementTarget = control;
            popup.IsOpen = true;
        }
    }

    private void OnMessageStartThreadRequested(object? sender, object message)
    {
        if (message is Services.MessageResponse msgResponse)
        {
            ViewModel?.OpenThreadCommand?.Execute(msgResponse).Subscribe();
        }
    }

    private void OnMessageViewThreadRequested(object? sender, object message)
    {
        if (message is Services.MessageResponse msgResponse)
        {
            ViewModel?.OpenThreadCommand?.Execute(msgResponse).Subscribe();
        }
    }

    private void OnMessageReactionToggleRequested(object? sender, Services.ReactionSummary reaction)
    {
        if (ViewModel == null) return;

        // Find the message from the sender control
        if (sender is MessageItemView messageItemView && messageItemView.Message is Services.MessageResponse msgResponse)
        {
            ViewModel.ToggleReactionCommand.Execute((msgResponse, reaction.Emoji)).Subscribe();
        }
    }

    // Thread message event handlers
    private void OnThreadMessageAddReactionRequested(object? sender, object message)
    {
        if (ViewModel == null || message is not Services.MessageResponse msgResponse) return;

        _emojiPickerMessage = msgResponse;
        var popup = this.FindControl<Popup>("EmojiPickerPopup");
        if (popup != null && sender is Control control)
        {
            popup.PlacementTarget = control;
            popup.IsOpen = true;
        }
    }

    private void OnThreadMessageReactionToggleRequested(object? sender, Services.ReactionSummary reaction)
    {
        if (ViewModel == null) return;

        // Find the message from the sender control
        if (sender is MessageItemView messageItemView && messageItemView.Message is Services.MessageResponse msgResponse)
        {
            ViewModel.ToggleReactionCommand.Execute((msgResponse, reaction.Emoji)).Subscribe();
        }
    }

    // Called when an emoji is selected from the picker
    private void EmojiPicker_EmojiSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is string emoji &&
            _emojiPickerMessage != null &&
            ViewModel != null)
        {
            ViewModel.AddReactionCommand.Execute((_emojiPickerMessage, emoji)).Subscribe();
            _emojiPickerMessage = null;

            // Close the popup
            var popup = this.FindControl<Popup>("EmojiPickerPopup");
            if (popup != null)
            {
                popup.IsOpen = false;
            }
        }
    }

    // GIF Picker handlers
    private async void OnGifButtonClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var popup = this.FindControl<Popup>("GifPickerPopup");
        var gifButton = this.FindControl<Button>("GifButton");

        if (popup != null && gifButton != null)
        {
            popup.PlacementTarget = gifButton;
            popup.IsOpen = true;

            // Load trending GIFs when opening
            await ViewModel.LoadTrendingGifsAsync();

            // Focus the search box
            var searchBox = this.FindControl<TextBox>("GifSearchBox");
            searchBox?.Focus();
        }
    }

    private async void GifSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel != null)
        {
            e.Handled = true;
            await ViewModel.SearchGifsAsync();
        }
    }

    private async void GifPreview_GifClicked(object? sender, Services.GifResult gif)
    {
        if (ViewModel == null) return;

        // Send the GIF as a message
        await ViewModel.SendGifMessageAsync(gif);

        // Close the popup
        var popup = this.FindControl<Popup>("GifPickerPopup");
        if (popup != null)
        {
            popup.IsOpen = false;
        }

        // Clear GIF state
        ViewModel.ClearGifResults();
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

    // ==================== File Attachment Handlers ====================

    /// <summary>
    /// Called when a file is dragged over the message input.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Check if the drag contains files
#pragma warning disable CS0618 // Type or member is obsolete
        if (e.Data.Contains(DataFormats.Files))
#pragma warning restore CS0618
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Called when files are dropped onto the message input.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel == null) return;

#pragma warning disable CS0618 // Type or member is obsolete
        if (e.Data.Contains(DataFormats.Files))
        {
            var items = e.Data.GetFiles();
#pragma warning restore CS0618
            if (items != null)
            {
                foreach (var item in items)
                {
                    // Only process files, not folders
                    if (item is IStorageFile file)
                    {
                        await AddFileAsAttachmentAsync(file);
                    }
                }
            }
        }
        e.Handled = true;
    }

    /// <summary>
    /// Called when the attach button is clicked to open file picker.
    /// </summary>
    private async void OnAttachButtonClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select files to attach",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All supported files")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.gif", "*.webp", "*.pdf", "*.txt", "*.doc", "*.docx", "*.zip", "*.mp3", "*.wav", "*.ogg", "*.m4a", "*.flac", "*.aac" }
                },
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.gif", "*.webp" },
                    MimeTypes = new[] { "image/*" }
                },
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.m4a", "*.flac", "*.aac" },
                    MimeTypes = new[] { "audio/*" }
                },
                new FilePickerFileType("Documents")
                {
                    Patterns = new[] { "*.pdf", "*.txt", "*.doc", "*.docx" }
                },
                FilePickerFileTypes.All
            }
        });

        foreach (var file in files)
        {
            await AddFileAsAttachmentAsync(file);
        }
    }

    /// <summary>
    /// Called when the remove button is clicked on a pending attachment.
    /// </summary>
    private void OnRemovePendingAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is PendingAttachment attachment && ViewModel != null)
        {
            ViewModel.RemovePendingAttachment(attachment);
        }
    }

    /// <summary>
    /// Called when an image attachment is clicked to open the lightbox.
    /// </summary>
    private void OnAttachmentImageClicked(object? sender, AttachmentResponse attachment)
    {
        ViewModel?.OpenLightbox(attachment);
    }

    /// <summary>
    /// Called when the lightbox close is requested.
    /// </summary>
    private void OnLightboxCloseRequested(object? sender, EventArgs e)
    {
        ViewModel?.CloseLightbox();
    }

    /// <summary>
    /// Adds a file from the storage provider as a pending attachment.
    /// </summary>
    private async Task AddFileAsAttachmentAsync(IStorageFile file)
    {
        if (ViewModel == null) return;

        try
        {
            var props = await file.GetBasicPropertiesAsync();
            var size = (long)(props?.Size ?? 0);

            // Check file size (25MB limit)
            if (size > 25 * 1024 * 1024)
            {
                Console.WriteLine($"File too large: {file.Name} ({size} bytes)");
                return;
            }

            // Read file into memory stream
            await using var fileStream = await file.OpenReadAsync();
            var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            // Determine content type based on extension
            var extension = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
            var contentType = GetContentTypeFromExtension(extension);

            ViewModel.AddPendingAttachment(file.Name, memoryStream, size, contentType);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to add attachment: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the content type based on file extension.
    /// </summary>
    private static string GetContentTypeFromExtension(string extension)
    {
        return extension switch
        {
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".zip" => "application/zip",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            _ => "application/octet-stream"
        };
    }

    // ==================== Audio Device Popup Handlers ====================

    /// <summary>
    /// Called when the audio device button is clicked to open the device selection popup.
    /// </summary>
    private void OnAudioDeviceButtonClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenAudioDevicePopup();
    }

    /// <summary>
    /// Called when the refresh devices button is clicked.
    /// </summary>
    private void OnRefreshAudioDevicesClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RefreshAudioDevices();
    }

    // ==================== Push-to-Talk Handlers ====================

    /// <summary>
    /// Called when a key is pressed - handles push-to-talk activation.
    /// </summary>
    private void OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        // Only handle Space key for push-to-talk
        if (e.Key != Key.Space) return;

        // Don't trigger PTT when typing in a text box
        if (e.Source is TextBox) return;

        // Check if push-to-talk is enabled and we're in a voice channel
        if (ViewModel?.PushToTalkEnabled == true && ViewModel?.IsInVoiceChannel == true)
        {
            ViewModel.HandlePushToTalk(true);
            // Don't mark as handled - allow other key handlers to process if needed
        }
    }

    /// <summary>
    /// Called when a key is released - handles push-to-talk deactivation.
    /// </summary>
    private void OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        // Only handle Space key for push-to-talk
        if (e.Key != Key.Space) return;

        // Don't trigger PTT when typing in a text box
        if (e.Source is TextBox) return;

        // Check if push-to-talk is enabled and we're in a voice channel
        if (ViewModel?.PushToTalkEnabled == true && ViewModel?.IsInVoiceChannel == true)
        {
            ViewModel.HandlePushToTalk(false);
        }
    }
}
