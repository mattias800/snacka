using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Miscord.Client.Controls;

/// <summary>
/// User panel showing avatar, username, and audio controls (mute/deafen/settings).
/// </summary>
public partial class UserPanelView : UserControl
{
    public static readonly StyledProperty<string?> UsernameProperty =
        AvaloniaProperty.Register<UserPanelView, string?>(nameof(Username));

    public static readonly StyledProperty<bool> IsSpeakingProperty =
        AvaloniaProperty.Register<UserPanelView, bool>(nameof(IsSpeaking));

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<UserPanelView, bool>(nameof(IsMuted));

    public static readonly StyledProperty<bool> IsDeafenedProperty =
        AvaloniaProperty.Register<UserPanelView, bool>(nameof(IsDeafened));

    public static readonly StyledProperty<ICommand?> ToggleMuteCommandProperty =
        AvaloniaProperty.Register<UserPanelView, ICommand?>(nameof(ToggleMuteCommand));

    public static readonly StyledProperty<ICommand?> ToggleDeafenCommandProperty =
        AvaloniaProperty.Register<UserPanelView, ICommand?>(nameof(ToggleDeafenCommand));

    public static readonly StyledProperty<ICommand?> OpenSettingsCommandProperty =
        AvaloniaProperty.Register<UserPanelView, ICommand?>(nameof(OpenSettingsCommand));

    public UserPanelView()
    {
        InitializeComponent();
    }

    public string? Username
    {
        get => GetValue(UsernameProperty);
        set => SetValue(UsernameProperty, value);
    }

    public bool IsSpeaking
    {
        get => GetValue(IsSpeakingProperty);
        set => SetValue(IsSpeakingProperty, value);
    }

    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public bool IsDeafened
    {
        get => GetValue(IsDeafenedProperty);
        set => SetValue(IsDeafenedProperty, value);
    }

    public ICommand? ToggleMuteCommand
    {
        get => GetValue(ToggleMuteCommandProperty);
        set => SetValue(ToggleMuteCommandProperty, value);
    }

    public ICommand? ToggleDeafenCommand
    {
        get => GetValue(ToggleDeafenCommandProperty);
        set => SetValue(ToggleDeafenCommandProperty, value);
    }

    public ICommand? OpenSettingsCommand
    {
        get => GetValue(OpenSettingsCommandProperty);
        set => SetValue(OpenSettingsCommandProperty, value);
    }

    // Event for audio device button click
    public event EventHandler? AudioDeviceButtonClicked;

    private void OnAudioDeviceButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AudioDeviceButtonClicked?.Invoke(this, EventArgs.Empty);
    }
}
