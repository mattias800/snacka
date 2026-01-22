using Snacka.Server.DTOs;

namespace Snacka.Server.Services;

/// <summary>
/// Service for direct message operations. This is a convenience wrapper around
/// ConversationService for 1:1 DM use cases.
/// </summary>
public interface IDirectMessageService
{
    /// <summary>
    /// Get all conversations for a user (both 1:1 and group).
    /// </summary>
    Task<IEnumerable<ConversationSummaryResponse>> GetConversationsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get or create a 1:1 conversation with another user.
    /// </summary>
    Task<ConversationResponse> GetOrCreateConversationAsync(
        Guid userId, Guid otherUserId, CancellationToken cancellationToken = default);
}
