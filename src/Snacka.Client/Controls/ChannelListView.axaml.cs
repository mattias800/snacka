using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;
using System.Reactive.Linq;
using ReactiveUI;

namespace Snacka.Client.Controls;

/// <summary>
/// A reusable channel list component that displays text and voice channels.
/// </summary>
public partial class ChannelListView : UserControl
{
    // Text channels
    public static readonly StyledProperty<IEnumerable<ChannelResponse>?> TextChannelsProperty =
        AvaloniaProperty.Register<ChannelListView, IEnumerable<ChannelResponse>?>(nameof(TextChannels));

    // Voice channels (with participants)
    public static readonly StyledProperty<IEnumerable<VoiceChannelViewModel>?> VoiceChannelsProperty =
        AvaloniaProperty.Register<ChannelListView, IEnumerable<VoiceChannelViewModel>?>(nameof(VoiceChannels));

    // Current voice channel (for detecting if already in channel)
    public static readonly StyledProperty<ChannelResponse?> CurrentVoiceChannelProperty =
        AvaloniaProperty.Register<ChannelListView, ChannelResponse?>(nameof(CurrentVoiceChannel));

    // Whether user can manage channels (for create/edit buttons)
    public static readonly StyledProperty<bool> CanManageChannelsProperty =
        AvaloniaProperty.Register<ChannelListView, bool>(nameof(CanManageChannels), false);

    // Whether user can manage voice (for server mute/deafen)
    public static readonly StyledProperty<bool> CanManageVoiceProperty =
        AvaloniaProperty.Register<ChannelListView, bool>(nameof(CanManageVoice), false);

    // Loading state
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<ChannelListView, bool>(nameof(IsLoading), false);

    // Channel rename editor
    public static readonly StyledProperty<ChannelResponse?> EditingChannelProperty =
        AvaloniaProperty.Register<ChannelListView, ChannelResponse?>(nameof(EditingChannel));

    public static readonly StyledProperty<string?> EditingChannelNameProperty =
        AvaloniaProperty.Register<ChannelListView, string?>(nameof(EditingChannelName));

    // Commands
    public static readonly StyledProperty<ICommand?> CreateChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CreateChannelCommand));

    public static readonly StyledProperty<ICommand?> CreateVoiceChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CreateVoiceChannelCommand));

    public static readonly StyledProperty<ICommand?> SelectChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(SelectChannelCommand));

    public static readonly StyledProperty<ICommand?> StartEditChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(StartEditChannelCommand));

    public static readonly StyledProperty<ICommand?> SaveChannelNameCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(SaveChannelNameCommand));

    public static readonly StyledProperty<ICommand?> CancelEditChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CancelEditChannelCommand));

    public static readonly StyledProperty<ICommand?> DeleteChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(DeleteChannelCommand));

    public static readonly StyledProperty<ICommand?> ConfirmDeleteChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(ConfirmDeleteChannelCommand));

    public static readonly StyledProperty<ICommand?> CancelDeleteChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CancelDeleteChannelCommand));

    public static readonly StyledProperty<ChannelResponse?> ChannelPendingDeleteProperty =
        AvaloniaProperty.Register<ChannelListView, ChannelResponse?>(nameof(ChannelPendingDelete));

    public static readonly StyledProperty<ICommand?> JoinVoiceChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(JoinVoiceChannelCommand));

    public static readonly StyledProperty<ICommand?> ServerMuteUserCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(ServerMuteUserCommand));

    public static readonly StyledProperty<ICommand?> ServerDeafenUserCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(ServerDeafenUserCommand));

    public static readonly StyledProperty<ICommand?> MoveUserToChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(MoveUserToChannelCommand));

    public static readonly StyledProperty<ICommand?> ReorderChannelsCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(ReorderChannelsCommand));

    public static readonly StyledProperty<ICommand?> PreviewReorderCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(PreviewReorderCommand));

    public static readonly StyledProperty<ICommand?> CancelPreviewCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CancelPreviewCommand));

    // For drag-drop
    private ChannelResponse? _draggedChannel;

    public ChannelListView()
    {
        InitializeComponent();

        // Get drag ghost references
        _dragGhost = this.FindControl<Border>("DragGhost");
        _dragGhostIcon = this.FindControl<TextBlock>("DragGhostIcon");
        _dragGhostName = this.FindControl<TextBlock>("DragGhostName");
    }

    public IEnumerable<ChannelResponse>? TextChannels
    {
        get => GetValue(TextChannelsProperty);
        set => SetValue(TextChannelsProperty, value);
    }

    public IEnumerable<VoiceChannelViewModel>? VoiceChannels
    {
        get => GetValue(VoiceChannelsProperty);
        set => SetValue(VoiceChannelsProperty, value);
    }

    public ChannelResponse? CurrentVoiceChannel
    {
        get => GetValue(CurrentVoiceChannelProperty);
        set => SetValue(CurrentVoiceChannelProperty, value);
    }

    public bool CanManageChannels
    {
        get => GetValue(CanManageChannelsProperty);
        set => SetValue(CanManageChannelsProperty, value);
    }

    public bool CanManageVoice
    {
        get => GetValue(CanManageVoiceProperty);
        set => SetValue(CanManageVoiceProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ChannelResponse? EditingChannel
    {
        get => GetValue(EditingChannelProperty);
        set => SetValue(EditingChannelProperty, value);
    }

    public string? EditingChannelName
    {
        get => GetValue(EditingChannelNameProperty);
        set => SetValue(EditingChannelNameProperty, value);
    }

    public ICommand? CreateChannelCommand
    {
        get => GetValue(CreateChannelCommandProperty);
        set => SetValue(CreateChannelCommandProperty, value);
    }

    public ICommand? CreateVoiceChannelCommand
    {
        get => GetValue(CreateVoiceChannelCommandProperty);
        set => SetValue(CreateVoiceChannelCommandProperty, value);
    }

    public ICommand? SelectChannelCommand
    {
        get => GetValue(SelectChannelCommandProperty);
        set => SetValue(SelectChannelCommandProperty, value);
    }

    public ICommand? StartEditChannelCommand
    {
        get => GetValue(StartEditChannelCommandProperty);
        set => SetValue(StartEditChannelCommandProperty, value);
    }

    public ICommand? SaveChannelNameCommand
    {
        get => GetValue(SaveChannelNameCommandProperty);
        set => SetValue(SaveChannelNameCommandProperty, value);
    }

    public ICommand? CancelEditChannelCommand
    {
        get => GetValue(CancelEditChannelCommandProperty);
        set => SetValue(CancelEditChannelCommandProperty, value);
    }

    public ICommand? DeleteChannelCommand
    {
        get => GetValue(DeleteChannelCommandProperty);
        set => SetValue(DeleteChannelCommandProperty, value);
    }

    public ICommand? ConfirmDeleteChannelCommand
    {
        get => GetValue(ConfirmDeleteChannelCommandProperty);
        set => SetValue(ConfirmDeleteChannelCommandProperty, value);
    }

    public ICommand? CancelDeleteChannelCommand
    {
        get => GetValue(CancelDeleteChannelCommandProperty);
        set => SetValue(CancelDeleteChannelCommandProperty, value);
    }

    public ChannelResponse? ChannelPendingDelete
    {
        get => GetValue(ChannelPendingDeleteProperty);
        set => SetValue(ChannelPendingDeleteProperty, value);
    }

    public ICommand? JoinVoiceChannelCommand
    {
        get => GetValue(JoinVoiceChannelCommandProperty);
        set => SetValue(JoinVoiceChannelCommandProperty, value);
    }

    public ICommand? ServerMuteUserCommand
    {
        get => GetValue(ServerMuteUserCommandProperty);
        set => SetValue(ServerMuteUserCommandProperty, value);
    }

    public ICommand? ServerDeafenUserCommand
    {
        get => GetValue(ServerDeafenUserCommandProperty);
        set => SetValue(ServerDeafenUserCommandProperty, value);
    }

    public ICommand? MoveUserToChannelCommand
    {
        get => GetValue(MoveUserToChannelCommandProperty);
        set => SetValue(MoveUserToChannelCommandProperty, value);
    }

    public ICommand? ReorderChannelsCommand
    {
        get => GetValue(ReorderChannelsCommandProperty);
        set => SetValue(ReorderChannelsCommandProperty, value);
    }

    public ICommand? PreviewReorderCommand
    {
        get => GetValue(PreviewReorderCommandProperty);
        set => SetValue(PreviewReorderCommandProperty, value);
    }

    public ICommand? CancelPreviewCommand
    {
        get => GetValue(CancelPreviewCommandProperty);
        set => SetValue(CancelPreviewCommandProperty, value);
    }

    // Events
    public event EventHandler<ChannelResponse>? VoiceChannelClicked;
    public event EventHandler<ChannelResponse>? VoiceChannelViewRequested;
    public event EventHandler<(VoiceParticipantViewModel Participant, VoiceChannelViewModel TargetChannel)>? MoveUserRequested;

    // Voice channel click/drag state
    private ChannelResponse? _voiceClickChannel;
    private Point _voiceClickStartPoint;
    private bool _voiceWasDragged;
    private VoiceChannelViewModel? _draggedVoiceChannel;
    private Point _voiceDragStartPoint;
    private bool _isVoiceDragging;

    // Drag ghost elements
    private Border? _dragGhost;
    private TextBlock? _dragGhostIcon;
    private TextBlock? _dragGhostName;
    private Guid? _currentDropTargetId;
    private bool _currentDropBefore;

    private void VoiceChannel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is ChannelResponse channel)
        {
            // Only handle left mouse button for click/drag
            var props = e.GetCurrentPoint(border).Properties;
            if (!props.IsLeftButtonPressed) return;

            // Visual feedback - darken on press
            border.Background = new SolidColorBrush(Color.Parse("#3f4248"));

            // Track for potential drag or click
            _voiceClickChannel = channel;
            _voiceClickStartPoint = e.GetPosition(this);
            _voiceWasDragged = false;

            // Also set up drag state if admin
            if (CanManageChannels)
            {
                var voiceVm = VoiceChannels?.FirstOrDefault(v => v.Id == channel.Id);
                if (voiceVm is not null)
                {
                    _draggedVoiceChannel = voiceVm;
                    _voiceDragStartPoint = _voiceClickStartPoint;
                    _isVoiceDragging = false;
                }
            }
        }
    }

    private void VoiceChannel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = Brushes.Transparent;

            // Only trigger click action if we didn't drag
            if (!_voiceWasDragged && _voiceClickChannel is not null && border.Tag is ChannelResponse channel && channel.Id == _voiceClickChannel.Id)
            {
                // Check if already in this voice channel
                if (CurrentVoiceChannel?.Id == channel.Id)
                {
                    // Already in this channel - just view it
                    VoiceChannelViewRequested?.Invoke(this, channel);
                }
                else
                {
                    // Join the channel
                    VoiceChannelClicked?.Invoke(this, channel);
                }
            }

            _voiceClickChannel = null;

            // Clear drag state if we didn't actually start dragging
            // (OnPointerReleased only fires when we have pointer capture from actual drag)
            if (!_isVoiceDragging)
            {
                _draggedVoiceChannel = null;
            }
        }
    }

    private void VoiceChannel_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanManageChannels || _draggedVoiceChannel is null) return;
        if (sender is not Border border) return;

        var currentPoint = e.GetPosition(this);
        var diff = currentPoint - _voiceDragStartPoint;

        // Check if we've moved enough to start a drag
        if (!_isVoiceDragging && (Math.Abs(diff.X) > 10 || Math.Abs(diff.Y) > 10))
        {
            _isVoiceDragging = true;
            _voiceWasDragged = true;
            _draggingControl = border;
            border.Classes.Add("dragging");

            // Show drag ghost
            ShowDragGhost(_draggedVoiceChannel.Name, isVoiceChannel: true);

            // Capture pointer for global tracking
            e.Pointer.Capture(this);
        }

        // Update ghost position and find drop target while dragging
        if (_isVoiceDragging)
        {
            UpdateDragGhostPosition(currentPoint);
            UpdateDropTarget(currentPoint);
        }
    }

    // Called when pointer moves over the control during drag (due to capture)
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isVoiceDragging && _draggedVoiceChannel is not null)
        {
            var currentPoint = e.GetPosition(this);
            UpdateDragGhostPosition(currentPoint);
            UpdateDropTarget(currentPoint);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isVoiceDragging && _draggedVoiceChannel is not null)
        {
            // Release capture
            e.Pointer.Capture(null);

            // Perform drop if we have a valid target
            PerformDrop();

            // Clean up
            _draggingControl?.Classes.Remove("dragging");
            _draggingControl = null;
            _draggedVoiceChannel = null;
            _isVoiceDragging = false;
            HideDragGhost();
            CancelPreviewCommand?.Execute(null);
        }
    }

    // Find the drop target based on pointer position
    private void UpdateDropTarget(Point position)
    {
        if (_draggedVoiceChannel is null || VoiceChannels is null) return;

        var voiceChannelsList = this.FindControl<ItemsControl>("VoiceChannelsList");
        if (voiceChannelsList is null) return;

        ChannelResponse? targetChannel = null;
        bool dropBefore = true;

        // Find which voice channel we're over
        foreach (var voiceVm in VoiceChannels)
        {
            if (voiceVm.Id == _draggedVoiceChannel.Id) continue; // Skip self

            // Find the visual element for this channel
            var container = voiceChannelsList.ContainerFromItem(voiceVm);
            if (container is not ContentPresenter presenter) continue;

            var bounds = presenter.Bounds;
            var presenterPos = presenter.TranslatePoint(new Point(0, 0), this);
            if (presenterPos is null) continue;

            var itemBounds = new Rect(presenterPos.Value, bounds.Size);

            if (itemBounds.Contains(position))
            {
                targetChannel = voiceVm.Channel;
                var midPoint = itemBounds.Top + itemBounds.Height / 2;
                dropBefore = position.Y < midPoint;
                break;
            }
        }

        if (targetChannel is not null)
        {
            // Track current target for drop
            _currentDropTargetId = targetChannel.Id;
            _currentDropBefore = dropBefore;

            // Show preview at this position
            PreviewReorderCommand?.Execute((
                _draggedVoiceChannel.Channel.Id,
                targetChannel.Id,
                dropBefore
            ));
        }
    }

    private void PerformDrop()
    {
        if (_draggedVoiceChannel is null || VoiceChannels is null) return;
        if (_currentDropTargetId is null) return; // No valid target

        // Get all channels
        var allChannels = (TextChannels?.ToList() ?? new List<ChannelResponse>())
            .Concat(VoiceChannels.Select(v => v.Channel))
            .OrderBy(c => c.Position)
            .ToList();

        var draggedChannel = _draggedVoiceChannel.Channel;
        var draggedIndex = allChannels.FindIndex(c => c.Id == draggedChannel.Id);
        var targetIndex = allChannels.FindIndex(c => c.Id == _currentDropTargetId);

        if (draggedIndex < 0 || targetIndex < 0) return;

        // Remove from old position
        allChannels.RemoveAt(draggedIndex);

        // Find where target is now (may have shifted)
        var newTargetIndex = allChannels.FindIndex(c => c.Id == _currentDropTargetId);

        // Insert at the appropriate position
        var insertIndex = _currentDropBefore ? newTargetIndex : newTargetIndex + 1;
        allChannels.Insert(insertIndex, draggedChannel);

        // Create the new order
        var newOrder = allChannels.Select(c => c.Id).ToList();
        ReorderChannelsCommand?.Execute(newOrder);

        // Clear target
        _currentDropTargetId = null;
    }

    private void OnChannelRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveChannelNameCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEditChannelCommand?.Execute(null);
            e.Handled = true;
        }
    }

    private void OnChannelDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only allow editing if user can manage channels
        if (!CanManageChannels) return;

        if (sender is Button button && button.Tag is ChannelResponse channel)
        {
            StartEditChannelCommand?.Execute(channel);
            e.Handled = true;
        }
    }

    // Context menu handlers
    private void OnTextChannelContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            foreach (var item in menu.Items)
            {
                if (item is MenuItem menuItem)
                {
                    menuItem.IsVisible = CanManageChannels;
                }
                else if (item is Separator separator)
                {
                    separator.IsVisible = CanManageChannels;
                }
            }
        }
    }

    private void OnVoiceChannelContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            foreach (var item in menu.Items)
            {
                if (item is MenuItem menuItem)
                {
                    menuItem.IsVisible = CanManageChannels;
                }
                else if (item is Separator separator)
                {
                    separator.IsVisible = CanManageChannels;
                }
            }
        }
    }

    private void OnRenameChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is ChannelResponse channel)
        {
            StartEditChannelCommand?.Execute(channel);
        }
    }

    private void OnDeleteChannelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is ChannelResponse channel)
        {
            DeleteChannelCommand?.Execute(channel);
        }
    }

    // Drag-drop handlers for channel reordering
    private Point _dragStartPoint;
    private bool _isDragging;
    private Border? _activeDropIndicator;
    private Control? _draggingControl;

    // Helper method to show a drop indicator
    private void ShowDropIndicator(Control target, bool above)
    {
        HideAllDropIndicators();

        // Find the drop indicator in the parent StackPanel
        if (target.Parent is StackPanel stackPanel)
        {
            foreach (var child in stackPanel.Children)
            {
                if (child is Border border && border.Classes.Contains("drop-indicator"))
                {
                    border.Classes.Add("active");
                    _activeDropIndicator = border;
                    return;
                }
            }
        }
    }

    // Helper method to hide all drop indicators
    private void HideAllDropIndicators()
    {
        _activeDropIndicator?.Classes.Remove("active");
        _activeDropIndicator = null;

        // Also hide the named indicators
        if (this.FindControl<Border>("TextChannelsTopDropIndicator") is Border textTop)
            textTop.Classes.Remove("active");
        if (this.FindControl<Border>("TextChannelsBottomDropIndicator") is Border textBottom)
            textBottom.Classes.Remove("active");
        if (this.FindControl<Border>("VoiceChannelsTopDropIndicator") is Border voiceTop)
            voiceTop.Classes.Remove("active");
        if (this.FindControl<Border>("VoiceChannelsBottomDropIndicator") is Border voiceBottom)
            voiceBottom.Classes.Remove("active");
    }

    // Drag ghost helper methods
    private void ShowDragGhost(string name, bool isVoiceChannel)
    {
        if (_dragGhost is null || _dragGhostIcon is null || _dragGhostName is null) return;

        _dragGhostIcon.Text = isVoiceChannel ? "♪" : "#";
        _dragGhostName.Text = name;
        _dragGhost.IsVisible = true;
    }

    private void HideDragGhost()
    {
        if (_dragGhost is not null)
        {
            _dragGhost.IsVisible = false;
        }
        // Gap hiding is handled by CancelPreviewCommand
    }

    private void UpdateDragGhostPosition(Point position)
    {
        if (_dragGhost is null) return;

        // Offset slightly from cursor so it doesn't interfere with drop detection
        _dragGhost.Margin = new Thickness(position.X + 15, position.Y - 10, 0, 0);
    }

    private void OnTextChannelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanManageChannels) return;
        if (sender is not Button button || button.Tag is not ChannelResponse channel) return;

        _dragStartPoint = e.GetPosition(this);
        _draggedChannel = channel;
        _isDragging = false;
    }

    #pragma warning disable CS0618 // Using deprecated DragDrop API - works fine, newer API requires more changes
    private async void OnTextChannelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!CanManageChannels || _draggedChannel is null) return;
        if (sender is not Button button) return;

        var currentPoint = e.GetPosition(this);
        var diff = currentPoint - _dragStartPoint;

        // Check if we've moved enough to start a drag
        if (!_isDragging && (Math.Abs(diff.X) > 10 || Math.Abs(diff.Y) > 10))
        {
            _isDragging = true;
            _draggingControl = button;
            button.Classes.Add("dragging");

            var dataObject = new DataObject();
            dataObject.Set("Channel", _draggedChannel);

            await DragDrop.DoDragDrop(e, dataObject, DragDropEffects.Move);

            // Remove dragging class
            button.Classes.Remove("dragging");
            _draggingControl = null;
            _draggedChannel = null;
            _isDragging = false;

            // Hide drop indicator when drag ends
            HideAllDropIndicators();
        }
    }
    #pragma warning restore CS0618

    private void OnTextChannelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingControl?.Classes.Remove("dragging");
        _draggingControl = null;
        _draggedChannel = null;
        _isDragging = false;
        HideAllDropIndicators();
    }

    #pragma warning disable CS0618 // Using deprecated DragDrop API
    private void OnTextChannelDragOver(object? sender, DragEventArgs e)
    {
        if (!CanManageChannels) return;
        if (!e.Data.Contains("Channel")) return;

        var draggedChannel = e.Data.Get("Channel") as ChannelResponse;

        // Don't show drop indicator if hovering over the same channel
        if (sender is Button button && button.Tag is ChannelResponse targetChannel)
        {
            if (draggedChannel?.Id == targetChannel.Id)
            {
                e.DragEffects = DragDropEffects.None;
                HideAllDropIndicators();
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            ShowDropIndicator(button, true);
        }
    }

    private void OnTextChannelDragLeave(object? sender, DragEventArgs e)
    {
        // Don't hide immediately - let other handlers show their indicators
        // The indicator will be hidden when drop happens or drag ends
    }

    private void OnTextChannelDrop(object? sender, DragEventArgs e)
    {
        Console.WriteLine("OnTextChannelDrop called");

        if (!CanManageChannels)
        {
            Console.WriteLine("OnTextChannelDrop: CanManageChannels is false");
            return;
        }
        if (!e.Data.Contains("Channel"))
        {
            Console.WriteLine("OnTextChannelDrop: Data does not contain Channel");
            return;
        }

        var draggedChannel = e.Data.Get("Channel") as ChannelResponse;
        if (draggedChannel is null)
        {
            Console.WriteLine("OnTextChannelDrop: draggedChannel is null");
            return;
        }

        Console.WriteLine($"OnTextChannelDrop: Dragging channel {draggedChannel.Name}");

        // Find the target channel based on where we dropped
        ChannelResponse? targetChannel = null;
        if (sender is Button button && button.Tag is ChannelResponse target)
        {
            targetChannel = target;
        }
        else if (sender is Border border && border.Tag is ChannelResponse borderTarget)
        {
            targetChannel = borderTarget;
        }

        if (targetChannel is null)
        {
            Console.WriteLine("OnTextChannelDrop: targetChannel is null");
            return;
        }

        if (targetChannel.Id == draggedChannel.Id)
        {
            Console.WriteLine("OnTextChannelDrop: Dropped on same channel");
            return;
        }

        Console.WriteLine($"OnTextChannelDrop: Target channel {targetChannel.Name}");

        // Get ALL channels (text + voice) for reordering
        var allChannels = (TextChannels?.ToList() ?? new List<ChannelResponse>())
            .Concat(VoiceChannels?.Select(v => v.Channel) ?? Enumerable.Empty<ChannelResponse>())
            .OrderBy(c => c.Position)
            .ToList();

        var draggedIndex = allChannels.FindIndex(c => c.Id == draggedChannel.Id);
        var targetIndex = allChannels.FindIndex(c => c.Id == targetChannel.Id);

        if (draggedIndex < 0 || targetIndex < 0)
        {
            Console.WriteLine($"OnTextChannelDrop: Invalid index - dragged={draggedIndex}, target={targetIndex}");
            return;
        }

        // Determine if dropping before or after target based on mouse position
        var position = e.GetPosition((Visual)sender!);
        var targetControl = (Control)sender!;
        var midPoint = targetControl.Bounds.Height / 2;
        var dropBeforeTarget = position.Y <= midPoint;

        // Remove the dragged item first
        allChannels.RemoveAt(draggedIndex);

        // Find where target is now (index may have shifted if we removed from before it)
        var newTargetIndex = allChannels.FindIndex(c => c.Id == targetChannel.Id);

        // Insert at the appropriate position
        var insertIndex = dropBeforeTarget ? newTargetIndex : newTargetIndex + 1;
        allChannels.Insert(insertIndex, draggedChannel);

        // Create the new order list
        var newOrder = allChannels.Select(c => c.Id).ToList();

        Console.WriteLine($"OnTextChannelDrop: New order has {newOrder.Count} channels, dropBeforeTarget={dropBeforeTarget}");

        // Hide drop indicator
        HideAllDropIndicators();

        // Execute the reorder command
        ReorderChannelsCommand?.Execute(newOrder);
        Console.WriteLine("OnTextChannelDrop: ReorderChannelsCommand executed");
    }

    // Voice channel drag-drop handlers (DragOver and Drop only - PointerPressed/Moved/Released are handled above)
    #pragma warning disable CS0618 // Using deprecated DragDrop API
    private void VoiceChannel_DragOver(object? sender, DragEventArgs e)
    {
        if (!CanManageChannels) return;
        if (!e.Data.Contains("VoiceChannel")) return;

        var draggedChannel = e.Data.Get("VoiceChannel") as ChannelResponse;
        if (draggedChannel is null) return;

        // Don't show preview if hovering over the same channel
        if (sender is Border border && border.Tag is ChannelResponse targetChannel)
        {
            if (draggedChannel.Id == targetChannel.Id)
            {
                e.DragEffects = DragDropEffects.None;
                return;
            }

            e.DragEffects = DragDropEffects.Move;

            // Calculate if dropping before or after target
            var position = e.GetPosition(border);
            var midPoint = border.Bounds.Height / 2;
            var dropBefore = position.Y <= midPoint;

            // Show live preview of the reorder
            PreviewReorderCommand?.Execute((draggedChannel.Id, targetChannel.Id, dropBefore));
        }
    }

    private void VoiceChannel_Drop(object? sender, DragEventArgs e)
    {
        if (!CanManageChannels) return;
        if (!e.Data.Contains("VoiceChannel")) return;

        var draggedChannel = e.Data.Get("VoiceChannel") as ChannelResponse;
        if (draggedChannel is null) return;

        // Find the target channel based on where we dropped
        ChannelResponse? targetChannel = null;
        if (sender is Border border && border.Tag is ChannelResponse target)
        {
            targetChannel = target;
        }

        if (targetChannel is null || targetChannel.Id == draggedChannel.Id) return;

        // Get all channels and recalculate order
        var allChannels = (TextChannels?.ToList() ?? new List<ChannelResponse>())
            .Concat(VoiceChannels?.Select(v => v.Channel) ?? Enumerable.Empty<ChannelResponse>())
            .OrderBy(c => c.Position)
            .ToList();

        var draggedIndex = allChannels.FindIndex(c => c.Id == draggedChannel.Id);
        var targetIndex = allChannels.FindIndex(c => c.Id == targetChannel.Id);

        if (draggedIndex < 0 || targetIndex < 0) return;

        // Determine if dropping before or after target based on mouse position
        var position = e.GetPosition((Visual)sender!);
        var targetControl = (Control)sender!;
        var midPoint = targetControl.Bounds.Height / 2;
        var dropBeforeTarget = position.Y <= midPoint;

        // Remove the dragged item first
        allChannels.RemoveAt(draggedIndex);

        // Find where target is now (index may have shifted if we removed from before it)
        var newTargetIndex = allChannels.FindIndex(c => c.Id == targetChannel.Id);

        // Insert at the appropriate position
        var insertIndex = dropBeforeTarget ? newTargetIndex : newTargetIndex + 1;
        allChannels.Insert(insertIndex, draggedChannel);

        // Create the new order list
        var newOrder = allChannels.Select(c => c.Id).ToList();

        // Hide drop indicator
        HideAllDropIndicators();

        // Execute the reorder command
        ReorderChannelsCommand?.Execute(newOrder);
    }

    // Bottom drop zone handlers - allows dropping at the end of the voice channel list
    private void OnVoiceBottomDropZoneDragOver(object? sender, DragEventArgs e)
    {
        if (!CanManageChannels) return;
        if (!e.Data.Contains("VoiceChannel")) return;

        e.DragEffects = DragDropEffects.Move;

        // Show the inner drop indicator within the drop zone
        if (sender is Border container && container.Child is Border indicator)
        {
            HideAllDropIndicators();
            indicator.Classes.Add("active");
            _activeDropIndicator = indicator;
        }
    }

    private void OnVoiceBottomDropZoneDrop(object? sender, DragEventArgs e)
    {
        if (!CanManageChannels) return;
        if (!e.Data.Contains("VoiceChannel")) return;

        var draggedChannel = e.Data.Get("VoiceChannel") as ChannelResponse;
        if (draggedChannel is null) return;

        Console.WriteLine($"OnVoiceBottomDropZoneDrop: Dropping {draggedChannel.Name} at end");

        // Get all channels and recalculate order
        var allChannels = (TextChannels?.ToList() ?? new List<ChannelResponse>())
            .Concat(VoiceChannels?.Select(v => v.Channel) ?? Enumerable.Empty<ChannelResponse>())
            .OrderBy(c => c.Position)
            .ToList();

        var draggedIndex = allChannels.FindIndex(c => c.Id == draggedChannel.Id);
        if (draggedIndex < 0) return;

        // Remove from current position and add at end
        allChannels.RemoveAt(draggedIndex);
        allChannels.Add(draggedChannel);

        // Create the new order list
        var newOrder = allChannels.Select(c => c.Id).ToList();

        Console.WriteLine($"OnVoiceBottomDropZoneDrop: New order has {newOrder.Count} channels");

        // Hide drop indicator
        HideAllDropIndicators();

        // Execute the reorder command
        ReorderChannelsCommand?.Execute(newOrder);
    }
    #pragma warning restore CS0618

    // Move user to channel - wire up click handlers on submenu items
    private VoiceParticipantViewModel? _moveUserParticipant;

    private void MoveToMenuItem_SubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parentMenuItem) return;

        // Store the participant from the parent menu item's Tag
        _moveUserParticipant = parentMenuItem.Tag as VoiceParticipantViewModel;
        if (_moveUserParticipant is null) return;

        // Wire up click handlers for each submenu item
        foreach (var item in parentMenuItem.Items)
        {
            if (item is MenuItem submenuItem)
            {
                // Remove any previous handler to avoid duplicates
                submenuItem.Click -= MoveToChannel_Click;
                submenuItem.Click += MoveToChannel_Click;
            }
        }
    }

    private void MoveToChannel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (_moveUserParticipant is null) return;

        var targetChannel = menuItem.Tag as VoiceChannelViewModel;
        if (targetChannel is null) return;

        // Don't move to the same channel
        if (targetChannel.Id == _moveUserParticipant.Participant.ChannelId) return;

        // Invoke the command or raise event
        if (MoveUserToChannelCommand?.CanExecute((_moveUserParticipant, targetChannel)) == true)
        {
            MoveUserToChannelCommand.Execute((_moveUserParticipant, targetChannel));
        }
        else
        {
            MoveUserRequested?.Invoke(this, (_moveUserParticipant, targetChannel));
        }

        _moveUserParticipant = null;
    }
}
