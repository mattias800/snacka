using Avalonia;
using Avalonia.ReactiveUI;

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
}
