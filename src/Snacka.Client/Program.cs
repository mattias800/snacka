using Avalonia;
using Avalonia.ReactiveUI;
using Snacka.Client.Services;
using Velopack;

namespace Snacka.Client;

public sealed class Program
{
    // Dev mode arguments for auto-login
    public static string? DevServerUrl { get; private set; }
    public static string? DevEmail { get; private set; }
    public static string? DevPassword { get; private set; }
    public static string? DevWindowTitle { get; private set; }
    public static string? Profile { get; private set; }

    /// <summary>
    /// Gets the system capability service with hardware acceleration status.
    /// Available after startup capability check completes.
    /// </summary>
    public static ISystemCapabilityService? CapabilityService { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must be first - handles install/update hooks
        VelopackApp.Build().Run();

        // Parse profile argument early so we can use it for logging
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--profile")
            {
                Profile = args[i + 1];
                break;
            }
        }
        if (string.IsNullOrEmpty(Profile))
        {
            Profile = Environment.GetEnvironmentVariable("SNACKA_PROFILE");
        }

        // Initialize logging early to capture all console output
        LogService.Initialize(Profile);

        // Set VLC environment variables FIRST, before anything else loads
        SetupVlcEnvironment();

        // Check system capabilities (hardware acceleration) at startup
        var capabilityService = new SystemCapabilityService();
        capabilityService.CheckCapabilities();
        CapabilityService = capabilityService;

        // Check for audio test mode
        if (args.Contains("--audio-test"))
        {
            AudioTest.Run();
            return;
        }

        // Parse dev mode arguments: --server URL --email EMAIL --password PASSWORD --title TITLE
        // Note: --profile is parsed earlier for logging initialization
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--server":
                    DevServerUrl = args[++i];
                    break;
                case "--email":
                    DevEmail = args[++i];
                    break;
                case "--password":
                    DevPassword = args[++i];
                    break;
                case "--title":
                    DevWindowTitle = args[++i];
                    break;
                case "--profile":
                    i++; // Skip, already parsed
                    break;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
        .UseReactiveUI();

    /// <summary>
    /// Sets up VLC environment variables for LibVLCSharp.
    /// This must be called BEFORE any LibVLC code is loaded.
    /// </summary>
    private static void SetupVlcEnvironment()
    {
        if (OperatingSystem.IsMacOS())
        {
            // VLC.app installed via Homebrew or DMG
            var vlcPluginPath = "/Applications/VLC.app/Contents/MacOS/plugins";
            var vlcLibPath = "/Applications/VLC.app/Contents/MacOS/lib";

            if (Directory.Exists(vlcPluginPath))
            {
                Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", vlcPluginPath);
            }
            if (Directory.Exists(vlcLibPath))
            {
                // Prepend to existing DYLD_LIBRARY_PATH if present
                var existing = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH");
                var newPath = string.IsNullOrEmpty(existing) ? vlcLibPath : $"{vlcLibPath}:{existing}";
                Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", newPath);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // VLC installed in Program Files
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var vlcPath = Path.Combine(programFiles, "VideoLAN", "VLC");
            var vlcPluginPath = Path.Combine(vlcPath, "plugins");

            if (Directory.Exists(vlcPluginPath))
            {
                Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", vlcPluginPath);
            }
        }
        // Linux typically has VLC plugins in standard locations that libvlc finds automatically
    }

}
