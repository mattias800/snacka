using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Snacka.Client.Services;

/// <summary>
/// Information about an available update.
/// </summary>
public record UpdateInfo(
    string Version,
    string ReleaseUrl,
    string? ReleaseNotes,
    DateTime PublishedAt,
    Dictionary<string, string> DownloadUrls
);

/// <summary>
/// Service for checking GitHub releases for updates.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Gets the current application version.
    /// </summary>
    Version CurrentVersion { get; }

    /// <summary>
    /// Checks for updates and returns info if a newer version is available.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdateAsync();

    /// <summary>
    /// Opens the release page in the default browser.
    /// </summary>
    void OpenReleasePage(string url);
}

public class UpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private const string GitHubApiUrl = "https://api.github.com/repos/mattias800/snacka/releases/latest";
    private const string GitHubReleasesUrl = "https://github.com/mattias800/snacka/releases";

    public UpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Snacka-Client");
    }

    public Version CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version ?? new Version(0, 1, 0);
        }
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            Console.WriteLine($"UpdateService: Checking for updates (current version: {CurrentVersion})");

            var response = await _httpClient.GetAsync(GitHubApiUrl);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"UpdateService: GitHub API returned {response.StatusCode}");
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>();

            if (release == null)
            {
                Console.WriteLine("UpdateService: Failed to parse release info");
                return null;
            }

            // Parse version from tag (remove 'v' prefix if present)
            var tagVersion = release.TagName.TrimStart('v');
            if (!Version.TryParse(tagVersion, out var latestVersion))
            {
                Console.WriteLine($"UpdateService: Failed to parse version from tag: {release.TagName}");
                return null;
            }

            Console.WriteLine($"UpdateService: Latest version is {latestVersion}");

            // Compare versions (only major.minor.build, ignore revision)
            var currentComparable = new Version(CurrentVersion.Major, CurrentVersion.Minor, CurrentVersion.Build);
            var latestComparable = new Version(latestVersion.Major, latestVersion.Minor, latestVersion.Build);

            if (latestComparable <= currentComparable)
            {
                Console.WriteLine("UpdateService: Already on latest version");
                return null;
            }

            Console.WriteLine($"UpdateService: Update available! {CurrentVersion} -> {latestVersion}");

            // Build download URLs from assets
            var downloadUrls = new Dictionary<string, string>();
            foreach (var asset in release.Assets)
            {
                var name = asset.Name.ToLowerInvariant();
                if (name.Contains("macos") || name.Contains("osx"))
                    downloadUrls["macOS"] = asset.BrowserDownloadUrl;
                else if (name.Contains("windows") || name.Contains("win"))
                    downloadUrls["Windows"] = asset.BrowserDownloadUrl;
                else if (name.Contains("linux"))
                    downloadUrls["Linux"] = asset.BrowserDownloadUrl;
            }

            return new UpdateInfo(
                Version: tagVersion,
                ReleaseUrl: release.HtmlUrl,
                ReleaseNotes: release.Body,
                PublishedAt: release.PublishedAt,
                DownloadUrls: downloadUrls
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Error checking for updates: {ex.Message}");
            return null;
        }
    }

    public void OpenReleasePage(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Failed to open URL: {ex.Message}");
        }
    }

    // GitHub API response models
    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
