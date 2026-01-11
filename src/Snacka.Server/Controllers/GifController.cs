using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Snacka.Server.DTOs;
using Snacka.Server.Services;

namespace Snacka.Server.Controllers;

[ApiController]
[Route("api/gifs")]
[Authorize]
public class GifController : ControllerBase
{
    private readonly IGifService _gifService;

    public GifController(IGifService gifService)
    {
        _gifService = gifService;
    }

    /// <summary>
    /// Search for GIFs matching a query
    /// </summary>
    /// <param name="q">Search query</param>
    /// <param name="limit">Maximum number of results (default 20)</param>
    /// <param name="pos">Pagination position from previous response</param>
    [HttpGet("search")]
    public async Task<ActionResult<GifSearchResponse>> SearchGifs(
        [FromQuery] string q,
        [FromQuery] int limit = 20,
        [FromQuery] string? pos = null)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Search query is required");

        limit = Math.Clamp(limit, 1, 50);

        var result = await _gifService.SearchGifsAsync(q, limit, pos);
        return Ok(result);
    }

    /// <summary>
    /// Get trending GIFs
    /// </summary>
    /// <param name="limit">Maximum number of results (default 20)</param>
    /// <param name="pos">Pagination position from previous response</param>
    [HttpGet("trending")]
    public async Task<ActionResult<GifSearchResponse>> GetTrendingGifs(
        [FromQuery] int limit = 20,
        [FromQuery] string? pos = null)
    {
        limit = Math.Clamp(limit, 1, 50);

        var result = await _gifService.GetTrendingGifsAsync(limit, pos);
        return Ok(result);
    }
}
