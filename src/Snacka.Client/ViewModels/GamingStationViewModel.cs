using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Models;
using Snacka.Client.Services;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for gaming station management and streaming.
/// Handles station listing, registration, connection, and input streaming.
/// Subscribes to SignalR events for real-time gaming station status updates.
/// </summary>
public class GamingStationViewModel : ReactiveObject
{
    private readonly IApiClient _apiClient;
    private readonly ISignalRService _signalR;
    private readonly ISettingsStore _settingsStore;
    private readonly Func<Guid?> _getCurrentVoiceChannelId;
    private readonly Guid _currentUserId;

    // Station list state (old architecture)
    private bool _isViewingGamingStations;
    private bool _isLoadingStations;
    private readonly ObservableCollection<GamingStationResponse> _myStations = new();
    private readonly ObservableCollection<GamingStationResponse> _sharedStations = new();

    // My gaming stations (new architecture)
    private readonly ObservableCollection<MyGamingStationInfo> _myGamingStations;
    private readonly string _currentMachineId;

    // Station stream state
    private bool _isViewingStationStream;
    private bool _isConnectingToStation;
    private Guid? _connectedStationId;
    private string _connectedStationName = "";
    private string _stationConnectionStatus = "Disconnected";
    private int _stationConnectedUserCount;
    private int _stationLatency;
    private string _stationResolution = "—";
    private int? _stationPlayerSlot;

    /// <summary>
    /// Event raised when the gaming stations view should be opened.
    /// Parent ViewModel should close other views.
    /// </summary>
    public event Action? ViewOpening;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    public GamingStationViewModel(
        IApiClient apiClient,
        ISignalRService signalR,
        ISettingsStore settingsStore,
        ObservableCollection<MyGamingStationInfo> myGamingStations,
        string currentMachineId,
        Func<Guid?> getCurrentVoiceChannelId,
        Guid currentUserId)
    {
        _apiClient = apiClient;
        _signalR = signalR;
        _settingsStore = settingsStore;
        _myGamingStations = myGamingStations;
        _currentMachineId = currentMachineId;
        _getCurrentVoiceChannelId = getCurrentVoiceChannelId;
        _currentUserId = currentUserId;

        // Commands
        OpenCommand = ReactiveCommand.CreateFromTask(OpenAsync);
        RegisterCommand = ReactiveCommand.CreateFromTask(RegisterStationAsync);
        ConnectCommand = ReactiveCommand.CreateFromTask<GamingStationResponse>(ConnectToStationAsync);
        ManageCommand = ReactiveCommand.Create<GamingStationResponse>(ManageStation);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectFromStationAsync);
        ToggleFullscreenCommand = ReactiveCommand.Create(ToggleStationFullscreen);
        DisableCommand = ReactiveCommand.CreateFromTask(DisableGamingStationAsync);

        // Subscribe to SignalR events for gaming station status updates
        SetupSignalRHandlers();
    }

    private void SetupSignalRHandlers()
    {
        _signalR.GamingStationStatusChanged += e => Dispatcher.UIThread.Post(() =>
        {
            OnGamingStationStatusChanged(e, _currentUserId);
        });
    }

    #region Properties

    public bool IsViewingGamingStations
    {
        get => _isViewingGamingStations;
        set => this.RaiseAndSetIfChanged(ref _isViewingGamingStations, value);
    }

    public bool IsLoadingStations
    {
        get => _isLoadingStations;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingStations, value);
    }

    public ObservableCollection<GamingStationResponse> MyStations => _myStations;
    public ObservableCollection<GamingStationResponse> SharedStations => _sharedStations;

    public bool HasNoStations => !IsLoadingStations && _myStations.Count == 0 && _sharedStations.Count == 0;
    public bool HasMyStations => _myStations.Count > 0;
    public bool HasSharedStations => _sharedStations.Count > 0;
    public bool IsCurrentMachineRegistered => _myStations.Any(s => s.IsOwner);

    public ObservableCollection<MyGamingStationInfo> MyGamingStations => _myGamingStations;
    public string CurrentMachineId => _currentMachineId;

    public bool IsGamingStationEnabled => _settingsStore.Settings.IsGamingStationEnabled;
    public bool ShowGamingStationBanner => IsGamingStationEnabled;

    public string GamingStationChannelStatus =>
        _myGamingStations.FirstOrDefault(s => s.MachineId == _currentMachineId)?.IsInVoiceChannel == true
            ? "In voice channel"
            : "Available";

    #endregion

    #region Station Stream Properties

    public bool IsViewingStationStream
    {
        get => _isViewingStationStream;
        set => this.RaiseAndSetIfChanged(ref _isViewingStationStream, value);
    }

    public bool IsConnectingToStation
    {
        get => _isConnectingToStation;
        set => this.RaiseAndSetIfChanged(ref _isConnectingToStation, value);
    }

    public Guid? ConnectedStationId
    {
        get => _connectedStationId;
        set => this.RaiseAndSetIfChanged(ref _connectedStationId, value);
    }

    public string ConnectedStationName
    {
        get => _connectedStationName;
        set => this.RaiseAndSetIfChanged(ref _connectedStationName, value);
    }

    public string StationConnectionStatus
    {
        get => _stationConnectionStatus;
        set => this.RaiseAndSetIfChanged(ref _stationConnectionStatus, value);
    }

    public int StationConnectedUserCount
    {
        get => _stationConnectedUserCount;
        set => this.RaiseAndSetIfChanged(ref _stationConnectedUserCount, value);
    }

    public int StationLatency
    {
        get => _stationLatency;
        set => this.RaiseAndSetIfChanged(ref _stationLatency, value);
    }

    public string StationResolution
    {
        get => _stationResolution;
        set => this.RaiseAndSetIfChanged(ref _stationResolution, value);
    }

    public int? StationPlayerSlot
    {
        get => _stationPlayerSlot;
        set => this.RaiseAndSetIfChanged(ref _stationPlayerSlot, value);
    }

    #endregion

    #region Commands

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> RegisterCommand { get; }
    public ReactiveCommand<GamingStationResponse, Unit> ConnectCommand { get; }
    public ReactiveCommand<GamingStationResponse, Unit> ManageCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFullscreenCommand { get; }
    public ReactiveCommand<Unit, Unit> DisableCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Opens the gaming stations view.
    /// </summary>
    public async Task OpenAsync()
    {
        ViewOpening?.Invoke();
        IsViewingGamingStations = true;
        await LoadStationsAsync();
    }

    /// <summary>
    /// Closes the gaming stations view.
    /// </summary>
    public void Close()
    {
        IsViewingGamingStations = false;
        IsViewingStationStream = false;
    }

    /// <summary>
    /// Loads the list of gaming stations.
    /// </summary>
    public async Task LoadStationsAsync()
    {
        IsLoadingStations = true;
        try
        {
            var result = await _apiClient.GetStationsAsync();
            if (result.Success && result.Data is not null)
            {
                _myStations.Clear();
                _sharedStations.Clear();

                foreach (var station in result.Data)
                {
                    if (station.IsOwner)
                        _myStations.Add(station);
                    else
                        _sharedStations.Add(station);
                }

                this.RaisePropertyChanged(nameof(HasNoStations));
                this.RaisePropertyChanged(nameof(HasMyStations));
                this.RaisePropertyChanged(nameof(HasSharedStations));
                this.RaisePropertyChanged(nameof(IsCurrentMachineRegistered));
            }
        }
        finally
        {
            IsLoadingStations = false;
        }
    }

    /// <summary>
    /// Registers this machine as a gaming station.
    /// </summary>
    private async Task RegisterStationAsync()
    {
        var machineId = Environment.MachineName + "-" + Environment.UserName;
        var stationName = Environment.MachineName;

        var result = await _apiClient.RegisterStationAsync(stationName, "Gaming Station", machineId);
        if (result.Success && result.Data is not null)
        {
            _myStations.Add(result.Data);
            this.RaisePropertyChanged(nameof(HasNoStations));
            this.RaisePropertyChanged(nameof(HasMyStations));
            this.RaisePropertyChanged(nameof(IsCurrentMachineRegistered));
        }
        else
        {
            ErrorOccurred?.Invoke(result.Error ?? "Failed to register station");
        }
    }

    // Placeholder methods for Phase 3+ implementation
    private Task ConnectToStationAsync(GamingStationResponse station) => Task.CompletedTask;
    private Task DisconnectFromStationAsync() => Task.CompletedTask;
    private void ToggleStationFullscreen() { }
    private void ManageStation(GamingStationResponse station) { }

    /// <summary>
    /// Disables gaming station mode.
    /// </summary>
    private async Task DisableGamingStationAsync()
    {
        _settingsStore.Settings.IsGamingStationEnabled = false;
        _settingsStore.Save();

        // Notify server that we're no longer available as a gaming station
        await ReportStatusAsync();

        this.RaisePropertyChanged(nameof(IsGamingStationEnabled));
        this.RaisePropertyChanged(nameof(ShowGamingStationBanner));
    }

    /// <summary>
    /// Sends keyboard input to a gaming station in the current voice channel.
    /// </summary>
    public async Task SendKeyboardInputAsync(StationKeyboardInput input)
    {
        var channelId = _getCurrentVoiceChannelId();
        if (channelId is null) return;

        try
        {
            await _signalR.SendStationKeyboardInputAsync(channelId.Value, input);
        }
        catch
        {
            // Silently ignore station input failures
        }
    }

    /// <summary>
    /// Sends mouse input to a gaming station in the current voice channel.
    /// </summary>
    public async Task SendMouseInputAsync(StationMouseInput input)
    {
        var channelId = _getCurrentVoiceChannelId();
        if (channelId is null) return;

        try
        {
            await _signalR.SendStationMouseInputAsync(channelId.Value, input);
        }
        catch
        {
            // Silently ignore station input failures
        }
    }

    /// <summary>
    /// Reports this client's gaming station status to the server.
    /// </summary>
    public async Task ReportStatusAsync()
    {
        if (!_settingsStore.Settings.IsGamingStationEnabled) return;

        var displayName = string.IsNullOrWhiteSpace(_settingsStore.Settings.GamingStationDisplayName)
            ? null
            : _settingsStore.Settings.GamingStationDisplayName;

        try
        {
            await _signalR.SetGamingStationAvailableAsync(true, displayName, _currentMachineId);
        }
        catch
        {
            // Gaming station status report failure - ignore
        }
    }

    /// <summary>
    /// Commands a gaming station to join the current voice channel.
    /// </summary>
    public async Task CommandJoinCurrentChannelAsync(string machineId)
    {
        var channelId = _getCurrentVoiceChannelId();
        if (channelId is null) return;

        try
        {
            await _signalR.CommandStationJoinChannelAsync(machineId, channelId.Value);
        }
        catch
        {
            // Station command failure - ignore
        }
    }

    /// <summary>
    /// Commands a gaming station to leave its current voice channel.
    /// </summary>
    public async Task CommandLeaveChannelAsync(string machineId)
    {
        try
        {
            await _signalR.CommandStationLeaveChannelAsync(machineId);
        }
        catch
        {
            // Station command failure - ignore
        }
    }

    /// <summary>
    /// Updates the status of a gaming station based on SignalR event.
    /// </summary>
    public void OnGamingStationStatusChanged(GamingStationStatusChangedEvent e, Guid currentUserId)
    {
        // Only track stations that belong to the current user
        if (e.UserId != currentUserId) return;

        var existingIndex = -1;
        for (int i = 0; i < _myGamingStations.Count; i++)
        {
            if (_myGamingStations[i].MachineId == e.MachineId)
            {
                existingIndex = i;
                break;
            }
        }

        if (e.IsAvailable)
        {
            var stationInfo = new MyGamingStationInfo(
                MachineId: e.MachineId,
                DisplayName: e.DisplayName,
                IsAvailable: e.IsAvailable,
                IsInVoiceChannel: e.IsInVoiceChannel,
                CurrentChannelId: e.CurrentChannelId,
                IsScreenSharing: e.IsScreenSharing,
                IsCurrentMachine: e.MachineId == _currentMachineId
            );

            if (existingIndex >= 0)
                _myGamingStations[existingIndex] = stationInfo;
            else
                _myGamingStations.Add(stationInfo);
        }
        else if (existingIndex >= 0)
        {
            _myGamingStations.RemoveAt(existingIndex);
        }

        this.RaisePropertyChanged(nameof(MyGamingStations));
        this.RaisePropertyChanged(nameof(GamingStationChannelStatus));
    }

    #endregion
}
