using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Snacka.Server.Services;
using Snacka.Shared.Models;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/link-previews")]
[Authorize]
public class LinkPreviewController : ControllerBase
{
    private readonly ILinkPreviewService _linkPreviewService;

    public LinkPreviewController(ILinkPreviewService linkPreviewService)
    {
        _linkPreviewService = linkPreviewService;
    }

    /// <summary>
    /// Fetches OpenGraph metadata for a URL to display as a link preview.
    /// </summary>
    /// <param name="url">The URL to fetch preview for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>LinkPreview with metadata or 404 if not available.</returns>
    [HttpGet]
    public async Task<ActionResult<LinkPreview>> GetLinkPreview(
        [FromQuery] string url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("URL is required");

        var preview = await _linkPreviewService.GetLinkPreviewAsync(url, cancellationToken);

        if (preview == null)
            return NotFound();

        return Ok(preview);
    }
}
