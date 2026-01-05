using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Miscord.Client.Services;
using Miscord.Client.ViewModels;
using Miscord.Client.Views;

namespace Miscord.Client;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // HttpClient without a predefined base address - will be set dynamically
            var httpClient = new HttpClient();
            var apiClient = new ApiClient(httpClient);
            var connectionStore = new ServerConnectionStore();
            var signalR = new SignalRService();
            var settingsStore = new SettingsStore(Program.Profile);
            var audioDeviceService = new AudioDeviceService(settingsStore);
            var videoDeviceService = new VideoDeviceService(settingsStore);
            var webRtc = new WebRtcService(signalR, settingsStore);

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

            var viewModel = new MainWindowViewModel(apiClient, connectionStore, signalR, webRtc, settingsStore, audioDeviceService, videoDeviceService, devConfig);

            var window = new MainWindow
            {
                DataContext = viewModel
            };

            // Set custom window title if provided
            if (!string.IsNullOrEmpty(Program.DevWindowTitle))
            {
                window.Title = Program.DevWindowTitle;
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public record DevLoginConfig(string ServerUrl, string Email, string Password);
