using System.Windows.Input;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly Services.ISettingsStore _settingsStore;
    private readonly Services.IAudioDeviceService _audioDeviceService;
    private readonly Services.IVideoDeviceService _videoDeviceService;

    private object? _currentPage;
    private string _selectedCategory = "Voice & Video";

    public SettingsViewModel(
        Action onClose,
        Services.ISettingsStore settingsStore,
        Services.IAudioDeviceService audioDeviceService,
        Services.IVideoDeviceService videoDeviceService)
    {
        _onClose = onClose;
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _videoDeviceService = videoDeviceService;

        CloseCommand = ReactiveCommand.Create(Close);
        SelectCategoryCommand = ReactiveCommand.Create<string>(SelectCategory);

        // Initialize ViewModels
        AudioSettingsViewModel = new AudioSettingsViewModel(_settingsStore, _audioDeviceService);
        VideoSettingsViewModel = new VideoSettingsViewModel(_settingsStore, _videoDeviceService);

        // Start with Voice & Video page
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
    public VideoSettingsViewModel VideoSettingsViewModel { get; }

    public ICommand CloseCommand { get; }
    public ICommand SelectCategoryCommand { get; }

    private void Close()
    {
        // Stop any tests before closing
        _ = _audioDeviceService.StopTestAsync();
        _ = _videoDeviceService.StopTestAsync();
        _onClose();
    }

    private void SelectCategory(string category)
    {
        SelectedCategory = category;

        CurrentPage = category switch
        {
            "Voice & Video" => AudioSettingsViewModel,
            "Video" => VideoSettingsViewModel,
            _ => AudioSettingsViewModel
        };
    }
}
