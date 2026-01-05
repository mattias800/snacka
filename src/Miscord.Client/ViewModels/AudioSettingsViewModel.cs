using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using Miscord.Client.Services;

namespace Miscord.Client.ViewModels;

public class AudioSettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;

    private string? _selectedInputDevice;
    private string? _selectedOutputDevice;
    private bool _isTestingMicrophone;
    private bool _isLoopbackEnabled;
    private float _inputLevel;

    public AudioSettingsViewModel(ISettingsStore settingsStore, IAudioDeviceService audioDeviceService)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;

        InputDevices = new ObservableCollection<AudioDeviceItem>();
        OutputDevices = new ObservableCollection<AudioDeviceItem>();

        TestMicrophoneCommand = ReactiveCommand.CreateFromTask(ToggleMicrophoneTest);
        ToggleLoopbackCommand = ReactiveCommand.Create(ToggleLoopback);
        RefreshDevicesCommand = ReactiveCommand.Create(RefreshDevices);

        // Load devices
        RefreshDevices();

        // Set initial selections from settings
        _selectedInputDevice = _settingsStore.Settings.AudioInputDevice;
        _selectedOutputDevice = _settingsStore.Settings.AudioOutputDevice;
    }

    public ObservableCollection<AudioDeviceItem> InputDevices { get; }
    public ObservableCollection<AudioDeviceItem> OutputDevices { get; }

    public string? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _selectedInputDevice, value) != value)
            {
                _settingsStore.Settings.AudioInputDevice = value;
                _settingsStore.Save();

                // Restart test with new device if testing
                if (_isTestingMicrophone)
                {
                    _ = RestartMicrophoneTest();
                }
            }
        }
    }

    public string? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _selectedOutputDevice, value) != value)
            {
                _settingsStore.Settings.AudioOutputDevice = value;
                _settingsStore.Save();

                // Update loopback device if enabled
                if (_isLoopbackEnabled)
                {
                    _audioDeviceService.SetLoopbackEnabled(true, value);
                }
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
        set => this.RaiseAndSetIfChanged(ref _inputLevel, value);
    }

    public ICommand TestMicrophoneCommand { get; }
    public ICommand ToggleLoopbackCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    private void RefreshDevices()
    {
        InputDevices.Clear();
        OutputDevices.Clear();

        // Add default option
        InputDevices.Add(new AudioDeviceItem(null, "Default"));
        OutputDevices.Add(new AudioDeviceItem(null, "Default"));

        // Add available devices
        foreach (var device in _audioDeviceService.GetInputDevices())
        {
            InputDevices.Add(new AudioDeviceItem(device, device));
        }

        foreach (var device in _audioDeviceService.GetOutputDevices())
        {
            OutputDevices.Add(new AudioDeviceItem(device, device));
        }
    }

    private async Task ToggleMicrophoneTest()
    {
        if (_isTestingMicrophone)
        {
            await _audioDeviceService.StopTestAsync();
            IsTestingMicrophone = false;
            IsLoopbackEnabled = false;
            InputLevel = 0;
        }
        else
        {
            try
            {
                await _audioDeviceService.StartInputTestAsync(
                    _selectedInputDevice,
                    level => Dispatcher.UIThread.Post(() => InputLevel = level)
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

        try
        {
            await _audioDeviceService.StartInputTestAsync(
                _selectedInputDevice,
                level => Dispatcher.UIThread.Post(() => InputLevel = level)
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
