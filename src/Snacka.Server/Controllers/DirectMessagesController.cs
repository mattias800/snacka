using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Snacka.Server.DTOs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

/// <summary>
/// Controller for direct message operations.
/// Most operations are handled by ConversationsController - this provides
/// convenience endpoints for listing conversations and starting 1:1 DMs.
/// </summary>
[ApiController]
[Route("api/direct-messages")]
[Authorize]
public class DirectMessagesController : ControllerBase
{
    private readonly IDirectMessageService _directMessageService;

    public DirectMessagesController(IDirectMessageService directMessageService)
    {
        _directMessageService = directMessageService;
    }

    /// <summary>
    /// Get all conversations for the current user.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConversationSummaryResponse>>> GetConversations(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var conversations = await _directMessageService.GetConversationsAsync(userId.Value, cancellationToken);
        return Ok(conversations);
    }

    /// <summary>
    /// Get or create a 1:1 conversation with a specific user.
    /// Returns the conversation details - use ConversationsController for messages.
    /// </summary>
    [HttpGet("conversations/{otherUserId:guid}")]
    public async Task<ActionResult<ConversationResponse>> GetOrCreateConversation(
        Guid otherUserId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var conversation = await _directMessageService.GetOrCreateConversationAsync(
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
