using System.Reactive;
using ReactiveUI;
using Snacka.Client.Coordinators;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for channel management operations (editing, deletion, reordering).
/// Encapsulates channel CRUD state and commands.
/// Reads state from ChannelStore and CommunityStore (Redux-style).
/// </summary>
public class ChannelManagementViewModel : ReactiveObject, IDisposable
{
    private readonly IChannelCoordinator _channelCoordinator;
    private readonly IChannelStore _channelStore;
    private readonly ICommunityStore _communityStore;
    private readonly Func<VoiceChannelViewModelManager?> _getVoiceChannelManager;

    private ChannelState? _editingChannel;
    private string _editingChannelName = string.Empty;
    private ChannelState? _channelPendingDelete;
    private Guid? _pendingReorderCommunityId;
    private bool _isLoading;

    /// <summary>
    /// Raised when an error occurs during channel operations.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Raised when loading state changes.
    /// </summary>
    public event Action<bool>? LoadingChanged;

    /// <summary>
    /// Creates a new ChannelManagementViewModel.
    /// Reads state from ChannelStore and CommunityStore (Redux-style).
    /// </summary>
    public ChannelManagementViewModel(
        IChannelCoordinator channelCoordinator,
        IChannelStore channelStore,
        ICommunityStore communityStore,
        Func<VoiceChannelViewModelManager?> getVoiceChannelManager)
    {
        _channelCoordinator = channelCoordinator;
        _channelStore = channelStore;
        _communityStore = communityStore;
        _getVoiceChannelManager = getVoiceChannelManager;

        // Create commands
        StartEditChannelCommand = ReactiveCommand.Create<ChannelState>(StartEditChannel);
        SaveChannelNameCommand = ReactiveCommand.CreateFromTask(SaveChannelNameAsync);
        CancelEditChannelCommand = ReactiveCommand.Create(CancelEditChannel);
        DeleteChannelCommand = ReactiveCommand.Create<ChannelState>(RequestDeleteChannel);
        ConfirmDeleteChannelCommand = ReactiveCommand.CreateFromTask(ConfirmDeleteChannelAsync);
        CancelDeleteChannelCommand = ReactiveCommand.Create(CancelDeleteChannel);
        ReorderChannelsCommand = ReactiveCommand.CreateFromTask<List<Guid>>(ReorderChannelsAsync);
        PreviewReorderCommand = ReactiveCommand.Create<(Guid DraggedId, Guid TargetId, bool DropBefore)>(PreviewReorder);
        CancelPreviewCommand = ReactiveCommand.Create(CancelPreview);
    }

    #region Properties

    /// <summary>
    /// The channel currently being edited (null if not editing).
    /// </summary>
    public ChannelState? EditingChannel
    {
        get => _editingChannel;
        set => this.RaiseAndSetIfChanged(ref _editingChannel, value);
    }

    /// <summary>
    /// The name being entered for the channel being edited.
    /// </summary>
    public string EditingChannelName
    {
        get => _editingChannelName;
        set => this.RaiseAndSetIfChanged(ref _editingChannelName, value);
    }

    /// <summary>
    /// The channel pending deletion (shown in confirmation dialog).
    /// </summary>
    public ChannelState? ChannelPendingDelete
    {
        get => _channelPendingDelete;
        set
        {
            this.RaiseAndSetIfChanged(ref _channelPendingDelete, value);
            this.RaisePropertyChanged(nameof(ShowChannelDeleteConfirmation));
        }
    }

    /// <summary>
    /// Whether to show the channel deletion confirmation dialog.
    /// </summary>
    public bool ShowChannelDeleteConfirmation => ChannelPendingDelete is not null;

    /// <summary>
    /// Whether a channel operation is in progress.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            var oldValue = _isLoading;
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            if (oldValue != value)
            {
                LoadingChanged?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// The community ID for which a reorder is pending (to skip redundant SignalR updates).
    /// </summary>
    public Guid? PendingReorderCommunityId
    {
        get => _pendingReorderCommunityId;
        set => _pendingReorderCommunityId = value;
    }

    #endregion

    #region Commands

    /// <summary>
    /// Command to start editing a channel.
    /// </summary>
    public ReactiveCommand<ChannelState, Unit> StartEditChannelCommand { get; }

    /// <summary>
    /// Command to save the channel name edit.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveChannelNameCommand { get; }

    /// <summary>
    /// Command to cancel channel editing.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelEditChannelCommand { get; }

    /// <summary>
    /// Command to request channel deletion (shows confirmation).
    /// </summary>
    public ReactiveCommand<ChannelState, Unit> DeleteChannelCommand { get; }

    /// <summary>
    /// Command to confirm channel deletion.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ConfirmDeleteChannelCommand { get; }

    /// <summary>
    /// Command to cancel channel deletion.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelDeleteChannelCommand { get; }

    /// <summary>
    /// Command to reorder channels.
    /// </summary>
    public ReactiveCommand<List<Guid>, Unit> ReorderChannelsCommand { get; }

    /// <summary>
    /// Command to preview channel reorder (during drag).
    /// </summary>
    public ReactiveCommand<(Guid DraggedId, Guid TargetId, bool DropBefore), Unit> PreviewReorderCommand { get; }

    /// <summary>
    /// Command to cancel reorder preview.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CancelPreviewCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Starts editing a channel.
    /// </summary>
    public void StartEditChannel(ChannelState channel)
    {
        EditingChannel = channel;
        EditingChannelName = channel.Name;
    }

    /// <summary>
    /// Cancels channel editing.
    /// </summary>
    public void CancelEditChannel()
    {
        EditingChannel = null;
        EditingChannelName = string.Empty;
    }

    /// <summary>
    /// Saves the channel name edit.
    /// </summary>
    public async Task SaveChannelNameAsync()
    {
        var selectedCommunity = _communityStore.GetSelectedCommunity();
        if (EditingChannel is null || selectedCommunity is null || string.IsNullOrWhiteSpace(EditingChannelName))
            return;

        IsLoading = true;
        try
        {
            var success = await _channelCoordinator.UpdateChannelAsync(
                selectedCommunity.Id, EditingChannel.Id, EditingChannelName.Trim(), null);

            if (success)
            {
                // The store is already updated by the coordinator, no need to update selection
                EditingChannel = null;
                EditingChannelName = string.Empty;
            }
            else
            {
                ErrorOccurred?.Invoke("Failed to update channel");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Requests channel deletion (shows confirmation dialog).
    /// </summary>
    public void RequestDeleteChannel(ChannelState channel)
    {
        ChannelPendingDelete = channel;
    }

    /// <summary>
    /// Cancels channel deletion.
    /// </summary>
    public void CancelDeleteChannel()
    {
        ChannelPendingDelete = null;
    }

    /// <summary>
    /// Confirms and executes channel deletion.
    /// </summary>
    public async Task ConfirmDeleteChannelAsync()
    {
        var selectedCommunity = _communityStore.GetSelectedCommunity();
        if (ChannelPendingDelete is null || selectedCommunity is null) return;

        var channel = ChannelPendingDelete;
        var selectedChannel = _channelStore.GetSelectedChannel();

        // Check if this is the currently selected channel
        var wasSelected = selectedChannel?.Id == channel.Id;

        IsLoading = true;
        try
        {
            var success = await _channelCoordinator.DeleteChannelAsync(channel.Id);
            if (success)
            {
                // If the deleted channel was selected, select another one
                if (wasSelected)
                {
                    var allChannels = _channelStore.GetAllChannels();
                    var textChannels = _channelStore.GetTextChannels();

                    if (allChannels.Count > 0)
                    {
                        var newSelection = textChannels.FirstOrDefault() ?? allChannels.FirstOrDefault();
                        _channelStore.SelectChannel(newSelection?.Id);
                    }
                    else
                    {
                        _channelStore.SelectChannel(null);
                    }
                }

                // Clear the pending delete
                ChannelPendingDelete = null;
            }
            else
            {
                ErrorOccurred?.Invoke("Failed to delete channel");
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Reorders channels with optimistic update and rollback on failure.
    /// </summary>
    public async Task ReorderChannelsAsync(List<Guid> channelIds)
    {
        var selectedCommunity = _communityStore.GetSelectedCommunity();
        if (selectedCommunity is null) return;

        var voiceChannelManager = _getVoiceChannelManager();

        // Store original order for rollback
        var originalChannels = _channelStore.GetAllChannels().ToList();
        var originalVoiceOrder = voiceChannelManager?.CaptureOrder() ?? new List<VoiceChannelViewModel>();

        // Apply optimistically - update UI immediately
        ApplyChannelOrder(channelIds, voiceChannelManager);
        ClearPreviewState(voiceChannelManager);

        // Mark that we're expecting a SignalR event for this reorder
        _pendingReorderCommunityId = selectedCommunity.Id;

        try
        {
            var success = await _channelCoordinator.ReorderChannelsAsync(selectedCommunity.Id, channelIds);
            if (!success)
            {
                // Server rejected - rollback to original order
                ErrorOccurred?.Invoke("Failed to reorder channels");
                RollbackChannelOrder(originalChannels, originalVoiceOrder, voiceChannelManager);
                _pendingReorderCommunityId = null;
            }
        }
        catch (Exception ex)
        {
            // Network error - rollback to original order
            ErrorOccurred?.Invoke($"Error reordering channels: {ex.Message}");
            RollbackChannelOrder(originalChannels, originalVoiceOrder, voiceChannelManager);
            _pendingReorderCommunityId = null;
        }
    }

    /// <summary>
    /// Previews channel reorder during drag operation.
    /// </summary>
    public void PreviewReorder((Guid DraggedId, Guid TargetId, bool DropBefore) args)
    {
        _getVoiceChannelManager()?.PreviewReorder(args.DraggedId, args.TargetId, args.DropBefore);
    }

    /// <summary>
    /// Cancels reorder preview.
    /// </summary>
    public void CancelPreview()
    {
        _getVoiceChannelManager()?.CancelPreview();
    }

    private void ApplyChannelOrder(List<Guid> channelIds, VoiceChannelViewModelManager? voiceChannelManager)
    {
        // Create a lookup for new positions
        var positionLookup = channelIds.Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        // Create updated channels with new positions and update the store
        var updatedChannels = _channelStore.GetAllChannels()
            .Select(c => ToChannelResponse(c with { Position = positionLookup.GetValueOrDefault(c.Id, int.MaxValue) }))
            .ToList();
        _channelStore.ReorderChannels(updatedChannels);

        // Update VoiceChannelViewModels positions and re-sort
        voiceChannelManager?.ApplyPositions(channelIds);
    }

    private void RollbackChannelOrder(
        List<ChannelState> originalChannels,
        List<VoiceChannelViewModel> originalVoiceOrder,
        VoiceChannelViewModelManager? voiceChannelManager)
    {
        // Restore channels in the store
        _channelStore.SetChannels(originalChannels.Select(ToChannelResponse));

        // Restore VoiceChannelViewModels
        voiceChannelManager?.RestoreOrder(originalVoiceOrder);
    }

    private void ClearPreviewState(VoiceChannelViewModelManager? voiceChannelManager)
    {
        voiceChannelManager?.ClearPreviewState();
    }

    /// <summary>
    /// Clears the pending reorder state (called after SignalR confirms the reorder).
    /// </summary>
    public void ClearPendingReorder()
    {
        _pendingReorderCommunityId = null;
    }

    private static ChannelResponse ToChannelResponse(ChannelState state)
    {
        return new ChannelResponse(
            state.Id,
            state.Name,
            state.Topic,
            state.CommunityId,
            state.Type,
            state.Position,
            state.CreatedAt,
            state.UnreadCount);
    }

    #endregion

    public void Dispose()
    {
        StartEditChannelCommand.Dispose();
        SaveChannelNameCommand.Dispose();
        CancelEditChannelCommand.Dispose();
        DeleteChannelCommand.Dispose();
        ConfirmDeleteChannelCommand.Dispose();
        CancelDeleteChannelCommand.Dispose();
        ReorderChannelsCommand.Dispose();
        PreviewReorderCommand.Dispose();
        CancelPreviewCommand.Dispose();
    }
}
