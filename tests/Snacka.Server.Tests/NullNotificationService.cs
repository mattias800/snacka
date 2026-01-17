using Snacka.Server.DTOs;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Tests;

/// <summary>
/// A no-op notification service for use in tests.
/// </summary>
public class NullNotificationService : INotificationService
{
    public Task<IEnumerable<NotificationResponse>> GetNotificationsAsync(Guid userId, int skip = 0, int take = 50, bool includeRead = true, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<NotificationResponse>>([]);

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task MarkAsReadAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DismissAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DismissAllAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<NotificationResponse> CreateNotificationAsync(Guid recipientId, string type, string title, string description, object? payload = null, Guid? actorId = null, Guid? communityId = null, Guid? channelId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new NotificationResponse(
            Guid.NewGuid(),
            type,
            title,
            description,
            "{}",
            false,
            false,
            actorId,
            null,
            null,
            communityId,
            null,
            channelId,
            null,
            DateTime.UtcNow,
            null
        ));

    public Task CreateNotificationsForCommunityAsync(Guid communityId, string type, string title, string description, object? payload = null, Guid? actorId = null, Guid? channelId = null, Func<UserRole, bool>? roleFilter = null, Guid? excludeUserId = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CreateNotificationsForAdminsAsync(string type, string title, string description, object? payload = null, Guid? actorId = null, Guid? excludeUserId = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
