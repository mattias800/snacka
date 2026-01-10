using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;
using Miscord.Client.Services;

namespace Miscord.Client.ViewModels;

public class ScreenSharePickerViewModel : ViewModelBase
{
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly Action<ScreenShareSettings?> _onComplete;

    private bool _showDisplays = true;
    private bool _showWindows;
    private bool _showApplications;
    private ScreenCaptureSource? _selectedSource;
    private ScreenShareResolution _selectedResolution;
    private ScreenShareFramerate _selectedFramerate;
    private bool _includeAudio;

    public ScreenSharePickerViewModel(IScreenCaptureService screenCaptureService, Action<ScreenShareSettings?> onComplete)
    {
        _screenCaptureService = screenCaptureService;
        _onComplete = onComplete;

        Displays = new ObservableCollection<ScreenCaptureSource>();
        Windows = new ObservableCollection<ScreenCaptureSource>();
        Applications = new ObservableCollection<ScreenCaptureSource>();

        // Default to 1080p @ 30fps (good balance for most use cases)
        _selectedResolution = ScreenShareResolution.HD1080;
        _selectedFramerate = ScreenShareFramerate.Fps30;

        ShareCommand = ReactiveCommand.Create(OnShare);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        RefreshCommand = ReactiveCommand.Create(RefreshSources);

        // Load sources
        RefreshSources();
    }

    public ObservableCollection<ScreenCaptureSource> Displays { get; }
    public ObservableCollection<ScreenCaptureSource> Windows { get; }
    public ObservableCollection<ScreenCaptureSource> Applications { get; }

    // Resolution and framerate options
    public IReadOnlyList<ScreenShareResolution> Resolutions => ScreenShareResolution.All;
    public IReadOnlyList<ScreenShareFramerate> Framerates => ScreenShareFramerate.All;

    public ScreenShareResolution SelectedResolution
    {
        get => _selectedResolution;
        set => this.RaiseAndSetIfChanged(ref _selectedResolution, value);
    }

    public ScreenShareFramerate SelectedFramerate
    {
        get => _selectedFramerate;
        set => this.RaiseAndSetIfChanged(ref _selectedFramerate, value);
    }

    public bool ShowDisplays
    {
        get => _showDisplays;
        set
        {
            this.RaiseAndSetIfChanged(ref _showDisplays, value);
            if (value)
            {
                _showWindows = false;
                _showApplications = false;
                this.RaisePropertyChanged(nameof(ShowWindows));
                this.RaisePropertyChanged(nameof(ShowApplications));
            }
        }
    }

    public bool ShowWindows
    {
        get => _showWindows;
        set
        {
            this.RaiseAndSetIfChanged(ref _showWindows, value);
            if (value)
            {
                _showDisplays = false;
                _showApplications = false;
                this.RaisePropertyChanged(nameof(ShowDisplays));
                this.RaisePropertyChanged(nameof(ShowApplications));
            }
        }
    }

    public bool ShowApplications
    {
        get => _showApplications;
        set
        {
            this.RaiseAndSetIfChanged(ref _showApplications, value);
            if (value)
            {
                _showDisplays = false;
                _showWindows = false;
                this.RaisePropertyChanged(nameof(ShowDisplays));
                this.RaisePropertyChanged(nameof(ShowWindows));
            }
        }
    }

    public ScreenCaptureSource? SelectedSource
    {
        get => _selectedSource;
        set => this.RaiseAndSetIfChanged(ref _selectedSource, value);
    }

    public bool HasWindows => Windows.Count > 0;
    public bool HasApplications => Applications.Count > 0;

    public bool IncludeAudio
    {
        get => _includeAudio;
        set => this.RaiseAndSetIfChanged(ref _includeAudio, value);
    }

    /// <summary>
    /// Whether audio capture is available on the current platform.
    /// Currently only macOS supports audio capture via ScreenCaptureKit.
    /// </summary>
    public bool CanCaptureAudio => OperatingSystem.IsMacOS();

    public ICommand ShareCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }

    private void RefreshSources()
    {
        Displays.Clear();
        Windows.Clear();
        Applications.Clear();

        foreach (var display in _screenCaptureService.GetDisplays())
        {
            Displays.Add(display);
        }

        foreach (var window in _screenCaptureService.GetWindows())
        {
            Windows.Add(window);
        }

        foreach (var app in _screenCaptureService.GetApplications())
        {
            Applications.Add(app);
        }

        // Select first display by default
        if (Displays.Count > 0)
        {
            SelectedSource = Displays[0];
        }

        this.RaisePropertyChanged(nameof(HasWindows));
        this.RaisePropertyChanged(nameof(HasApplications));
    }

    private void OnShare()
    {
        if (SelectedSource != null)
        {
            var settings = new ScreenShareSettings(
                SelectedSource,
                SelectedResolution,
                SelectedFramerate,
                IncludeAudio && CanCaptureAudio);
            _onComplete(settings);
        }
        else
        {
            _onComplete(null);
        }
    }

    private void OnCancel()
    {
        _onComplete(null);
    }
}
