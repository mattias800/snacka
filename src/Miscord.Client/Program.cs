using Avalonia;
using Avalonia.ReactiveUI;
using Miscord.Client.Services.HardwareVideo;

namespace Miscord.Client;

public sealed class Program
{
    // Dev mode arguments for auto-login
    public static string? DevServerUrl { get; private set; }
    public static string? DevEmail { get; private set; }
    public static string? DevPassword { get; private set; }
    public static string? DevWindowTitle { get; private set; }
    public static string? Profile { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Set VLC environment variables FIRST, before anything else loads
        SetupVlcEnvironment();

        // Check hardware video decoding availability at startup
        CheckHardwareDecodingAvailability();

        // Check for audio test mode
        if (args.Contains("--audio-test"))
        {
            AudioTest.Run();
            return;
        }

        // Parse dev mode arguments: --server URL --email EMAIL --password PASSWORD --title TITLE --profile NAME
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
                    Profile = args[++i];
                    break;
            }
        }

        // Allow MISCORD_PROFILE env var as fallback
        if (string.IsNullOrEmpty(Profile))
        {
            Profile = Environment.GetEnvironmentVariable("MISCORD_PROFILE");
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

    /// <summary>
    /// Check and log hardware video decoding availability at startup.
    /// </summary>
    private static void CheckHardwareDecodingAvailability()
    {
        Console.WriteLine("Checking hardware video decoding availability...");
        try
        {
            var isAvailable = HardwareVideoDecoderFactory.IsAvailable();
            Console.WriteLine($"Hardware video decoding available: {isAvailable}");

            if (isAvailable)
            {
                // Try to create a decoder to verify it works
                var decoder = HardwareVideoDecoderFactory.Create();
                if (decoder != null)
                {
                    Console.WriteLine("Hardware video decoder created successfully");
                    decoder.Dispose();
                }
                else
                {
                    Console.WriteLine("Hardware video decoder creation returned null");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Hardware video decoding check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
