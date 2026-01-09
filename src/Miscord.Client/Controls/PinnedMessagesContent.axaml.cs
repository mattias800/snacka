using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Miscord.Client.Services;

namespace Miscord.Client.Controls;

/// <summary>
/// Content for pinned messages popup showing list of pinned messages.
/// </summary>
public partial class PinnedMessagesContent : UserControl
{
    public static readonly StyledProperty<IEnumerable<MessageResponse>?> PinnedMessagesProperty =
        AvaloniaProperty.Register<PinnedMessagesContent, IEnumerable<MessageResponse>?>(nameof(PinnedMessages));

    public static readonly StyledProperty<ICommand?> ClosePinnedPopupCommandProperty =
        AvaloniaProperty.Register<PinnedMessagesContent, ICommand?>(nameof(ClosePinnedPopupCommand));

    public static readonly StyledProperty<ICommand?> TogglePinCommandProperty =
        AvaloniaProperty.Register<PinnedMessagesContent, ICommand?>(nameof(TogglePinCommand));

    public PinnedMessagesContent()
    {
        InitializeComponent();
    }

    public IEnumerable<MessageResponse>? PinnedMessages
    {
        get => GetValue(PinnedMessagesProperty);
        set => SetValue(PinnedMessagesProperty, value);
    }

    public ICommand? ClosePinnedPopupCommand
    {
        get => GetValue(ClosePinnedPopupCommandProperty);
        set => SetValue(ClosePinnedPopupCommandProperty, value);
    }

    public ICommand? TogglePinCommand
    {
        get => GetValue(TogglePinCommandProperty);
        set => SetValue(TogglePinCommandProperty, value);
    }
}
