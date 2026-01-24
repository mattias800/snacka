using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

/// <summary>
/// Manages the collection of VoiceChannelViewModels for the sidebar.
/// Handles creation, destruction, reordering, and drag-drop preview state.
/// Subscribes to SignalR events directly for channel lifecycle management.
/// </summary>
public class VoiceChannelViewModelManager : ReactiveObject, IDisposable
{
    private readonly IVoiceStore _voiceStore;
    private readonly ISignalRService _signalR;
    private readonly Guid _currentUserId;
    private readonly Action<Guid, float>? _onVolumeChanged;
    private readonly Func<Guid, float>? _getInitialVolume;
    private readonly Func<Guid?> _getPendingReorderCommunityId;
    private readonly Action _clearPendingReorder;

    // Current community filter
    private Guid? _currentCommunityId;

    // Drag preview state
    private List<VoiceChannelViewModel>? _originalOrder;
    private Guid? _currentDraggedId;
    private Guid? _previewGapTargetId;
    private bool _previewGapAbove;

    public VoiceChannelViewModelManager(
        IVoiceStore voiceStore,
        ISignalRService signalR,
        Guid currentUserId,
        Func<Guid?> getPendingReorderCommunityId,
        Action clearPendingReorder,
        Action<Guid, float>? onVolumeChanged = null,
        Func<Guid, float>? getInitialVolume = null)
    {
        _voiceStore = voiceStore;
        _signalR = signalR;
        _currentUserId = currentUserId;
        _getPendingReorderCommunityId = getPendingReorderCommunityId;
        _clearPendingReorder = clearPendingReorder;
        _onVolumeChanged = onVolumeChanged;
        _getInitialVolume = getInitialVolume;
        ViewModels = new ObservableCollection<VoiceChannelViewModel>();

        // Subscribe to SignalR events
        _signalR.ChannelCreated += OnChannelCreated;
        _signalR.ChannelDeleted += OnChannelDeleted;
        _signalR.ChannelsReordered += OnChannelsReordered;
    }

    private void OnChannelCreated(ChannelResponse channel)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_currentCommunityId.HasValue && channel.CommunityId == _currentCommunityId.Value)
            {
                AddChannel(channel);
            }
        });
    }

    private void OnChannelDeleted(ChannelDeletedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RemoveChannel(e.ChannelId);
        });
    }

    private void OnChannelsReordered(ChannelsReorderedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Only update if it's for the current community
            if (_currentCommunityId != e.CommunityId) return;

            // Skip if we just initiated this reorder (we already updated optimistically)
            if (_getPendingReorderCommunityId() == e.CommunityId)
            {
                _clearPendingReorder();
                return;
            }

            UpdatePositions(e.Channels);
        });
    }

    /// <summary>
    /// Sets the current community ID for filtering channel events.
    /// </summary>
    public void SetCurrentCommunity(Guid? communityId)
    {
        _currentCommunityId = communityId;
    }

    public ObservableCollection<VoiceChannelViewModel> ViewModels { get; }

    /// <summary>
    /// Creates a VoiceChannelViewModel for the given channel with proper callbacks.
    /// </summary>
    public VoiceChannelViewModel CreateViewModel(ChannelResponse channel)
    {
        return new VoiceChannelViewModel(
            channel,
            _voiceStore,
            _currentUserId,
            _onVolumeChanged,
            _getInitialVolume);
    }

    /// <summary>
    /// Initializes the collection from a list of voice channels.
    /// </summary>
    public void Initialize(IEnumerable<ChannelResponse> voiceChannels)
    {
        ViewModels.Clear();
        foreach (var channel in voiceChannels.OrderBy(c => c.Position))
        {
            var vm = CreateViewModel(channel);
            ViewModels.Add(vm);
        }
    }

    /// <summary>
    /// Adds a VoiceChannelViewModel for a newly created channel.
    /// </summary>
    public void AddChannel(ChannelResponse channel)
    {
        if (channel.Type != ChannelType.Voice) return;
        if (ViewModels.Any(v => v.Id == channel.Id)) return;

        var vm = CreateViewModel(channel);
        ViewModels.Add(vm);
    }

    /// <summary>
    /// Removes the VoiceChannelViewModel for a deleted channel.
    /// </summary>
    public void RemoveChannel(Guid channelId)
    {
        var vm = ViewModels.FirstOrDefault(v => v.Id == channelId);
        if (vm is not null)
        {
            ViewModels.Remove(vm);
            vm.Dispose();
        }
    }

    /// <summary>
    /// Gets a VoiceChannelViewModel by channel ID.
    /// </summary>
    public VoiceChannelViewModel? GetViewModel(Guid channelId) =>
        ViewModels.FirstOrDefault(v => v.Id == channelId);

    /// <summary>
    /// Updates positions from reordered channel list and re-sorts.
    /// </summary>
    public void UpdatePositions(IEnumerable<ChannelResponse> channels)
    {
        var positionLookup = channels.ToDictionary(c => c.Id, c => c.Position);
        foreach (var vm in ViewModels)
        {
            if (positionLookup.TryGetValue(vm.Id, out var newPosition))
            {
                vm.Position = newPosition;
            }
        }
        SortByPosition();
    }

    /// <summary>
    /// Applies new positions from a list of channel IDs in order.
    /// </summary>
    public void ApplyPositions(List<Guid> channelIds)
    {
        var positionLookup = channelIds.Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        foreach (var vm in ViewModels)
        {
            if (positionLookup.TryGetValue(vm.Id, out var newPosition))
            {
                vm.Position = newPosition;
            }
        }
        SortByPosition();
    }

    /// <summary>
    /// Sorts ViewModels by position in place.
    /// </summary>
    public void SortByPosition()
    {
        var sorted = ViewModels.OrderBy(v => v.Position).ToList();
        ViewModels.Clear();
        foreach (var vm in sorted)
        {
            ViewModels.Add(vm);
        }
        this.RaisePropertyChanged(nameof(ViewModels));
    }

    /// <summary>
    /// Clears all ViewModels.
    /// </summary>
    public void Clear()
    {
        foreach (var vm in ViewModels)
        {
            vm.Dispose();
        }
        ViewModels.Clear();
    }

    #region Drag-Drop Preview

    /// <summary>
    /// Captures the current order for potential rollback.
    /// </summary>
    public List<VoiceChannelViewModel> CaptureOrder() => ViewModels.ToList();

    /// <summary>
    /// Restores a previously captured order.
    /// </summary>
    public void RestoreOrder(List<VoiceChannelViewModel> originalOrder)
    {
        ViewModels.Clear();
        foreach (var vm in originalOrder)
        {
            ViewModels.Add(vm);
        }
        this.RaisePropertyChanged(nameof(ViewModels));
    }

    /// <summary>
    /// Shows a preview gap for drag-drop feedback.
    /// </summary>
    public void PreviewReorder(Guid draggedId, Guid targetId, bool dropBefore)
    {
        // Store original order on first preview call for this drag
        if (_originalOrder is null || _currentDraggedId != draggedId)
        {
            _originalOrder = ViewModels.ToList();
            _currentDraggedId = draggedId;
        }

        // Track where the gap should be
        _previewGapTargetId = targetId;
        _previewGapAbove = dropBefore;

        // Update gap visibility for all items
        foreach (var vm in ViewModels)
        {
            if (vm.Id == targetId)
            {
                vm.ShowGapAbove = dropBefore;
                vm.ShowGapBelow = !dropBefore;
            }
            else
            {
                vm.ShowGapAbove = false;
                vm.ShowGapBelow = false;
            }

            // Mark the dragged item
            vm.IsDragSource = vm.Id == draggedId;
        }
    }

    /// <summary>
    /// Cancels the current drag preview and clears gap indicators.
    /// </summary>
    public void CancelPreview()
    {
        ClearPreviewState();
        _originalOrder = null;
        _currentDraggedId = null;
        _previewGapTargetId = null;
    }

    /// <summary>
    /// Clears preview state after a successful reorder.
    /// </summary>
    public void ClearPreviewState()
    {
        foreach (var vm in ViewModels)
        {
            vm.ShowGapAbove = false;
            vm.ShowGapBelow = false;
            vm.IsDragSource = false;
        }
    }

    #endregion

    public void Dispose()
    {
        _signalR.ChannelCreated -= OnChannelCreated;
        _signalR.ChannelDeleted -= OnChannelDeleted;
        _signalR.ChannelsReordered -= OnChannelsReordered;
        Clear();
    }
}
