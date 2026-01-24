using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.Coordinators;

/// <summary>
/// Coordinator for message operations including reactions and pinning.
/// </summary>
public interface IMessageCoordinator
{
    /// <summary>
    /// Toggles a reaction on a message (adds if not present, removes if present).
    /// </summary>
    Task<bool> ToggleReactionAsync(Guid channelId, Guid messageId, string emoji, bool hasReacted);

    /// <summary>
    /// Adds a reaction to a message.
    /// </summary>
    Task<bool> AddReactionAsync(Guid channelId, Guid messageId, string emoji);

    /// <summary>
    /// Removes a reaction from a message.
    /// </summary>
    Task<bool> RemoveReactionAsync(Guid channelId, Guid messageId, string emoji);

    /// <summary>
    /// Toggles pin state on a message.
    /// </summary>
    Task<bool> TogglePinAsync(Guid channelId, Guid messageId, bool isPinned);

    /// <summary>
    /// Pins a message.
    /// </summary>
    Task<bool> PinMessageAsync(Guid channelId, Guid messageId);

    /// <summary>
    /// Unpins a message.
    /// </summary>
    Task<bool> UnpinMessageAsync(Guid channelId, Guid messageId);
}

public class MessageCoordinator : IMessageCoordinator
{
    private readonly IApiClient _apiClient;
    private readonly IMessageStore _messageStore;

    public MessageCoordinator(IApiClient apiClient, IMessageStore messageStore)
    {
        _apiClient = apiClient;
        _messageStore = messageStore;
    }

    public async Task<bool> ToggleReactionAsync(Guid channelId, Guid messageId, string emoji, bool hasReacted)
    {
        if (hasReacted)
        {
            return await RemoveReactionAsync(channelId, messageId, emoji);
        }
        else
        {
            return await AddReactionAsync(channelId, messageId, emoji);
        }
    }

    public async Task<bool> AddReactionAsync(Guid channelId, Guid messageId, string emoji)
    {
        try
        {
            var result = await _apiClient.AddReactionAsync(channelId, messageId, emoji);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RemoveReactionAsync(Guid channelId, Guid messageId, string emoji)
    {
        try
        {
            var result = await _apiClient.RemoveReactionAsync(channelId, messageId, emoji);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TogglePinAsync(Guid channelId, Guid messageId, bool isPinned)
    {
        if (isPinned)
        {
            return await UnpinMessageAsync(channelId, messageId);
        }
        else
        {
            return await PinMessageAsync(channelId, messageId);
        }
    }

    public async Task<bool> PinMessageAsync(Guid channelId, Guid messageId)
    {
        try
        {
            var result = await _apiClient.PinMessageAsync(channelId, messageId);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> UnpinMessageAsync(Guid channelId, Guid messageId)
    {
        try
        {
            var result = await _apiClient.UnpinMessageAsync(channelId, messageId);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }
}
