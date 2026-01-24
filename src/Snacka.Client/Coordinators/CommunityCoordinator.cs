using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.Coordinators;

/// <summary>
/// Coordinator for community-related operations that span multiple stores.
/// Handles selecting communities, loading channels/members, and community management.
/// </summary>
public interface ICommunityCoordinator
{
    /// <summary>
    /// Loads all communities for the current user.
    /// </summary>
    Task<bool> LoadCommunitiesAsync();

    /// <summary>
    /// Selects a community and loads its channels and members.
    /// </summary>
    Task SelectCommunityAsync(Guid communityId);

    /// <summary>
    /// Creates a new community.
    /// </summary>
    Task<CommunityResponse?> CreateCommunityAsync(string name, string? description);

    /// <summary>
    /// Invites a user to the current community.
    /// </summary>
    Task<bool> InviteUserAsync(Guid communityId, Guid userId);

    /// <summary>
    /// Updates a member's role.
    /// </summary>
    Task<bool> UpdateMemberRoleAsync(Guid communityId, Guid userId, UserRole role);

    /// <summary>
    /// Updates a member's nickname.
    /// </summary>
    Task<bool> UpdateMemberNicknameAsync(Guid communityId, Guid userId, string? nickname);

    /// <summary>
    /// Clears community selection.
    /// </summary>
    void ClearSelection();
}

public class CommunityCoordinator : ICommunityCoordinator
{
    private readonly ICommunityStore _communityStore;
    private readonly IChannelStore _channelStore;
    private readonly IMessageStore _messageStore;
    private readonly IVoiceStore _voiceStore;
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;

    private Guid? _previousCommunityId;

    public CommunityCoordinator(
        ICommunityStore communityStore,
        IChannelStore channelStore,
        IMessageStore messageStore,
        IVoiceStore voiceStore,
        IApiClient apiClient,
        ISignalRService signalR)
    {
        _communityStore = communityStore;
        _channelStore = channelStore;
        _messageStore = messageStore;
        _voiceStore = voiceStore;
        _apiClient = apiClient;
        _signalR = signalR;
    }

    public async Task<bool> LoadCommunitiesAsync()
    {
        var result = await _apiClient.GetCommunitiesAsync();

        if (result.Success && result.Data is not null)
        {
            _communityStore.SetCommunities(result.Data);
            return true;
        }

        return false;
    }

    public async Task SelectCommunityAsync(Guid communityId)
    {
        var community = _communityStore.GetCommunity(communityId);
        if (community is null)
            return;

        // Leave previous community's SignalR group
        if (_previousCommunityId.HasValue && _previousCommunityId.Value != communityId)
        {
            try
            {
                await _signalR.LeaveServerAsync(_previousCommunityId.Value);
            }
            catch
            {
                // Ignore leave errors
            }
        }

        // Update store selection
        _communityStore.SelectCommunity(communityId);
        _previousCommunityId = communityId;

        // Clear previous channel selection and messages
        _channelStore.Clear();
        _messageStore.Clear();

        // Join SignalR group for real-time updates
        try
        {
            await _signalR.JoinServerAsync(communityId);
        }
        catch
        {
            // Continue even if join fails
        }

        // Load channels and members in parallel
        var channelsTask = _apiClient.GetChannelsAsync(communityId);
        var membersTask = _apiClient.GetMembersAsync(communityId);

        await Task.WhenAll(channelsTask, membersTask);

        if (channelsTask.Result.Success && channelsTask.Result.Data is not null)
        {
            _channelStore.SetChannels(channelsTask.Result.Data);
        }

        if (membersTask.Result.Success && membersTask.Result.Data is not null)
        {
            _communityStore.SetMembers(communityId, membersTask.Result.Data);
        }

        // Load voice participants for voice channels
        var channels = _channelStore.GetChannel(communityId);
        // Voice participants are loaded when viewing voice channels
    }

    public async Task<CommunityResponse?> CreateCommunityAsync(string name, string? description)
    {
        var result = await _apiClient.CreateCommunityAsync(name, description);

        if (result.Success && result.Data is not null)
        {
            _communityStore.AddCommunity(result.Data);
            return result.Data;
        }

        return null;
    }

    public async Task<bool> InviteUserAsync(Guid communityId, Guid userId)
    {
        var result = await _apiClient.CreateCommunityInviteAsync(communityId, userId);
        return result.Success;
    }

    public async Task<bool> UpdateMemberRoleAsync(Guid communityId, Guid userId, UserRole role)
    {
        var result = await _apiClient.UpdateMemberRoleAsync(communityId, userId, role);

        if (result.Success)
        {
            _communityStore.UpdateMemberRole(communityId, userId, role);
            return true;
        }

        return false;
    }

    public async Task<bool> UpdateMemberNicknameAsync(Guid communityId, Guid userId, string? nickname)
    {
        var result = await _apiClient.UpdateMemberNicknameAsync(communityId, userId, nickname);

        if (result.Success)
        {
            _communityStore.UpdateMemberNickname(communityId, userId, nickname);
            return true;
        }

        return false;
    }

    public void ClearSelection()
    {
        _communityStore.SelectCommunity(null);
        _channelStore.Clear();
        _messageStore.Clear();
    }
}
