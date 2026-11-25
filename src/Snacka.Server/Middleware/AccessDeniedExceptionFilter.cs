using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Snacka.Server.Middleware;

/// <summary>
/// Maps <see cref="UnauthorizedAccessException"/> thrown by services to 403 Forbidden for every
/// controller, so an access check in a service can't turn into a 500 or be swallowed.
/// </summary>
public sealed class AccessDeniedExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not UnauthorizedAccessException ex) return;
        context.Result = new ObjectResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status403Forbidden };
        context.ExceptionHandled = true;
    }
}
