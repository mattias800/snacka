using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace Snacka.Client.Services;

/// <summary>
/// Information about an available update.
/// </summary>
public record UpdateInfo(
    string Version,
    string? ReleaseNotes,
    bool IsDownloaded,
    bool CanAutoUpdate // True if Velopack can handle the update, false if manual download required
);

/// <summary>
/// Update state for UI binding.
/// </summary>
public enum UpdateState
{
    NoUpdate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Error
}

/// <summary>
/// Service for checking and applying updates using Velopack.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Gets the current application version.
    /// </summary>
    Version CurrentVersion { get; }

    /// <summary>
    /// Gets whether the app was installed via Velopack (vs running from source/dev).
    /// Determines if automatic updates can be downloaded and applied.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Gets whether auto-updates are supported on this platform.
    /// When false, users must manually download updates from GitHub.
    /// </summary>
    bool SupportsAutoUpdate { get; }

    /// <summary>
    /// Checks for updates and returns info if a newer version is available.
    /// Works on all platforms - uses Velopack when available, otherwise checks GitHub API.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdateAsync();

    /// <summary>
    /// Downloads the update in the background. Only works when SupportsAutoUpdate is true.
    /// </summary>
    Task DownloadUpdateAsync(Action<int>? progressCallback = null);

    /// <summary>
    /// Applies the downloaded update and restarts the application.
    /// Only works when SupportsAutoUpdate is true.
    /// </summary>
    void ApplyUpdateAndRestart();

    /// <summary>
    /// Opens the releases page in the default browser.
    /// </summary>
    void OpenReleasesPage();
}

public class UpdateService : IUpdateService
{
    private readonly UpdateManager? _updateManager;
    private readonly HttpClient _httpClient;
    private UpdateInfo? _cachedUpdate;
    private const string GitHubRepoUrl = "https://github.com/mattias800/snacka";
    private const string GitHubReleasesUrl = "https://github.com/mattias800/snacka/releases";
    private const string GitHubApiReleasesUrl = "https://api.github.com/repos/mattias800/snacka/releases/latest";

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Snacka-Client");

        try
        {
            // GithubSource: repoUrl, accessToken (null for public), prerelease
            var source = new GithubSource(GitHubRepoUrl, null, false);
            _updateManager = new UpdateManager(source);
            Console.WriteLine($"UpdateService: Initialized with Velopack. IsInstalled: {IsInstalled}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Failed to initialize Velopack: {ex.Message}");
            _updateManager = null;
        }
    }

    public Version CurrentVersion
    {
        get
        {
            // Try to get version from Velopack first
            if (_updateManager?.IsInstalled == true)
            {
                try
                {
                    var veloVersion = _updateManager.CurrentVersion;
                    if (veloVersion != null)
                    {
                        return new Version(veloVersion.Major, veloVersion.Minor, veloVersion.Patch);
                    }
                }
                catch
                {
                    // Fall through to assembly version
                }
            }

            // Fallback to informational version (set by MinVer)
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (infoVersionAttr != null)
            {
                var infoVersion = infoVersionAttr.InformationalVersion;
                // Strip build metadata and prerelease info to get base version
                var plusIndex = infoVersion.IndexOf('+');
                if (plusIndex >= 0) infoVersion = infoVersion.Substring(0, plusIndex);
                var dashIndex = infoVersion.IndexOf('-');
                if (dashIndex >= 0) infoVersion = infoVersion.Substring(0, dashIndex);

                if (Version.TryParse(infoVersion, out var parsedVersion))
                {
                    return parsedVersion;
                }
            }

            return new Version(0, 1, 0);
        }
    }

    public bool IsInstalled => _updateManager?.IsInstalled ?? false;

    public bool SupportsAutoUpdate => IsInstalled;

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        // If Velopack is installed, use it for update checking (supports auto-update)
        if (IsInstalled && _updateManager != null)
        {
            return await CheckForUpdateViaVelopackAsync();
        }

        // Otherwise, check GitHub API directly (manual update only)
        return await CheckForUpdateViaGitHubAsync();
    }

    private async Task<UpdateInfo?> CheckForUpdateViaVelopackAsync()
    {
        try
        {
            Console.WriteLine($"UpdateService: Checking for updates via Velopack (current version: {CurrentVersion})");

            var updateInfo = await _updateManager!.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                Console.WriteLine("UpdateService: No updates available");
                _cachedUpdate = null;
                return null;
            }

            var newVersion = updateInfo.TargetFullRelease.Version;
            Console.WriteLine($"UpdateService: Update available via Velopack: {newVersion}");

            _cachedUpdate = new UpdateInfo(
                Version: newVersion.ToString(),
                ReleaseNotes: null, // Velopack doesn't provide release notes directly
                IsDownloaded: false,
                CanAutoUpdate: true
            );

            return _cachedUpdate;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Error checking for updates via Velopack: {ex.Message}");
            // Fall back to GitHub API check
            return await CheckForUpdateViaGitHubAsync();
        }
    }

    private async Task<UpdateInfo?> CheckForUpdateViaGitHubAsync()
    {
        try
        {
            Console.WriteLine($"UpdateService: Checking for updates via GitHub API (current version: {CurrentVersion})");

            var response = await _httpClient.GetAsync(GitHubApiReleasesUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"UpdateService: GitHub API returned {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get the tag name (e.g., "v0.3.5")
            if (!root.TryGetProperty("tag_name", out var tagNameElement))
            {
                Console.WriteLine("UpdateService: No tag_name in GitHub response");
                return null;
            }

            var tagName = tagNameElement.GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                return null;
            }

            // Strip 'v' prefix if present
            var versionString = tagName.StartsWith("v") ? tagName.Substring(1) : tagName;

            if (!Version.TryParse(versionString, out var latestVersion))
            {
                Console.WriteLine($"UpdateService: Could not parse version from tag: {tagName}");
                return null;
            }

            // Compare versions
            if (latestVersion <= CurrentVersion)
            {
                Console.WriteLine($"UpdateService: Current version {CurrentVersion} is up to date (latest: {latestVersion})");
                _cachedUpdate = null;
                return null;
            }

            Console.WriteLine($"UpdateService: Update available via GitHub: {latestVersion}");

            // Try to get release notes
            string? releaseNotes = null;
            if (root.TryGetProperty("body", out var bodyElement))
            {
                releaseNotes = bodyElement.GetString();
            }

            _cachedUpdate = new UpdateInfo(
                Version: versionString,
                ReleaseNotes: releaseNotes,
                IsDownloaded: false,
                CanAutoUpdate: false // Manual update required
            );

            return _cachedUpdate;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Error checking for updates via GitHub API: {ex.Message}");
            return null;
        }
    }

    public async Task DownloadUpdateAsync(Action<int>? progressCallback = null)
    {
        if (_updateManager == null || !IsInstalled)
        {
            Console.WriteLine("UpdateService: Cannot download - not installed or manager unavailable");
            return;
        }

        try
        {
            Console.WriteLine("UpdateService: Starting update download...");

            var updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                Console.WriteLine("UpdateService: No update to download");
                return;
            }

            await _updateManager.DownloadUpdatesAsync(
                updateInfo,
                progress => progressCallback?.Invoke(progress)
            );

            Console.WriteLine("UpdateService: Download complete");

            // Update cached info to mark as downloaded
            if (_cachedUpdate != null)
            {
                _cachedUpdate = _cachedUpdate with { IsDownloaded = true, CanAutoUpdate = true };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Error downloading update: {ex.Message}");
            throw;
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_updateManager == null || !IsInstalled)
        {
            Console.WriteLine("UpdateService: Cannot apply update - not installed or manager unavailable");
            return;
        }

        try
        {
            Console.WriteLine("UpdateService: Applying update and restarting...");
            _updateManager.ApplyUpdatesAndRestart(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Error applying update: {ex.Message}");
            throw;
        }
    }

    public void OpenReleasesPage()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = GitHubReleasesUrl,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UpdateService: Failed to open URL: {ex.Message}");
        }
    }
}
