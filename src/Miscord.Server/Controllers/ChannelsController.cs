using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Miscord.Server.DTOs;
using Miscord.Server.Hubs;
using Miscord.Server.Services;

namespace Miscord.Server.Controllers;

[ApiController]
[Route("api/communities/{communityId:guid}/[controller]")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly IChannelService _channelService;
    private readonly ICommunityMemberService _memberService;
    private readonly IHubContext<MiscordHub> _hubContext;

    public ChannelsController(
        IChannelService channelService,
        ICommunityMemberService memberService,
        IHubContext<MiscordHub> hubContext)
    {
        _channelService = channelService;
        _memberService = memberService;
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
            await _channelService.MarkChannelAsReadAsync(channelId, userId.Value, cancellationToken);
            return NoContent();
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

[ApiController]
[Route("api/channels/{channelId:guid}/messages")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;
    private readonly IReactionService _reactionService;
    private readonly IHubContext<MiscordHub> _hubContext;

    public MessagesController(IMessageService messageService, IReactionService reactionService, IHubContext<MiscordHub> hubContext)
    {
        _messageService = messageService;
        _reactionService = reactionService;
        _hubContext = hubContext;
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
            return Forbid(ex.Message);
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
