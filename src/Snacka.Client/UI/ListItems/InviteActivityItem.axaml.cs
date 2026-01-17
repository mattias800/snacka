using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Snacka.Client.UI.ListItems;

/// <summary>
/// List item component for displaying community invite notifications in the activity feed.
/// Shows an invite icon, community name, inviter info, and Accept/Decline buttons.
/// </summary>
public partial class InviteActivityItem : UserControl
{
    public static readonly StyledProperty<Control?> IconContentProperty =
        AvaloniaProperty.Register<InviteActivityItem, Control?>(nameof(IconContent));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<InviteActivityItem, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<InviteActivityItem, string?>(nameof(Description));

    public static readonly StyledProperty<string?> RelativeTimeProperty =
        AvaloniaProperty.Register<InviteActivityItem, string?>(nameof(RelativeTime));

    public static readonly StyledProperty<bool> IsReadProperty =
        AvaloniaProperty.Register<InviteActivityItem, bool>(nameof(IsRead));

    public static readonly StyledProperty<ICommand?> AcceptCommandProperty =
        AvaloniaProperty.Register<InviteActivityItem, ICommand?>(nameof(AcceptCommand));

    public static readonly StyledProperty<ICommand?> DeclineCommandProperty =
        AvaloniaProperty.Register<InviteActivityItem, ICommand?>(nameof(DeclineCommand));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<InviteActivityItem, object?>(nameof(CommandParameter));


    public InviteActivityItem()
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
    /// The title (e.g., "Invite to CommunityName").
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Description (e.g., "From username").
    /// </summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
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
    /// Command to execute when Accept is clicked.
    /// </summary>
    public ICommand? AcceptCommand
    {
        get => GetValue(AcceptCommandProperty);
        set => SetValue(AcceptCommandProperty, value);
    }

    /// <summary>
    /// Command to execute when Decline is clicked.
    /// </summary>
    public ICommand? DeclineCommand
    {
        get => GetValue(DeclineCommandProperty);
        set => SetValue(DeclineCommandProperty, value);
    }

    /// <summary>
    /// Parameter to pass to the Accept/Decline commands.
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}
