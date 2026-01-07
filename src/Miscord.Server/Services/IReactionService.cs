using Miscord.Server.DTOs;

namespace Miscord.Server.Services;

public interface IReactionService
{
    /// <summary>
    /// Adds a reaction to a message. If the user already reacted with this emoji, returns the existing reaction info.
    /// </summary>
    Task<ReactionUpdatedEvent> AddReactionAsync(Guid messageId, Guid userId, string emoji, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a reaction from a message.
    /// </summary>
    Task<ReactionUpdatedEvent?> RemoveReactionAsync(Guid messageId, Guid userId, string emoji, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all reactions for a message, grouped by emoji with user info.
    /// </summary>
    Task<List<ReactionSummary>> GetReactionsAsync(Guid messageId, Guid currentUserId, CancellationToken cancellationToken = default);
}
