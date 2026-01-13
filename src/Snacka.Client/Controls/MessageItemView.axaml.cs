using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snacka.Client.Services;

namespace Snacka.Client.Controls;

/// <summary>
/// A reusable message display component that works with both channel messages and DMs.
/// Individual properties can be bound to allow flexibility with different message types.
/// </summary>
public partial class MessageItemView : UserControl
{
    private bool _isFlyoutOpen;
    private bool _isReactionPickerOpen;
    // The source message object (can be MessageResponse, DirectMessageResponse, or any other type)
    // Used for command parameters and event args
    public static readonly StyledProperty<object?> MessageProperty =
        AvaloniaProperty.Register<MessageItemView, object?>(nameof(Message));

    // Display properties - these are bound in XAML
    public static readonly StyledProperty<string?> AuthorUsernameProperty =
        AvaloniaProperty.Register<MessageItemView, string?>(nameof(AuthorUsername));

    public static readonly StyledProperty<string?> MessageContentProperty =
        AvaloniaProperty.Register<MessageItemView, string?>(nameof(MessageContent));

    public static readonly StyledProperty<DateTime?> CreatedAtProperty =
        AvaloniaProperty.Register<MessageItemView, DateTime?>(nameof(CreatedAt));

    public static readonly StyledProperty<bool> IsEditedProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(IsEdited), false);

    public static readonly StyledProperty<ReplyPreview?> ReplyToProperty =
        AvaloniaProperty.Register<MessageItemView, ReplyPreview?>(nameof(ReplyTo));

    public static readonly StyledProperty<IEnumerable<ReactionSummary>?> ReactionsProperty =
        AvaloniaProperty.Register<MessageItemView, IEnumerable<ReactionSummary>?>(nameof(Reactions));

    public static readonly StyledProperty<IEnumerable<AttachmentResponse>?> AttachmentsProperty =
        AvaloniaProperty.Register<MessageItemView, IEnumerable<AttachmentResponse>?>(nameof(Attachments));

    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(IsPinned), false);

    public static readonly StyledProperty<int> ReplyCountProperty =
        AvaloniaProperty.Register<MessageItemView, int>(nameof(ReplyCount), 0);

    // Feature toggle properties
    public static readonly StyledProperty<bool> ShowDateSeparatorProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowDateSeparator), false);

    public static readonly StyledProperty<bool> ShowThreadIndicatorProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowThreadIndicator), true);

    public static readonly StyledProperty<bool> ShowStartThreadButtonProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowStartThreadButton), true);

    public static readonly StyledProperty<bool> ShowPinButtonProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowPinButton), true);

    public static readonly StyledProperty<bool> ShowReplyButtonProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowReplyButton), true);

    public static readonly StyledProperty<bool> ShowReactionsProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowReactions), true);

    public static readonly StyledProperty<bool> ShowAttachmentsProperty =
        AvaloniaProperty.Register<MessageItemView, bool>(nameof(ShowAttachments), true);

    // Configuration properties
    public static readonly StyledProperty<string?> BaseUrlProperty =
        AvaloniaProperty.Register<MessageItemView, string?>(nameof(BaseUrl));

    public static readonly StyledProperty<string?> AccessTokenProperty =
        AvaloniaProperty.Register<MessageItemView, string?>(nameof(AccessToken));

    // Command properties
    public static readonly StyledProperty<ICommand?> TogglePinCommandProperty =
        AvaloniaProperty.Register<MessageItemView, ICommand?>(nameof(TogglePinCommand));

    public static readonly StyledProperty<ICommand?> ReplyToMessageCommandProperty =
        AvaloniaProperty.Register<MessageItemView, ICommand?>(nameof(ReplyToMessageCommand));

    public static readonly StyledProperty<ICommand?> StartEditMessageCommandProperty =
        AvaloniaProperty.Register<MessageItemView, ICommand?>(nameof(StartEditMessageCommand));

    public static readonly StyledProperty<ICommand?> DeleteMessageCommandProperty =
        AvaloniaProperty.Register<MessageItemView, ICommand?>(nameof(DeleteMessageCommand));

    public MessageItemView()
    {
        InitializeComponent();

        // Show/hide action buttons on hover
        MessageBorder.PointerEntered += OnMessagePointerEntered;
        MessageBorder.PointerExited += OnMessagePointerExited;

        // Track flyout state to keep buttons visible while flyout is open
        if (MoreOptionsButton.Flyout is FlyoutBase flyout)
        {
            flyout.Opened += OnFlyoutOpened;
            flyout.Closed += OnFlyoutClosed;
        }
    }

    private void OnMessagePointerEntered(object? sender, PointerEventArgs e)
    {
        ActionButtonsContainer.Opacity = 1;
        ActionButtonsContainer.IsHitTestVisible = true;
    }

    private void OnMessagePointerExited(object? sender, PointerEventArgs e)
    {
        // Don't hide if flyout or reaction picker is open
        if (!_isFlyoutOpen && !_isReactionPickerOpen)
        {
            ActionButtonsContainer.Opacity = 0;
            ActionButtonsContainer.IsHitTestVisible = false;
        }
    }

    /// <summary>
    /// Call this when the reaction picker is closed to allow hiding the action buttons.
    /// </summary>
    public void NotifyReactionPickerClosed()
    {
        _isReactionPickerOpen = false;
        ActionButtonsContainer.Opacity = 0;
        ActionButtonsContainer.IsHitTestVisible = false;
    }

    /// <summary>
    /// Gets the action buttons container for popup anchoring.
    /// </summary>
    public Control GetActionButtonsContainer() => ActionButtonsContainer;

    private void OnFlyoutOpened(object? sender, EventArgs e)
    {
        _isFlyoutOpen = true;
    }

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        _isFlyoutOpen = false;
        // Hide buttons after flyout closes if pointer is not over the message
        ActionButtonsContainer.Opacity = 0;
        ActionButtonsContainer.IsHitTestVisible = false;
    }

    // Source message (any type)
    public object? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    // Display properties
    public string? AuthorUsername
    {
        get => GetValue(AuthorUsernameProperty);
        set => SetValue(AuthorUsernameProperty, value);
    }

    public string? MessageContent
    {
        get => GetValue(MessageContentProperty);
        set => SetValue(MessageContentProperty, value);
    }

    public DateTime? CreatedAt
    {
        get => GetValue(CreatedAtProperty);
        set => SetValue(CreatedAtProperty, value);
    }

    public bool IsEdited
    {
        get => GetValue(IsEditedProperty);
        set => SetValue(IsEditedProperty, value);
    }

    public ReplyPreview? ReplyTo
    {
        get => GetValue(ReplyToProperty);
        set => SetValue(ReplyToProperty, value);
    }

    public IEnumerable<ReactionSummary>? Reactions
    {
        get => GetValue(ReactionsProperty);
        set => SetValue(ReactionsProperty, value);
    }

    public IEnumerable<AttachmentResponse>? Attachments
    {
        get => GetValue(AttachmentsProperty);
        set => SetValue(AttachmentsProperty, value);
    }

    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    public int ReplyCount
    {
        get => GetValue(ReplyCountProperty);
        set => SetValue(ReplyCountProperty, value);
    }

    // Feature toggles
    public bool ShowDateSeparator
    {
        get => GetValue(ShowDateSeparatorProperty);
        set => SetValue(ShowDateSeparatorProperty, value);
    }

    public bool ShowThreadIndicator
    {
        get => GetValue(ShowThreadIndicatorProperty);
        set => SetValue(ShowThreadIndicatorProperty, value);
    }

    public bool ShowStartThreadButton
    {
        get => GetValue(ShowStartThreadButtonProperty);
        set => SetValue(ShowStartThreadButtonProperty, value);
    }

    public bool ShowPinButton
    {
        get => GetValue(ShowPinButtonProperty);
        set => SetValue(ShowPinButtonProperty, value);
    }

    public bool ShowReplyButton
    {
        get => GetValue(ShowReplyButtonProperty);
        set => SetValue(ShowReplyButtonProperty, value);
    }

    public bool ShowReactions
    {
        get => GetValue(ShowReactionsProperty);
        set => SetValue(ShowReactionsProperty, value);
    }

    public bool ShowAttachments
    {
        get => GetValue(ShowAttachmentsProperty);
        set => SetValue(ShowAttachmentsProperty, value);
    }

    // Configuration
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

    // Commands
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

    // Events that bubble up to the parent - use object to support any message type
    public event EventHandler<object>? AddReactionRequested;
    public event EventHandler<object>? StartThreadRequested;
    public event EventHandler<object>? ViewThreadRequested;
    public event EventHandler<ReactionSummary>? ReactionToggleRequested;
    public event EventHandler<AttachmentResponse>? ImageClicked;

    private void AddReactionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Message != null)
        {
            _isReactionPickerOpen = true;
            AddReactionRequested?.Invoke(this, Message);
        }
    }

    private void StartThreadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Message != null)
        {
            StartThreadRequested?.Invoke(this, Message);
        }
    }

    private void ThreadIndicator_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Message != null)
        {
            ViewThreadRequested?.Invoke(this, Message);
        }
    }

    private void ReactionChip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is ReactionSummary reaction)
        {
            ReactionToggleRequested?.Invoke(this, reaction);
        }
    }

    private void AttachmentPreview_ImageClicked(object? sender, AttachmentResponse attachment)
    {
        ImageClicked?.Invoke(this, attachment);
    }

    private void MoreOptionsButton_Click(object? sender, RoutedEventArgs e)
    {
        // Update Pin menu item text based on current state
        PinMenuItem.Header = IsPinned ? "Unpin" : "Pin";
        MoreOptionsButton.Flyout?.ShowAt(MoreOptionsButton);
    }

    private void ReplyMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (Message != null)
        {
            ReplyToMessageCommand?.Execute(Message);
        }
    }

    private void PinMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (Message != null)
        {
            TogglePinCommand?.Execute(Message);
        }
    }

    private void EditMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (Message != null)
        {
            StartEditMessageCommand?.Execute(Message);
        }
    }

    private void DeleteMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (Message != null)
        {
            DeleteMessageCommand?.Execute(Message);
        }
    }
}
