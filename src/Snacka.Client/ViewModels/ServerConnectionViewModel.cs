using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Snacka.Client.Services;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

public class ServerConnectionViewModel : ViewModelBase
{
    private readonly IApiClient _apiClient;
    private readonly IServerConnectionStore _connectionStore;
    private readonly Action<ServerConnection, ServerInfoResponse, string?> _onServerConnected;
    private readonly Action<ServerConnection>? _onExistingServerSelected;

    private string _serverUrl = string.Empty;
    private string? _errorMessage;
    private string? _serverName;
    private string? _serverDescription;
    private bool _isLoading;
    private bool _isConnected;
    private string? _extractedInviteCode;

    public ServerConnectionViewModel(
        IApiClient apiClient,
        IServerConnectionStore connectionStore,
        Action<ServerConnection, ServerInfoResponse, string?> onServerConnected,
        Action<ServerConnection>? onExistingServerSelected = null)
    {
        _apiClient = apiClient;
        _connectionStore = connectionStore;
        _onServerConnected = onServerConnected;
        _onExistingServerSelected = onExistingServerSelected;

        SavedServers = new ObservableCollection<ServerConnection>(_connectionStore.GetAll());

        var canConnect = this.WhenAnyValue(
            x => x.ServerUrl,
            x => x.IsLoading,
            (url, loading) => !string.IsNullOrWhiteSpace(url) && !loading);

        var canContinue = this.WhenAnyValue(
            x => x.IsConnected,
            x => x.IsLoading,
            (connected, loading) => connected && !loading);

        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
        ConnectCommand.ThrownExceptions.Subscribe(ex => ErrorMessage = $"Connection failed: {ex.Message}");

        ContinueCommand = ReactiveCommand.Create(Continue, canContinue);
        SelectServerCommand = ReactiveCommand.Create<ServerConnection>(SelectServer);
        RemoveServerCommand = ReactiveCommand.Create<ServerConnection>(RemoveServer);
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            // Only reset connection state if the URL actually changed
            var urlChanged = _serverUrl != value;
            this.RaiseAndSetIfChanged(ref _serverUrl, value);
            if (urlChanged && IsConnected)
            {
                IsConnected = false;
                ServerName = null;
                ServerDescription = null;
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public string? ServerName
    {
        get => _serverName;
        set => this.RaiseAndSetIfChanged(ref _serverName, value);
    }

    public string? ServerDescription
    {
        get => _serverDescription;
        set => this.RaiseAndSetIfChanged(ref _serverDescription, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public ObservableCollection<ServerConnection> SavedServers { get; }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> ContinueCommand { get; }
    public ReactiveCommand<ServerConnection, Unit> SelectServerCommand { get; }
    public ReactiveCommand<ServerConnection, Unit> RemoveServerCommand { get; }

    private ServerInfoResponse? _serverInfo;

    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        IsLoading = true;
        _extractedInviteCode = null;

        try
        {
            // Parse URL - support plain URLs, share links, and invite links
            var url = ServerUrl.Trim();

            if (string.IsNullOrWhiteSpace(url))
            {
                ErrorMessage = "Please enter a server address";
                return;
            }

            if (url.StartsWith("snacka://"))
            {
                var (parsedUrl, name) = ServerConnection.ParseShareLink(url);
                if (parsedUrl is not null)
                    url = parsedUrl;
                else
                {
                    ErrorMessage = "Invalid share link format";
                    return;
                }
            }

            // Extract invite code from URL fragment (e.g., https://server.com#invite=ABC123)
            var fragmentIndex = url.IndexOf("#invite=", StringComparison.OrdinalIgnoreCase);
            if (fragmentIndex >= 0)
            {
                _extractedInviteCode = url[(fragmentIndex + 8)..]; // Skip "#invite="
                url = url[..fragmentIndex]; // Remove fragment from URL
            }

            // Ensure URL has protocol
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = $"http://{url}";
            }

            _apiClient.SetBaseUrl(url);
            var result = await _apiClient.GetServerInfoAsync();

            if (result.Success && result.Data is not null)
            {
                _serverInfo = result.Data;
                ServerName = result.Data.Name;
                ServerDescription = result.Data.Description;
                IsConnected = true;
                ServerUrl = url; // Update to normalized URL
            }
            else
            {
                ErrorMessage = result.Error ?? "Failed to connect to server";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Continue()
    {
        if (!IsConnected || _serverInfo is null)
            return;

        var connection = new ServerConnection
        {
            Id = GenerateServerId(ServerUrl),
            Url = ServerUrl,
            Name = _serverInfo.Name,
            Description = _serverInfo.Description,
            LastConnected = DateTime.UtcNow
        };

        _onServerConnected(connection, _serverInfo, _extractedInviteCode);
    }

    private void SelectServer(ServerConnection server)
    {
        if (_onExistingServerSelected is not null)
        {
            _onExistingServerSelected(server);
        }
        else
        {
            ServerUrl = server.Url;
        }
    }

    private void RemoveServer(ServerConnection server)
    {
        _connectionStore.Remove(server.Id);
        SavedServers.Remove(server);
    }

    private static string GenerateServerId(string url)
    {
        // Use a hash of the URL as a stable ID
        var uri = new Uri(url);
        return $"{uri.Host}:{uri.Port}".GetHashCode().ToString("x8");
    }
}
