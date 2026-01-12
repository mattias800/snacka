using System.Reactive;
using Avalonia.Platform.Storage;
using Snacka.Client.Services;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly IServerConnectionStore _connectionStore;
    private readonly ISignalRService _signalR;
    private readonly IWebRtcService _webRtc;
    private readonly Services.ISettingsStore _settingsStore;
    private readonly Services.IAudioDeviceService _audioDeviceService;
    private readonly Services.IVideoDeviceService _videoDeviceService;
    private readonly Services.IScreenCaptureService _screenCaptureService;
    private readonly Services.IControllerService _controllerService;
    private readonly Services.IUpdateService _updateService;
    private ViewModelBase _currentView;
    private AuthResponse? _currentUser;
    private ServerConnection? _currentServer;
    private ServerInfoResponse? _currentServerInfo;
    private string? _extractedInviteCode; // From invite link (e.g., https://server.com#invite=CODE)
    private UpdateInfo? _updateInfo;
    private bool _showUpdateBanner;
    private UpdateState _updateState = UpdateState.NoUpdate;
    private int _updateDownloadProgress;

    // File picker provider (set from View)
    public Func<Task<IStorageFile?>>? ImageFilePickerProvider { get; set; }

    public MainWindowViewModel(IApiClient apiClient, IServerConnectionStore connectionStore, ISignalRService signalR, IWebRtcService webRtc, Services.ISettingsStore settingsStore, Services.IAudioDeviceService audioDeviceService, Services.IVideoDeviceService videoDeviceService, Services.IScreenCaptureService screenCaptureService, Services.IControllerService controllerService, Services.IUpdateService? updateService = null, DevLoginConfig? devConfig = null)
    {
        _apiClient = apiClient;
        _connectionStore = connectionStore;
        _signalR = signalR;
        _webRtc = webRtc;
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _videoDeviceService = videoDeviceService;
        _screenCaptureService = screenCaptureService;
        _controllerService = controllerService;
        _updateService = updateService ?? new Services.UpdateService();

        // Initialize update commands
        OpenUpdatePage = ReactiveCommand.Create(() =>
        {
            _updateService.OpenReleasesPage();
        });
        DismissUpdateBanner = ReactiveCommand.Create(() =>
        {
            ShowUpdateBanner = false;
        });
        DownloadUpdate = ReactiveCommand.CreateFromTask(DownloadUpdateAsync);
        RestartToUpdate = ReactiveCommand.Create(() =>
        {
            _updateService.ApplyUpdateAndRestart();
        });

        // Check for updates on startup
        _ = CheckForUpdatesAsync();

        // Dev mode: auto-login with provided credentials
        if (devConfig is not null)
        {
            _currentView = new LoadingViewModel($"Dev mode: connecting to {devConfig.ServerUrl}...");
            _ = DevModeAutoLoginAsync(devConfig);
            return;
        }

        // Check for last connected server with saved credentials
        var lastServer = _connectionStore.GetLastConnected();
        if (lastServer is not null && !string.IsNullOrEmpty(lastServer.AccessToken))
        {
            // Start with a loading view, then try auto-login
            _currentView = new LoadingViewModel("Connecting to server...");
            _ = TryAutoLoginOnStartupAsync(lastServer);
        }
        else
        {
            _currentView = CreateServerConnectionViewModel();
        }
    }

    private async Task DevModeAutoLoginAsync(DevLoginConfig config)
    {
        _apiClient.SetBaseUrl(config.ServerUrl);

        AuthResponse? auth = null;

        // Try to login directly
        var loginResult = await _apiClient.LoginAsync(config.Email, config.Password);
        if (loginResult.Success && loginResult.Data is not null)
        {
            auth = loginResult.Data;
            Console.WriteLine($"Dev mode: logged in as {auth.Username}");
        }
        else
        {
            // Login failed - maybe user doesn't exist, try to register
            Console.WriteLine($"Dev mode: login failed ({loginResult.Error}), trying to register...");

            // Get bootstrap invite code
            var serverInfo = await _apiClient.GetServerInfoAsync();
            var inviteCode = serverInfo.Data?.BootstrapInviteCode ?? "";
            if (string.IsNullOrEmpty(inviteCode))
            {
                Console.WriteLine($"Dev mode: no bootstrap invite code available");
                CurrentView = CreateServerConnectionViewModel();
                return;
            }

            // Extract username from email (before @)
            var username = config.Email.Split('@')[0];
            var registerResult = await _apiClient.RegisterAsync(username, config.Email, config.Password, inviteCode);

            if (registerResult.Success && registerResult.Data is not null)
            {
                auth = registerResult.Data;
                Console.WriteLine($"Dev mode: registered and logged in as {auth.Username}");
            }
            else
            {
                Console.WriteLine($"Dev mode: registration failed - {registerResult.Error}");
                CurrentView = CreateServerConnectionViewModel();
                return;
            }
        }

        if (auth is null) return;

        CurrentServer = new ServerConnection
        {
            Id = $"dev-{config.Email}",
            Name = "Dev Server",
            Url = config.ServerUrl,
            AccessToken = auth.AccessToken,
            RefreshToken = auth.RefreshToken,
            LastConnected = DateTime.UtcNow
        };

        // Check if user has any communities, if not create or join one
        var communitiesResult = await _apiClient.GetCommunitiesAsync();
        if (communitiesResult.Success && communitiesResult.Data is not null && communitiesResult.Data.Count == 0)
        {
            Console.WriteLine("Dev mode: no communities found, creating/joining 'Dev Community'...");
            await EnsureDevCommunityExistsAsync();
        }

        OnAuthSuccess(auth);
    }

    private async Task EnsureDevCommunityExistsAsync()
    {
        // First, check if there are any discoverable communities we can join
        var discoverResult = await _apiClient.DiscoverCommunitiesAsync();
        if (discoverResult.Success && discoverResult.Data is not null && discoverResult.Data.Count > 0)
        {
            // Join the first available community (in dev mode, this will be another user's Dev Community)
            var communityToJoin = discoverResult.Data[0];
            var joinResult = await _apiClient.JoinCommunityAsync(communityToJoin.Id);
            if (joinResult.Success)
            {
                Console.WriteLine($"Dev mode: joined existing community '{communityToJoin.Name}' (ID: {communityToJoin.Id})");
                return;
            }
            Console.WriteLine($"Dev mode: failed to join community - {joinResult.Error}");
        }

        // No communities to join, create a new one
        var createResult = await _apiClient.CreateCommunityAsync("Dev Community", "A shared community for development testing");
        if (createResult.Success && createResult.Data is not null)
        {
            Console.WriteLine($"Dev mode: created community 'Dev Community' (ID: {createResult.Data.Id})");
        }
        else
        {
            Console.WriteLine($"Dev mode: could not create community - {createResult.Error}");
        }
    }

    private async Task TryAutoLoginOnStartupAsync(ServerConnection server)
    {
        _apiClient.SetBaseUrl(server.Url);
        _apiClient.SetAuthToken(server.AccessToken!);

        // Try to get server info first
        var serverInfoResult = await _apiClient.GetServerInfoAsync();
        if (!serverInfoResult.Success || serverInfoResult.Data is null)
        {
            // Server not reachable, go to connection screen
            CurrentView = CreateServerConnectionViewModel();
            return;
        }

        _currentServerInfo = serverInfoResult.Data;

        // Try to get profile to verify token is still valid
        var profileResult = await _apiClient.GetProfileAsync();
        if (profileResult.Success && profileResult.Data is not null)
        {
            // Token is valid, go directly to main app
            CurrentServer = server with { LastConnected = DateTime.UtcNow };
            _connectionStore.Save(CurrentServer);

            var profile = profileResult.Data;
            var auth = new AuthResponse(
                UserId: profile.Id,
                Username: profile.Username,
                Email: profile.Email,
                IsServerAdmin: profile.IsServerAdmin,
                AccessToken: server.AccessToken!,
                RefreshToken: server.RefreshToken ?? "",
                ExpiresAt: DateTime.UtcNow.AddHours(1)
            );

            OnAuthSuccess(auth);
        }
        else if (!string.IsNullOrEmpty(server.RefreshToken))
        {
            // Try to refresh token
            var refreshResult = await _apiClient.RefreshTokenAsync(server.RefreshToken);
            if (refreshResult.Success && refreshResult.Data is not null)
            {
                var updatedServer = server with
                {
                    AccessToken = refreshResult.Data.AccessToken,
                    RefreshToken = refreshResult.Data.RefreshToken,
                    LastConnected = DateTime.UtcNow
                };
                CurrentServer = updatedServer;
                _connectionStore.Save(updatedServer);

                OnAuthSuccess(refreshResult.Data);
            }
            else
            {
                // Token refresh failed, go to login
                CurrentServer = server;
                CurrentView = CreateLoginViewModel(serverInfoResult.Data.AllowRegistration);
            }
        }
        else
        {
            // No refresh token, go to login
            CurrentServer = server;
            CurrentView = CreateLoginViewModel(serverInfoResult.Data.AllowRegistration);
        }
    }

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => this.RaiseAndSetIfChanged(ref _currentView, value);
    }

    public AuthResponse? CurrentUser
    {
        get => _currentUser;
        set => this.RaiseAndSetIfChanged(ref _currentUser, value);
    }

    public ServerConnection? CurrentServer
    {
        get => _currentServer;
        set => this.RaiseAndSetIfChanged(ref _currentServer, value);
    }

    public bool IsLoggedIn => CurrentUser is not null;

    // Update-related properties
    public UpdateInfo? UpdateInfo
    {
        get => _updateInfo;
        set => this.RaiseAndSetIfChanged(ref _updateInfo, value);
    }

    public bool ShowUpdateBanner
    {
        get => _showUpdateBanner;
        set => this.RaiseAndSetIfChanged(ref _showUpdateBanner, value);
    }

    public string CurrentVersion => _updateService.CurrentVersion.ToString();

    public UpdateState UpdateState
    {
        get => _updateState;
        set => this.RaiseAndSetIfChanged(ref _updateState, value);
    }

    public int UpdateDownloadProgress
    {
        get => _updateDownloadProgress;
        set => this.RaiseAndSetIfChanged(ref _updateDownloadProgress, value);
    }

    public bool IsUpdateDownloading => UpdateState == UpdateState.Downloading;
    public bool IsUpdateReadyToInstall => UpdateState == UpdateState.ReadyToInstall;
    public bool IsUpdateAvailable => UpdateState == UpdateState.UpdateAvailable;

    public ReactiveCommand<Unit, Unit> OpenUpdatePage { get; }
    public ReactiveCommand<Unit, Unit> DismissUpdateBanner { get; }
    public ReactiveCommand<Unit, Unit> DownloadUpdate { get; }
    public ReactiveCommand<Unit, Unit> RestartToUpdate { get; }

    private async Task CheckForUpdatesAsync()
    {
        var update = await _updateService.CheckForUpdateAsync();
        if (update != null)
        {
            UpdateInfo = update;
            UpdateState = update.IsDownloaded ? UpdateState.ReadyToInstall : UpdateState.UpdateAvailable;
            ShowUpdateBanner = true;
        }
    }

    private async Task DownloadUpdateAsync()
    {
        try
        {
            UpdateState = UpdateState.Downloading;
            UpdateDownloadProgress = 0;

            await _updateService.DownloadUpdateAsync(progress =>
            {
                UpdateDownloadProgress = progress;
            });

            UpdateState = UpdateState.ReadyToInstall;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download update: {ex.Message}");
            UpdateState = UpdateState.Error;
        }
    }

    private ServerConnectionViewModel CreateServerConnectionViewModel()
    {
        return new ServerConnectionViewModel(
            _apiClient,
            _connectionStore,
            onServerConnected: OnServerConnected,
            onExistingServerSelected: OnExistingServerSelected
        );
    }

    private void OnServerConnected(ServerConnection connection, ServerInfoResponse serverInfo, string? extractedInviteCode)
    {
        CurrentServer = connection;
        _currentServerInfo = serverInfo;
        _extractedInviteCode = extractedInviteCode;

        // Save the connection
        _connectionStore.Save(connection);

        // Go to login/register flow
        CurrentView = CreateLoginViewModel(serverInfo.AllowRegistration);
    }

    private void OnExistingServerSelected(ServerConnection server)
    {
        // Try to use saved credentials if available
        if (!string.IsNullOrEmpty(server.AccessToken))
        {
            _apiClient.SetBaseUrl(server.Url);
            _apiClient.SetAuthToken(server.AccessToken);

            // Try to refresh/verify the token
            _ = TryAutoLoginAsync(server);
        }
        else
        {
            _apiClient.SetBaseUrl(server.Url);
            CurrentServer = server;
            CurrentView = CreateLoginViewModel(true); // Default to allowing registration
        }
    }

    private async Task TryAutoLoginAsync(ServerConnection server)
    {
        // First try to get server info
        var serverInfoResult = await _apiClient.GetServerInfoAsync();
        if (!serverInfoResult.Success || serverInfoResult.Data is null)
        {
            // Server not reachable, go to connection screen
            CurrentView = CreateServerConnectionViewModel();
            return;
        }

        // Try to get profile to verify token is still valid
        var profileResult = await _apiClient.GetProfileAsync();
        if (profileResult.Success && profileResult.Data is not null)
        {
            // Token is valid, go directly to main app
            CurrentServer = server with { LastConnected = DateTime.UtcNow };
            _connectionStore.Save(CurrentServer);

            var profile = profileResult.Data;
            var auth = new AuthResponse(
                UserId: profile.Id,
                Username: profile.Username,
                Email: profile.Email,
                IsServerAdmin: profile.IsServerAdmin,
                AccessToken: server.AccessToken!,
                RefreshToken: server.RefreshToken ?? "",
                ExpiresAt: DateTime.UtcNow.AddHours(1) // We don't know exact expiry
            );

            OnAuthSuccess(auth);
        }
        else if (!string.IsNullOrEmpty(server.RefreshToken))
        {
            // Try to refresh token
            var refreshResult = await _apiClient.RefreshTokenAsync(server.RefreshToken);
            if (refreshResult.Success && refreshResult.Data is not null)
            {
                // Update saved credentials
                var updatedServer = server with
                {
                    AccessToken = refreshResult.Data.AccessToken,
                    RefreshToken = refreshResult.Data.RefreshToken,
                    LastConnected = DateTime.UtcNow
                };
                CurrentServer = updatedServer;
                _connectionStore.Save(updatedServer);

                OnAuthSuccess(refreshResult.Data);
            }
            else
            {
                // Token refresh failed, go to login
                CurrentServer = server;
                _currentServerInfo = serverInfoResult.Data;
                CurrentView = CreateLoginViewModel(serverInfoResult.Data.AllowRegistration);
            }
        }
        else
        {
            // No refresh token, go to login
            CurrentServer = server;
            _currentServerInfo = serverInfoResult.Data;
            CurrentView = CreateLoginViewModel(serverInfoResult.Data.AllowRegistration);
        }
    }

    private LoginViewModel CreateLoginViewModel(bool allowRegistration = true)
    {
        return new LoginViewModel(
            _apiClient,
            onLoginSuccess: OnAuthSuccess,
            onSwitchToRegister: allowRegistration
                ? () => CurrentView = CreateRegisterViewModel()
                : null
        );
    }

    private RegisterViewModel CreateRegisterViewModel()
    {
        // Prefer extracted invite code from invite link over bootstrap code
        var inviteCode = _extractedInviteCode ?? _currentServerInfo?.BootstrapInviteCode;

        return new RegisterViewModel(
            _apiClient,
            onRegisterSuccess: OnAuthSuccess,
            onSwitchToLogin: () => CurrentView = CreateLoginViewModel(_currentServerInfo?.AllowRegistration ?? true),
            initialInviteCode: inviteCode
        );
    }

    private void OnAuthSuccess(AuthResponse auth)
    {
        CurrentUser = auth;
        this.RaisePropertyChanged(nameof(IsLoggedIn));

        // Set the current user ID on WebRTC service so it can skip creating decoders for own streams
        _webRtc.SetCurrentUserId(auth.UserId);

        // Save the credentials to the server connection
        if (CurrentServer is not null)
        {
            var updatedServer = CurrentServer with
            {
                AccessToken = auth.AccessToken,
                RefreshToken = auth.RefreshToken,
                LastConnected = DateTime.UtcNow
            };
            CurrentServer = updatedServer;
            _connectionStore.Save(updatedServer);
        }

        CurrentView = new MainAppViewModel(_apiClient, _signalR, _webRtc, _screenCaptureService, _settingsStore, _audioDeviceService, CurrentServer!.Url, auth, OnLogout, OnSwitchServer, OnOpenDirectMessages, OnOpenDirectMessagesWithUser, OnOpenSettings, gifsEnabled: _currentServerInfo?.GifsEnabled ?? false);
    }

    private void OnOpenDirectMessages()
    {
        OnOpenDirectMessagesWithUser(null, null);
    }

    private void OnOpenSettings()
    {
        if (CurrentUser is null) return;

        CurrentView = new SettingsViewModel(
            onClose: () => OnAuthSuccess(CurrentUser),
            settingsStore: _settingsStore,
            audioDeviceService: _audioDeviceService,
            videoDeviceService: _videoDeviceService,
            controllerService: _controllerService,
            apiClient: _apiClient,
            signalRService: _signalR,
            onAccountDeleted: OnLogout,
            isServerAdmin: CurrentUser.IsServerAdmin,
            selectImageFile: ImageFilePickerProvider,
            gifsEnabled: _currentServerInfo?.GifsEnabled ?? false
        );
    }

    private void OnOpenDirectMessagesWithUser(Guid? userId, string? username)
    {
        if (CurrentUser is null || CurrentServer is null) return;

        try
        {
            CurrentView = new DirectMessagesViewModel(
                _apiClient,
                _signalR,
                CurrentUser,
                onBack: () => OnAuthSuccess(CurrentUser),
                initialUserId: userId,
                initialUsername: username
            );
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error creating DirectMessagesViewModel: {ex}");
            Console.WriteLine($"Error creating DirectMessagesViewModel: {ex}");
            throw;
        }
    }

    private void OnLogout()
    {
        _apiClient.ClearAuthToken();

        // Clear credentials from saved connection but keep the connection
        if (CurrentServer is not null)
        {
            var clearedServer = CurrentServer with
            {
                AccessToken = null,
                RefreshToken = null
            };
            _connectionStore.Save(clearedServer);
        }

        CurrentUser = null;
        this.RaisePropertyChanged(nameof(IsLoggedIn));
        CurrentView = CreateLoginViewModel(_currentServerInfo?.AllowRegistration ?? true);
    }

    private void OnSwitchServer()
    {
        _apiClient.ClearAuthToken();
        CurrentUser = null;
        CurrentServer = null;
        _currentServerInfo = null;
        this.RaisePropertyChanged(nameof(IsLoggedIn));
        CurrentView = CreateServerConnectionViewModel();
    }
}
