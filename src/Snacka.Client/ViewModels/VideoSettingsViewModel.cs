using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Services.GpuVideo;

namespace Snacka.Client.ViewModels;

/// <summary>
/// Option item for resolution dropdown (height-based).
/// </summary>
public record ResolutionOption(int Value, string DisplayName);

/// <summary>
/// Option item for framerate dropdown.
/// </summary>
public record FramerateOption(int Value, string DisplayName);

/// <summary>
/// Option item for bitrate dropdown.
/// </summary>
public record BitrateOption(int Value, string DisplayName);

public class VideoSettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IVideoDeviceService _videoDeviceService;
    private readonly CameraTestService _cameraTestService;

    private string? _selectedVideoDevice;
    private bool _isTestingCamera;
    private bool _isLoadingDevices;
    private int _frameCount;
    private int _frameWidth;
    private int _frameHeight;
    private string _cameraStatus = "Not testing";

    // Quality settings
    private int _selectedHeight;
    private int _selectedFramerate;
    private int _selectedBitrate;

    /// <summary>
    /// Fired when a preview frame is received (NV12 format for GPU rendering).
    /// Parameters: width, height, nv12Data
    /// </summary>
    public event Action<int, int, byte[]>? OnPreviewFrame;

    public VideoSettingsViewModel(ISettingsStore settingsStore, IVideoDeviceService videoDeviceService)
    {
        _settingsStore = settingsStore;
        _videoDeviceService = videoDeviceService;
        _cameraTestService = new CameraTestService();

        VideoDevices = new ObservableCollection<VideoDeviceItem>();

        // Initialize quality options (height-based, width calculated assuming 16:9)
        // Standard 16:9 resolutions: 640x360, 1280x720, 1920x1080
        ResolutionOptions = new ObservableCollection<ResolutionOption>
        {
            new(360, "360p (640×360)"),
            new(720, "720p (1280×720)"),
            new(1080, "1080p (1920×1080)")
        };

        FramerateOptions = new ObservableCollection<FramerateOption>
        {
            new(15, "15 fps"),
            new(30, "30 fps")
        };

        BitrateOptions = new ObservableCollection<BitrateOption>
        {
            new(1, "Low (1 Mbps)"),
            new(2, "Medium (2 Mbps)"),
            new(4, "High (4 Mbps)")
        };

        TestCameraCommand = ReactiveCommand.CreateFromTask(ToggleCameraTest);
        RefreshDevicesCommand = ReactiveCommand.CreateFromTask(RefreshDevicesAsync);

        // Load saved selections
        _selectedVideoDevice = _settingsStore.Settings.VideoDevice;
        _selectedHeight = _settingsStore.Settings.CameraHeight;
        _selectedFramerate = _settingsStore.Settings.CameraFramerate;
        _selectedBitrate = _settingsStore.Settings.CameraBitrateMbps;

        // Wire up camera test service events
        _cameraTestService.OnPreviewFrameReceived += (width, height, nv12Data) =>
            Dispatcher.UIThread.Post(() => HandlePreviewFrame(width, height, nv12Data));
        _cameraTestService.OnError += error =>
            Dispatcher.UIThread.Post(() => CameraStatus = $"Error: {error}");

        // Add "None" option immediately so UI has something to show
        VideoDevices.Add(new VideoDeviceItem(null, "None"));

        // Enumerate devices immediately (fire-and-forget)
        _ = InitializeAsync();
    }

    /// <summary>
    /// Initialize device lists asynchronously. Call this after construction.
    /// </summary>
    public Task InitializeAsync() => RefreshDevicesAsync();

    public ObservableCollection<VideoDeviceItem> VideoDevices { get; }

    // Quality options
    public ObservableCollection<ResolutionOption> ResolutionOptions { get; }
    public ObservableCollection<FramerateOption> FramerateOptions { get; }
    public ObservableCollection<BitrateOption> BitrateOptions { get; }

    public int SelectedHeight
    {
        get => _selectedHeight;
        set
        {
            if (_selectedHeight == value) return;
            this.RaiseAndSetIfChanged(ref _selectedHeight, value);
            _settingsStore.Settings.CameraHeight = value;
            _settingsStore.Save();

            // Restart test with new resolution if testing
            if (_isTestingCamera)
            {
                _ = RestartCameraTest();
            }
        }
    }

    public int SelectedFramerate
    {
        get => _selectedFramerate;
        set
        {
            if (_selectedFramerate == value) return;
            this.RaiseAndSetIfChanged(ref _selectedFramerate, value);
            _settingsStore.Settings.CameraFramerate = value;
            _settingsStore.Save();

            // Restart test with new framerate if testing
            if (_isTestingCamera)
            {
                _ = RestartCameraTest();
            }
        }
    }

    public int SelectedBitrate
    {
        get => _selectedBitrate;
        set
        {
            if (_selectedBitrate == value) return;
            this.RaiseAndSetIfChanged(ref _selectedBitrate, value);
            _settingsStore.Settings.CameraBitrateMbps = value;
            _settingsStore.Save();

            // Restart test with new bitrate if testing
            if (_isTestingCamera)
            {
                _ = RestartCameraTest();
            }
        }
    }

    public string? SelectedVideoDevice
    {
        get => _selectedVideoDevice;
        set
        {
            if (_selectedVideoDevice == value) return;

            this.RaiseAndSetIfChanged(ref _selectedVideoDevice, value);
            _settingsStore.Settings.VideoDevice = value;
            _settingsStore.Save();

            // Restart test with new device if testing
            if (_isTestingCamera)
            {
                _ = RestartCameraTest();
            }
        }
    }

    public bool IsTestingCamera
    {
        get => _isTestingCamera;
        set => this.RaiseAndSetIfChanged(ref _isTestingCamera, value);
    }

    public int FrameCount
    {
        get => _frameCount;
        set => this.RaiseAndSetIfChanged(ref _frameCount, value);
    }

    public string CameraStatus
    {
        get => _cameraStatus;
        set => this.RaiseAndSetIfChanged(ref _cameraStatus, value);
    }

    public string Resolution => _frameWidth > 0 ? $"{_frameWidth}x{_frameHeight}" : "—";

    /// <summary>
    /// Gets whether GPU video rendering is available on this system.
    /// </summary>
    public bool IsGpuAvailable => GpuVideoRendererFactory.IsAvailable();

    public bool IsLoadingDevices
    {
        get => _isLoadingDevices;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingDevices, value);
    }

    public ICommand TestCameraCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    private async Task RefreshDevicesAsync()
    {
        IsLoadingDevices = true;

        try
        {
            // Run device enumeration on background thread to avoid blocking UI
            var devices = await Task.Run(() => _videoDeviceService.GetCameraDevices());

            // Update collection on UI thread
            VideoDevices.Clear();

            // Add "None" option
            VideoDevices.Add(new VideoDeviceItem(null, "None"));

            // Add available devices
            foreach (var device in devices)
            {
                VideoDevices.Add(new VideoDeviceItem(device.Path, device.Name));
            }
        }
        finally
        {
            IsLoadingDevices = false;
        }
    }

    private async Task ToggleCameraTest()
    {
        if (_isTestingCamera)
        {
            await _cameraTestService.StopAsync();
            IsTestingCamera = false;
            FrameCount = 0;
            _frameWidth = 0;
            _frameHeight = 0;
            CameraStatus = "Not testing";
            this.RaisePropertyChanged(nameof(Resolution));
        }
        else
        {
            try
            {
                FrameCount = 0;
                CameraStatus = "Starting...";

                // Get camera ID - if null, use "0" as default
                var cameraId = _selectedVideoDevice ?? "0";

                await _cameraTestService.StartAsync(cameraId, _selectedHeight, _selectedFramerate, _selectedBitrate);
                IsTestingCamera = true;
                CameraStatus = "Receiving frames";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VideoSettings: Failed to start camera test - {ex.Message}");
                IsTestingCamera = false;
                CameraStatus = $"Error: {ex.Message}";
            }
        }
    }

    private async Task RestartCameraTest()
    {
        await _cameraTestService.StopAsync();
        FrameCount = 0;
        _frameWidth = 0;
        _frameHeight = 0;
        CameraStatus = "Restarting...";
        this.RaisePropertyChanged(nameof(Resolution));

        try
        {
            var cameraId = _selectedVideoDevice ?? "0";
            await _cameraTestService.StartAsync(cameraId, _selectedHeight, _selectedFramerate, _selectedBitrate);
            CameraStatus = "Receiving frames";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"VideoSettings: Failed to restart camera test - {ex.Message}");
            IsTestingCamera = false;
            CameraStatus = $"Error: {ex.Message}";
        }
    }

    private void HandlePreviewFrame(int width, int height, byte[] nv12Data)
    {
        FrameCount++;

        if (_frameWidth != width || _frameHeight != height)
        {
            _frameWidth = width;
            _frameHeight = height;
            this.RaisePropertyChanged(nameof(Resolution));
        }

        // Fire event for View to render (GPU will handle YUV→RGB conversion)
        OnPreviewFrame?.Invoke(width, height, nv12Data);
    }
}

public record VideoDeviceItem(string? Value, string DisplayName);
