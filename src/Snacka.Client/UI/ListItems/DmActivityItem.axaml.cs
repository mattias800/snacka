using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Snacka.Client.UI.ListItems;

/// <summary>
/// List item component for displaying direct message notifications in the activity feed.
/// Shows a chat icon, sender name, message preview, and relative time.
/// </summary>
public partial class DmActivityItem : UserControl
{
    public static readonly StyledProperty<Control?> IconContentProperty =
        AvaloniaProperty.Register<DmActivityItem, Control?>(nameof(IconContent));

    public static readonly StyledProperty<string?> SenderNameProperty =
        AvaloniaProperty.Register<DmActivityItem, string?>(nameof(SenderName));

    public static readonly StyledProperty<string?> MessagePreviewProperty =
        AvaloniaProperty.Register<DmActivityItem, string?>(nameof(MessagePreview));

    public static readonly StyledProperty<string?> RelativeTimeProperty =
        AvaloniaProperty.Register<DmActivityItem, string?>(nameof(RelativeTime));

    public static readonly StyledProperty<bool> IsReadProperty =
        AvaloniaProperty.Register<DmActivityItem, bool>(nameof(IsRead));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<DmActivityItem, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<DmActivityItem, object?>(nameof(CommandParameter));


    public DmActivityItem()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The content to display in the icon area (left side).
    /// Typically a Border with an icon.
    /// </summary>
    public Control? IconContent
    {
        get => GetValue(IconContentProperty);
        set => SetValue(IconContentProperty, value);
    }

    /// <summary>
    /// The display name of the message sender.
    /// </summary>
    public string? SenderName
    {
        get => GetValue(SenderNameProperty);
        set => SetValue(SenderNameProperty, value);
    }

    /// <summary>
    /// Preview of the message content (truncated if needed).
    /// </summary>
    public string? MessagePreview
    {
        get => GetValue(MessagePreviewProperty);
        set => SetValue(MessagePreviewProperty, value);
    }

    /// <summary>
    /// Relative time string (e.g., "2m ago", "1h ago").
    /// </summary>
    public string? RelativeTime
    {
        get => GetValue(RelativeTimeProperty);
        set => SetValue(RelativeTimeProperty, value);
    }

    /// <summary>
    /// Whether this notification has been read.
    /// Controls the text color (white for unread, muted for read).
    /// </summary>
    public bool IsRead
    {
        get => GetValue(IsReadProperty);
        set => SetValue(IsReadProperty, value);
    }

    /// <summary>
    /// Command to execute when the item is clicked.
    /// </summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// Parameter to pass to the command.
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}
