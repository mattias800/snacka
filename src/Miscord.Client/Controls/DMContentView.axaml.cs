using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.Controls;

/// <summary>
/// DM conversation content area showing header, messages, and input.
/// </summary>
public partial class DMContentView : UserControl
{
    private const double ScrollBottomThreshold = 50;
    private bool _isDMMessagesAtBottom = true;
    private double _lastDMMessagesExtentHeight;

    public static readonly StyledProperty<string?> DMRecipientNameProperty =
        AvaloniaProperty.Register<DMContentView, string?>(nameof(DMRecipientName));

    public static readonly StyledProperty<bool> IsDMTypingProperty =
        AvaloniaProperty.Register<DMContentView, bool>(nameof(IsDMTyping));

    public static readonly StyledProperty<string?> DMTypingIndicatorTextProperty =
        AvaloniaProperty.Register<DMContentView, string?>(nameof(DMTypingIndicatorText));

    public static readonly StyledProperty<string?> DMMessageInputProperty =
        AvaloniaProperty.Register<DMContentView, string?>(nameof(DMMessageInput));

    public static readonly StyledProperty<IEnumerable<DirectMessageResponse>?> DMMessagesProperty =
        AvaloniaProperty.Register<DMContentView, IEnumerable<DirectMessageResponse>?>(nameof(DMMessages));

    public static readonly StyledProperty<DirectMessageResponse?> EditingDMMessageProperty =
        AvaloniaProperty.Register<DMContentView, DirectMessageResponse?>(nameof(EditingDMMessage));

    public static readonly StyledProperty<string?> EditingDMMessageContentProperty =
        AvaloniaProperty.Register<DMContentView, string?>(nameof(EditingDMMessageContent));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<DMContentView, bool>(nameof(IsLoading));

    public static readonly StyledProperty<ICommand?> CloseDMCommandProperty =
        AvaloniaProperty.Register<DMContentView, ICommand?>(nameof(CloseDMCommand));

    public static readonly StyledProperty<ICommand?> SendDMMessageCommandProperty =
        AvaloniaProperty.Register<DMContentView, ICommand?>(nameof(SendDMMessageCommand));

    public static readonly StyledProperty<ICommand?> StartEditDMMessageCommandProperty =
        AvaloniaProperty.Register<DMContentView, ICommand?>(nameof(StartEditDMMessageCommand));

    public static readonly StyledProperty<ICommand?> DeleteDMMessageCommandProperty =
        AvaloniaProperty.Register<DMContentView, ICommand?>(nameof(DeleteDMMessageCommand));

    public static readonly StyledProperty<ICommand?> CancelEditDMMessageCommandProperty =
        AvaloniaProperty.Register<DMContentView, ICommand?>(nameof(CancelEditDMMessageCommand));

    public static readonly StyledProperty<ICommand?> SaveDMMessageEditCommandProperty =
        AvaloniaProperty.Register<DMContentView, ICommand?>(nameof(SaveDMMessageEditCommand));

    public DMContentView()
    {
        InitializeComponent();

        // Use tunneling events to intercept Enter before AcceptsReturn processes it
        DMMessageInputBox.AddHandler(KeyDownEvent, OnDMMessageKeyDown, RoutingStrategies.Tunnel);
        EditDMMessageInputBox.AddHandler(KeyDownEvent, OnEditDMMessageKeyDown, RoutingStrategies.Tunnel);

        // Track scroll position for smart auto-scrolling
        DMMessagesScrollViewer.ScrollChanged += OnDMMessagesScrollChanged;

        // Subscribe to collection changes for auto-scrolling
        this.GetObservable(DMMessagesProperty).Subscribe(OnDMMessagesChanged);
    }

    public string? DMRecipientName
    {
        get => GetValue(DMRecipientNameProperty);
        set => SetValue(DMRecipientNameProperty, value);
    }

    public bool IsDMTyping
    {
        get => GetValue(IsDMTypingProperty);
        set => SetValue(IsDMTypingProperty, value);
    }

    public string? DMTypingIndicatorText
    {
        get => GetValue(DMTypingIndicatorTextProperty);
        set => SetValue(DMTypingIndicatorTextProperty, value);
    }

    public string? DMMessageInput
    {
        get => GetValue(DMMessageInputProperty);
        set => SetValue(DMMessageInputProperty, value);
    }

    public IEnumerable<DirectMessageResponse>? DMMessages
    {
        get => GetValue(DMMessagesProperty);
        set => SetValue(DMMessagesProperty, value);
    }

    public DirectMessageResponse? EditingDMMessage
    {
        get => GetValue(EditingDMMessageProperty);
        set => SetValue(EditingDMMessageProperty, value);
    }

    public string? EditingDMMessageContent
    {
        get => GetValue(EditingDMMessageContentProperty);
        set => SetValue(EditingDMMessageContentProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ICommand? CloseDMCommand
    {
        get => GetValue(CloseDMCommandProperty);
        set => SetValue(CloseDMCommandProperty, value);
    }

    public ICommand? SendDMMessageCommand
    {
        get => GetValue(SendDMMessageCommandProperty);
        set => SetValue(SendDMMessageCommandProperty, value);
    }

    public ICommand? StartEditDMMessageCommand
    {
        get => GetValue(StartEditDMMessageCommandProperty);
        set => SetValue(StartEditDMMessageCommandProperty, value);
    }

    public ICommand? DeleteDMMessageCommand
    {
        get => GetValue(DeleteDMMessageCommandProperty);
        set => SetValue(DeleteDMMessageCommandProperty, value);
    }

    public ICommand? CancelEditDMMessageCommand
    {
        get => GetValue(CancelEditDMMessageCommandProperty);
        set => SetValue(CancelEditDMMessageCommandProperty, value);
    }

    public ICommand? SaveDMMessageEditCommand
    {
        get => GetValue(SaveDMMessageEditCommandProperty);
        set => SetValue(SaveDMMessageEditCommandProperty, value);
    }

    // Track collection changes for auto-scrolling
    private INotifyCollectionChanged? _currentCollection;

    private void OnDMMessagesChanged(IEnumerable<DirectMessageResponse>? messages)
    {
        // Unsubscribe from old collection
        if (_currentCollection != null)
        {
            _currentCollection.CollectionChanged -= OnDMMessagesCollectionChanged;
        }

        // Subscribe to new collection
        if (messages is INotifyCollectionChanged notifyCollection)
        {
            _currentCollection = notifyCollection;
            _currentCollection.CollectionChanged += OnDMMessagesCollectionChanged;
        }

        // Scroll to bottom after a delay to ensure content is loaded
        ScrollToBottomAfterDelay();
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

            if (SendDMMessageCommand is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                if (reactiveCmd.CanExecute.FirstAsync().GetAwaiter().GetResult())
                {
                    reactiveCmd.Execute().Subscribe();
                }
            }
            else
            {
                SendDMMessageCommand?.Execute(null);
            }
        }
    }

    // Enter saves edit, Shift+Enter inserts newline, Escape cancels
    private void OnEditDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (SaveDMMessageEditCommand is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                if (reactiveCmd.CanExecute.FirstAsync().GetAwaiter().GetResult())
                {
                    reactiveCmd.Execute().Subscribe();
                }
            }
            else
            {
                SaveDMMessageEditCommand?.Execute(null);
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (CancelEditDMMessageCommand is ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> reactiveCmd)
            {
                reactiveCmd.Execute().Subscribe();
            }
            else
            {
                CancelEditDMMessageCommand?.Execute(null);
            }
            e.Handled = true;
        }
    }
}
