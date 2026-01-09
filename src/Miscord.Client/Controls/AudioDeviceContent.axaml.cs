using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Controls;

/// <summary>
/// Content for audio device popup showing input/output device selection.
/// </summary>
public partial class AudioDeviceContent : UserControl
{
    public static readonly StyledProperty<IEnumerable<AudioDeviceItem>?> InputDevicesProperty =
        AvaloniaProperty.Register<AudioDeviceContent, IEnumerable<AudioDeviceItem>?>(nameof(InputDevices));

    public static readonly StyledProperty<AudioDeviceItem?> SelectedInputDeviceItemProperty =
        AvaloniaProperty.Register<AudioDeviceContent, AudioDeviceItem?>(nameof(SelectedInputDeviceItem));

    public static readonly StyledProperty<IEnumerable<AudioDeviceItem>?> OutputDevicesProperty =
        AvaloniaProperty.Register<AudioDeviceContent, IEnumerable<AudioDeviceItem>?>(nameof(OutputDevices));

    public static readonly StyledProperty<AudioDeviceItem?> SelectedOutputDeviceItemProperty =
        AvaloniaProperty.Register<AudioDeviceContent, AudioDeviceItem?>(nameof(SelectedOutputDeviceItem));

    public static readonly StyledProperty<double> InputLevelProperty =
        AvaloniaProperty.Register<AudioDeviceContent, double>(nameof(InputLevel));

    public static readonly StyledProperty<bool> PushToTalkEnabledProperty =
        AvaloniaProperty.Register<AudioDeviceContent, bool>(nameof(PushToTalkEnabled));

    public static readonly StyledProperty<string?> VoiceModeDescriptionProperty =
        AvaloniaProperty.Register<AudioDeviceContent, string?>(nameof(VoiceModeDescription));

    public AudioDeviceContent()
    {
        InitializeComponent();
    }

    public IEnumerable<AudioDeviceItem>? InputDevices
    {
        get => GetValue(InputDevicesProperty);
        set => SetValue(InputDevicesProperty, value);
    }

    public AudioDeviceItem? SelectedInputDeviceItem
    {
        get => GetValue(SelectedInputDeviceItemProperty);
        set => SetValue(SelectedInputDeviceItemProperty, value);
    }

    public IEnumerable<AudioDeviceItem>? OutputDevices
    {
        get => GetValue(OutputDevicesProperty);
        set => SetValue(OutputDevicesProperty, value);
    }

    public AudioDeviceItem? SelectedOutputDeviceItem
    {
        get => GetValue(SelectedOutputDeviceItemProperty);
        set => SetValue(SelectedOutputDeviceItemProperty, value);
    }

    public double InputLevel
    {
        get => GetValue(InputLevelProperty);
        set => SetValue(InputLevelProperty, value);
    }

    public bool PushToTalkEnabled
    {
        get => GetValue(PushToTalkEnabledProperty);
        set => SetValue(PushToTalkEnabledProperty, value);
    }

    public string? VoiceModeDescription
    {
        get => GetValue(VoiceModeDescriptionProperty);
        set => SetValue(VoiceModeDescriptionProperty, value);
    }

    // Event for refresh button click
    public event EventHandler? RefreshDevicesRequested;

    private void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RefreshDevicesRequested?.Invoke(this, EventArgs.Empty);
    }
}
