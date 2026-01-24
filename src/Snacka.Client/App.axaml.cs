using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Snacka.Client.Controls;
using Snacka.Client.Services;
using Snacka.Client.Stores;
using Snacka.Client.ViewModels;
using Snacka.Client.Views;

namespace Snacka.Client;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // HttpClient without a predefined base address - will be set dynamically
            // Timeout set to 20 seconds for better UX when server is slow/unreachable
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var apiClient = new ApiClient(httpClient);

            // Initialize MessageContentBlock with API client for link previews
            MessageContentBlock.SetApiClient(apiClient);

            var connectionStore = new ServerConnectionStore();
            var signalR = new SignalRService();
            var settingsStore = new SettingsStore(Program.Profile);
            var audioDeviceService = new AudioDeviceService(settingsStore);
            var videoDeviceService = new VideoDeviceService();
            var screenCaptureService = new ScreenCaptureService();
            var controllerService = new ControllerService();
            var controllerStreamingService = new ControllerStreamingService(signalR, controllerService, settingsStore);
            var controllerHostService = new ControllerHostService(signalR);
            var webRtc = new WebRtcService(signalR, settingsStore, apiClient);

            // Initialize Redux-style stores
            var stores = new StoreContainer(
                PresenceStore: new PresenceStore(),
                ChannelStore: new ChannelStore(),
                MessageStore: new MessageStore(),
                CommunityStore: new CommunityStore(),
                VoiceStore: new VoiceStore(),
                GamingStationStore: new GamingStationStore(),
                TypingStore: new TypingStore()
            );

            // SignalR event dispatcher (will be initialized after login with user ID)
            var signalREventDispatcher = new SignalREventDispatcher(signalR);

            // Check for dev mode auto-login
            DevLoginConfig? devConfig = null;
            if (!string.IsNullOrEmpty(Program.DevServerUrl) &&
                !string.IsNullOrEmpty(Program.DevEmail) &&
                !string.IsNullOrEmpty(Program.DevPassword))
            {
                devConfig = new DevLoginConfig(
                    Program.DevServerUrl,
                    Program.DevEmail,
                    Program.DevPassword
                );
            }

            var viewModel = new MainWindowViewModel(apiClient, connectionStore, signalR, webRtc, settingsStore, audioDeviceService, videoDeviceService, screenCaptureService, controllerService, controllerStreamingService, controllerHostService, stores, signalREventDispatcher, devConfig: devConfig);

            var window = new MainWindow
            {
                DataContext = viewModel
            };

            // Set custom window title if provided
            if (!string.IsNullOrEmpty(Program.DevWindowTitle))
            {
                window.Title = Program.DevWindowTitle;
            }

            // Restore window position and size from settings
            WindowPositionHelper.RestoreWindowPosition(window, settingsStore);

            // Save window position and size when app exits
            desktop.ShutdownRequested += (_, _) => WindowPositionHelper.SaveWindowPosition(window, settingsStore);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public record DevLoginConfig(string ServerUrl, string Email, string Password);

public static class WindowPositionHelper
{
    public static void RestoreWindowPosition(MainWindow window, ISettingsStore settingsStore)
    {
        var settings = settingsStore.Settings;

        // Restore size if saved
        if (settings.WindowWidth.HasValue && settings.WindowHeight.HasValue &&
            settings.WindowWidth.Value > 100 && settings.WindowHeight.Value > 100)
        {
            window.Width = settings.WindowWidth.Value;
            window.Height = settings.WindowHeight.Value;
        }

        // Restore position if saved
        if (settings.WindowX.HasValue && settings.WindowY.HasValue)
        {
            window.Position = new Avalonia.PixelPoint(settings.WindowX.Value, settings.WindowY.Value);
        }

        // Restore maximized state
        if (settings.WindowMaximized)
        {
            window.WindowState = Avalonia.Controls.WindowState.Maximized;
        }
    }

    public static void SaveWindowPosition(MainWindow window, ISettingsStore settingsStore)
    {
        var settings = settingsStore.Settings;

        // Save maximized state
        settings.WindowMaximized = window.WindowState == Avalonia.Controls.WindowState.Maximized;

        // Only save position/size if not maximized (so we restore to the right size when un-maximizing)
        if (window.WindowState != Avalonia.Controls.WindowState.Maximized)
        {
            settings.WindowX = window.Position.X;
            settings.WindowY = window.Position.Y;
            settings.WindowWidth = (int)window.Width;
            settings.WindowHeight = (int)window.Height;
        }

        settingsStore.Save();
    }
}
