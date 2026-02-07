using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;

namespace Snacka.Client.ViewModels;

public class AudioSettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;

    private string? _selectedInputDevice;
    private string? _selectedOutputDevice;
    private bool _isTestingMicrophone;
    private bool _isLoopbackEnabled;
    private float _inputLevel;
    private float _inputGain;
    private float _gateThreshold;
    private bool _gateEnabled;
    private bool _noiseSuppression;
    private bool _isRefreshingDevices; // Flag to prevent binding feedback during refresh
    private bool _isLoadingDevices;
    private bool _isDevicesLoaded;
    private bool _echoCancellation;
    private float _agcGain = 1.0f;
    private DateTime _lastDropdownRefresh = DateTime.MinValue;

    public AudioSettingsViewModel(ISettingsStore settingsStore, IAudioDeviceService audioDeviceService)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;

        InputDevices = new ObservableCollection<AudioDeviceItem>();
        OutputDevices = new ObservableCollection<AudioDeviceItem>();

        TestMicrophoneCommand = ReactiveCommand.CreateFromTask(ToggleMicrophoneTest);
        ToggleLoopbackCommand = ReactiveCommand.Create(ToggleLoopback);
        RefreshDevicesCommand = ReactiveCommand.CreateFromTask(RefreshDevicesAsync);

        // Load saved selections first (before populating devices)
        _selectedInputDevice = _settingsStore.Settings.AudioInputDevice;
        _selectedOutputDevice = _settingsStore.Settings.AudioOutputDevice;
        _inputGain = _settingsStore.Settings.InputGain;
        _gateThreshold = _settingsStore.Settings.GateThreshold;
        _gateEnabled = _settingsStore.Settings.GateEnabled;
        _noiseSuppression = _settingsStore.Settings.NoiseSuppression;
        _echoCancellation = _settingsStore.Settings.EchoCancellation;

        // Add default options immediately so UI has something to show
        InputDevices.Add(new AudioDeviceItem(null, "System default"));
        OutputDevices.Add(new AudioDeviceItem(null, "System default"));
    }

    /// <summary>
    /// Initialize device lists asynchronously. Call this after construction.
    /// </summary>
    public Task InitializeAsync() => RefreshDevicesAsync();

    public ObservableCollection<AudioDeviceItem> InputDevices { get; }
    public ObservableCollection<AudioDeviceItem> OutputDevices { get; }

    public AudioDeviceItem? SelectedInputDeviceItem
    {
        get => InputDevices.FirstOrDefault(d => d.Value == _selectedInputDevice)
               ?? InputDevices.FirstOrDefault(); // Fall back to "Default"
        set
        {
            var newValue = value?.Value;

            // Ignore binding updates during device refresh
            if (_isRefreshingDevices) return;

            if (_selectedInputDevice == newValue) return;

            _selectedInputDevice = newValue;
            _settingsStore.Settings.AudioInputDevice = newValue;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(SelectedInputDeviceItem));

            // Restart test with new device if testing
            if (_isTestingMicrophone)
            {
                _ = RestartMicrophoneTest();
            }
        }
    }

    public AudioDeviceItem? SelectedOutputDeviceItem
    {
        get => OutputDevices.FirstOrDefault(d => d.Value == _selectedOutputDevice)
               ?? OutputDevices.FirstOrDefault(); // Fall back to "Default"
        set
        {
            var newValue = value?.Value;

            // Ignore binding updates during device refresh
            if (_isRefreshingDevices) return;

            if (_selectedOutputDevice == newValue) return;

            _selectedOutputDevice = newValue;
            _settingsStore.Settings.AudioOutputDevice = newValue;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(SelectedOutputDeviceItem));

            // Update loopback device if enabled
            if (_isLoopbackEnabled)
            {
                _audioDeviceService.SetLoopbackEnabled(true, newValue);
            }
        }
    }

    public bool IsTestingMicrophone
    {
        get => _isTestingMicrophone;
        set => this.RaiseAndSetIfChanged(ref _isTestingMicrophone, value);
    }

    public bool IsLoopbackEnabled
    {
        get => _isLoopbackEnabled;
        set => this.RaiseAndSetIfChanged(ref _isLoopbackEnabled, value);
    }

    public float InputLevel
    {
        get => _inputLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _inputLevel, value);
            this.RaisePropertyChanged(nameof(IsAboveGate));
        }
    }

    public float InputGain
    {
        get => _inputGain;
        set
        {
            if (Math.Abs(_inputGain - value) < 0.001f) return;

            this.RaiseAndSetIfChanged(ref _inputGain, value);
            _settingsStore.Settings.InputGain = value;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(InputGainPercent));
        }
    }

    public int InputGainPercent => (int)(_inputGain * 100);

    public float GateThreshold
    {
        get => _gateThreshold;
        set
        {
            if (Math.Abs(_gateThreshold - value) < 0.001f) return;

            this.RaiseAndSetIfChanged(ref _gateThreshold, value);
            _settingsStore.Settings.GateThreshold = value;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(GateThresholdPercent));
        }
    }

    public int GateThresholdPercent => (int)(_gateThreshold * 100);

    public bool GateEnabled
    {
        get => _gateEnabled;
        set
        {
            if (_gateEnabled == value) return;

            this.RaiseAndSetIfChanged(ref _gateEnabled, value);
            _settingsStore.Settings.GateEnabled = value;
            _settingsStore.Save();
        }
    }

    /// <summary>
    /// Enable AI-powered noise suppression for microphone input.
    /// Note: Changes require reconnecting the microphone to take effect.
    /// </summary>
    public bool NoiseSuppression
    {
        get => _noiseSuppression;
        set
        {
            if (_noiseSuppression == value) return;

            this.RaiseAndSetIfChanged(ref _noiseSuppression, value);
            _settingsStore.Settings.NoiseSuppression = value;
            _settingsStore.Save();
        }
    }

    // Is the current input level above the gate threshold?
    public bool IsAboveGate => !_gateEnabled || _inputLevel >= _gateThreshold;

    // AGC gain (1.0 to 16.0)
    public float AgcGain
    {
        get => _agcGain;
        set
        {
            this.RaiseAndSetIfChanged(ref _agcGain, value);
            this.RaisePropertyChanged(nameof(AgcGainDisplay));
            this.RaisePropertyChanged(nameof(AgcBoostPercent));
            this.RaisePropertyChanged(nameof(AgcStatus));
        }
    }

    // Display string for AGC gain (e.g., "2.3x")
    public string AgcGainDisplay => $"{_agcGain:F1}x";

    // AGC boost as percentage (0-100 where 100 = max boost)
    // AGC goes from 1x to 8x, so we map that to 0-100%
    public float AgcBoostPercent => Math.Min(100f, (_agcGain - 1f) / 7f * 100f);

    // Status text based on AGC level
    public string AgcStatus => _agcGain switch
    {
        < 1.5f => "Normal",
        < 3f => "Boosting",
        < 6f => "High boost",
        _ => "Max boost"
    };

    public bool IsLoadingDevices
    {
        get => _isLoadingDevices;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingDevices, value);
    }

    /// <summary>
    /// True when device enumeration has completed at least once.
    /// Used to disable ComboBoxes until devices are loaded.
    /// </summary>
    public bool IsDevicesLoaded
    {
        get => _isDevicesLoaded;
        private set => this.RaiseAndSetIfChanged(ref _isDevicesLoaded, value);
    }

    /// <summary>
    /// Enable OS-level acoustic echo cancellation.
    /// Note: Changes require reconnecting the microphone to take effect.
    /// </summary>
    public bool EchoCancellation
    {
        get => _echoCancellation;
        set
        {
            if (_echoCancellation == value) return;

            this.RaiseAndSetIfChanged(ref _echoCancellation, value);
            _settingsStore.Settings.EchoCancellation = value;
            _settingsStore.Save();
        }
    }

    /// <summary>
    /// True if running on Linux. Used to show system-level AEC instructions.
    /// </summary>
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// True if NOT running on Linux (Windows/macOS). Used to show AEC info message.
    /// </summary>
    public static bool IsNotLinux => !IsLinux;

    public ICommand TestMicrophoneCommand { get; }
    public ICommand ToggleLoopbackCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    private async Task RefreshDevicesAsync()
    {
        IsLoadingDevices = true;
        _isRefreshingDevices = true;

        try
        {
            // Get input devices via native enumeration (async)
            var inputDevices = await _audioDeviceService.GetInputDevicesAsync();

            // Get output devices via SDL2 (sync, but run on background thread)
            var outputDevices = await Task.Run(() => _audioDeviceService.GetOutputDevices());

            // Update collections on UI thread
            InputDevices.Clear();
            OutputDevices.Clear();

            // Add default option
            InputDevices.Add(new AudioDeviceItem(null, "System default"));
            OutputDevices.Add(new AudioDeviceItem(null, "System default"));

            // Add available input devices (ID is device index, DisplayName is device name)
            foreach (var device in inputDevices)
            {
                InputDevices.Add(new AudioDeviceItem(device.Id, device.Name));
            }

            // Add available output devices
            foreach (var device in outputDevices)
            {
                OutputDevices.Add(new AudioDeviceItem(device, device));
            }
        }
        finally
        {
            _isRefreshingDevices = false;
            IsLoadingDevices = false;
            IsDevicesLoaded = true;
        }

        // Notify UI to re-sync the selection after items are populated
        this.RaisePropertyChanged(nameof(SelectedInputDeviceItem));
        this.RaisePropertyChanged(nameof(SelectedOutputDeviceItem));
    }

    /// <summary>
    /// Called when a device dropdown is opened. Refreshes device list if enough time has passed.
    /// </summary>
    public void OnDropdownOpened()
    {
        // Debounce: don't refresh more than once every 2 seconds
        if ((DateTime.UtcNow - _lastDropdownRefresh).TotalSeconds < 2)
            return;

        _lastDropdownRefresh = DateTime.UtcNow;
        _ = RefreshDevicesAsync();
    }

    private async Task ToggleMicrophoneTest()
    {
        if (_isTestingMicrophone)
        {
            await _audioDeviceService.StopTestAsync();
            IsTestingMicrophone = false;
            IsLoopbackEnabled = false;
            InputLevel = 0;
            AgcGain = 1.0f;
        }
        else
        {
            try
            {
                AgcGain = 1.0f; // Reset AGC display
                await _audioDeviceService.StartInputTestAsync(
                    _selectedInputDevice,
                    level => Dispatcher.UIThread.Post(() =>
                    {
                        // Level already has gain applied by AudioDeviceService
                        InputLevel = level;
                    }),
                    agcGain => Dispatcher.UIThread.Post(() =>
                    {
                        AgcGain = agcGain;
                    })
                );
                IsTestingMicrophone = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AudioSettings: Failed to start microphone test - {ex.Message}");
                IsTestingMicrophone = false;
            }
        }
    }

    private async Task RestartMicrophoneTest()
    {
        await _audioDeviceService.StopTestAsync();
        IsLoopbackEnabled = false;
        InputLevel = 0;
        AgcGain = 1.0f;

        try
        {
            await _audioDeviceService.StartInputTestAsync(
                _selectedInputDevice,
                level => Dispatcher.UIThread.Post(() =>
                {
                    // Level already has gain applied by AudioDeviceService
                    InputLevel = level;
                }),
                agcGain => Dispatcher.UIThread.Post(() =>
                {
                    AgcGain = agcGain;
                })
            );

            if (_isLoopbackEnabled)
            {
                _audioDeviceService.SetLoopbackEnabled(true, _selectedOutputDevice);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioSettings: Failed to restart microphone test - {ex.Message}");
            IsTestingMicrophone = false;
        }
    }

    private void ToggleLoopback()
    {
        if (!_isTestingMicrophone) return;

        IsLoopbackEnabled = !IsLoopbackEnabled;
        _audioDeviceService.SetLoopbackEnabled(IsLoopbackEnabled, _selectedOutputDevice);
    }
}

public record AudioDeviceItem(string? Value, string DisplayName);
