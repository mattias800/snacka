using System.Text.Json;
using Microsoft.Extensions.Options;
using Snacka.Server.DTOs;

namespace Snacka.Server.Services;

public class KlipyService : IGifService
{
    private readonly HttpClient _httpClient;
    private readonly KlipySettings _settings;
    private readonly ILogger<KlipyService> _logger;

    // Cache for search results
    private readonly Dictionary<string, (GifSearchResponse Response, DateTime FetchedAt)> _cache = new();
    private readonly object _cacheLock = new();
    private TimeSpan CacheDuration => TimeSpan.FromMinutes(_settings.CacheDurationMinutes);

    private const string KlipyApiBaseUrl = "https://api.klipy.com/api/v1";

    public KlipyService(HttpClient httpClient, IOptions<KlipySettings> settings, ILogger<KlipyService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<GifSearchResponse> SearchGifsAsync(string query, int limit = 20, string? pos = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogWarning("Klipy API key not configured");
            return new GifSearchResponse(new List<GifResult>(), null);
        }

        var cacheKey = $"search:{query}:{limit}:{pos ?? ""}";

        // Check cache
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
            {
                return cached.Response;
            }
        }

        try
        {
            var page = 1;
            if (!string.IsNullOrEmpty(pos) && int.TryParse(pos, out var parsedPage))
            {
                page = parsedPage;
            }

            var url = $"{KlipyApiBaseUrl}/{_settings.ApiKey}/gifs/search?q={Uri.EscapeDataString(query)}&per_page={limit}&page={page}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var klipyResponse = JsonSerializer.Deserialize<KlipyApiResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var result = MapToGifSearchResponse(klipyResponse);

            // Cache the result
            lock (_cacheLock)
            {
                _cache[cacheKey] = (result, DateTime.UtcNow);
                CleanCacheIfNeeded();
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Klipy GIFs for query: {Query}", query);
            return new GifSearchResponse(new List<GifResult>(), null);
        }
    }

    public async Task<GifSearchResponse> GetTrendingGifsAsync(int limit = 20, string? pos = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogWarning("Klipy API key not configured");
            return new GifSearchResponse(new List<GifResult>(), null);
        }

        var cacheKey = $"trending:{limit}:{pos ?? ""}";

        // Check cache
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
            {
                return cached.Response;
            }
        }

        try
        {
            var page = 1;
            if (!string.IsNullOrEmpty(pos) && int.TryParse(pos, out var parsedPage))
            {
                page = parsedPage;
            }

            var url = $"{KlipyApiBaseUrl}/{_settings.ApiKey}/gifs/trending?per_page={limit}&page={page}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var klipyResponse = JsonSerializer.Deserialize<KlipyApiResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var result = MapToGifSearchResponse(klipyResponse);

            // Cache the result
            lock (_cacheLock)
            {
                _cache[cacheKey] = (result, DateTime.UtcNow);
                CleanCacheIfNeeded();
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch trending Klipy GIFs");
            return new GifSearchResponse(new List<GifResult>(), null);
        }
    }

    private static GifSearchResponse MapToGifSearchResponse(KlipyApiResponse? klipyResponse)
    {
        if (klipyResponse?.Data?.Data == null)
        {
            return new GifSearchResponse(new List<GifResult>(), null);
        }

        var results = klipyResponse.Data.Data.Select(r =>
        {
            // Get preview and full GIF URLs from files object
            var previewUrl = r.Files?.Preview?.Url ?? r.Files?.Original?.Url ?? "";
            var fullUrl = r.Files?.Original?.Url ?? previewUrl;

            return new GifResult(
                Id: r.Slug ?? r.Id?.ToString() ?? "",
                Title: r.Title ?? "",
                PreviewUrl: previewUrl,
                Url: fullUrl,
                Width: r.Files?.Original?.Width ?? 0,
                Height: r.Files?.Original?.Height ?? 0
            );
        }).Where(r => !string.IsNullOrEmpty(r.Url)).ToList();

        // Use page number as position for pagination
        var nextPos = klipyResponse.Data.Has_Next == true
            ? (klipyResponse.Data.Current_Page + 1).ToString()
            : null;

        return new GifSearchResponse(results, nextPos);
    }

    private void CleanCacheIfNeeded()
    {
        if (_cache.Count > 500)
        {
            var oldEntries = _cache
                .Where(kv => DateTime.UtcNow - kv.Value.FetchedAt > CacheDuration)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldEntries)
            {
                _cache.Remove(key);
            }
        }
    }

    // Klipy API response models
    private record KlipyApiResponse(
        bool Result,
        KlipyDataContainer? Data
    );

    private record KlipyDataContainer(
        List<KlipyGif>? Data,
        int Current_Page,
        int Per_Page,
        bool? Has_Next
    );

    private record KlipyGif(
        int? Id,
        string? Slug,
        string? Title,
        KlipyFiles? Files
    );

    private record KlipyFiles(
        KlipyMedia? Original,
        KlipyMedia? Preview
    );

    private record KlipyMedia(
        string? Url,
        int Width,
        int Height
    );
}
