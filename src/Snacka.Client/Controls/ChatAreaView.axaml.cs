using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Snacka.Client.Services;
using Snacka.Client.Services.Autocomplete;
using Snacka.Client.ViewModels;
using ReactiveUI;

namespace Snacka.Client.Controls;

/// <summary>
/// Chat area showing channel header, messages list, and message input.
/// </summary>
public partial class ChatAreaView : UserControl
{
    private const double ScrollBottomThreshold = 50;
    private bool _isMessagesAtBottom = true;
    private double _lastMessagesExtentHeight;

    #region Styled Properties

    public static readonly StyledProperty<string?> ChannelNameProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(ChannelName));

    public static readonly StyledProperty<string?> ChannelTopicProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(ChannelTopic));

    public static readonly StyledProperty<int> PinnedCountProperty =
        AvaloniaProperty.Register<ChatAreaView, int>(nameof(PinnedCount));

    public static readonly StyledProperty<bool> IsReplyingProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsReplying));

    public static readonly StyledProperty<string?> ReplyingToAuthorProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(ReplyingToAuthor));

    public static readonly StyledProperty<string?> ReplyingToContentProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(ReplyingToContent));

    public static readonly StyledProperty<bool> IsAnyoneTypingProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsAnyoneTyping));

    public static readonly StyledProperty<string?> TypingIndicatorTextProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(TypingIndicatorText));

    public static readonly StyledProperty<bool> HasPendingAttachmentsProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(HasPendingAttachments));

    public static readonly StyledProperty<IEnumerable<PendingAttachment>?> PendingAttachmentsProperty =
        AvaloniaProperty.Register<ChatAreaView, IEnumerable<PendingAttachment>?>(nameof(PendingAttachments));

    public static readonly StyledProperty<bool> IsAutocompletePopupOpenProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsAutocompletePopupOpen));

    public static readonly StyledProperty<IEnumerable<IAutocompleteSuggestion>?> AutocompleteSuggestionsProperty =
        AvaloniaProperty.Register<ChatAreaView, IEnumerable<IAutocompleteSuggestion>?>(nameof(AutocompleteSuggestions));

    public static readonly StyledProperty<int> SelectedAutocompleteIndexProperty =
        AvaloniaProperty.Register<ChatAreaView, int>(nameof(SelectedAutocompleteIndex));

    public static readonly StyledProperty<bool> IsGifsEnabledProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsGifsEnabled));

    public static readonly StyledProperty<bool> IsGifPreviewVisibleProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsGifPreviewVisible));

    public static readonly StyledProperty<GifResult?> GifPreviewResultProperty =
        AvaloniaProperty.Register<ChatAreaView, GifResult?>(nameof(GifPreviewResult));

    public static readonly StyledProperty<string?> GifPreviewQueryProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(GifPreviewQuery));

    public static readonly StyledProperty<string?> MessageInputProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(MessageInput));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsLoading));

    public static readonly StyledProperty<IEnumerable<MessageResponse>?> MessagesProperty =
        AvaloniaProperty.Register<ChatAreaView, IEnumerable<MessageResponse>?>(nameof(Messages));

    public static readonly StyledProperty<string?> BaseUrlProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(BaseUrl));

    public static readonly StyledProperty<string?> AccessTokenProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(AccessToken));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<ChatAreaView, bool>(nameof(IsEditing));

    public static readonly StyledProperty<string?> EditingMessageContentProperty =
        AvaloniaProperty.Register<ChatAreaView, string?>(nameof(EditingMessageContent));

    #endregion

    #region Command Properties

    public static readonly StyledProperty<ICommand?> ShowPinnedMessagesCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(ShowPinnedMessagesCommand));

    public static readonly StyledProperty<ICommand?> CancelReplyCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(CancelReplyCommand));

    public static readonly StyledProperty<ICommand?> SendMessageCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(SendMessageCommand));

    public static readonly StyledProperty<ICommand?> TogglePinCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(TogglePinCommand));

    public static readonly StyledProperty<ICommand?> ReplyToMessageCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(ReplyToMessageCommand));

    public static readonly StyledProperty<ICommand?> StartEditMessageCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(StartEditMessageCommand));

    public static readonly StyledProperty<ICommand?> DeleteMessageCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(DeleteMessageCommand));

    public static readonly StyledProperty<ICommand?> CancelEditMessageCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(CancelEditMessageCommand));

    public static readonly StyledProperty<ICommand?> SaveMessageEditCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(SaveMessageEditCommand));

    public static readonly StyledProperty<ICommand?> SendGifPreviewCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(SendGifPreviewCommand));

    public static readonly StyledProperty<ICommand?> ShuffleGifPreviewCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(ShuffleGifPreviewCommand));

    public static readonly StyledProperty<ICommand?> CancelGifPreviewCommandProperty =
        AvaloniaProperty.Register<ChatAreaView, ICommand?>(nameof(CancelGifPreviewCommand));

    #endregion

    public ChatAreaView()
    {
        InitializeComponent();

        // Use tunneling events to intercept Enter before AcceptsReturn processes it
        MessageInputBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        EditMessageInputBox.AddHandler(KeyDownEvent, OnEditMessageKeyDown, RoutingStrategies.Tunnel);

        // Track scroll position for smart auto-scrolling
        MessagesScrollViewer.ScrollChanged += OnMessagesScrollChanged;

        // Drag-drop handlers for file attachments
        MessageInputBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        MessageInputBox.AddHandler(DragDrop.DropEvent, OnDrop);

        // Subscribe to collection changes for auto-scrolling
        this.GetObservable(MessagesProperty).Subscribe(OnMessagesChanged);
    }

    #region Property Accessors

    public string? ChannelName
    {
        get => GetValue(ChannelNameProperty);
        set => SetValue(ChannelNameProperty, value);
    }

    public string? ChannelTopic
    {
        get => GetValue(ChannelTopicProperty);
        set => SetValue(ChannelTopicProperty, value);
    }

    public int PinnedCount
    {
        get => GetValue(PinnedCountProperty);
        set => SetValue(PinnedCountProperty, value);
    }

    public bool IsReplying
    {
        get => GetValue(IsReplyingProperty);
        set => SetValue(IsReplyingProperty, value);
    }

    public string? ReplyingToAuthor
    {
        get => GetValue(ReplyingToAuthorProperty);
        set => SetValue(ReplyingToAuthorProperty, value);
    }

    public string? ReplyingToContent
    {
        get => GetValue(ReplyingToContentProperty);
        set => SetValue(ReplyingToContentProperty, value);
    }

    public bool IsAnyoneTyping
    {
        get => GetValue(IsAnyoneTypingProperty);
        set => SetValue(IsAnyoneTypingProperty, value);
    }

    public string? TypingIndicatorText
    {
        get => GetValue(TypingIndicatorTextProperty);
        set => SetValue(TypingIndicatorTextProperty, value);
    }

    public bool HasPendingAttachments
    {
        get => GetValue(HasPendingAttachmentsProperty);
        set => SetValue(HasPendingAttachmentsProperty, value);
    }

    public IEnumerable<PendingAttachment>? PendingAttachments
    {
        get => GetValue(PendingAttachmentsProperty);
        set => SetValue(PendingAttachmentsProperty, value);
    }

    public bool IsAutocompletePopupOpen
    {
        get => GetValue(IsAutocompletePopupOpenProperty);
        set => SetValue(IsAutocompletePopupOpenProperty, value);
    }

    public IEnumerable<IAutocompleteSuggestion>? AutocompleteSuggestions
    {
        get => GetValue(AutocompleteSuggestionsProperty);
        set => SetValue(AutocompleteSuggestionsProperty, value);
    }

    public int SelectedAutocompleteIndex
    {
        get => GetValue(SelectedAutocompleteIndexProperty);
        set => SetValue(SelectedAutocompleteIndexProperty, value);
    }

    public bool IsGifsEnabled
    {
        get => GetValue(IsGifsEnabledProperty);
        set => SetValue(IsGifsEnabledProperty, value);
    }

    public bool IsGifPreviewVisible
    {
        get => GetValue(IsGifPreviewVisibleProperty);
        set => SetValue(IsGifPreviewVisibleProperty, value);
    }

    public GifResult? GifPreviewResult
    {
        get => GetValue(GifPreviewResultProperty);
        set => SetValue(GifPreviewResultProperty, value);
    }

    public string? GifPreviewQuery
    {
        get => GetValue(GifPreviewQueryProperty);
        set => SetValue(GifPreviewQueryProperty, value);
    }

    public string? MessageInput
    {
        get => GetValue(MessageInputProperty);
        set => SetValue(MessageInputProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public IEnumerable<MessageResponse>? Messages
    {
        get => GetValue(MessagesProperty);
        set => SetValue(MessagesProperty, value);
    }

    public string? BaseUrl
    {
        get => GetValue(BaseUrlProperty);
        set => SetValue(BaseUrlProperty, value);
    }

    public string? AccessToken
    {
        get => GetValue(AccessTokenProperty);
        set => SetValue(AccessTokenProperty, value);
    }

    public bool IsEditing
    {
        get => GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public string? EditingMessageContent
    {
        get => GetValue(EditingMessageContentProperty);
        set => SetValue(EditingMessageContentProperty, value);
    }

    #endregion

    #region Command Accessors

    public ICommand? ShowPinnedMessagesCommand
    {
        get => GetValue(ShowPinnedMessagesCommandProperty);
        set => SetValue(ShowPinnedMessagesCommandProperty, value);
    }

    public ICommand? CancelReplyCommand
    {
        get => GetValue(CancelReplyCommandProperty);
        set => SetValue(CancelReplyCommandProperty, value);
    }

    public ICommand? SendMessageCommand
    {
        get => GetValue(SendMessageCommandProperty);
        set => SetValue(SendMessageCommandProperty, value);
    }

    public ICommand? TogglePinCommand
    {
        get => GetValue(TogglePinCommandProperty);
        set => SetValue(TogglePinCommandProperty, value);
    }

    public ICommand? ReplyToMessageCommand
    {
        get => GetValue(ReplyToMessageCommandProperty);
        set => SetValue(ReplyToMessageCommandProperty, value);
    }

    public ICommand? StartEditMessageCommand
    {
        get => GetValue(StartEditMessageCommandProperty);
        set => SetValue(StartEditMessageCommandProperty, value);
    }

    public ICommand? DeleteMessageCommand
    {
        get => GetValue(DeleteMessageCommandProperty);
        set => SetValue(DeleteMessageCommandProperty, value);
    }

    public ICommand? CancelEditMessageCommand
    {
        get => GetValue(CancelEditMessageCommandProperty);
        set => SetValue(CancelEditMessageCommandProperty, value);
    }

    public ICommand? SaveMessageEditCommand
    {
        get => GetValue(SaveMessageEditCommandProperty);
        set => SetValue(SaveMessageEditCommandProperty, value);
    }

    public ICommand? SendGifPreviewCommand
    {
        get => GetValue(SendGifPreviewCommandProperty);
        set => SetValue(SendGifPreviewCommandProperty, value);
    }

    public ICommand? ShuffleGifPreviewCommand
    {
        get => GetValue(ShuffleGifPreviewCommandProperty);
        set => SetValue(ShuffleGifPreviewCommandProperty, value);
    }

    public ICommand? CancelGifPreviewCommand
    {
        get => GetValue(CancelGifPreviewCommandProperty);
        set => SetValue(CancelGifPreviewCommandProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<object>? AddReactionRequested;
    public event EventHandler<object>? StartThreadRequested;
    public event EventHandler<object>? ViewThreadRequested;
    public event EventHandler<ReactionSummary>? ReactionToggleRequested;
    public event EventHandler<AttachmentResponse>? ImageClicked;
    public event EventHandler<IAutocompleteSuggestion>? AutocompleteSuggestionSelected;
    public event EventHandler? GifButtonClicked;
    public event EventHandler? AttachButtonClicked;
    public event EventHandler<PendingAttachment>? RemovePendingAttachmentRequested;
    public event EventHandler<IStorageFile>? FileDropped;
    public event Func<int>? NavigateAutocompleteUp;
    public event Func<int>? NavigateAutocompleteDown;
    public event Func<(string newText, int cursorPosition)?>? SelectCurrentAutocompleteSuggestion;
    public event Action? CloseAutocompletePopup;

    #endregion

    #region Message Collection Changes and Scrolling

    private INotifyCollectionChanged? _currentMessagesCollection;

    private void OnMessagesChanged(IEnumerable<MessageResponse>? messages)
    {
        if (_currentMessagesCollection != null)
        {
            _currentMessagesCollection.CollectionChanged -= OnMessagesCollectionChanged;
        }

        if (messages is INotifyCollectionChanged notifyCollection)
        {
            _currentMessagesCollection = notifyCollection;
            _currentMessagesCollection.CollectionChanged += OnMessagesCollectionChanged;
        }

        ScrollToBottomAfterDelay();
    }

    private async void ScrollToBottomAfterDelay()
    {
        await Task.Delay(100);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            MessagesScrollViewer?.ScrollToEnd();
        });
    }

    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = MessagesScrollViewer;
        if (scrollViewer == null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;

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

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _isMessagesAtBottom = true;
            Dispatcher.UIThread.Post(() =>
            {
                MessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && _isMessagesAtBottom)
        {
            Dispatcher.UIThread.Post(() =>
            {
                MessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    #endregion

    #region Keyboard Handlers

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        // Handle autocomplete popup navigation (@ mentions and / commands)
        if (IsAutocompletePopupOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    NavigateAutocompleteUp?.Invoke();
                    e.Handled = true;
                    return;
                case Key.Down:
                    NavigateAutocompleteDown?.Invoke();
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    var result = SelectCurrentAutocompleteSuggestion?.Invoke();
                    if (result.HasValue && MessageInputBox != null)
                    {
                        var textBox = MessageInputBox;
                        var newText = result.Value.newText;
                        var cursorPos = result.Value.cursorPosition;

                        // Set TextBox.Text directly to bypass binding timing issues
                        textBox.Text = newText;

                        // Post caret update to run after TextBox finishes processing
                        Dispatcher.UIThread.Post(() =>
                        {
                            textBox.SelectionStart = cursorPos;
                            textBox.SelectionEnd = cursorPos;
                        }, DispatcherPriority.Render);
                    }
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseAutocompletePopup?.Invoke();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (SendMessageCommand is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                if (reactiveCmd.CanExecute.FirstAsync().GetAwaiter().GetResult())
                {
                    reactiveCmd.Execute().Subscribe();
                }
            }
            else
            {
                SendMessageCommand?.Execute(null);
            }
        }
    }

    private void OnEditMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (SaveMessageEditCommand is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                if (reactiveCmd.CanExecute.FirstAsync().GetAwaiter().GetResult())
                {
                    reactiveCmd.Execute().Subscribe();
                }
            }
            else
            {
                SaveMessageEditCommand?.Execute(null);
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (CancelEditMessageCommand is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                reactiveCmd.Execute().Subscribe();
            }
            else
            {
                CancelEditMessageCommand?.Execute(null);
            }
            e.Handled = true;
        }
    }

    #endregion

    #region Formatting Toolbar

    private void OnBoldClick(object? sender, RoutedEventArgs e) => WrapSelectionWith("**");
    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelectionWith("*");
    private void OnCodeClick(object? sender, RoutedEventArgs e) => WrapSelectionWith("`");

    private void WrapSelectionWith(string wrapper)
    {
        var textBox = MessageInputBox;
        if (textBox == null) return;

        var text = MessageInput ?? "";
        var selStart = textBox.SelectionStart;
        var selEnd = textBox.SelectionEnd;

        if (selStart > selEnd)
            (selStart, selEnd) = (selEnd, selStart);

        var selectedText = selEnd > selStart ? text.Substring(selStart, selEnd - selStart) : "";

        if (string.IsNullOrEmpty(selectedText))
        {
            var newText = text.Insert(selStart, wrapper + wrapper);
            MessageInput = newText;
            textBox.SelectionStart = selStart + wrapper.Length;
            textBox.SelectionEnd = selStart + wrapper.Length;
        }
        else
        {
            var newText = text.Substring(0, selStart) + wrapper + selectedText + wrapper + text.Substring(selEnd);
            MessageInput = newText;
            textBox.SelectionStart = selStart;
            textBox.SelectionEnd = selEnd + wrapper.Length * 2;
        }

        textBox.Focus();
    }

    #endregion

    #region Message Event Handlers

    private void OnMessageAddReactionRequested(object? sender, object message)
    {
        AddReactionRequested?.Invoke(sender, message);
    }

    private void OnMessageStartThreadRequested(object? sender, object message)
    {
        StartThreadRequested?.Invoke(sender, message);
    }

    private void OnMessageViewThreadRequested(object? sender, object message)
    {
        ViewThreadRequested?.Invoke(sender, message);
    }

    private void OnMessageReactionToggleRequested(object? sender, ReactionSummary reaction)
    {
        ReactionToggleRequested?.Invoke(sender, reaction);
    }

    private void OnAttachmentImageClicked(object? sender, AttachmentResponse attachment)
    {
        ImageClicked?.Invoke(sender, attachment);
    }

    private void AutocompleteSuggestion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is IAutocompleteSuggestion suggestion)
        {
            AutocompleteSuggestionSelected?.Invoke(this, suggestion);
            MessageInputBox?.Focus();
        }
    }

    #endregion

    #region Button Click Handlers

    private void OnGifButtonClick(object? sender, RoutedEventArgs e)
    {
        GifButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnAttachButtonClick(object? sender, RoutedEventArgs e)
    {
        AttachButtonClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemovePendingAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is PendingAttachment attachment)
        {
            RemovePendingAttachmentRequested?.Invoke(this, attachment);
        }
    }

    #endregion

    #region Drag and Drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
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

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        if (e.Data.Contains(DataFormats.Files))
        {
            var items = e.Data.GetFiles();
#pragma warning restore CS0618
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item is IStorageFile file)
                    {
                        FileDropped?.Invoke(this, file);
                    }
                }
            }
        }
        e.Handled = true;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Gets the GIF button control for positioning the GIF picker popup.
    /// </summary>
    public Button? GetGifButton() => GifButton;

    /// <summary>
    /// Sets both the text and cursor position in the message input box.
    /// This sets the TextBox.Text directly to ensure correct caret positioning.
    /// </summary>
    public void SetMessageInputTextAndCursor(string text, int cursorPosition)
    {
        if (MessageInputBox != null)
        {
            var textBox = MessageInputBox;
            textBox.Text = text;

            // Post caret update to run after TextBox finishes processing
            Dispatcher.UIThread.Post(() =>
            {
                textBox.SelectionStart = cursorPosition;
                textBox.SelectionEnd = cursorPosition;
            }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Sets the cursor position in the message input box.
    /// </summary>
    public void SetMessageInputCursorPosition(int position)
    {
        if (MessageInputBox != null)
        {
            MessageInputBox.SelectionStart = position;
            MessageInputBox.SelectionEnd = position;
        }
    }

    /// <summary>
    /// Focuses the message input box.
    /// </summary>
    public void FocusMessageInput()
    {
        MessageInputBox?.Focus();
    }

    /// <summary>
    /// Scrolls to a specific message by its ID.
    /// </summary>
    public void ScrollToMessage(Guid messageId)
    {
        if (Messages == null) return;

        // Find the index of the message
        var messages = Messages.ToList();
        var index = messages.FindIndex(m => m.Id == messageId);
        if (index < 0) return;

        // Delay to allow layout to complete after channel switch
        Dispatcher.UIThread.Post(() =>
        {
            // Get the container for this index
            var container = MessagesList.ContainerFromIndex(index);
            if (container is Control control)
            {
                // Scroll the item into view
                control.BringIntoView();

                // Flash highlight effect could be added here
            }
        }, DispatcherPriority.Background);
    }

    #endregion
}
