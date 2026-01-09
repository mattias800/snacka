using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Miscord.Client.Services;
using Miscord.Client.ViewModels;
using System.Reactive.Linq;
using ReactiveUI;

namespace Miscord.Client.Controls;

/// <summary>
/// A reusable channel list component that displays text and voice channels.
/// </summary>
public partial class ChannelListView : UserControl
{
    // Text channels
    public static readonly StyledProperty<IEnumerable<ChannelResponse>?> TextChannelsProperty =
        AvaloniaProperty.Register<ChannelListView, IEnumerable<ChannelResponse>?>(nameof(TextChannels));

    // Voice channels (with participants)
    public static readonly StyledProperty<IEnumerable<VoiceChannelViewModel>?> VoiceChannelsProperty =
        AvaloniaProperty.Register<ChannelListView, IEnumerable<VoiceChannelViewModel>?>(nameof(VoiceChannels));

    // Current voice channel (for detecting if already in channel)
    public static readonly StyledProperty<ChannelResponse?> CurrentVoiceChannelProperty =
        AvaloniaProperty.Register<ChannelListView, ChannelResponse?>(nameof(CurrentVoiceChannel));

    // Whether user can manage channels (for create/edit buttons)
    public static readonly StyledProperty<bool> CanManageChannelsProperty =
        AvaloniaProperty.Register<ChannelListView, bool>(nameof(CanManageChannels), false);

    // Whether user can manage voice (for server mute/deafen)
    public static readonly StyledProperty<bool> CanManageVoiceProperty =
        AvaloniaProperty.Register<ChannelListView, bool>(nameof(CanManageVoice), false);

    // Loading state
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<ChannelListView, bool>(nameof(IsLoading), false);

    // Channel rename editor
    public static readonly StyledProperty<ChannelResponse?> EditingChannelProperty =
        AvaloniaProperty.Register<ChannelListView, ChannelResponse?>(nameof(EditingChannel));

    public static readonly StyledProperty<string?> EditingChannelNameProperty =
        AvaloniaProperty.Register<ChannelListView, string?>(nameof(EditingChannelName));

    // Commands
    public static readonly StyledProperty<ICommand?> CreateChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CreateChannelCommand));

    public static readonly StyledProperty<ICommand?> CreateVoiceChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CreateVoiceChannelCommand));

    public static readonly StyledProperty<ICommand?> SelectChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(SelectChannelCommand));

    public static readonly StyledProperty<ICommand?> StartEditChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(StartEditChannelCommand));

    public static readonly StyledProperty<ICommand?> SaveChannelNameCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(SaveChannelNameCommand));

    public static readonly StyledProperty<ICommand?> CancelEditChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(CancelEditChannelCommand));

    public static readonly StyledProperty<ICommand?> JoinVoiceChannelCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(JoinVoiceChannelCommand));

    public static readonly StyledProperty<ICommand?> ServerMuteUserCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(ServerMuteUserCommand));

    public static readonly StyledProperty<ICommand?> ServerDeafenUserCommandProperty =
        AvaloniaProperty.Register<ChannelListView, ICommand?>(nameof(ServerDeafenUserCommand));

    public ChannelListView()
    {
        InitializeComponent();
    }

    public IEnumerable<ChannelResponse>? TextChannels
    {
        get => GetValue(TextChannelsProperty);
        set => SetValue(TextChannelsProperty, value);
    }

    public IEnumerable<VoiceChannelViewModel>? VoiceChannels
    {
        get => GetValue(VoiceChannelsProperty);
        set => SetValue(VoiceChannelsProperty, value);
    }

    public ChannelResponse? CurrentVoiceChannel
    {
        get => GetValue(CurrentVoiceChannelProperty);
        set => SetValue(CurrentVoiceChannelProperty, value);
    }

    public bool CanManageChannels
    {
        get => GetValue(CanManageChannelsProperty);
        set => SetValue(CanManageChannelsProperty, value);
    }

    public bool CanManageVoice
    {
        get => GetValue(CanManageVoiceProperty);
        set => SetValue(CanManageVoiceProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ChannelResponse? EditingChannel
    {
        get => GetValue(EditingChannelProperty);
        set => SetValue(EditingChannelProperty, value);
    }

    public string? EditingChannelName
    {
        get => GetValue(EditingChannelNameProperty);
        set => SetValue(EditingChannelNameProperty, value);
    }

    public ICommand? CreateChannelCommand
    {
        get => GetValue(CreateChannelCommandProperty);
        set => SetValue(CreateChannelCommandProperty, value);
    }

    public ICommand? CreateVoiceChannelCommand
    {
        get => GetValue(CreateVoiceChannelCommandProperty);
        set => SetValue(CreateVoiceChannelCommandProperty, value);
    }

    public ICommand? SelectChannelCommand
    {
        get => GetValue(SelectChannelCommandProperty);
        set => SetValue(SelectChannelCommandProperty, value);
    }

    public ICommand? StartEditChannelCommand
    {
        get => GetValue(StartEditChannelCommandProperty);
        set => SetValue(StartEditChannelCommandProperty, value);
    }

    public ICommand? SaveChannelNameCommand
    {
        get => GetValue(SaveChannelNameCommandProperty);
        set => SetValue(SaveChannelNameCommandProperty, value);
    }

    public ICommand? CancelEditChannelCommand
    {
        get => GetValue(CancelEditChannelCommandProperty);
        set => SetValue(CancelEditChannelCommandProperty, value);
    }

    public ICommand? JoinVoiceChannelCommand
    {
        get => GetValue(JoinVoiceChannelCommandProperty);
        set => SetValue(JoinVoiceChannelCommandProperty, value);
    }

    public ICommand? ServerMuteUserCommand
    {
        get => GetValue(ServerMuteUserCommandProperty);
        set => SetValue(ServerMuteUserCommandProperty, value);
    }

    public ICommand? ServerDeafenUserCommand
    {
        get => GetValue(ServerDeafenUserCommandProperty);
        set => SetValue(ServerDeafenUserCommandProperty, value);
    }

    // Events
    public event EventHandler<ChannelResponse>? VoiceChannelClicked;
    public event EventHandler<ChannelResponse>? VoiceChannelViewRequested;

    private void VoiceChannel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is ChannelResponse channel)
        {
            // Visual feedback - darken on press
            border.Background = new SolidColorBrush(Color.Parse("#3f4248"));

            // Check if already in this voice channel
            if (CurrentVoiceChannel?.Id == channel.Id)
            {
                // Already in this channel - just view it
                VoiceChannelViewRequested?.Invoke(this, channel);
            }
            else
            {
                // Join the channel
                VoiceChannelClicked?.Invoke(this, channel);
            }
        }
    }

    private void VoiceChannel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = Brushes.Transparent;
        }
    }

    private void OnChannelRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveChannelNameCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEditChannelCommand?.Execute(null);
            e.Handled = true;
        }
    }
}
