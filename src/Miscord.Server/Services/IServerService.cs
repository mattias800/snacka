using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public interface ICommunityService
{
    // Community operations
    Task<IEnumerable<CommunityResponse>> GetUserCommunitiesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<IEnumerable<CommunityResponse>> GetDiscoverableCommunitiesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<CommunityResponse> GetCommunityAsync(Guid communityId, CancellationToken cancellationToken = default);
    Task<CommunityResponse> CreateCommunityAsync(Guid ownerId, CreateCommunityRequest request, CancellationToken cancellationToken = default);
    Task<CommunityResponse> UpdateCommunityAsync(Guid communityId, Guid userId, UpdateCommunityRequest request, CancellationToken cancellationToken = default);
    Task DeleteCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);
}
