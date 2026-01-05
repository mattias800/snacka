using System.Windows.Input;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly Services.ISettingsStore _settingsStore;
    private readonly Services.IAudioDeviceService _audioDeviceService;

    private object? _currentPage;
    private string _selectedCategory = "Voice & Video";

    public SettingsViewModel(
        Action onClose,
        Services.ISettingsStore settingsStore,
        Services.IAudioDeviceService audioDeviceService)
    {
        _onClose = onClose;
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;

        CloseCommand = ReactiveCommand.Create(Close);
        SelectCategoryCommand = ReactiveCommand.Create<string>(SelectCategory);

        // Initialize with Voice & Video page
        AudioSettingsViewModel = new AudioSettingsViewModel(_settingsStore, _audioDeviceService);
        CurrentPage = AudioSettingsViewModel;
    }

    public object? CurrentPage
    {
        get => _currentPage;
        set => this.RaiseAndSetIfChanged(ref _currentPage, value);
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set => this.RaiseAndSetIfChanged(ref _selectedCategory, value);
    }

    public AudioSettingsViewModel AudioSettingsViewModel { get; }

    public ICommand CloseCommand { get; }
    public ICommand SelectCategoryCommand { get; }

    private void Close()
    {
        // Stop any audio tests before closing
        _ = _audioDeviceService.StopTestAsync();
        _onClose();
    }

    private void SelectCategory(string category)
    {
        SelectedCategory = category;

        CurrentPage = category switch
        {
            "Voice & Video" => AudioSettingsViewModel,
            // Future categories can be added here
            _ => AudioSettingsViewModel
        };
    }
}
