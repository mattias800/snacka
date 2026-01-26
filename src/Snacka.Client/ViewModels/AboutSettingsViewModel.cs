using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using Snacka.Client.Services;

namespace Snacka.Client.ViewModels;

public class AboutSettingsViewModel : ViewModelBase
{
    private readonly IUpdateService _updateService;
    private string _copyStatus = "";
    private bool _isCheckingForUpdate;
    private UpdateInfo? _availableUpdate;
    private string _updateCheckStatus = "";

    public AboutSettingsViewModel() : this(new UpdateService())
    {
    }

    public AboutSettingsViewModel(IUpdateService updateService)
    {
        _updateService = updateService;

        var assembly = Assembly.GetExecutingAssembly();

        // Get version from AssemblyInformationalVersionAttribute (set by MinVer)
        var infoVersionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var infoVersion = infoVersionAttr?.InformationalVersion ?? "0.1.0";

        // The informational version may include build metadata (e.g., "0.3.4+abc123")
        // Strip the build metadata for display, keep prerelease info (e.g., "0.3.5-alpha.1")
        var plusIndex = infoVersion.IndexOf('+');
        var cleanVersion = plusIndex >= 0 ? infoVersion.Substring(0, plusIndex) : infoVersion;

        Version = cleanVersion;
        FullVersion = infoVersion;
        DotNetVersion = RuntimeInformation.FrameworkDescription;
        OperatingSystem = RuntimeInformation.OSDescription;
        Architecture = RuntimeInformation.OSArchitecture.ToString();
        RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier;

        // Log-related properties
        try
        {
            LogFilePath = LogService.Instance.LogFilePath;
            LogDirectory = LogService.Instance.LogDirectory;
        }
        catch
        {
            LogFilePath = "Logging not initialized";
            LogDirectory = "";
        }

        CopyLogsCommand = ReactiveCommand.CreateFromTask(CopyLogsToClipboardAsync);
        OpenLogsFolderCommand = ReactiveCommand.Create(OpenLogsFolder);
        CheckForUpdateCommand = ReactiveCommand.CreateFromTask(CheckForUpdateAsync);
        OpenReleasesPageCommand = ReactiveCommand.Create(() => _updateService.OpenReleasesPage());

        // Check for updates on load
        _ = CheckForUpdateAsync();
    }

    public string Version { get; }
    public string FullVersion { get; }
    public string DotNetVersion { get; }
    public string OperatingSystem { get; }
    public string Architecture { get; }
    public string RuntimeIdentifier { get; }

    // Log-related properties
    public string LogFilePath { get; }
    public string LogDirectory { get; }

    public string CopyStatus
    {
        get => _copyStatus;
        set => this.RaiseAndSetIfChanged(ref _copyStatus, value);
    }

    // Update-related properties
    public bool IsCheckingForUpdate
    {
        get => _isCheckingForUpdate;
        set => this.RaiseAndSetIfChanged(ref _isCheckingForUpdate, value);
    }

    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        set
        {
            this.RaiseAndSetIfChanged(ref _availableUpdate, value);
            this.RaisePropertyChanged(nameof(HasAvailableUpdate));
            this.RaisePropertyChanged(nameof(IsUpToDate));
        }
    }

    public bool HasAvailableUpdate => AvailableUpdate != null;
    public bool IsUpToDate => AvailableUpdate == null && !IsCheckingForUpdate && string.IsNullOrEmpty(UpdateCheckStatus);

    public string UpdateCheckStatus
    {
        get => _updateCheckStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _updateCheckStatus, value);
            this.RaisePropertyChanged(nameof(IsUpToDate));
        }
    }

    public ICommand CopyLogsCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand CheckForUpdateCommand { get; }
    public ICommand OpenReleasesPageCommand { get; }

    private async Task CheckForUpdateAsync()
    {
        IsCheckingForUpdate = true;
        UpdateCheckStatus = "";
        AvailableUpdate = null;

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            AvailableUpdate = update;

            if (update == null)
            {
                UpdateCheckStatus = "You're up to date!";
                // Clear status after 5 seconds
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateCheckStatus = "");
                });
            }
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = $"Error checking for updates: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    private async Task CopyLogsToClipboardAsync()
    {
        try
        {
            var logs = LogService.Instance.GetLogs();

            // Add system info header for bug reports
            var header = $"""
                === Snacka Bug Report ===
                Version: {FullVersion}
                .NET: {DotNetVersion}
                OS: {OperatingSystem}
                Architecture: {Architecture}
                Runtime: {RuntimeIdentifier}
                Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

                === Logs ===

                """;

            var fullReport = header + logs;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(fullReport);
                    CopyStatus = "Copied to clipboard!";

                    // Clear status after 3 seconds
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => CopyStatus = "");
                    });
                }
            }
        }
        catch (Exception ex)
        {
            CopyStatus = $"Error: {ex.Message}";
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            if (string.IsNullOrEmpty(LogDirectory) || !Directory.Exists(LogDirectory))
            {
                CopyStatus = "Log folder not found";
                return;
            }

            // Open folder in file manager
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", LogDirectory);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", LogDirectory);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", LogDirectory);
            }
        }
        catch (Exception ex)
        {
            CopyStatus = $"Error: {ex.Message}";
        }
    }
}
