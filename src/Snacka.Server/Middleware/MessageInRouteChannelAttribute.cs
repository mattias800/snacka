using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;

namespace Snacka.Server.Middleware;

/// <summary>
/// For routes with both {channelId} and {messageId}: the message must be in that channel, or the
/// request is a 404. Otherwise realtime events (edits, reactions, pins) would be broadcast to the
/// channel named in the URL instead of the people actually looking at the message.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class MessageInRouteChannelAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var route = context.RouteData.Values;
        if (Guid.TryParse(route["channelId"]?.ToString(), out var channelId) &&
            Guid.TryParse(route["messageId"]?.ToString(), out var messageId))
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<SnackaDbContext>();
            if (!await db.Messages.AnyAsync(m => m.Id == messageId && m.ChannelId == channelId, context.HttpContext.RequestAborted))
            {
                context.Result = new NotFoundObjectResult(new { error = "Message not found in this channel." });
                return;
            }
        }
        await next();
    }
}
