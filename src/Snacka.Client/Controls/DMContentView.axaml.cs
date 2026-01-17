using System.Collections.Specialized;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;
using ReactiveUI;

namespace Snacka.Client.Controls;

/// <summary>
/// DM conversation content area showing header, messages, and input.
/// </summary>
public partial class DMContentView : UserControl
{
    private const double ScrollBottomThreshold = 50;
    private bool _isDMMessagesAtBottom = true;
    private double _lastDMMessagesExtentHeight;

    public static readonly StyledProperty<DMContentViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<DMContentView, DMContentViewModel?>(nameof(ViewModel));

    public DMContentView()
    {
        InitializeComponent();

        // Subscribe to ViewModel changes to wire up collection change handling
        this.GetObservable(ViewModelProperty).Subscribe(OnViewModelChanged);
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // Use tunneling events to intercept Enter before AcceptsReturn processes it
        // These must be set up after the control is fully initialized
        if (DMMessageInputBox != null)
        {
            DMMessageInputBox.AddHandler(KeyDownEvent, OnDMMessageKeyDown, RoutingStrategies.Tunnel);
        }
        if (EditDMMessageInputBox != null)
        {
            EditDMMessageInputBox.AddHandler(KeyDownEvent, OnEditDMMessageKeyDown, RoutingStrategies.Tunnel);
        }
        if (DMMessagesScrollViewer != null)
        {
            DMMessagesScrollViewer.ScrollChanged += OnDMMessagesScrollChanged;
        }
    }

    public DMContentViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Track collection changes for auto-scrolling
    private INotifyCollectionChanged? _currentCollection;

    private void OnViewModelChanged(DMContentViewModel? viewModel)
    {
        // Unsubscribe from old collection
        if (_currentCollection != null)
        {
            _currentCollection.CollectionChanged -= OnDMMessagesCollectionChanged;
        }

        // Subscribe to new collection
        if (viewModel?.Messages is INotifyCollectionChanged notifyCollection)
        {
            _currentCollection = notifyCollection;
            _currentCollection.CollectionChanged += OnDMMessagesCollectionChanged;
        }

        // Scroll to bottom after a delay to ensure content is loaded
        ScrollToBottomAfterDelay();

        // Auto-focus message input when navigating to a DM conversation
        if (viewModel != null)
        {
            Dispatcher.UIThread.Post(FocusMessageInput, DispatcherPriority.Input);
        }
    }

    public void FocusMessageInput()
    {
        DMMessageInputBox?.Focus();
    }

    private async void ScrollToBottomAfterDelay()
    {
        await System.Threading.Tasks.Task.Delay(100);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DMMessagesScrollViewer?.ScrollToEnd();
        });
    }

    private void OnDMMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = DMMessagesScrollViewer;
        if (scrollViewer == null) return;

        var distanceFromBottom = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;

        // If content grew and we were at bottom, scroll to bottom again
        if (_isDMMessagesAtBottom && scrollViewer.Extent.Height > _lastDMMessagesExtentHeight && _lastDMMessagesExtentHeight > 0)
        {
            Dispatcher.UIThread.Post(() => scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }

        _lastDMMessagesExtentHeight = scrollViewer.Extent.Height;
        _isDMMessagesAtBottom = distanceFromBottom <= ScrollBottomThreshold;
    }

    private void OnDMMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset scroll state when collection is cleared
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _isDMMessagesAtBottom = true;
            Dispatcher.UIThread.Post(() =>
            {
                DMMessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && _isDMMessagesAtBottom)
        {
            Dispatcher.UIThread.Post(() =>
            {
                DMMessagesScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    // Enter sends message, Shift+Enter inserts newline
    private void OnDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            var cmd = ViewModel?.SendMessageCommand;
            if (cmd is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                if (reactiveCmd.CanExecute.FirstAsync().GetAwaiter().GetResult())
                {
                    reactiveCmd.Execute().Subscribe();
                }
            }
            else
            {
                cmd?.Execute().Subscribe();
            }
        }
    }

    // Enter saves edit, Shift+Enter inserts newline, Escape cancels
    private void OnEditDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            var cmd = ViewModel?.SaveMessageEditCommand;
            if (cmd is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                if (reactiveCmd.CanExecute.FirstAsync().GetAwaiter().GetResult())
                {
                    reactiveCmd.Execute().Subscribe();
                }
            }
            else
            {
                cmd?.Execute().Subscribe();
            }
        }
        else if (e.Key == Key.Escape)
        {
            var cmd = ViewModel?.CancelEditMessageCommand;
            if (cmd is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                reactiveCmd.Execute().Subscribe();
            }
            else
            {
                cmd?.Execute().Subscribe();
            }
            e.Handled = true;
        }
    }
}
