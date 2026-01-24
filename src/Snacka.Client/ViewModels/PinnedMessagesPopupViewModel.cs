using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the pinned messages popup.
/// Handles loading and displaying pinned messages for a channel.
/// Reads current channel from ChannelStore (Redux-style).
/// </summary>
public class PinnedMessagesPopupViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly IChannelStore _channelStore;

    private bool _isOpen;
    private ObservableCollection<MessageResponse> _messages = new();

    public PinnedMessagesPopupViewModel(IApiClient apiClient, IChannelStore channelStore)
    {
        _apiClient = apiClient;
        _channelStore = channelStore;

        ShowCommand = ReactiveCommand.CreateFromTask(ShowAsync);
        CloseCommand = ReactiveCommand.Create(Close);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public ObservableCollection<MessageResponse> Messages
    {
        get => _messages;
        set => this.RaiseAndSetIfChanged(ref _messages, value);
    }

    public ReactiveCommand<Unit, Unit> ShowCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    private async Task ShowAsync()
    {
        var channelId = _channelStore.GetSelectedChannelId();
        if (channelId == null) return;

        await LoadMessagesAsync(channelId.Value);
        IsOpen = true;
    }

    private void Close()
    {
        IsOpen = false;
    }

    private async Task LoadMessagesAsync(Guid channelId)
    {
        var result = await _apiClient.GetPinnedMessagesAsync(channelId);
        if (result.Success && result.Data is not null)
        {
            Messages.Clear();
            foreach (var message in result.Data)
                Messages.Add(message);
        }
    }

    /// <summary>
    /// Called when a message's pinned status changes via SignalR.
    /// </summary>
    public void OnMessagePinStatusChanged(Guid messageId, bool isPinned)
    {
        if (!IsOpen) return;

        if (isPinned)
        {
            // Reload to get the new pinned message
            var channelId = _channelStore.GetSelectedChannelId();
            if (channelId != null)
                _ = LoadMessagesAsync(channelId.Value);
        }
        else
        {
            // Remove unpinned message
            var index = Messages.ToList().FindIndex(m => m.Id == messageId);
            if (index >= 0)
                Messages.RemoveAt(index);
        }
    }
}
