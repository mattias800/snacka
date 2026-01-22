using Snacka.Server.DTOs;

namespace Snacka.Server.Services;

public interface IConversationService
{
    /// <summary>
    /// Creates a new conversation with the specified participants.
    /// For 2 participants, creates a 1:1 conversation. For 3+, creates a group.
    /// </summary>
    Task<ConversationResponse> CreateConversationAsync(
        Guid creatorId,
        List<Guid> participantIds,
        string? name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a conversation by ID if the user is a participant.
    /// </summary>
    Task<ConversationResponse?> GetConversationAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or creates a 1:1 conversation between two users.
    /// </summary>
    Task<ConversationResponse> GetOrCreateDirectConversationAsync(
        Guid userId1,
        Guid userId2,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all conversations for a user, ordered by most recent message.
    /// </summary>
    Task<List<ConversationResponse>> GetUserConversationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets conversation IDs for a user (used for SignalR groups).
    /// </summary>
    Task<List<Guid>> GetUserConversationIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets messages for a conversation with pagination.
    /// </summary>
    Task<List<ConversationMessageResponse>> GetMessagesAsync(
        Guid conversationId,
        Guid userId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to a conversation.
    /// </summary>
    Task<ConversationMessageResponse> SendMessageAsync(
        Guid conversationId,
        Guid authorId,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a message in a conversation.
    /// </summary>
    Task<ConversationMessageResponse> UpdateMessageAsync(
        Guid conversationId,
        Guid messageId,
        Guid userId,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a message from a conversation.
    /// </summary>
    Task DeleteMessageAsync(
        Guid conversationId,
        Guid messageId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a participant to a group conversation.
    /// </summary>
    Task<ParticipantInfo> AddParticipantAsync(
        Guid conversationId,
        Guid userId,
        Guid addedById,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a participant from a group conversation.
    /// </summary>
    Task RemoveParticipantAsync(
        Guid conversationId,
        Guid userId,
        Guid removedById,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates conversation properties (name, icon). Only for groups.
    /// </summary>
    Task<ConversationResponse> UpdateConversationAsync(
        Guid conversationId,
        Guid userId,
        string? name,
        string? iconFileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a conversation as read for a user.
    /// </summary>
    Task MarkAsReadAsync(
        Guid conversationId,
        Guid userId,
        Guid? messageId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the unread count for a user in a conversation.
    /// </summary>
    Task<int> GetUnreadCountAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user is a participant in a conversation.
    /// </summary>
    Task<bool> IsParticipantAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
