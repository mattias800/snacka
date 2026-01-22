using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly IHubContext<SnackaHub> _hubContext;

    public ConversationsController(
        IConversationService conversationService,
        IHubContext<SnackaHub> hubContext)
    {
        _conversationService = conversationService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Get all conversations for the current user.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ConversationResponse>>> GetConversations(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var conversations = await _conversationService.GetUserConversationsAsync(userId.Value, cancellationToken);
        return Ok(conversations);
    }

    /// <summary>
    /// Create a new conversation.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ConversationResponse>> CreateConversation(
        [FromBody] CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var conversation = await _conversationService.CreateConversationAsync(
                userId.Value, request.ParticipantIds, request.Name, cancellationToken);

            // Notify all participants via SignalR
            foreach (var participant in conversation.Participants.Where(p => p.UserId != userId.Value))
            {
                await _hubContext.Clients.User(participant.UserId.ToString())
                    .SendAsync("ConversationCreated", conversation, cancellationToken);
            }

            return Ok(conversation);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get a conversation by ID.
    /// </summary>
    [HttpGet("{conversationId:guid}")]
    public async Task<ActionResult<ConversationResponse>> GetConversation(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var conversation = await _conversationService.GetConversationAsync(
            conversationId, userId.Value, cancellationToken);

        if (conversation == null) return NotFound();

        return Ok(conversation);
    }

    /// <summary>
    /// Get messages in a conversation with pagination.
    /// </summary>
    [HttpGet("{conversationId:guid}/messages")]
    public async Task<ActionResult<List<ConversationMessageResponse>>> GetMessages(
        Guid conversationId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var messages = await _conversationService.GetMessagesAsync(
                conversationId, userId.Value, skip, take, cancellationToken);
            return Ok(messages);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Send a message to a conversation.
    /// </summary>
    [HttpPost("{conversationId:guid}/messages")]
    public async Task<ActionResult<ConversationMessageResponse>> SendMessage(
        Guid conversationId,
        [FromBody] SendConversationMessageRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var message = await _conversationService.SendMessageAsync(
                conversationId, userId.Value, request.Content, cancellationToken);

            // Notify all participants via SignalR group
            await _hubContext.Clients.Group($"conv:{conversationId}")
                .SendAsync("ConversationMessageReceived", message, cancellationToken);

            return Ok(message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Update a message in a conversation.
    /// </summary>
    [HttpPut("{conversationId:guid}/messages/{messageId:guid}")]
    public async Task<ActionResult<ConversationMessageResponse>> UpdateMessage(
        Guid conversationId,
        Guid messageId,
        [FromBody] SendConversationMessageRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var message = await _conversationService.UpdateMessageAsync(
                conversationId, messageId, userId.Value, request.Content, cancellationToken);

            // Notify all participants via SignalR group
            await _hubContext.Clients.Group($"conv:{conversationId}")
                .SendAsync("ConversationMessageUpdated", message, cancellationToken);

            return Ok(message);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Delete a message from a conversation.
    /// </summary>
    [HttpDelete("{conversationId:guid}/messages/{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _conversationService.DeleteMessageAsync(
                conversationId, messageId, userId.Value, cancellationToken);

            // Notify all participants via SignalR group
            await _hubContext.Clients.Group($"conv:{conversationId}")
                .SendAsync("ConversationMessageDeleted", new { ConversationId = conversationId, MessageId = messageId }, cancellationToken);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Update conversation properties (name, icon). Only for groups.
    /// </summary>
    [HttpPut("{conversationId:guid}")]
    public async Task<ActionResult<ConversationResponse>> UpdateConversation(
        Guid conversationId,
        [FromBody] UpdateConversationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var conversation = await _conversationService.UpdateConversationAsync(
                conversationId, userId.Value, request.Name, request.IconFileName, cancellationToken);

            // Notify all participants via SignalR group
            await _hubContext.Clients.Group($"conv:{conversationId}")
                .SendAsync("ConversationUpdated", conversation, cancellationToken);

            return Ok(conversation);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Add a participant to a group conversation.
    /// </summary>
    [HttpPost("{conversationId:guid}/participants")]
    public async Task<ActionResult<ParticipantInfo>> AddParticipant(
        Guid conversationId,
        [FromBody] AddParticipantRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var participant = await _conversationService.AddParticipantAsync(
                conversationId, request.UserId, userId.Value, cancellationToken);

            // Notify all participants via SignalR group
            await _hubContext.Clients.Group($"conv:{conversationId}")
                .SendAsync("ConversationParticipantAdded", new { ConversationId = conversationId, Participant = participant }, cancellationToken);

            // Also notify the new participant
            await _hubContext.Clients.User(request.UserId.ToString())
                .SendAsync("AddedToConversation", conversationId, cancellationToken);

            return Ok(participant);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Remove a participant from a group conversation.
    /// </summary>
    [HttpDelete("{conversationId:guid}/participants/{participantUserId:guid}")]
    public async Task<IActionResult> RemoveParticipant(
        Guid conversationId,
        Guid participantUserId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _conversationService.RemoveParticipantAsync(
                conversationId, participantUserId, userId.Value, cancellationToken);

            // Notify remaining participants via SignalR group
            await _hubContext.Clients.Group($"conv:{conversationId}")
                .SendAsync("ConversationParticipantRemoved", new { ConversationId = conversationId, UserId = participantUserId }, cancellationToken);

            // Notify the removed user
            await _hubContext.Clients.User(participantUserId.ToString())
                .SendAsync("RemovedFromConversation", conversationId, cancellationToken);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Mark a conversation as read.
    /// </summary>
    [HttpPost("{conversationId:guid}/read")]
    public async Task<IActionResult> MarkAsRead(
        Guid conversationId,
        [FromQuery] Guid? messageId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _conversationService.MarkAsReadAsync(conversationId, userId.Value, messageId, cancellationToken);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>
    /// Get or create a direct (1:1) conversation with another user.
    /// </summary>
    [HttpGet("direct/{otherUserId:guid}")]
    public async Task<ActionResult<ConversationResponse>> GetOrCreateDirectConversation(
        Guid otherUserId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var conversation = await _conversationService.GetOrCreateDirectConversationAsync(
                userId.Value, otherUserId, cancellationToken);
            return Ok(conversation);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
