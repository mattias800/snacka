using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Miscord.Shared.Models;

namespace Miscord.Client.Services;

public class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private string? _authToken;
    private string _baseUrl = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string? BaseUrl => string.IsNullOrEmpty(_baseUrl) ? null : _baseUrl;

    public void SetBaseUrl(string url)
    {
        // Normalize the URL - remove trailing slashes
        _baseUrl = url.TrimEnd('/');
    }

    private string BuildUrl(string path) => $"{_baseUrl}{path}";

    public async Task<ApiResult<ServerInfoResponse>> GetServerInfoAsync()
    {
        return await GetAsync<ServerInfoResponse>("/api/health");
    }

    public bool IsAuthenticated => !string.IsNullOrEmpty(_authToken);

    public void SetAuthToken(string token)
    {
        _authToken = token;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void ClearAuthToken()
    {
        _authToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    public async Task<ApiResult<AuthResponse>> RegisterAsync(string username, string email, string password, string inviteCode)
    {
        try
        {
            var request = new RegisterRequest(username, email, password, inviteCode);
            var response = await _httpClient.PostAsJsonAsync(BuildUrl("/api/auth/register"), request);

            if (response.IsSuccessStatusCode)
            {
                var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
                if (auth is not null)
                {
                    SetAuthToken(auth.AccessToken);
                    return ApiResult<AuthResponse>.Ok(auth);
                }
                return ApiResult<AuthResponse>.Fail("Invalid response from server");
            }

            var error = await TryReadError(response);
            return ApiResult<AuthResponse>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<AuthResponse>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<AuthResponse>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    public async Task<ApiResult<AuthResponse>> LoginAsync(string email, string password)
    {
        try
        {
            var request = new LoginRequest(email, password);
            var url = BuildUrl("/api/auth/login");
            Console.WriteLine($"POST {url} with email={email}");
            var response = await _httpClient.PostAsJsonAsync(url, request);
            Console.WriteLine($"Response status: {response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
                if (auth is not null)
                {
                    SetAuthToken(auth.AccessToken);
                    return ApiResult<AuthResponse>.Ok(auth);
                }
                return ApiResult<AuthResponse>.Fail("Invalid response from server");
            }

            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {content}");
            var error = TryParseError(content, response.StatusCode);
            return ApiResult<AuthResponse>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<AuthResponse>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<AuthResponse>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    public async Task<ApiResult<AuthResponse>> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            var request = new RefreshTokenRequest(refreshToken);
            var response = await _httpClient.PostAsJsonAsync(BuildUrl("/api/auth/refresh"), request);

            if (response.IsSuccessStatusCode)
            {
                var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
                if (auth is not null)
                {
                    SetAuthToken(auth.AccessToken);
                    return ApiResult<AuthResponse>.Ok(auth);
                }
                return ApiResult<AuthResponse>.Fail("Invalid response from server");
            }

            var error = await TryReadError(response);
            return ApiResult<AuthResponse>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<AuthResponse>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<AuthResponse>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    public async Task<ApiResult<UserProfileResponse>> GetProfileAsync()
    {
        return await GetAsync<UserProfileResponse>("/api/users/me");
    }

    public async Task<ApiResult<bool>> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        return await PutWithBodyNoResponseAsync("/api/users/me/password",
            new ChangePasswordRequest(currentPassword, newPassword));
    }

    public async Task<ApiResult<bool>> DeleteAccountAsync()
    {
        return await DeleteAsync("/api/users/me");
    }

    // Admin methods
    public async Task<ApiResult<List<ServerInviteResponse>>> GetInvitesAsync()
    {
        return await GetAsync<List<ServerInviteResponse>>("/api/admin/invites");
    }

    public async Task<ApiResult<ServerInviteResponse>> CreateInviteAsync(int maxUses = 0, DateTime? expiresAt = null)
    {
        return await PostAsync<CreateInviteRequest, ServerInviteResponse>(
            "/api/admin/invites",
            new CreateInviteRequest(maxUses, expiresAt));
    }

    public async Task<ApiResult<bool>> RevokeInviteAsync(Guid inviteId)
    {
        return await DeleteAsync($"/api/admin/invites/{inviteId}");
    }

    public async Task<ApiResult<List<AdminUserResponse>>> GetAllUsersAsync()
    {
        return await GetAsync<List<AdminUserResponse>>("/api/admin/users");
    }

    public async Task<ApiResult<AdminUserResponse>> SetUserAdminStatusAsync(Guid userId, bool isAdmin)
    {
        return await PutAsync<SetAdminStatusRequest, AdminUserResponse>(
            $"/api/admin/users/{userId}/admin",
            new SetAdminStatusRequest(isAdmin));
    }

    public async Task<ApiResult<bool>> DeleteUserAsync(Guid userId)
    {
        return await DeleteAsync($"/api/admin/users/{userId}");
    }

    // Community methods
    public async Task<ApiResult<List<CommunityResponse>>> GetCommunitiesAsync()
    {
        return await GetAsync<List<CommunityResponse>>("/api/communities");
    }

    public async Task<ApiResult<List<CommunityResponse>>> DiscoverCommunitiesAsync()
    {
        return await GetAsync<List<CommunityResponse>>("/api/communities/discover");
    }

    public async Task<ApiResult<CommunityResponse>> GetCommunityAsync(Guid communityId)
    {
        return await GetAsync<CommunityResponse>($"/api/communities/{communityId}");
    }

    public async Task<ApiResult<CommunityResponse>> CreateCommunityAsync(string name, string? description)
    {
        return await PostAsync<CreateCommunityRequest, CommunityResponse>(
            "/api/communities",
            new CreateCommunityRequest(name, description));
    }

    public async Task<ApiResult<bool>> JoinCommunityAsync(Guid communityId)
    {
        return await PostEmptyAsync($"/api/communities/{communityId}/join");
    }

    // Channel methods
    public async Task<ApiResult<List<ChannelResponse>>> GetChannelsAsync(Guid communityId)
    {
        return await GetAsync<List<ChannelResponse>>($"/api/communities/{communityId}/channels");
    }

    public async Task<ApiResult<ChannelResponse>> CreateChannelAsync(Guid communityId, string name, string? topic, ChannelType type = ChannelType.Text)
    {
        return await PostAsync<CreateChannelRequest, ChannelResponse>(
            $"/api/communities/{communityId}/channels",
            new CreateChannelRequest(name, topic, type));
    }

    public async Task<ApiResult<ChannelResponse>> UpdateChannelAsync(Guid communityId, Guid channelId, string? name, string? topic)
    {
        return await PutAsync<UpdateChannelRequest, ChannelResponse>(
            $"/api/communities/{communityId}/channels/{channelId}",
            new UpdateChannelRequest(name, topic));
    }

    public async Task<ApiResult<bool>> MarkChannelAsReadAsync(Guid communityId, Guid channelId)
    {
        return await PostEmptyAsync($"/api/communities/{communityId}/channels/{channelId}/read");
    }

    // Message methods
    public async Task<ApiResult<List<MessageResponse>>> GetMessagesAsync(Guid channelId, int skip = 0, int take = 50)
    {
        return await GetAsync<List<MessageResponse>>($"/api/channels/{channelId}/messages?skip={skip}&take={take}");
    }

    public async Task<ApiResult<MessageResponse>> SendMessageAsync(Guid channelId, string content, Guid? replyToId = null)
    {
        return await PostAsync<SendMessageRequest, MessageResponse>(
            $"/api/channels/{channelId}/messages",
            new SendMessageRequest(content, replyToId));
    }

    public async Task<ApiResult<MessageResponse>> UpdateMessageAsync(Guid channelId, Guid messageId, string content)
    {
        return await PutAsync<UpdateMessageRequest, MessageResponse>(
            $"/api/channels/{channelId}/messages/{messageId}",
            new UpdateMessageRequest(content));
    }

    public async Task<ApiResult<bool>> DeleteMessageAsync(Guid channelId, Guid messageId)
    {
        return await DeleteAsync($"/api/channels/{channelId}/messages/{messageId}");
    }

    // Reaction methods
    public async Task<ApiResult<ReactionUpdatedEvent>> AddReactionAsync(Guid channelId, Guid messageId, string emoji)
    {
        return await PostAsync<AddReactionRequest, ReactionUpdatedEvent>(
            $"/api/channels/{channelId}/messages/{messageId}/reactions",
            new AddReactionRequest(emoji));
    }

    public async Task<ApiResult<bool>> RemoveReactionAsync(Guid channelId, Guid messageId, string emoji)
    {
        // URL encode the emoji to handle special characters
        var encodedEmoji = Uri.EscapeDataString(emoji);
        return await DeleteAsync($"/api/channels/{channelId}/messages/{messageId}/reactions/{encodedEmoji}");
    }

    // Member methods
    public async Task<ApiResult<List<CommunityMemberResponse>>> GetMembersAsync(Guid communityId)
    {
        return await GetAsync<List<CommunityMemberResponse>>($"/api/communities/{communityId}/members");
    }

    public async Task<ApiResult<CommunityMemberResponse>> GetMemberAsync(Guid communityId, Guid userId)
    {
        return await GetAsync<CommunityMemberResponse>($"/api/communities/{communityId}/members/{userId}");
    }

    public async Task<ApiResult<CommunityMemberResponse>> UpdateMemberRoleAsync(Guid communityId, Guid userId, UserRole newRole)
    {
        return await PutAsync<UpdateMemberRoleRequest, CommunityMemberResponse>(
            $"/api/communities/{communityId}/members/{userId}/role",
            new UpdateMemberRoleRequest(newRole));
    }

    public async Task<ApiResult<bool>> TransferOwnershipAsync(Guid communityId, Guid newOwnerId)
    {
        return await PostWithBodyNoResponseAsync(
            $"/api/communities/{communityId}/transfer-ownership",
            new TransferOwnershipRequest(newOwnerId));
    }

    // Direct Message methods
    public async Task<ApiResult<List<ConversationSummary>>> GetConversationsAsync()
    {
        return await GetAsync<List<ConversationSummary>>("/api/directmessages");
    }

    public async Task<ApiResult<List<DirectMessageResponse>>> GetDirectMessagesAsync(Guid userId, int skip = 0, int take = 50)
    {
        return await GetAsync<List<DirectMessageResponse>>($"/api/directmessages/{userId}?skip={skip}&take={take}");
    }

    public async Task<ApiResult<DirectMessageResponse>> SendDirectMessageAsync(Guid userId, string content)
    {
        return await PostAsync<SendDirectMessageRequest, DirectMessageResponse>(
            $"/api/directmessages/{userId}",
            new SendDirectMessageRequest(content));
    }

    public async Task<ApiResult<DirectMessageResponse>> UpdateDirectMessageAsync(Guid messageId, string content)
    {
        return await PutAsync<SendDirectMessageRequest, DirectMessageResponse>(
            $"/api/directmessages/{messageId}",
            new SendDirectMessageRequest(content));
    }

    public async Task<ApiResult<bool>> DeleteDirectMessageAsync(Guid messageId)
    {
        return await DeleteAsync($"/api/directmessages/{messageId}");
    }

    public async Task<ApiResult<bool>> MarkConversationAsReadAsync(Guid userId)
    {
        return await PostEmptyAsync($"/api/directmessages/{userId}/read");
    }

    // Link Preview methods
    public async Task<ApiResult<LinkPreview>> GetLinkPreviewAsync(string url)
    {
        return await GetAsync<LinkPreview>($"/api/linkpreview?url={Uri.EscapeDataString(url)}");
    }

    // Generic HTTP helpers
    private async Task<ApiResult<T>> GetAsync<T>(string path)
    {
        try
        {
            var response = await _httpClient.GetAsync(BuildUrl(path));

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
                if (data is not null)
                    return ApiResult<T>.Ok(data);
                return ApiResult<T>.Fail("Invalid response from server");
            }

            var error = await TryReadError(response);
            return ApiResult<T>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<T>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<ApiResult<TResponse>> PostAsync<TRequest, TResponse>(string path, TRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(BuildUrl(path), request);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions);
                if (data is not null)
                    return ApiResult<TResponse>.Ok(data);
                return ApiResult<TResponse>.Fail("Invalid response from server");
            }

            var error = await TryReadError(response);
            return ApiResult<TResponse>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<TResponse>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<TResponse>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<ApiResult<TResponse>> PutAsync<TRequest, TResponse>(string path, TRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync(BuildUrl(path), request);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions);
                if (data is not null)
                    return ApiResult<TResponse>.Ok(data);
                return ApiResult<TResponse>.Fail("Invalid response from server");
            }

            var error = await TryReadError(response);
            return ApiResult<TResponse>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<TResponse>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<TResponse>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<ApiResult<bool>> DeleteAsync(string path)
    {
        try
        {
            var response = await _httpClient.DeleteAsync(BuildUrl(path));

            if (response.IsSuccessStatusCode)
            {
                return ApiResult<bool>.Ok(true);
            }

            var error = await TryReadError(response);
            return ApiResult<bool>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<ApiResult<bool>> PostEmptyAsync(string path)
    {
        try
        {
            var response = await _httpClient.PostAsync(BuildUrl(path), null);

            if (response.IsSuccessStatusCode)
            {
                return ApiResult<bool>.Ok(true);
            }

            var error = await TryReadError(response);
            return ApiResult<bool>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<ApiResult<bool>> PostWithBodyNoResponseAsync<TRequest>(string path, TRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(BuildUrl(path), request);

            if (response.IsSuccessStatusCode)
            {
                return ApiResult<bool>.Ok(true);
            }

            var error = await TryReadError(response);
            return ApiResult<bool>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private async Task<ApiResult<bool>> PutWithBodyNoResponseAsync<TRequest>(string path, TRequest request)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync(BuildUrl(path), request);

            if (response.IsSuccessStatusCode)
            {
                return ApiResult<bool>.Ok(true);
            }

            var error = await TryReadError(response);
            return ApiResult<bool>.Fail(error);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Fail($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return ApiResult<bool>.Fail($"Unexpected error: {ex.Message}");
        }
    }

    private static async Task<string> TryReadError(HttpResponseMessage response)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync();
            return TryParseError(content, response.StatusCode);
        }
        catch
        {
            return $"Request failed: {response.StatusCode}";
        }
    }

    private static string TryParseError(string content, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            if (!string.IsNullOrEmpty(content))
            {
                var error = JsonSerializer.Deserialize<ApiError>(content, JsonOptions);
                if (error?.Error is not null)
                    return error.Error;

                // Try parsing as validation errors
                var validationErrors = JsonSerializer.Deserialize<ValidationProblemDetails>(content, JsonOptions);
                if (validationErrors?.Errors is not null && validationErrors.Errors.Count > 0)
                {
                    var messages = validationErrors.Errors.SelectMany(e => e.Value).ToList();
                    if (messages.Count > 0)
                        return string.Join("; ", messages);
                }
            }
            return $"Request failed: {statusCode}";
        }
        catch
        {
            return $"Request failed: {statusCode}";
        }
    }
}

// For parsing ASP.NET Core validation errors
internal record ValidationProblemDetails(
    string? Title,
    int? Status,
    Dictionary<string, string[]>? Errors
);
