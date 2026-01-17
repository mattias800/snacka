using Snacka.Server.DTOs;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

public interface INotificationService
{
    // Query operations
    Task<IEnumerable<NotificationResponse>> GetNotificationsAsync(
        Guid userId,
        int skip = 0,
        int take = 50,
        bool includeRead = true,
        CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    // State mutations
    Task MarkAsReadAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken = default);
    Task MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default);
    Task DismissAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken = default);
    Task DismissAllAsync(Guid userId, CancellationToken cancellationToken = default);

    // Single recipient notification creation
    Task<NotificationResponse> CreateNotificationAsync(
        Guid recipientId,
        string type,
        string title,
        string description,
        object? payload = null,
        Guid? actorId = null,
        Guid? communityId = null,
        Guid? channelId = null,
        CancellationToken cancellationToken = default);

    // Bulk creation for community-wide notifications
    Task CreateNotificationsForCommunityAsync(
        Guid communityId,
        string type,
        string title,
        string description,
        object? payload = null,
        Guid? actorId = null,
        Guid? channelId = null,
        Func<UserRole, bool>? roleFilter = null,
        Guid? excludeUserId = null,
        CancellationToken cancellationToken = default);

    // Bulk creation for server admin notifications
    Task CreateNotificationsForAdminsAsync(
        string type,
        string title,
        string description,
        object? payload = null,
        Guid? actorId = null,
        Guid? excludeUserId = null,
        CancellationToken cancellationToken = default);
}
