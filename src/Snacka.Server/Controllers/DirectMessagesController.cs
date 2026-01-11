using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/direct-messages")]
[Authorize]
public class DirectMessagesController : ControllerBase
{
    private readonly SnackaDbContext _db;
    private readonly IDirectMessageService _directMessageService;
    private readonly IHubContext<SnackaHub> _hubContext;

    public DirectMessagesController(
        SnackaDbContext db,
        IDirectMessageService directMessageService,
        IHubContext<SnackaHub> hubContext)
    {
        _db = db;
        _directMessageService = directMessageService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Get all conversations for the current user.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConversationSummary>>> GetConversations(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var conversations = await _directMessageService.GetConversationsAsync(userId.Value, cancellationToken);
        return Ok(conversations);
    }

    /// <summary>
    /// Get messages in a conversation with a specific user.
    /// </summary>
    [HttpGet("conversations/{userId:guid}")]
    public async Task<ActionResult<IEnumerable<DirectMessageResponse>>> GetConversation(
        Guid userId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();

        var messages = await _directMessageService.GetConversationAsync(
            currentUserId.Value, userId, skip, take, cancellationToken);
        return Ok(messages);
    }

    /// <summary>
    /// Send a message to a specific user.
    /// </summary>
    [HttpPost("conversations/{userId:guid}")]
    public async Task<ActionResult<DirectMessageResponse>> SendMessage(
        Guid userId,
        [FromBody] SendDirectMessageRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();

        try
        {
            var message = await _directMessageService.SendMessageAsync(
                currentUserId.Value, userId, request.Content, cancellationToken);

            // Notify recipient via SignalR
            await _hubContext.Clients.User(userId.ToString())
                .SendAsync("ReceiveDirectMessage", message, cancellationToken);

            return Ok(message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Mark a conversation as read.
    /// </summary>
    [HttpPost("conversations/{userId:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid userId, CancellationToken cancellationToken)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();

        await _directMessageService.MarkAsReadAsync(currentUserId.Value, userId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Update a direct message.
    /// </summary>
    [HttpPut("messages/{messageId:guid}")]
    public async Task<ActionResult<DirectMessageResponse>> UpdateMessage(
        Guid messageId,
        [FromBody] DirectMessageUpdate request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var message = await _directMessageService.UpdateMessageAsync(
                messageId, userId.Value, request.Content, cancellationToken);

            // Notify both parties about the edit
            await _hubContext.Clients.User(message.RecipientId.ToString())
                .SendAsync("DirectMessageEdited", message, cancellationToken);

            return Ok(message);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    /// <summary>
    /// Delete a direct message.
    /// </summary>
    [HttpDelete("messages/{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(Guid messageId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            // SECURITY: Get message info BEFORE deletion to notify only the participants
            var message = await _db.DirectMessages
                .AsNoTracking()
                .Where(m => m.Id == messageId)
                .Select(m => new { m.SenderId, m.RecipientId })
                .FirstOrDefaultAsync(cancellationToken);

            if (message is null)
                return NotFound(new { error = "Message not found." });

            await _directMessageService.DeleteMessageAsync(messageId, userId.Value, cancellationToken);

            // Notify only the two participants (not all users)
            await _hubContext.Clients
                .Users(message.SenderId.ToString(), message.RecipientId.ToString())
                .SendAsync("DirectMessageDeleted", messageId, cancellationToken);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
