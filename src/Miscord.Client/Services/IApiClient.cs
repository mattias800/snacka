using Miscord.Shared.Models;

namespace Miscord.Client.Services;

public interface IApiClient
{
    // Connection
    string? BaseUrl { get; }
    void SetBaseUrl(string url);
    Task<ApiResult<ServerInfoResponse>> GetServerInfoAsync();

    // Auth
    Task<ApiResult<AuthResponse>> RegisterAsync(string username, string email, string password, string inviteCode);
    Task<ApiResult<AuthResponse>> LoginAsync(string email, string password);
    Task<ApiResult<AuthResponse>> RefreshTokenAsync(string refreshToken);
    Task<ApiResult<UserProfileResponse>> GetProfileAsync();
    Task<ApiResult<bool>> ChangePasswordAsync(string currentPassword, string newPassword);
    Task<ApiResult<bool>> DeleteAccountAsync();
    void SetAuthToken(string token);
    void ClearAuthToken();
    bool IsAuthenticated { get; }

    // Admin
    Task<ApiResult<List<ServerInviteResponse>>> GetInvitesAsync();
    Task<ApiResult<ServerInviteResponse>> CreateInviteAsync(int maxUses = 0, DateTime? expiresAt = null);
    Task<ApiResult<bool>> RevokeInviteAsync(Guid inviteId);
    Task<ApiResult<List<AdminUserResponse>>> GetAllUsersAsync();
    Task<ApiResult<AdminUserResponse>> SetUserAdminStatusAsync(Guid userId, bool isAdmin);
    Task<ApiResult<bool>> DeleteUserAsync(Guid userId);

    // Communities
    Task<ApiResult<List<CommunityResponse>>> GetCommunitiesAsync();
    Task<ApiResult<List<CommunityResponse>>> DiscoverCommunitiesAsync();
    Task<ApiResult<CommunityResponse>> GetCommunityAsync(Guid communityId);
    Task<ApiResult<CommunityResponse>> CreateCommunityAsync(string name, string? description);
    Task<ApiResult<bool>> JoinCommunityAsync(Guid communityId);

    // Channels
    Task<ApiResult<List<ChannelResponse>>> GetChannelsAsync(Guid communityId);
    Task<ApiResult<ChannelResponse>> CreateChannelAsync(Guid communityId, string name, string? topic, ChannelType type = ChannelType.Text);
    Task<ApiResult<ChannelResponse>> UpdateChannelAsync(Guid communityId, Guid channelId, string? name, string? topic);
    Task<ApiResult<bool>> MarkChannelAsReadAsync(Guid communityId, Guid channelId);

    // Messages
    Task<ApiResult<List<MessageResponse>>> GetMessagesAsync(Guid channelId, int skip = 0, int take = 50);
    Task<ApiResult<MessageResponse>> SendMessageAsync(Guid channelId, string content, Guid? replyToId = null);
    Task<ApiResult<MessageResponse>> SendMessageWithAttachmentsAsync(Guid channelId, string? content, Guid? replyToId, IEnumerable<FileAttachment> files);
    Task<ApiResult<MessageResponse>> UpdateMessageAsync(Guid channelId, Guid messageId, string content);
    Task<ApiResult<bool>> DeleteMessageAsync(Guid channelId, Guid messageId);

    // Reactions
    Task<ApiResult<ReactionUpdatedEvent>> AddReactionAsync(Guid channelId, Guid messageId, string emoji);
    Task<ApiResult<bool>> RemoveReactionAsync(Guid channelId, Guid messageId, string emoji);

    // Pinned Messages
    Task<ApiResult<MessagePinnedEvent>> PinMessageAsync(Guid channelId, Guid messageId);
    Task<ApiResult<MessagePinnedEvent>> UnpinMessageAsync(Guid channelId, Guid messageId);
    Task<ApiResult<List<MessageResponse>>> GetPinnedMessagesAsync(Guid channelId);

    // Members
    Task<ApiResult<List<CommunityMemberResponse>>> GetMembersAsync(Guid communityId);
    Task<ApiResult<CommunityMemberResponse>> GetMemberAsync(Guid communityId, Guid userId);
    Task<ApiResult<CommunityMemberResponse>> UpdateMemberRoleAsync(Guid communityId, Guid userId, UserRole newRole);
    Task<ApiResult<bool>> TransferOwnershipAsync(Guid communityId, Guid newOwnerId);

    // Direct Messages
    Task<ApiResult<List<ConversationSummary>>> GetConversationsAsync();
    Task<ApiResult<List<DirectMessageResponse>>> GetDirectMessagesAsync(Guid userId, int skip = 0, int take = 50);
    Task<ApiResult<DirectMessageResponse>> SendDirectMessageAsync(Guid userId, string content);
    Task<ApiResult<DirectMessageResponse>> UpdateDirectMessageAsync(Guid messageId, string content);
    Task<ApiResult<bool>> DeleteDirectMessageAsync(Guid messageId);
    Task<ApiResult<bool>> MarkConversationAsReadAsync(Guid userId);

    // Link Previews
    Task<ApiResult<LinkPreview>> GetLinkPreviewAsync(string url);
}

public record ApiResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }

    public static ApiResult<T> Ok(T data) => new() { Success = true, Data = data };
    public static ApiResult<T> Fail(string error) => new() { Success = false, Error = error };
}
