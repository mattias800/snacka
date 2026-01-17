using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Snacka.Client.UI;

/// <summary>
/// A reusable list item component with icon, primary/secondary text, and optional right content.
/// Used for consistent list item presentation across members list, activity feed, etc.
/// </summary>
public partial class ListItemView : UserControl
{
    public static readonly StyledProperty<Control?> IconContentProperty =
        AvaloniaProperty.Register<ListItemView, Control?>(nameof(IconContent));

    public static readonly StyledProperty<string?> PrimaryTextProperty =
        AvaloniaProperty.Register<ListItemView, string?>(nameof(PrimaryText));

    public static readonly StyledProperty<string?> SecondaryTextProperty =
        AvaloniaProperty.Register<ListItemView, string?>(nameof(SecondaryText));

    public static readonly StyledProperty<Control?> RightContentProperty =
        AvaloniaProperty.Register<ListItemView, Control?>(nameof(RightContent));

    public static readonly StyledProperty<IBrush?> PrimaryTextForegroundProperty =
        AvaloniaProperty.Register<ListItemView, IBrush?>(nameof(PrimaryTextForeground));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ListItemView, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<ListItemView, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<bool> IsClickableProperty =
        AvaloniaProperty.Register<ListItemView, bool>(nameof(IsClickable), defaultValue: true);

    public static readonly StyledProperty<Control?> AdditionalContentProperty =
        AvaloniaProperty.Register<ListItemView, Control?>(nameof(AdditionalContent));

    public ListItemView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The content to display in the icon/avatar area (left side).
    /// Typically a Border with an image, icon, or initials.
    /// </summary>
    public Control? IconContent
    {
        get => GetValue(IconContentProperty);
        set => SetValue(IconContentProperty, value);
    }

    /// <summary>
    /// The primary text to display (e.g., username, title).
    /// </summary>
    public string? PrimaryText
    {
        get => GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    /// <summary>
    /// The secondary text to display below the primary text (e.g., role, description).
    /// </summary>
    public string? SecondaryText
    {
        get => GetValue(SecondaryTextProperty);
        set => SetValue(SecondaryTextProperty, value);
    }

    /// <summary>
    /// Optional content to display on the right side (e.g., badge, time, button).
    /// </summary>
    public Control? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    /// <summary>
    /// The foreground brush for the primary text.
    /// Use this to indicate read/unread state or other visual states.
    /// </summary>
    public IBrush? PrimaryTextForeground
    {
        get => GetValue(PrimaryTextForegroundProperty);
        set => SetValue(PrimaryTextForegroundProperty, value);
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

    /// <summary>
    /// Whether the item is clickable. When false, no hover effect or cursor change.
    /// </summary>
    public bool IsClickable
    {
        get => GetValue(IsClickableProperty);
        set => SetValue(IsClickableProperty, value);
    }

    /// <summary>
    /// Additional content to display below the main row (e.g., action buttons).
    /// </summary>
    public Control? AdditionalContent
    {
        get => GetValue(AdditionalContentProperty);
        set => SetValue(AdditionalContentProperty, value);
    }
}
