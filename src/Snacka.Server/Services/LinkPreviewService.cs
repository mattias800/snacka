using System.Net;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Text.RegularExpressions;
using Snacka.Shared.Models;

namespace Snacka.Server.Services;

public partial class LinkPreviewService : ILinkPreviewService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LinkPreviewService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    // YouTube URL patterns
    private static readonly Regex YouTubeRegex = new(
        @"(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/embed/)([a-zA-Z0-9_-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Spotify URL patterns - matches track, album, playlist, artist, episode, show
    private static readonly Regex SpotifyRegex = new(
        @"open\.spotify\.com/(track|album|playlist|artist|episode|show)/([a-zA-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="httpClient">Must use <see cref="SsrfGuard.CreateHandler"/> (see Program.cs): previews fetch user-supplied URLs.</param>
    public LinkPreviewService(HttpClient httpClient, IMemoryCache cache, ILogger<LinkPreviewService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;

        // Configure HttpClient with browser-like user agent to avoid being blocked
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");
    }

    public async Task<LinkPreview?> GetLinkPreviewAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Normalize URL
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // Only allow http/https
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return null;

        // The service is per request; the cache is shared. Failures are cached too.
        var key = $"link-preview:{url}";
        if (_cache.TryGetValue(key, out LinkPreview? cached))
            return cached;

        try
        {
            var preview = await FetchPreviewAsync(uri, cancellationToken);
            _cache.Set(key, preview, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration, Size = 1 });
            return preview;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch link preview for {Url}", url);
            return null;
        }
    }

    private async Task<LinkPreview?> FetchPreviewAsync(Uri uri, CancellationToken cancellationToken)
    {
        // Check for YouTube URLs and use oEmbed API
        var youtubeMatch = YouTubeRegex.Match(uri.ToString());
        if (youtubeMatch.Success)
        {
            return await FetchYouTubePreviewAsync(uri.ToString(), youtubeMatch.Groups[1].Value, cancellationToken);
        }

        // Check for Spotify URLs and use oEmbed API
        var spotifyMatch = SpotifyRegex.Match(uri.ToString());
        if (spotifyMatch.Success)
        {
            return await FetchSpotifyPreviewAsync(uri.ToString(), spotifyMatch.Groups[1].Value, spotifyMatch.Groups[2].Value, cancellationToken);
        }

        using var response = await SsrfGuard.SendAsync(_httpClient, uri, r => r.Headers.Accept.ParseAdd("text/html,application/xhtml+xml"), cancellationToken);

        if (!response.IsSuccessStatusCode)
            return null;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        // Handle direct image URLs
        if (contentType.StartsWith("image/"))
        {
            return new LinkPreview(
                Url: uri.ToString(),
                Title: null,
                Description: null,
                ImageUrl: uri.ToString(),
                SiteName: uri.Host,
                Type: "image",
                FaviconUrl: GetFaviconUrl(uri)
            );
        }

        // Only parse HTML content
        if (!contentType.Contains("html"))
            return null;

        // Read limited amount of content (some sites put meta tags at the end, so we need more)
        var content = await ReadLimitedContentAsync(response.Content, 700 * 1024, cancellationToken);

        if (string.IsNullOrEmpty(content))
            return null;

        return ParseOpenGraphTags(uri, content);
    }

    private static async Task<string> ReadLimitedContentAsync(HttpContent httpContent, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await httpContent.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var buffer = new char[maxBytes];
        var charsRead = await reader.ReadBlockAsync(buffer, 0, maxBytes);

        return new string(buffer, 0, charsRead);
    }

    private LinkPreview? ParseOpenGraphTags(Uri uri, string html)
    {
        var ogTitle = ExtractMetaContent(html, "og:title") ?? ExtractMetaContent(html, "twitter:title");
        var ogDescription = ExtractMetaContent(html, "og:description") ?? ExtractMetaContent(html, "twitter:description") ?? ExtractMetaContent(html, "description");
        var ogImage = ExtractMetaContent(html, "og:image") ?? ExtractMetaContent(html, "twitter:image");
        var ogSiteName = ExtractMetaContent(html, "og:site_name");
        var ogType = ExtractMetaContent(html, "og:type");

        // Fall back to <title> tag if no og:title
        ogTitle ??= ExtractTitleTag(html);

        // Skip if we have no meaningful content
        if (string.IsNullOrEmpty(ogTitle) && string.IsNullOrEmpty(ogDescription) && string.IsNullOrEmpty(ogImage))
            return null;

        // Make relative image URLs absolute
        if (!string.IsNullOrEmpty(ogImage) && !ogImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ogImage = new Uri(uri, ogImage).ToString();
        }

        return new LinkPreview(
            Url: uri.ToString(),
            Title: TruncateString(ogTitle, 200),
            Description: TruncateString(ogDescription, 500),
            ImageUrl: ogImage,
            SiteName: ogSiteName ?? uri.Host,
            Type: ogType,
            FaviconUrl: GetFaviconUrl(uri)
        );
    }

    private static string? ExtractMetaContent(string html, string property)
    {
        // Try property attribute (OpenGraph style)
        var propertyMatch = Regex.Match(html,
            $@"<meta[^>]+property=[""']{Regex.Escape(property)}[""'][^>]+content=[""']([^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (propertyMatch.Success)
            return WebUtility.HtmlDecode(propertyMatch.Groups[1].Value.Trim());

        // Try content before property
        propertyMatch = Regex.Match(html,
            $@"<meta[^>]+content=[""']([^""']*)[""'][^>]+property=[""']{Regex.Escape(property)}[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (propertyMatch.Success)
            return WebUtility.HtmlDecode(propertyMatch.Groups[1].Value.Trim());

        // Try name attribute (standard meta tags like description)
        var nameMatch = Regex.Match(html,
            $@"<meta[^>]+name=[""']{Regex.Escape(property)}[""'][^>]+content=[""']([^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (nameMatch.Success)
            return WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());

        // Try content before name
        nameMatch = Regex.Match(html,
            $@"<meta[^>]+content=[""']([^""']*)[""'][^>]+name=[""']{Regex.Escape(property)}[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (nameMatch.Success)
            return WebUtility.HtmlDecode(nameMatch.Groups[1].Value.Trim());

        return null;
    }

    private static string? ExtractTitleTag(string html)
    {
        var match = Regex.Match(html, @"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
    }

    private static string GetFaviconUrl(Uri uri)
    {
        return $"{uri.Scheme}://{uri.Host}/favicon.ico";
    }

    private static string? TruncateString(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Fetches YouTube video preview using the oEmbed API.
    /// </summary>
    private async Task<LinkPreview?> FetchYouTubePreviewAsync(string originalUrl, string videoId, CancellationToken cancellationToken)
    {
        try
        {
            var oEmbedUrl = $"https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v={videoId}&format=json";

            _logger.LogInformation("Fetching YouTube oEmbed for video {VideoId}", videoId);

            using var response = await SsrfGuard.SendAsync(_httpClient, new Uri(oEmbedUrl), null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("YouTube oEmbed returned {StatusCode} for video {VideoId}", response.StatusCode, videoId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var oEmbed = JsonSerializer.Deserialize<YouTubeOEmbed>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (oEmbed == null)
                return null;

            // YouTube thumbnail URL - use maxresdefault for best quality, fallback to hqdefault
            var thumbnailUrl = $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg";

            return new LinkPreview(
                Url: originalUrl,
                Title: oEmbed.Title,
                Description: $"by {oEmbed.Author_Name}",
                ImageUrl: thumbnailUrl,
                SiteName: "YouTube",
                Type: "video",
                FaviconUrl: "https://www.youtube.com/favicon.ico"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch YouTube preview for video {VideoId}", videoId);
            return null;
        }
    }

    private record YouTubeOEmbed(
        string? Title,
        string? Author_Name,
        string? Author_Url,
        string? Thumbnail_Url,
        int? Thumbnail_Width,
        int? Thumbnail_Height
    );

    /// <summary>
    /// Fetches Spotify content preview using the oEmbed API.
    /// </summary>
    private async Task<LinkPreview?> FetchSpotifyPreviewAsync(string originalUrl, string contentType, string contentId, CancellationToken cancellationToken)
    {
        try
        {
            var oEmbedUrl = $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(originalUrl)}";

            _logger.LogInformation("Fetching Spotify oEmbed for {ContentType} {ContentId}", contentType, contentId);

            using var response = await SsrfGuard.SendAsync(_httpClient, new Uri(oEmbedUrl), null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Spotify oEmbed returned {StatusCode} for {ContentType} {ContentId}", response.StatusCode, contentType, contentId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var oEmbed = JsonSerializer.Deserialize<SpotifyOEmbed>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (oEmbed == null)
                return null;

            // Determine display type based on Spotify content type
            var displayType = contentType.ToLower() switch
            {
                "track" => "music.song",
                "album" => "music.album",
                "playlist" => "music.playlist",
                "artist" => "music.artist",
                "episode" => "music.episode",
                "show" => "music.podcast",
                _ => "music"
            };

            return new LinkPreview(
                Url: originalUrl,
                Title: oEmbed.Title,
                Description: contentType.ToLower() == "track" ? "Track" : contentType,
                ImageUrl: oEmbed.Thumbnail_Url,
                SiteName: "Spotify",
                Type: displayType,
                FaviconUrl: "https://open.spotify.com/favicon.ico"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Spotify preview for {ContentType} {ContentId}", contentType, contentId);
            return null;
        }
    }

    private record SpotifyOEmbed(
        string? Title,
        string? Thumbnail_Url,
        int? Thumbnail_Width,
        int? Thumbnail_Height,
        string? Provider_Name,
        string? Provider_Url,
        string? Type,
        string? Html
    );
}
