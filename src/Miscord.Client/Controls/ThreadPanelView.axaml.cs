using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Miscord.Client.Services;
using System.Reactive.Linq;
using ReactiveUI;

namespace Miscord.Client.Controls;

/// <summary>
/// A reusable thread panel component that displays a message thread with replies.
/// </summary>
public partial class ThreadPanelView : UserControl
{
    // Commands from parent for message actions
    public static readonly StyledProperty<ICommand?> StartEditMessageCommandProperty =
        AvaloniaProperty.Register<ThreadPanelView, ICommand?>(nameof(StartEditMessageCommand));

    public static readonly StyledProperty<ICommand?> DeleteMessageCommandProperty =
        AvaloniaProperty.Register<ThreadPanelView, ICommand?>(nameof(DeleteMessageCommand));

    // Configuration
    public static readonly StyledProperty<string?> BaseUrlProperty =
        AvaloniaProperty.Register<ThreadPanelView, string?>(nameof(BaseUrl));

    public ThreadPanelView()
    {
        InitializeComponent();
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

    public string? BaseUrl
    {
        get => GetValue(BaseUrlProperty);
        set => SetValue(BaseUrlProperty, value);
    }

    // Events that bubble up to the parent
    public event EventHandler<object>? AddReactionRequested;
    public event EventHandler<ReactionSummary>? ReactionToggleRequested;
    public event EventHandler<AttachmentResponse>? ImageClicked;

    // Handler for reply input key down - sends on Enter
    private void OnThreadReplyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            // Get the ThreadPanelViewModel from DataContext
            if (DataContext is ViewModels.ThreadViewModel vm &&
                vm.SendReplyCommand.CanExecute.FirstAsync().GetAwaiter().GetResult())
            {
                vm.SendReplyCommand.Execute().Subscribe();
            }
        }
    }

    // Forward events from MessageItemView
    private void OnMessageAddReactionRequested(object? sender, object message)
    {
        AddReactionRequested?.Invoke(sender, message);
    }

    private void OnMessageReactionToggleRequested(object? sender, ReactionSummary reaction)
    {
        ReactionToggleRequested?.Invoke(sender, reaction);
    }

    private void OnMessageImageClicked(object? sender, AttachmentResponse attachment)
    {
        ImageClicked?.Invoke(sender, attachment);
    }
}
