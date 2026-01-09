using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Miscord.Server.DTOs;
using Miscord.Server.Hubs;
using Miscord.Server.Services;

namespace Miscord.Server.Controllers;

[ApiController]
[Route("api/messages")]
[Authorize]
public class ThreadsController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly IHubContext<MiscordHub> _hubContext;

    public ThreadsController(IMessageService messageService, IHubContext<MiscordHub> hubContext)
    {
        _messageService = messageService;
        _hubContext = hubContext;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Get a thread with its parent message and paginated replies
    /// </summary>
    [HttpGet("{parentMessageId:guid}/thread")]
    public async Task<ActionResult<ThreadResponse>> GetThread(
        Guid parentMessageId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var thread = await _messageService.GetThreadAsync(parentMessageId, GetUserId(), page, pageSize, cancellationToken);
            return Ok(thread);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Create a reply in a thread
    /// </summary>
    [HttpPost("{parentMessageId:guid}/replies")]
    public async Task<ActionResult<MessageResponse>> CreateThreadReply(
        Guid parentMessageId,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _messageService.CreateThreadReplyAsync(
                parentMessageId,
                GetUserId(),
                request.Content,
                request.ReplyToId,
                cancellationToken);

            // Get the updated parent message to get the correct ReplyCount
            var thread = await _messageService.GetThreadAsync(parentMessageId, GetUserId(), 1, 1, cancellationToken);

            // Broadcast the new reply to users viewing the channel
            await _hubContext.Clients.Group($"channel:{reply.ChannelId}")
                .SendAsync("ReceiveThreadReply", new
                {
                    ChannelId = reply.ChannelId,
                    ParentMessageId = parentMessageId,
                    Reply = reply
                }, cancellationToken);

            // Notify the channel about thread metadata update
            await _hubContext.Clients.Group($"channel:{reply.ChannelId}")
                .SendAsync("ThreadMetadataUpdated", new
                {
                    ChannelId = reply.ChannelId,
                    MessageId = parentMessageId,
                    ReplyCount = thread.ParentMessage.ReplyCount,
                    LastReplyAt = thread.ParentMessage.LastReplyAt
                }, cancellationToken);

            return Ok(reply);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
