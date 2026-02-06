using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ReactiveUI;
using Snacka.Client.Coordinators;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the port sharing picker flyout.
/// Shows auto-detected ports, manual port entry, and active shares.
/// </summary>
public class SharePortPickerViewModel : ReactiveObject
{
    private readonly IPortForwardCoordinator _coordinator;
    private readonly IPortDetectionService _portDetection;
    private readonly ITunnelClientService _tunnelClient;
    private readonly IVoiceStore _voiceStore;

    private ObservableCollection<DetectedPort> _detectedPorts = new();
    private int? _selectedPort;
    private string? _label;
    private string _manualPortText = "";
    private bool _isScanning;

    public SharePortPickerViewModel(
        IPortForwardCoordinator coordinator,
        IPortDetectionService portDetection,
        ITunnelClientService tunnelClient,
        IVoiceStore voiceStore)
    {
        _coordinator = coordinator;
        _portDetection = portDetection;
        _tunnelClient = tunnelClient;
        _voiceStore = voiceStore;

        ShareCommand = ReactiveCommand.CreateFromTask(ShareAsync);
        StopShareCommand = ReactiveCommand.CreateFromTask<string>(StopShareAsync);
        RefreshDetectionCommand = ReactiveCommand.CreateFromTask(RefreshDetectionAsync);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
    }

    /// <summary>
    /// Auto-detected open ports.
    /// </summary>
    public ObservableCollection<DetectedPort> DetectedPorts
    {
        get => _detectedPorts;
        set => this.RaiseAndSetIfChanged(ref _detectedPorts, value);
    }

    /// <summary>
    /// Currently selected port (from detection or manual entry).
    /// </summary>
    public int? SelectedPort
    {
        get => _selectedPort;
        set => this.RaiseAndSetIfChanged(ref _selectedPort, value);
    }

    /// <summary>
    /// Manual port number text input.
    /// </summary>
    public string ManualPortText
    {
        get => _manualPortText;
        set
        {
            this.RaiseAndSetIfChanged(ref _manualPortText, value);
            if (int.TryParse(value, out var port) && port is >= 1 and <= 65535)
            {
                SelectedPort = port;
            }
        }
    }

    /// <summary>
    /// Optional friendly label for the shared port.
    /// </summary>
    public string? Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    /// <summary>
    /// Whether port scanning is in progress.
    /// </summary>
    public bool IsScanning
    {
        get => _isScanning;
        set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    /// <summary>
    /// Active tunnels that this user owns.
    /// </summary>
    public IReadOnlyList<ActiveTunnel> ActiveShares => _tunnelClient.GetActiveTunnels();

    /// <summary>
    /// Whether the user has any active shares.
    /// </summary>
    public bool HasActiveShares => ActiveShares.Count > 0;

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ShareCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> StopShareCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshDetectionCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CloseCommand { get; }

    /// <summary>
    /// Event fired when the picker should close.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Scans for open ports on startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        await RefreshDetectionAsync();
        RefreshActiveShares();
    }

    private async Task ShareAsync()
    {
        if (SelectedPort is null) return;

        var success = await _coordinator.SharePortAsync(SelectedPort.Value, Label);
        if (success)
        {
            RefreshActiveShares();
            // Reset selection
            SelectedPort = null;
            Label = null;
            ManualPortText = "";
        }
    }

    private async Task StopShareAsync(string tunnelId)
    {
        await _coordinator.StopSharingPortAsync(tunnelId);
        RefreshActiveShares();
    }

    private async Task RefreshDetectionAsync()
    {
        IsScanning = true;
        try
        {
            var ports = await _portDetection.DetectOpenPortsAsync();
            DetectedPorts = new ObservableCollection<DetectedPort>(ports);

            // Auto-select the first detected port if none is selected
            if (SelectedPort is null && ports.Count > 0)
            {
                SelectedPort = ports[0].Port;
                ManualPortText = ports[0].Port.ToString();
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void RefreshActiveShares()
    {
        this.RaisePropertyChanged(nameof(ActiveShares));
        this.RaisePropertyChanged(nameof(HasActiveShares));
    }

    /// <summary>
    /// Select a detected port.
    /// </summary>
    public void SelectDetectedPort(DetectedPort port)
    {
        SelectedPort = port.Port;
        ManualPortText = port.Port.ToString();
        if (port.ServerType is not null && string.IsNullOrEmpty(Label))
        {
            Label = port.ServerType;
        }
    }
}
