using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.Coordinators;

/// <summary>
/// Coordinator for channel-related operations that span multiple stores.
/// Handles selecting channels, loading messages, and managing channel lifecycle.
/// </summary>
public interface IChannelCoordinator
{
    /// <summary>
    /// Selects a text channel and loads its messages.
    /// </summary>
    Task SelectTextChannelAsync(Guid communityId, Guid channelId);

    /// <summary>
    /// Creates a new channel in the current community.
    /// </summary>
    Task<ChannelResponse?> CreateChannelAsync(Guid communityId, string name, string? topic, ChannelType type);

    /// <summary>
    /// Updates a channel's name and/or topic.
    /// </summary>
    Task<bool> UpdateChannelAsync(Guid communityId, Guid channelId, string? name, string? topic);

    /// <summary>
    /// Deletes a channel.
    /// </summary>
    Task<bool> DeleteChannelAsync(Guid channelId);

    /// <summary>
    /// Reorders channels in a community.
    /// </summary>
    Task<bool> ReorderChannelsAsync(Guid communityId, List<Guid> channelIds);

    /// <summary>
    /// Loads more messages for the current channel (pagination).
    /// </summary>
    Task<bool> LoadMoreMessagesAsync(Guid channelId, int offset, int limit);

    /// <summary>
    /// Marks the current channel as read.
    /// </summary>
    Task MarkChannelAsReadAsync(Guid communityId, Guid channelId);

    /// <summary>
    /// Clears channel selection.
    /// </summary>
    void ClearSelection();
}

public class ChannelCoordinator : IChannelCoordinator
{
    private readonly IChannelStore _channelStore;
    private readonly IMessageStore _messageStore;
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;

    private Guid? _previousChannelId;

    public ChannelCoordinator(
        IChannelStore channelStore,
        IMessageStore messageStore,
        IApiClient apiClient,
        ISignalRService signalR)
    {
        _channelStore = channelStore;
        _messageStore = messageStore;
        _apiClient = apiClient;
        _signalR = signalR;
    }

    public async Task SelectTextChannelAsync(Guid communityId, Guid channelId)
    {
        var channel = _channelStore.GetChannel(channelId);
        if (channel is null || channel.Type != ChannelType.Text)
            return;

        // Leave previous channel's SignalR group
        if (_previousChannelId.HasValue && _previousChannelId.Value != channelId)
        {
            try
            {
                await _signalR.LeaveChannelAsync(_previousChannelId.Value);
            }
            catch
            {
                // Ignore leave errors
            }
        }

        // Update store selection
        _channelStore.SelectChannel(channelId);
        _messageStore.SetCurrentChannel(channelId);
        _previousChannelId = channelId;

        // Load messages for this channel
        var messagesResult = await _apiClient.GetMessagesAsync(channelId, 0, 50);
        if (messagesResult.Success && messagesResult.Data is not null)
        {
            _messageStore.SetMessages(channelId, messagesResult.Data);
        }

        // Join SignalR group for real-time updates
        try
        {
            await _signalR.JoinChannelAsync(channelId);
        }
        catch
        {
            // Continue even if join fails - we have messages loaded
        }

        // Mark channel as read
        await MarkChannelAsReadAsync(communityId, channelId);
    }

    public async Task<ChannelResponse?> CreateChannelAsync(Guid communityId, string name, string? topic, ChannelType type)
    {
        var result = await _apiClient.CreateChannelAsync(communityId, name, topic, type);

        if (result.Success && result.Data is not null)
        {
            _channelStore.AddChannel(result.Data);
            return result.Data;
        }

        return null;
    }

    public async Task<bool> UpdateChannelAsync(Guid communityId, Guid channelId, string? name, string? topic)
    {
        var result = await _apiClient.UpdateChannelAsync(communityId, channelId, name, topic);

        if (result.Success && result.Data is not null)
        {
            _channelStore.UpdateChannel(result.Data);
            return true;
        }

        return false;
    }

    public async Task<bool> DeleteChannelAsync(Guid channelId)
    {
        var result = await _apiClient.DeleteChannelAsync(channelId);

        if (result.Success)
        {
            _channelStore.RemoveChannel(channelId);
            _messageStore.ClearChannel(channelId);
            return true;
        }

        return false;
    }

    public async Task<bool> ReorderChannelsAsync(Guid communityId, List<Guid> channelIds)
    {
        var result = await _apiClient.ReorderChannelsAsync(communityId, channelIds);
        return result.Success;
        // Note: Server will send ChannelsReordered event via SignalR
    }

    public async Task<bool> LoadMoreMessagesAsync(Guid channelId, int offset, int limit)
    {
        var result = await _apiClient.GetMessagesAsync(channelId, offset, limit);

        if (result.Success && result.Data is not null)
        {
            // Add messages to existing ones (append older messages)
            foreach (var message in result.Data)
            {
                _messageStore.AddMessage(message);
            }
            return true;
        }

        return false;
    }

    public async Task MarkChannelAsReadAsync(Guid communityId, Guid channelId)
    {
        // Optimistically update local state
        _channelStore.UpdateUnreadCount(channelId, 0);

        // Sync with server
        await _apiClient.MarkChannelAsReadAsync(communityId, channelId);
    }

    public void ClearSelection()
    {
        _channelStore.SelectChannel(null);
        _messageStore.SetCurrentChannel(null);
    }
}
