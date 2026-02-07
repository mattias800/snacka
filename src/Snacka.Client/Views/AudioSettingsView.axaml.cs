using Avalonia.Controls;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Views;

public partial class AudioSettingsView : UserControl
{
    public AudioSettingsView()
    {
        InitializeComponent();
    }

    private void OnDeviceDropdownOpened(object? sender, EventArgs e)
    {
        if (DataContext is AudioSettingsViewModel vm)
        {
            vm.OnDropdownOpened();
        }
    }
}
