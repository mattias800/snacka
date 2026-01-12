using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Snacka.Client.Services;

namespace Snacka.Client.Controls;

/// <summary>
/// Panel showing voice channel connection status and controls (camera, screen share, disconnect).
/// </summary>
public partial class VoiceConnectedPanelView : UserControl
{
    public static readonly StyledProperty<bool> IsInVoiceChannelProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsInVoiceChannel));

    public static readonly StyledProperty<bool> IsVoiceConnectedProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsVoiceConnected));

    public static readonly StyledProperty<bool> IsVoiceConnectingProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsVoiceConnecting));

    public static readonly StyledProperty<string?> VoiceConnectionStatusTextProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, string?>(nameof(VoiceConnectionStatusText));

    public static readonly StyledProperty<ChannelResponse?> CurrentVoiceChannelProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, ChannelResponse?>(nameof(CurrentVoiceChannel));

    public static readonly StyledProperty<bool> IsCameraOnProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsCameraOn));

    public static readonly StyledProperty<bool> IsScreenSharingProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsScreenSharing));

    public static readonly StyledProperty<bool> IsDrawingAllowedForViewersProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsDrawingAllowedForViewers));

    public static readonly StyledProperty<ICommand?> ToggleCameraCommandProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, ICommand?>(nameof(ToggleCameraCommand));

    public static readonly StyledProperty<ICommand?> ToggleScreenShareCommandProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, ICommand?>(nameof(ToggleScreenShareCommand));

    public static readonly StyledProperty<ICommand?> LeaveVoiceChannelCommandProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, ICommand?>(nameof(LeaveVoiceChannelCommand));

    public static readonly StyledProperty<ICommand?> ShowVoiceVideoOverlayCommandProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, ICommand?>(nameof(ShowVoiceVideoOverlayCommand));

    public static readonly StyledProperty<bool> IsVoiceInDifferentCommunityProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, bool>(nameof(IsVoiceInDifferentCommunity));

    public static readonly StyledProperty<string?> VoiceCommunityNameProperty =
        AvaloniaProperty.Register<VoiceConnectedPanelView, string?>(nameof(VoiceCommunityName));

    public VoiceConnectedPanelView()
    {
        InitializeComponent();
    }

    public bool IsInVoiceChannel
    {
        get => GetValue(IsInVoiceChannelProperty);
        set => SetValue(IsInVoiceChannelProperty, value);
    }

    public bool IsVoiceConnected
    {
        get => GetValue(IsVoiceConnectedProperty);
        set => SetValue(IsVoiceConnectedProperty, value);
    }

    public bool IsVoiceConnecting
    {
        get => GetValue(IsVoiceConnectingProperty);
        set => SetValue(IsVoiceConnectingProperty, value);
    }

    public string? VoiceConnectionStatusText
    {
        get => GetValue(VoiceConnectionStatusTextProperty);
        set => SetValue(VoiceConnectionStatusTextProperty, value);
    }

    public ChannelResponse? CurrentVoiceChannel
    {
        get => GetValue(CurrentVoiceChannelProperty);
        set => SetValue(CurrentVoiceChannelProperty, value);
    }

    public bool IsCameraOn
    {
        get => GetValue(IsCameraOnProperty);
        set => SetValue(IsCameraOnProperty, value);
    }

    public bool IsScreenSharing
    {
        get => GetValue(IsScreenSharingProperty);
        set => SetValue(IsScreenSharingProperty, value);
    }

    public bool IsDrawingAllowedForViewers
    {
        get => GetValue(IsDrawingAllowedForViewersProperty);
        set => SetValue(IsDrawingAllowedForViewersProperty, value);
    }

    public ICommand? ToggleCameraCommand
    {
        get => GetValue(ToggleCameraCommandProperty);
        set => SetValue(ToggleCameraCommandProperty, value);
    }

    public ICommand? ToggleScreenShareCommand
    {
        get => GetValue(ToggleScreenShareCommandProperty);
        set => SetValue(ToggleScreenShareCommandProperty, value);
    }

    public ICommand? LeaveVoiceChannelCommand
    {
        get => GetValue(LeaveVoiceChannelCommandProperty);
        set => SetValue(LeaveVoiceChannelCommandProperty, value);
    }

    public ICommand? ShowVoiceVideoOverlayCommand
    {
        get => GetValue(ShowVoiceVideoOverlayCommandProperty);
        set => SetValue(ShowVoiceVideoOverlayCommandProperty, value);
    }

    public bool IsVoiceInDifferentCommunity
    {
        get => GetValue(IsVoiceInDifferentCommunityProperty);
        set => SetValue(IsVoiceInDifferentCommunityProperty, value);
    }

    public string? VoiceCommunityName
    {
        get => GetValue(VoiceCommunityNameProperty);
        set => SetValue(VoiceCommunityNameProperty, value);
    }
}
