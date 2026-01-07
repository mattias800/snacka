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

        var channels = await _channelService.GetChannelsAsync(communityId, cancellationToken);
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
    private readonly IHubContext<MiscordHub> _hubContext;

    public MessagesController(IMessageService messageService, IHubContext<MiscordHub> hubContext)
    {
        _messageService = messageService;
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
            var messages = await _messageService.GetMessagesAsync(channelId, skip, take, cancellationToken);
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
            var message = await _messageService.SendMessageAsync(channelId, userId.Value, request.Content, cancellationToken);
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

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
