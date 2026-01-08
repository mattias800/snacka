using System.Windows.Input;
using Avalonia.Platform.Storage;
using Miscord.Client.Services;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly ISettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly IVideoDeviceService _videoDeviceService;

    private object? _currentPage;
    private string _selectedCategory = "Voice & Video";

    public SettingsViewModel(
        Action onClose,
        ISettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        IVideoDeviceService videoDeviceService,
        IApiClient? apiClient = null,
        Action? onAccountDeleted = null,
        bool isServerAdmin = false,
        Func<Task<IStorageFile?>>? selectImageFile = null,
        bool gifsEnabled = false)
    {
        _onClose = onClose;
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _videoDeviceService = videoDeviceService;
        IsServerAdmin = isServerAdmin;

        CloseCommand = ReactiveCommand.Create(Close);
        SelectCategoryCommand = ReactiveCommand.Create<string>(SelectCategory);

        // Initialize ViewModels
        AudioSettingsViewModel = new AudioSettingsViewModel(_settingsStore, _audioDeviceService);
        VideoSettingsViewModel = new VideoSettingsViewModel(_settingsStore, _videoDeviceService);

        if (apiClient is not null && onAccountDeleted is not null)
        {
            AccountSettingsViewModel = new AccountSettingsViewModel(
                apiClient,
                onAccountDeleted,
                selectImageFile: selectImageFile);
        }

        if (apiClient is not null && isServerAdmin)
        {
            AdminPanelViewModel = new AdminPanelViewModel(apiClient, gifsEnabled);
        }

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
    public AccountSettingsViewModel? AccountSettingsViewModel { get; }
    public AdminPanelViewModel? AdminPanelViewModel { get; }
    public bool IsServerAdmin { get; }

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
            "My Account" => AccountSettingsViewModel,
            "Server Admin" => AdminPanelViewModel,
            _ => AudioSettingsViewModel
        };
    }
}
