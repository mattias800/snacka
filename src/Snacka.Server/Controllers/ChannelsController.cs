using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacka.Server.DTOs;
using Snacka.Server.Hubs;
using Snacka.Server.Middleware;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/communities/{communityId:guid}/[controller]")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly IChannelService _channelService;
    private readonly ICommunityMemberService _memberService;
    private readonly IMessageService _messageService;
    private readonly IHubContext<SnackaHub> _hubContext;

    public ChannelsController(
        IChannelService channelService,
        ICommunityMemberService memberService,
        IMessageService messageService,
        IHubContext<SnackaHub> hubContext)
    {
        _channelService = channelService;
        _memberService = memberService;
        _messageService = messageService;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ChannelResponse>>> GetChannels(
        Guid communityId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        var channels = await _channelService.GetChannelsAsync(communityId, userId.Value, cancellationToken);
        return Ok(channels);
    }

    [HttpGet("{channelId:guid}")]
    public async Task<ActionResult<ChannelResponse>> GetChannel(
        Guid communityId,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        try
        {
            var channel = await _channelService.GetChannelAsync(channelId, cancellationToken);
            return Ok(channel);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<ChannelResponse>> CreateChannel(
        Guid communityId,
        [FromBody] CreateChannelRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var channel = await _channelService.CreateChannelAsync(communityId, userId.Value, request, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("ChannelCreated", channel, cancellationToken);
            return CreatedAtAction(nameof(GetChannel), new { communityId, channelId = channel.Id }, channel);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPut("{channelId:guid}")]
    public async Task<ActionResult<ChannelResponse>> UpdateChannel(
        Guid communityId,
        Guid channelId,
        [FromBody] UpdateChannelRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var channel = await _channelService.UpdateChannelAsync(channelId, userId.Value, request, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("ChannelUpdated", channel, cancellationToken);
            return Ok(channel);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpDelete("{channelId:guid}")]
    public async Task<IActionResult> DeleteChannel(
        Guid communityId,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _channelService.DeleteChannelAsync(channelId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("ChannelDeleted", new ChannelDeletedEvent(channelId), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPut("reorder")]
    public async Task<ActionResult<IEnumerable<ChannelResponse>>> ReorderChannels(
        Guid communityId,
        [FromBody] ReorderChannelsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var channels = await _channelService.ReorderChannelsAsync(communityId, userId.Value, request.ChannelIds, cancellationToken);
            var channelList = channels.ToList();
            await _hubContext.Clients.Group($"community:{communityId}")
                .SendAsync("ChannelsReordered", new ChannelsReorderedEvent(communityId, channelList), cancellationToken);
            return Ok(channelList);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPost("{channelId:guid}/read")]
    public async Task<IActionResult> MarkChannelAsRead(
        Guid communityId,
        Guid channelId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        try
        {
            await _channelService.MarkChannelAsReadAsync(communityId, channelId, userId.Value, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("search")]
    public async Task<ActionResult<MessageSearchResponse>> SearchMessages(
        Guid communityId,
        [FromQuery] string q,
        [FromQuery] Guid? channelId = null,
        [FromQuery] Guid? authorId = null,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        if (!await _memberService.IsMemberAsync(communityId, userId.Value, cancellationToken))
            return Forbid();

        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Search query is required" });

        var results = await _messageService.SearchMessagesAsync(
            communityId, userId.Value, q, channelId, authorId, take, cancellationToken);
        return Ok(results);
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

[ApiController]
[Route("api/channels/{channelId:guid}/messages")]
[Authorize]
[MessageInRouteChannel]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly IReactionService _reactionService;
    private readonly IHubContext<SnackaHub> _hubContext;
    private readonly IChannelActivityService _activity;

    public MessagesController(IMessageService messageService, IReactionService reactionService, IHubContext<SnackaHub> hubContext, IChannelActivityService activity)
    {
        _messageService = messageService;
        _reactionService = reactionService;
        _hubContext = hubContext;
        _activity = activity;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<MessageResponse>>> GetMessages(
        Guid channelId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var messages = await _messageService.GetMessagesAsync(channelId, userId.Value, skip, take, cancellationToken);
            return Ok(messages);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<MessageResponse>> SendMessage(
        Guid channelId,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var message = await _messageService.SendMessageAsync(channelId, userId.Value, request.Content, request.ReplyToId, cancellationToken);
            await _hubContext.Clients.Group($"channel:{channelId}")
                .SendAsync("ReceiveChannelMessage", message, cancellationToken);
            await _activity.MessagePostedAsync(message, cancellationToken);
            return Ok(message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{messageId:guid}")]
    public async Task<ActionResult<MessageResponse>> UpdateMessage(
        Guid channelId,
        Guid messageId,
        [FromBody] UpdateMessageRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var message = await _messageService.UpdateMessageAsync(messageId, userId.Value, request.Content, cancellationToken);
            await _hubContext.Clients.Group($"channel:{channelId}")
                .SendAsync("ChannelMessageEdited", message, cancellationToken);
            return Ok(message);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpDelete("{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(
        Guid channelId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            await _messageService.DeleteMessageAsync(messageId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"channel:{channelId}")
                .SendAsync("ChannelMessageDeleted", new MessageDeletedEvent(channelId, messageId), cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPost("{messageId:guid}/reactions")]
    public async Task<ActionResult<ReactionUpdatedEvent>> AddReaction(
        Guid channelId,
        Guid messageId,
        [FromBody] AddReactionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var reactionEvent = await _reactionService.AddReactionAsync(messageId, userId.Value, request.Emoji, cancellationToken);
            await _hubContext.Clients.Group($"channel:{channelId}")
                .SendAsync("ReactionUpdated", reactionEvent, cancellationToken);
            return Ok(reactionEvent);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("{messageId:guid}/reactions/{emoji}")]
    public async Task<IActionResult> RemoveReaction(
        Guid channelId,
        Guid messageId,
        string emoji,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var reactionEvent = await _reactionService.RemoveReactionAsync(messageId, userId.Value, emoji, cancellationToken);
            if (reactionEvent is not null)
            {
                await _hubContext.Clients.Group($"channel:{channelId}")
                    .SendAsync("ReactionUpdated", reactionEvent, cancellationToken);
            }
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("{messageId:guid}/pin")]
    public async Task<ActionResult<MessagePinnedEvent>> PinMessage(
        Guid channelId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var pinnedEvent = await _messageService.PinMessageAsync(messageId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"channel:{channelId}")
                .SendAsync("MessagePinned", pinnedEvent, cancellationToken);
            return Ok(pinnedEvent);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpDelete("{messageId:guid}/pin")]
    public async Task<ActionResult<MessagePinnedEvent>> UnpinMessage(
        Guid channelId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var unpinnedEvent = await _messageService.UnpinMessageAsync(messageId, userId.Value, cancellationToken);
            await _hubContext.Clients.Group($"channel:{channelId}")
                .SendAsync("MessagePinned", unpinnedEvent, cancellationToken);
            return Ok(unpinnedEvent);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpGet("pinned")]
    public async Task<ActionResult<IEnumerable<MessageResponse>>> GetPinnedMessages(
        Guid channelId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var messages = await _messageService.GetPinnedMessagesAsync(channelId, userId.Value, cancellationToken);
            return Ok(messages);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
