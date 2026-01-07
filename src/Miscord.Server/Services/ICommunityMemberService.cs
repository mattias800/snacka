using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public interface ICommunityMemberService
{
    Task<IEnumerable<CommunityMemberResponse>> GetMembersAsync(Guid communityId, CancellationToken cancellationToken = default);
    Task<CommunityMemberResponse> GetMemberAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);
    Task JoinCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);
    Task LeaveCommunityAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> IsMemberAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);
    Task<CommunityMemberResponse> UpdateMemberRoleAsync(Guid communityId, Guid targetUserId, Guid requestingUserId, UserRole newRole, CancellationToken cancellationToken = default);
    Task TransferOwnershipAsync(Guid communityId, Guid newOwnerId, Guid currentOwnerId, CancellationToken cancellationToken = default);
}
