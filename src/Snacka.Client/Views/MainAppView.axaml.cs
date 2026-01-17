using System.Linq;
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
using Snacka.Client.Controls;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;
using Snacka.Shared.Models;

namespace Snacka.Client.Views;

public partial class MainAppView : ReactiveUserControl<MainAppViewModel>
{
    // Drawing state
    private bool _isDrawing;
    private List<PointF> _currentStrokePoints = new();
    private Polyline? _currentPolyline;
    private Guid _currentStrokeId;
    private int _lastSentPointCount;
    private const int LiveUpdateThreshold = 30; // Send update every N points

    public MainAppView()
    {
        InitializeComponent();

        // ESC key to exit fullscreen video, Cmd+K for quick switcher (handledEventsToo to capture globally)
        this.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Push-to-talk: Space key handling
        this.AddHandler(KeyDownEvent, OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        this.AddHandler(KeyUpEvent, OnPushToTalkKeyUp, RoutingStrategies.Tunnel);

        // Subscribe to ViewModel changes for annotation redraw
        this.DataContextChanged += OnDataContextChanged;

        // Handle emoji picker popup closed (for light dismiss)
        EmojiPickerPopup.Closed += OnEmojiPickerPopupClosed;

        // Note: Autocomplete events are wired in XAML, not here
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.GpuFullscreenFrameReceived += OnGpuFullscreenFrameReceived;

            // Load saved panel widths
            LoadPanelWidths();
        }
    }

    private Grid? _mainContentGrid;
    private Grid? _rightSidebarGrid;

    private void LoadPanelWidths()
    {
        if (ViewModel?.SettingsStore == null) return;

        // Find the main content grid
        _mainContentGrid = this.FindControl<Grid>("MainContentGrid");
        if (_mainContentGrid == null)
        {
            Console.WriteLine("MainAppView: Could not find MainContentGrid");
            return;
        }

        var settings = ViewModel.SettingsStore.Settings;
        // Column 1 is the channel list, Column 5 is the members list
        _mainContentGrid.ColumnDefinitions[1].Width = new GridLength(settings.ChannelListWidth);
        _mainContentGrid.ColumnDefinitions[5].Width = new GridLength(settings.MembersListWidth);

        // Load activity panel ratio
        _rightSidebarGrid = this.FindControl<Grid>("RightSidebarGrid");
        if (_rightSidebarGrid != null)
        {
            var ratio = Math.Max(0.1, Math.Min(0.9, settings.ActivityPanelRatio));
            _rightSidebarGrid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
            _rightSidebarGrid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);
        }
    }

    private void OnLeftSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel?.SettingsStore == null || _mainContentGrid == null) return;

        var width = _mainContentGrid.ColumnDefinitions[1].Width.Value;
        ViewModel.SettingsStore.Settings.ChannelListWidth = width;
        ViewModel.SettingsStore.Save();
    }

    private void OnRightSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel?.SettingsStore == null || _mainContentGrid == null) return;

        var width = _mainContentGrid.ColumnDefinitions[5].Width.Value;
        ViewModel.SettingsStore.Settings.MembersListWidth = width;
        ViewModel.SettingsStore.Save();
    }

    private void OnActivitySplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (ViewModel?.SettingsStore == null || _rightSidebarGrid == null) return;

        // Calculate the ratio from the current star values
        var activityHeight = _rightSidebarGrid.RowDefinitions[0].Height;
        var membersHeight = _rightSidebarGrid.RowDefinitions[2].Height;

        if (activityHeight.IsStar && membersHeight.IsStar)
        {
            var total = activityHeight.Value + membersHeight.Value;
            if (total > 0)
            {
                var ratio = activityHeight.Value / total;
                ViewModel.SettingsStore.Settings.ActivityPanelRatio = ratio;
                ViewModel.SettingsStore.Save();
            }
        }
    }

    private bool _gpuVideoInitialized;

    private void OnGpuFullscreenFrameReceived(int width, int height, byte[] nv12Data)
    {
        // Ensure GpuVideoView is initialized with correct dimensions
        if (!_gpuVideoInitialized || FullscreenGpuVideo.VideoDimensions != (width, height))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (FullscreenGpuVideo.InitializeRenderer(width, height))
                {
                    _gpuVideoInitialized = true;
                    Console.WriteLine($"MainAppView: GPU video initialized for {width}x{height}");
                }
            });
        }

        // Render the frame (can be called from any thread)
        if (_gpuVideoInitialized)
        {
            FullscreenGpuVideo.RenderFrame(nv12Data);
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
        else if (e.PropertyName == nameof(MainAppViewModel.SelectedChannel))
        {
            // Auto-focus message input when a text channel is selected
            if (ViewModel?.SelectedChannel != null)
            {
                Dispatcher.UIThread.Post(() => ChatArea?.FocusMessageInput(), DispatcherPriority.Input);
            }
        }
        else if (e.PropertyName == nameof(MainAppViewModel.IsViewingDM))
        {
            if (ViewModel?.IsViewingDM == true)
            {
                // Auto-focus message input when viewing DM conversation
                Dispatcher.UIThread.Post(() => DMContentArea?.FocusMessageInput(), DispatcherPriority.Input);
            }
            else if (ViewModel?.SelectedChannel != null && ViewModel?.IsViewingVoiceChannel == false)
            {
                // Auto-focus channel input when closing DM and returning to text channel
                Dispatcher.UIThread.Post(() => ChatArea?.FocusMessageInput(), DispatcherPriority.Input);
            }
        }
        else if (e.PropertyName == nameof(MainAppViewModel.IsViewingVoiceChannel))
        {
            // Auto-focus channel input when closing voice channel view and returning to text channel
            if (ViewModel?.IsViewingVoiceChannel == false &&
                ViewModel?.IsViewingDM == false &&
                ViewModel?.SelectedChannel != null)
            {
                Dispatcher.UIThread.Post(() => ChatArea?.FocusMessageInput(), DispatcherPriority.Input);
            }
        }
        else if (e.PropertyName == nameof(MainAppViewModel.IsVideoFullscreen) ||
                 e.PropertyName == nameof(MainAppViewModel.FullscreenStream))
        {
            // Update controller button state when entering/exiting fullscreen
            Dispatcher.UIThread.Post(UpdateFullscreenControllerButton, DispatcherPriority.Normal);
        }
    }

    // Global key handler for ESC to exit fullscreen/overlay and Cmd+K / Ctrl+K for quick switcher
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel?.IsVideoFullscreen == true)
        {
            ViewModel.CloseFullscreen();
            e.Handled = true;
            return;
        }

        // ESC to close voice video overlay
        if (e.Key == Key.Escape && ViewModel?.IsVoiceVideoOverlayOpen == true)
        {
            ViewModel.HideVoiceVideoOverlayCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Handle Cmd+K (Mac) or Ctrl+K (Windows/Linux) for quick switcher
        var cmdOrCtrl = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.K && e.KeyModifiers == cmdOrCtrl)
        {
            OpenQuickSwitcher();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the quick switcher popup and initializes its ViewModel.
    /// </summary>
    public void OpenQuickSwitcher()
    {
        if (ViewModel == null) return;

        var quickSwitcherVm = new QuickSwitcherViewModel(
            ViewModel.Channels,
            ViewModel.VoiceChannelViewModels,
            ViewModel.Members,
            ViewModel.UserId,
            ViewModel.SettingsStore,
            OnQuickSwitcherItemSelected,
            () => QuickSwitcherPopup.IsOpen = false);

        QuickSwitcherContent.ViewModel = quickSwitcherVm;
        QuickSwitcherPopup.IsOpen = true;
    }

    /// <summary>
    /// Called when an item is selected in the quick switcher.
    /// </summary>
    private void OnQuickSwitcherItemSelected(QuickSwitcherItem item)
    {
        QuickSwitcherPopup.IsOpen = false;

        if (ViewModel == null) return;

        switch (item.Type)
        {
            case QuickSwitcherItemType.TextChannel:
                // Find and select the channel
                var channel = ViewModel.Channels.FirstOrDefault(c => c.Id == item.Id);
                if (channel != null)
                {
                    ViewModel.SelectChannelCommand.Execute(channel).Subscribe();
                }
                break;

            case QuickSwitcherItemType.VoiceChannel:
                // Find and join the voice channel
                var voiceChannel = ViewModel.VoiceChannelViewModels.FirstOrDefault(c => c.Id == item.Id);
                if (voiceChannel != null)
                {
                    ViewModel.JoinVoiceChannelCommand.Execute(voiceChannel.Channel).Subscribe();
                }
                break;

            case QuickSwitcherItemType.User:
                // Start DM with user
                var member = ViewModel.Members.FirstOrDefault(m => m.UserId == item.Id);
                if (member != null)
                {
                    ViewModel.MembersList?.StartDMCommand.Execute(member).Subscribe();
                }
                break;
        }
    }

    /// <summary>
    /// Opens the message search popup and initializes its ViewModel.
    /// </summary>
    public void OpenMessageSearch()
    {
        if (ViewModel?.SelectedCommunity == null) return;

        var messageSearchVm = new MessageSearchViewModel(
            ViewModel.ApiClient,
            ViewModel.SelectedCommunity.Id,
            OnMessageSearchResultSelected,
            () => MessageSearchPopup.IsOpen = false);

        MessageSearchContent.ViewModel = messageSearchVm;
        MessageSearchPopup.IsOpen = true;
    }

    /// <summary>
    /// Called when a message search result is selected.
    /// </summary>
    private async void OnMessageSearchResultSelected(Services.MessageSearchResult result)
    {
        MessageSearchPopup.IsOpen = false;

        if (ViewModel == null) return;

        // Find and select the channel containing the message
        var channel = ViewModel.Channels.FirstOrDefault(c => c.Id == result.Message.ChannelId);
        if (channel != null)
        {
            ViewModel.SelectChannelCommand.Execute(channel).Subscribe();

            // Wait for messages to load and layout to complete, then scroll to message
            await Task.Delay(300);
            ChatArea?.ScrollToMessage(result.Message.Id);
        }
    }

    // Called when clicking a member in the members list - opens DMs
    private void OnMemberClicked(object? sender, Services.CommunityMemberResponse member)
    {
        ViewModel?.MembersList?.StartDMCommand.Execute(member).Subscribe();
    }

    // Called when clicking a voice channel to join it
    private void OnVoiceChannelClicked(object? sender, Services.ChannelResponse channel)
    {
        if (ViewModel != null)
        {
            // Join the channel and show the voice UI
            ViewModel.JoinVoiceChannelCommand.Execute(channel).Subscribe();
            ViewModel.SelectedVoiceChannelForViewing = channel;
        }
    }

    // Called when clicking a voice channel we're already in (just view it)
    private void OnVoiceChannelViewRequested(object? sender, Services.ChannelResponse channel)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedVoiceChannelForViewing = channel;
        }
    }

    // Called when double-clicking a participant in the voice channel list
    private async void OnParticipantDoubleClicked(object? sender, (ViewModels.VoiceParticipantViewModel Participant, Services.ChannelResponse Channel) args)
    {
        if (ViewModel == null) return;

        var (participant, channel) = args;

        // Join the channel if not already in it
        if (ViewModel.CurrentVoiceChannel?.Id != channel.Id)
        {
            ViewModel.JoinVoiceChannelCommand.Execute(channel).Subscribe();
        }

        // Show the voice channel UI
        ViewModel.SelectedVoiceChannelForViewing = channel;

        // If the participant is screen sharing, auto-watch their stream
        if (participant.IsScreenSharing && ViewModel.VoiceChannelContent != null)
        {
            // Find the screen share stream for this user
            var screenShareStream = ViewModel.VoiceChannelContent.VideoStreams
                .FirstOrDefault(s => s.UserId == participant.Participant.UserId && s.IsRemoteScreenShare);

            if (screenShareStream != null)
            {
                await ViewModel.VoiceChannelContent.WatchScreenShareAsync(screenShareStream);
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

    // Store the current message and view for the emoji picker
    private Services.MessageResponse? _emojiPickerMessage;
    private MessageItemView? _emojiPickerMessageView;

    // ==================== ChatAreaView Event Handlers ====================

    private void OnChatAreaAddReactionRequested(object? sender, object message)
    {
        if (ViewModel == null || message is not Services.MessageResponse msgResponse) return;

        _emojiPickerMessage = msgResponse;
        _emojiPickerMessageView = sender as MessageItemView;
        var popup = this.FindControl<Popup>("EmojiPickerPopup");
        if (popup != null && _emojiPickerMessageView != null)
        {
            // Anchor to the action buttons container for proper positioning
            popup.PlacementTarget = _emojiPickerMessageView.GetActionButtonsContainer();
            popup.IsOpen = true;
        }
    }

    private void OnChatAreaStartThreadRequested(object? sender, object message)
    {
        if (message is Services.MessageResponse msgResponse)
        {
            ViewModel?.OpenThreadCommand?.Execute(msgResponse).Subscribe();
        }
    }

    private void OnChatAreaViewThreadRequested(object? sender, object message)
    {
        if (message is Services.MessageResponse msgResponse)
        {
            ViewModel?.OpenThreadCommand?.Execute(msgResponse).Subscribe();
        }
    }

    private void OnChatAreaReactionToggleRequested(object? sender, Services.ReactionSummary reaction)
    {
        if (ViewModel == null) return;

        // Find the message from the ChatAreaView
        if (sender is ChatAreaView chatArea)
        {
            // The reaction event includes the message context through the sender chain
            // We need to get the message from the original MessageItemView
        }
    }

    private void OnChatAreaImageClicked(object? sender, AttachmentResponse attachment)
    {
        ViewModel?.OpenLightbox(attachment);
    }

    /// <summary>
    /// Called when an image attachment is clicked to open the lightbox (legacy handler for ThreadPanel).
    /// </summary>
    private void OnAttachmentImageClicked(object? sender, AttachmentResponse attachment)
    {
        ViewModel?.OpenLightbox(attachment);
    }

    private void OnChatAreaAutocompleteSuggestionSelected(object? sender, Services.Autocomplete.IAutocompleteSuggestion suggestion)
    {
        if (ViewModel == null) return;
        var result = ViewModel.SelectAutocompleteSuggestionWithText(suggestion);
        if (result.HasValue)
        {
            ChatArea?.SetMessageInputTextAndCursor(result.Value.newText, result.Value.cursorPosition);
        }
        ChatArea?.FocusMessageInput();
    }

    private async void OnChatAreaGifButtonClicked(object? sender, EventArgs e)
    {
        if (ViewModel == null) return;

        var gifButton = ChatArea?.GetGifButton();
        if (gifButton != null)
        {
            GifPickerPopup.PlacementTarget = gifButton;
            GifPickerPopup.IsOpen = true;

            await ViewModel.LoadTrendingGifsAsync();
            GifPickerContent?.FocusSearchBox();
        }
    }

    private async void OnChatAreaAttachButtonClicked(object? sender, EventArgs e)
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

    private void OnChatAreaRemovePendingAttachment(object? sender, PendingAttachment attachment)
    {
        ViewModel?.RemovePendingAttachment(attachment);
    }

    private async void OnChatAreaFileDropped(object? sender, IStorageFile file)
    {
        await AddFileAsAttachmentAsync(file);
    }

    private int OnChatAreaNavigateAutocompleteUp()
    {
        ViewModel?.NavigateAutocompleteUp();
        return -1;
    }

    private int OnChatAreaNavigateAutocompleteDown()
    {
        ViewModel?.NavigateAutocompleteDown();
        return -1;
    }

    private (string newText, int cursorPosition)? OnChatAreaSelectCurrentAutocompleteSuggestion()
    {
        return ViewModel?.SelectCurrentAutocompleteSuggestionWithText();
    }

    private void OnChatAreaCloseAutocompletePopup()
    {
        ViewModel?.CloseAutocompletePopup();
    }

    // Thread message event handlers
    private void OnThreadMessageAddReactionRequested(object? sender, object message)
    {
        if (ViewModel == null || message is not Services.MessageResponse msgResponse) return;

        _emojiPickerMessage = msgResponse;
        _emojiPickerMessageView = sender as MessageItemView;
        var popup = this.FindControl<Popup>("EmojiPickerPopup");
        if (popup != null && _emojiPickerMessageView != null)
        {
            // Anchor to the action buttons container for proper positioning
            popup.PlacementTarget = _emojiPickerMessageView.GetActionButtonsContainer();
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

    // Called when an emoji is selected from the EmojiPickerContent component
    private void OnEmojiPickerEmojiSelected(object? sender, string emoji)
    {
        if (_emojiPickerMessage != null && ViewModel != null)
        {
            ViewModel.AddReactionCommand.Execute((_emojiPickerMessage, emoji)).Subscribe();
            CloseEmojiPicker();
        }
    }

    // Called when the emoji picker popup is closed (e.g., by light dismiss)
    private void OnEmojiPickerPopupClosed(object? sender, EventArgs e)
    {
        _emojiPickerMessageView?.NotifyReactionPickerClosed();
        _emojiPickerMessageView = null;
        _emojiPickerMessage = null;
    }

    private void CloseEmojiPicker()
    {
        EmojiPickerPopup.IsOpen = false;
        _emojiPickerMessageView?.NotifyReactionPickerClosed();
        _emojiPickerMessageView = null;
        _emojiPickerMessage = null;
    }

    // Legacy handler - Called when an emoji is selected from the picker (unused, kept for reference)
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

    /// <summary>
    /// Called when a GIF is selected from the GifPickerContent component.
    /// </summary>
    private async void OnGifPickerGifSelected(object? sender, Services.GifResult gif)
    {
        if (ViewModel == null) return;

        // Send the GIF as a message
        await ViewModel.SendGifMessageAsync(gif);

        // Close the popup
        GifPickerPopup.IsOpen = false;

        // Clear GIF state
        ViewModel.ClearGifResults();
    }

    // Called when Watch button is clicked in VoiceChannelContentView
    private async void OnVoiceChannelWatchScreenShare(object? sender, VideoStreamViewModel stream)
    {
        if (ViewModel?.VoiceChannelContent != null)
        {
            await ViewModel.VoiceChannelContent.WatchScreenShareAsync(stream);
        }
    }

    // Called when Stop Watching button is clicked in VoiceChannelContentView
    private async void OnVoiceChannelStopWatching(object? sender, VideoStreamViewModel stream)
    {
        if (ViewModel?.VoiceChannelContent != null)
        {
            await ViewModel.VoiceChannelContent.StopWatchingScreenShareAsync(stream);
        }
    }

    // Called when fullscreen button is clicked in VoiceChannelContentView
    private void OnVoiceChannelFullscreen(object? sender, VideoStreamViewModel stream)
    {
        ViewModel?.OpenFullscreen(stream);
    }

    // Called when video tile is double-tapped in VoiceChannelContentView
    private void OnVoiceChannelVideoTileDoubleTapped(object? sender, VideoStreamViewModel stream)
    {
        // Only allow fullscreen for screen shares (not camera streams)
        if (!string.IsNullOrEmpty(stream.StreamLabel))
        {
            ViewModel?.OpenFullscreen(stream);
        }
    }

    // Called when Share Controller button is clicked in VoiceChannelContentView
    private async void OnVoiceChannelShareController(object? sender, VideoStreamViewModel stream)
    {
        if (ViewModel != null)
        {
            await ViewModel.ToggleControllerAccessAsync(stream);
        }
    }

    // Called when clicking the close button on fullscreen video overlay
    private void OnCloseFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseFullscreen();
    }

    // Called when clicking the share controller button in fullscreen mode
    private async void OnFullscreenShareControllerClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.ToggleFullscreenControllerAccessAsync();
            UpdateFullscreenControllerButton();
        }
    }

    // Update the fullscreen controller button text based on streaming state
    private void UpdateFullscreenControllerButton()
    {
        var textBlock = this.FindControl<TextBlock>("FullscreenControllerButtonText");
        if (textBlock != null && ViewModel != null)
        {
            var isStreaming = ViewModel.FullscreenStream != null &&
                              ViewModel.IsStreamingControllerTo(ViewModel.FullscreenStream.UserId);
            textBlock.Text = isStreaming ? "Stop Sharing" : "Share Controller";
        }
    }

    // Called when clicking outside the voice video overlay content (closes the overlay)
    private void OnVoiceVideoOverlayBackgroundClick(object? sender, PointerPressedEventArgs e)
    {
        ViewModel?.HideVoiceVideoOverlayCommand.Execute().Subscribe();
    }

    // Called when clicking inside the voice video overlay content (prevents closing)
    private void OnVoiceVideoOverlayContentClick(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true; // Prevent bubbling to background
    }

    // Legacy handler - Called when clicking the Watch button on a screen share (unused)
    private async void OnWatchScreenShareClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is VideoStreamViewModel stream &&
            ViewModel?.VoiceChannelContent != null)
        {
            await ViewModel.VoiceChannelContent.WatchScreenShareAsync(stream);
        }
    }

    // Legacy handler - Called when clicking the fullscreen button on a video tile (unused)
    private void OnFullscreenButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VideoStreamViewModel stream)
        {
            ViewModel?.OpenFullscreen(stream);
        }
    }

    // Legacy handler - Called when double-clicking a screen share video tile (unused)
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

    // ==================== File Attachment Helpers ====================

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
    /// Called when the audio device button is clicked in UserPanelView to open the device selection popup.
    /// </summary>
    private void OnUserPanelAudioDeviceButtonClick(object? sender, EventArgs e)
    {
        ViewModel?.OpenAudioDevicePopup();
    }

    /// <summary>
    /// Called when the refresh devices button is clicked in AudioDeviceContent component.
    /// </summary>
    private void OnAudioDeviceRefreshRequested(object? sender, EventArgs e)
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

    // ==================== Invite User Popup Handlers ====================

    /// <summary>
    /// Called when a search is requested in the invite user popup.
    /// </summary>
    private async void OnInviteUserSearchRequested(object? sender, string query)
    {
        if (ViewModel != null)
        {
            await ViewModel.SearchUsersToInviteAsync(query);
        }
    }
}
