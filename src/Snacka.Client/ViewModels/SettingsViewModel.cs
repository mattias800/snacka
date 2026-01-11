using System.Windows.Input;
using Avalonia.Platform.Storage;
using Snacka.Client.Services;
using ReactiveUI;

namespace Snacka.Client.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly ISettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly IVideoDeviceService _videoDeviceService;
    private readonly IControllerService? _controllerService;

    private object? _currentPage;
    private string _selectedCategory = "Voice & Video";

    public SettingsViewModel(
        Action onClose,
        ISettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        IVideoDeviceService videoDeviceService,
        IControllerService? controllerService = null,
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
        _controllerService = controllerService;
        IsServerAdmin = isServerAdmin;

        CloseCommand = ReactiveCommand.Create(Close);
        SelectCategoryCommand = ReactiveCommand.Create<string>(SelectCategory);

        // Initialize ViewModels
        AudioSettingsViewModel = new AudioSettingsViewModel(_settingsStore, _audioDeviceService);
        VideoSettingsViewModel = new VideoSettingsViewModel(_settingsStore, _videoDeviceService);

        if (_controllerService != null)
        {
            ControllerSettingsViewModel = new ControllerSettingsViewModel(_controllerService);
        }

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

        AboutSettingsViewModel = new AboutSettingsViewModel();

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
    public ControllerSettingsViewModel? ControllerSettingsViewModel { get; }
    public AccountSettingsViewModel? AccountSettingsViewModel { get; }
    public AdminPanelViewModel? AdminPanelViewModel { get; }
    public AboutSettingsViewModel AboutSettingsViewModel { get; }
    public bool IsServerAdmin { get; }
    public bool HasControllerSettings => ControllerSettingsViewModel != null;

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
            "Controllers" => ControllerSettingsViewModel,
            "My Account" => AccountSettingsViewModel,
            "Server Admin" => AdminPanelViewModel,
            "About" => AboutSettingsViewModel,
            _ => AudioSettingsViewModel
        };
    }
}
