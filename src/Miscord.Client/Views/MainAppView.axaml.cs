using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Views;

public partial class MainAppView : ReactiveUserControl<MainAppViewModel>
{
    public MainAppView()
    {
        InitializeComponent();

        // Use tunneling (Preview) events to intercept Enter before AcceptsReturn processes it
        MessageInputBox.AddHandler(KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        EditMessageInputBox.AddHandler(KeyDownEvent, OnEditMessageKeyDown, RoutingStrategies.Tunnel);
        DMMessageInputBox.AddHandler(KeyDownEvent, OnDMMessageKeyDown, RoutingStrategies.Tunnel);
        EditDMMessageInputBox.AddHandler(KeyDownEvent, OnEditDMMessageKeyDown, RoutingStrategies.Tunnel);
    }

    // Called for message input TextBox (tunneling event)
    // Enter sends message, Shift+Enter inserts newline
    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Enter only = send message (mark handled to prevent newline)
            e.Handled = true;

            if (ViewModel?.SendMessageCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SendMessageCommand.Execute().Subscribe();
            }
        }
        // Shift+Enter = let AcceptsReturn handle it (inserts newline)
    }

    // Called from XAML for channel rename TextBox
    public void OnChannelRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel?.SaveChannelNameCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
        {
            ViewModel.SaveChannelNameCommand.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelEditChannelCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    // Called for message edit TextBox (tunneling event)
    // Enter saves edit, Shift+Enter inserts newline
    private void OnEditMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Enter only = save edit (mark handled to prevent newline)
            e.Handled = true;

            if (ViewModel?.SaveMessageEditCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SaveMessageEditCommand.Execute().Subscribe();
            }
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelEditMessageCommand.Execute().Subscribe();
            e.Handled = true;
        }
        // Shift+Enter = let AcceptsReturn handle it (inserts newline)
    }

    // Called when clicking a voice channel
    private void VoiceChannel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is Services.ChannelResponse channel)
        {
            // Visual feedback - darken on press
            border.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3f4248"));

            // Check if already in this voice channel
            if (ViewModel?.CurrentVoiceChannel?.Id == channel.Id)
            {
                // Already in this channel - just view it (don't rejoin)
                ViewModel.SelectedVoiceChannelForViewing = channel;
            }
            else
            {
                // Join the channel
                ViewModel?.JoinVoiceChannelCommand.Execute(channel).Subscribe();
            }
        }
    }

    // Reset background when pointer released
    private void VoiceChannel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = Avalonia.Media.Brushes.Transparent;
        }
    }

    // Called when clicking a member in the members list - opens DMs
    private void Member_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is Services.CommunityMemberResponse member)
        {
            ViewModel?.StartDMCommand.Execute(member).Subscribe();
        }
    }

    // Called for DM message input TextBox (tunneling event)
    // Enter sends message, Shift+Enter inserts newline
    private void OnDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (ViewModel?.SendDMMessageCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SendDMMessageCommand.Execute().Subscribe();
            }
        }
    }

    // Called for DM message edit TextBox (tunneling event)
    // Enter saves edit, Shift+Enter inserts newline
    private void OnEditDMMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;

            if (ViewModel?.SaveDMMessageEditCommand.CanExecute.FirstAsync().GetAwaiter().GetResult() == true)
            {
                ViewModel.SaveDMMessageEditCommand.Execute().Subscribe();
            }
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelEditDMMessageCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    // Called when clicking the Watch button on a screen share
    private async void OnWatchScreenShareClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is VideoStreamViewModel stream &&
            ViewModel?.VoiceChannelContent != null)
        {
            await ViewModel.VoiceChannelContent.WatchScreenShareAsync(stream);
        }
    }

    // Called when clicking the close button on fullscreen video overlay
    private void OnCloseFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CloseFullscreen();
    }

    // Called when clicking the fullscreen button on a video tile
    private void OnFullscreenButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VideoStreamViewModel stream)
        {
            ViewModel?.OpenFullscreen(stream);
        }
    }
}
