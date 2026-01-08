using Miscord.Server.DTOs;
using Miscord.Shared.Models;

namespace Miscord.Server.Services;

public interface IMessageService
{
    Task<IEnumerable<MessageResponse>> GetMessagesAsync(Guid channelId, Guid currentUserId, int skip = 0, int take = 50, CancellationToken cancellationToken = default);
    Task<MessageResponse> SendMessageAsync(Guid channelId, Guid authorId, string content, Guid? replyToId = null, CancellationToken cancellationToken = default);
    Task<MessageResponse> UpdateMessageAsync(Guid messageId, Guid userId, string content, CancellationToken cancellationToken = default);
    Task DeleteMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default);
    Task<MessagePinnedEvent> PinMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default);
    Task<MessagePinnedEvent> UnpinMessageAsync(Guid messageId, Guid userId, CancellationToken cancellationToken = default);
    Task<IEnumerable<MessageResponse>> GetPinnedMessagesAsync(Guid channelId, Guid currentUserId, CancellationToken cancellationToken = default);
}
