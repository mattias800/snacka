using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

public sealed class NotificationService : INotificationService
{
    private readonly SnackaDbContext _db;
    private readonly IHubContext<SnackaHub> _hubContext;
    private readonly ILogger<NotificationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NotificationService(
        SnackaDbContext db,
        IHubContext<SnackaHub> hubContext,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<IEnumerable<NotificationResponse>> GetNotificationsAsync(
        Guid userId,
        int skip = 0,
        int take = 50,
        bool includeRead = true,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Notifications
            .Include(n => n.Actor)
            .Include(n => n.Community)
            .Include(n => n.Channel)
            .Where(n => n.RecipientId == userId && !n.IsDismissed);

        if (!includeRead)
            query = query.Where(n => !n.IsRead);

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return notifications.Select(ToResponse);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.Notifications
            .CountAsync(n => n.RecipientId == userId && !n.IsRead && !n.IsDismissed,
                cancellationToken);
    }

    public async Task MarkAsReadAsync(Guid notificationId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientId == userId,
                cancellationToken);

        if (notification is not null && !notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _db.Notifications
            .Where(n => n.RecipientId == userId && !n.IsRead && !n.IsDismissed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now),
                cancellationToken);
    }

    public async Task DismissAsync(Guid notificationId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientId == userId,
                cancellationToken);

        if (notification is not null && !notification.IsDismissed)
        {
            notification.IsDismissed = true;
            notification.DismissedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DismissAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await _db.Notifications
            .Where(n => n.RecipientId == userId && !n.IsDismissed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsDismissed, true)
                .SetProperty(n => n.DismissedAt, now),
                cancellationToken);
    }

    public async Task<NotificationResponse> CreateNotificationAsync(
        Guid recipientId,
        string type,
        string title,
        string description,
        object? payload = null,
        Guid? actorId = null,
        Guid? communityId = null,
        Guid? channelId = null,
        CancellationToken cancellationToken = default)
    {
        var notification = new Notification
        {
            RecipientId = recipientId,
            Type = type,
            Title = title,
            Description = description,
            PayloadJson = payload is not null
                ? JsonSerializer.Serialize(payload, JsonOptions)
                : "{}",
            ActorId = actorId,
            CommunityId = communityId,
            ChannelId = channelId
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(cancellationToken);

        // Load related entities for response
        await _db.Entry(notification).Reference(n => n.Actor).LoadAsync(cancellationToken);
        await _db.Entry(notification).Reference(n => n.Community).LoadAsync(cancellationToken);
        await _db.Entry(notification).Reference(n => n.Channel).LoadAsync(cancellationToken);

        var response = ToResponse(notification);

        // Send real-time notification via SignalR
        await _hubContext.Clients.User(recipientId.ToString())
            .SendAsync("NotificationReceived", response, cancellationToken);

        _logger.LogDebug("Created notification {Type} for user {UserId}", type, recipientId);

        return response;
    }

    public async Task CreateNotificationsForCommunityAsync(
        Guid communityId,
        string type,
        string title,
        string description,
        object? payload = null,
        Guid? actorId = null,
        Guid? channelId = null,
        Func<UserRole, bool>? roleFilter = null,
        Guid? excludeUserId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.UserCommunities
            .Where(uc => uc.CommunityId == communityId);

        if (excludeUserId.HasValue)
            query = query.Where(uc => uc.UserId != excludeUserId.Value);

        var members = await query.ToListAsync(cancellationToken);

        if (roleFilter is not null)
            members = members.Where(m => roleFilter(m.Role)).ToList();

        if (members.Count == 0)
            return;

        var payloadJson = payload is not null
            ? JsonSerializer.Serialize(payload, JsonOptions)
            : "{}";

        var notifications = members.Select(m => new Notification
        {
            RecipientId = m.UserId,
            Type = type,
            Title = title,
            Description = description,
            PayloadJson = payloadJson,
            ActorId = actorId,
            CommunityId = communityId,
            ChannelId = channelId
        }).ToList();

        _db.Notifications.AddRange(notifications);
        await _db.SaveChangesAsync(cancellationToken);

        // Send real-time notifications
        foreach (var notification in notifications)
        {
            var response = ToResponse(notification);
            await _hubContext.Clients.User(notification.RecipientId.ToString())
                .SendAsync("NotificationReceived", response, cancellationToken);
        }

        _logger.LogDebug("Created {Count} notifications of type {Type} for community {CommunityId}",
            notifications.Count, type, communityId);
    }

    public async Task CreateNotificationsForAdminsAsync(
        string type,
        string title,
        string description,
        object? payload = null,
        Guid? actorId = null,
        Guid? excludeUserId = null,
        CancellationToken cancellationToken = default)
    {
        var admins = await _db.Users
            .Where(u => u.IsServerAdmin)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (excludeUserId.HasValue)
            admins = admins.Where(id => id != excludeUserId.Value).ToList();

        if (admins.Count == 0)
            return;

        var payloadJson = payload is not null
            ? JsonSerializer.Serialize(payload, JsonOptions)
            : "{}";

        var notifications = admins.Select(adminId => new Notification
        {
            RecipientId = adminId,
            Type = type,
            Title = title,
            Description = description,
            PayloadJson = payloadJson,
            ActorId = actorId
        }).ToList();

        _db.Notifications.AddRange(notifications);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            var response = ToResponse(notification);
            await _hubContext.Clients.User(notification.RecipientId.ToString())
                .SendAsync("NotificationReceived", response, cancellationToken);
        }

        _logger.LogDebug("Created {Count} notifications of type {Type} for server admins",
            notifications.Count, type);
    }

    private static NotificationResponse ToResponse(Notification n) => new(
        n.Id,
        n.Type,
        n.Title,
        n.Description,
        n.PayloadJson,
        n.IsRead,
        n.IsDismissed,
        n.ActorId,
        n.Actor?.Username,
        n.Actor?.EffectiveDisplayName,
        n.CommunityId,
        n.Community?.Name,
        n.ChannelId,
        n.Channel?.Name,
        n.CreatedAt,
        n.ReadAt
    );
}
