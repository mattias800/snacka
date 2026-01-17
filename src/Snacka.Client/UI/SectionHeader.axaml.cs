using Avalonia;
using Avalonia.Controls;

namespace Snacka.Client.UI;

/// <summary>
/// A reusable section header component with icon, title, optional badge, and action buttons.
/// Used for consistent section headers across panels (Members, Activity, etc.).
/// </summary>
public partial class SectionHeader : UserControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Title));

    public static readonly StyledProperty<bool> ShowBadgeProperty =
        AvaloniaProperty.Register<SectionHeader, bool>(nameof(ShowBadge));

    public static readonly StyledProperty<int> BadgeCountProperty =
        AvaloniaProperty.Register<SectionHeader, int>(nameof(BadgeCount));

    public static readonly StyledProperty<Control?> ActionContentProperty =
        AvaloniaProperty.Register<SectionHeader, Control?>(nameof(ActionContent));

    public SectionHeader()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The icon symbol name (e.g., "People", "Alert").
    /// </summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>
    /// The section title text (e.g., "MEMBERS", "ACTIVITY").
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Whether to show the badge.
    /// </summary>
    public bool ShowBadge
    {
        get => GetValue(ShowBadgeProperty);
        set => SetValue(ShowBadgeProperty, value);
    }

    /// <summary>
    /// The count to display in the badge.
    /// </summary>
    public int BadgeCount
    {
        get => GetValue(BadgeCountProperty);
        set => SetValue(BadgeCountProperty, value);
    }

    /// <summary>
    /// Optional action buttons to display on the right side.
    /// </summary>
    public Control? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}
