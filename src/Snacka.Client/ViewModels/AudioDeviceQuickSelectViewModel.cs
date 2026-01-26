using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the quick audio device selector popup.
/// Handles device selection, push-to-talk settings, and audio level monitoring.
/// </summary>
public class AudioDeviceQuickSelectViewModel : ReactiveObject, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;

    private bool _isOpen;
    private float _inputLevel;
    private bool _isRefreshingDevices;
    private bool _isPushToTalkActive;

    /// <summary>
    /// Raised when push-to-talk changes the mute state.
    /// Parameter is true when should unmute (key pressed), false when should mute (key released).
    /// </summary>
    public event Action<bool>? PushToTalkMuteChanged;

    public AudioDeviceQuickSelectViewModel(ISettingsStore settingsStore, IAudioDeviceService audioDeviceService)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;

        InputDevices = new ObservableCollection<AudioDeviceItem>();
        OutputDevices = new ObservableCollection<AudioDeviceItem>();

        // Add default options immediately
        InputDevices.Add(new AudioDeviceItem(null, "Default"));
        OutputDevices.Add(new AudioDeviceItem(null, "Default"));
    }

    public ObservableCollection<AudioDeviceItem> InputDevices { get; }
    public ObservableCollection<AudioDeviceItem> OutputDevices { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;
            this.RaiseAndSetIfChanged(ref _isOpen, value);

            // Start/stop audio level monitoring when popup opens/closes
            if (value)
            {
                _ = StartAudioLevelMonitoringAsync();
            }
            else
            {
                _ = StopAudioLevelMonitoringAsync();
            }
        }
    }

    public float InputLevel
    {
        get => _inputLevel;
        set => this.RaiseAndSetIfChanged(ref _inputLevel, value);
    }

    public AudioDeviceItem? SelectedInputDeviceItem
    {
        get => InputDevices.FirstOrDefault(d => d.Value == _settingsStore.Settings.AudioInputDevice)
               ?? InputDevices.FirstOrDefault();
        set
        {
            var newValue = value?.Value;

            if (_isRefreshingDevices) return;
            if (_settingsStore.Settings.AudioInputDevice == newValue) return;

            _settingsStore.Settings.AudioInputDevice = newValue;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(SelectedInputDeviceItem));
            this.RaisePropertyChanged(nameof(SelectedInputDeviceDisplay));
            this.RaisePropertyChanged(nameof(HasNoInputDevice));
        }
    }

    public AudioDeviceItem? SelectedOutputDeviceItem
    {
        get => OutputDevices.FirstOrDefault(d => d.Value == _settingsStore.Settings.AudioOutputDevice)
               ?? OutputDevices.FirstOrDefault();
        set
        {
            var newValue = value?.Value;

            if (_isRefreshingDevices) return;
            if (_settingsStore.Settings.AudioOutputDevice == newValue) return;

            _settingsStore.Settings.AudioOutputDevice = newValue;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(SelectedOutputDeviceItem));
            this.RaisePropertyChanged(nameof(SelectedOutputDeviceDisplay));
            this.RaisePropertyChanged(nameof(HasNoOutputDevice));
        }
    }

    public string SelectedInputDeviceDisplay => SelectedInputDeviceItem?.DisplayName ?? "Default";
    public string SelectedOutputDeviceDisplay => _settingsStore.Settings.AudioOutputDevice ?? "Default";

    public bool HasNoInputDevice => string.IsNullOrEmpty(_settingsStore.Settings.AudioInputDevice);
    public bool HasNoOutputDevice => string.IsNullOrEmpty(_settingsStore.Settings.AudioOutputDevice);

    public bool PushToTalkEnabled
    {
        get => _settingsStore.Settings.PushToTalkEnabled;
        set
        {
            if (_settingsStore.Settings.PushToTalkEnabled == value) return;
            _settingsStore.Settings.PushToTalkEnabled = value;
            _settingsStore.Save();
            this.RaisePropertyChanged(nameof(PushToTalkEnabled));
            this.RaisePropertyChanged(nameof(VoiceModeDescription));
        }
    }

    public string VoiceModeDescription => PushToTalkEnabled
        ? "Push-to-talk: Hold Space to talk"
        : "Voice activity: Speak to transmit";

    /// <summary>
    /// Opens the popup and refreshes device list.
    /// </summary>
    public void Open()
    {
        RefreshDevices();
        IsOpen = true;
    }

    /// <summary>
    /// Closes the popup.
    /// </summary>
    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>
    /// Called when push-to-talk key is pressed or released.
    /// </summary>
    /// <param name="isPressed">True when key pressed, false when released.</param>
    /// <param name="isInVoiceChannel">Whether user is currently in a voice channel.</param>
    public void HandlePushToTalk(bool isPressed, bool isInVoiceChannel)
    {
        if (!PushToTalkEnabled || !isInVoiceChannel) return;

        _isPushToTalkActive = isPressed;
        // Notify parent to change mute state: pressed = unmute, released = mute
        PushToTalkMuteChanged?.Invoke(isPressed);
    }

    /// <summary>
    /// Refreshes the list of available audio devices.
    /// </summary>
    public void RefreshDevices()
    {
        _ = RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        _isRefreshingDevices = true;
        try
        {
            InputDevices.Clear();
            OutputDevices.Clear();

            // Add default option
            InputDevices.Add(new AudioDeviceItem(null, "Default"));
            OutputDevices.Add(new AudioDeviceItem(null, "Default"));

            // Get input devices via native enumeration (async)
            var inputDevices = await _audioDeviceService.GetInputDevicesAsync();
            foreach (var device in inputDevices)
            {
                InputDevices.Add(new AudioDeviceItem(device.Id, device.Name));
            }

            // Get output devices via SDL2
            foreach (var device in _audioDeviceService.GetOutputDevices())
            {
                OutputDevices.Add(new AudioDeviceItem(device, device));
            }
        }
        finally
        {
            _isRefreshingDevices = false;
        }

        // Notify UI to re-sync the selection after items are populated
        this.RaisePropertyChanged(nameof(SelectedInputDeviceItem));
        this.RaisePropertyChanged(nameof(SelectedOutputDeviceItem));
    }

    private async Task StartAudioLevelMonitoringAsync()
    {
        try
        {
            await _audioDeviceService.StartInputTestAsync(
                _settingsStore.Settings.AudioInputDevice,
                level => Dispatcher.UIThread.Post(() => InputLevel = level)
            );
        }
        catch
        {
            // Audio monitoring startup failure is non-critical
        }
    }

    private async Task StopAudioLevelMonitoringAsync()
    {
        try
        {
            await _audioDeviceService.StopTestAsync();
            InputLevel = 0;
        }
        catch
        {
            // Audio monitoring stop failure is non-critical
        }
    }

    public void Dispose()
    {
        if (_isOpen)
        {
            _ = StopAudioLevelMonitoringAsync();
        }
    }
}
