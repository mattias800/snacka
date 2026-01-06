using Avalonia.Controls;
using Avalonia.Interactivity;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Views;

public partial class VoiceChannelContentView : UserControl
{
    public VoiceChannelContentView()
    {
        InitializeComponent();
    }

    private async void OnWatchButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            button.Tag is VideoStreamViewModel stream &&
            DataContext is VoiceChannelContentViewModel viewModel)
        {
            await viewModel.WatchScreenShareAsync(stream);
        }
    }
}
