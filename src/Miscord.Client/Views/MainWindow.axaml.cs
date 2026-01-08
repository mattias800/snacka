using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ImageFilePickerProvider = SelectImageFileAsync;
            }
        };
    }

    private async Task<IStorageFile?> SelectImageFileAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select an image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp"]
                }
            ]
        });

        return files.Count > 0 ? files[0] : null;
    }
}
