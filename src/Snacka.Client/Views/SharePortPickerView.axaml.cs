using Avalonia.Controls;
using Avalonia.Interactivity;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Views;

public partial class SharePortPickerView : UserControl
{
    public SharePortPickerView()
    {
        InitializeComponent();
    }

    private void OnDetectedPortClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DetectedPort port } && DataContext is SharePortPickerViewModel vm)
        {
            vm.SelectDetectedPort(port);
        }
    }
}
