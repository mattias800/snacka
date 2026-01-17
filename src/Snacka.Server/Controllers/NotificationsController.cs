using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Snacka.Server.DTOs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>
    /// Get notifications for the current user with pagination.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<NotificationResponse>>> GetNotifications(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] bool includeRead = true,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var notifications = await _notificationService.GetNotificationsAsync(
            userId.Value, skip, take, includeRead, cancellationToken);
        return Ok(notifications);
    }

    /// <summary>
    /// Get unread notification count.
    /// </summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<NotificationCountResponse>> GetUnreadCount(
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var count = await _notificationService.GetUnreadCountAsync(userId.Value, cancellationToken);
        return Ok(new NotificationCountResponse(count));
    }

    /// <summary>
    /// Mark a single notification as read.
    /// </summary>
    [HttpPost("{notificationId:guid}/read")]
    public async Task<IActionResult> MarkAsRead(
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        await _notificationService.MarkAsReadAsync(notificationId, userId.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Mark all notifications as read.
    /// </summary>
    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        await _notificationService.MarkAllAsReadAsync(userId.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Dismiss a single notification (logical delete).
    /// </summary>
    [HttpPost("{notificationId:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        await _notificationService.DismissAsync(notificationId, userId.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Dismiss all notifications (logical delete).
    /// </summary>
    [HttpPost("dismiss-all")]
    public async Task<IActionResult> DismissAll(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        await _notificationService.DismissAllAsync(userId.Value, cancellationToken);
        return NoContent();
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
