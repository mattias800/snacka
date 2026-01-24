using System.Reactive;
using ReactiveUI;
using Snacka.Client.Services;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for screen sharing functionality.
/// Encapsulates screen share state, picker, and annotation management.
/// </summary>
public class ScreenShareViewModel : ReactiveObject, IDisposable
{
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly ISignalRService _signalR;
    private readonly IWebRtcService _webRtc;
    private readonly AnnotationService _annotationService;
    private readonly Guid _userId;
    private readonly string _username;
    private readonly Func<Guid?> _getCurrentChannelId;
    private readonly Action<Guid, VoiceStateUpdate>? _onLocalStateChanged;

    private bool _isScreenSharePickerOpen;
    private ScreenSharePickerViewModel? _screenSharePicker;
    private ScreenShareSettings? _currentScreenShareSettings;
    private bool _isScreenSharing;
    private ScreenAnnotationViewModel? _screenAnnotationViewModel;

    /// <summary>
    /// Raised when screen sharing starts with display sharing (monitor).
    /// Args: (settings, screenAnnotationViewModel)
    /// The parent should show the annotation overlay.
    /// </summary>
    public event Action<ScreenShareSettings, ScreenAnnotationViewModel>? ShowAnnotationOverlayRequested;

    /// <summary>
    /// Raised when screen sharing stops or when annotation overlay should be hidden.
    /// The parent should hide the annotation overlay.
    /// </summary>
    public event Action? HideAnnotationOverlayRequested;

    /// <summary>
    /// Raised when an error occurs during screen sharing.
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Creates a new ScreenShareViewModel.
    /// </summary>
    public ScreenShareViewModel(
        IScreenCaptureService screenCaptureService,
        ISignalRService signalR,
        IWebRtcService webRtc,
        AnnotationService annotationService,
        Guid userId,
        string username,
        Func<Guid?> getCurrentChannelId,
        Action<Guid, VoiceStateUpdate>? onLocalStateChanged = null)
    {
        _screenCaptureService = screenCaptureService;
        _signalR = signalR;
        _webRtc = webRtc;
        _annotationService = annotationService;
        _userId = userId;
        _username = username;
        _getCurrentChannelId = getCurrentChannelId;
        _onLocalStateChanged = onLocalStateChanged;

        ToggleScreenShareCommand = ReactiveCommand.CreateFromTask(ToggleScreenShareAsync);
    }

    #region Properties

    /// <summary>
    /// Whether the screen share picker popup is open.
    /// </summary>
    public bool IsScreenSharePickerOpen
    {
        get => _isScreenSharePickerOpen;
        set => this.RaiseAndSetIfChanged(ref _isScreenSharePickerOpen, value);
    }

    /// <summary>
    /// The screen share picker ViewModel (for selecting source).
    /// </summary>
    public ScreenSharePickerViewModel? ScreenSharePicker
    {
        get => _screenSharePicker;
        set => this.RaiseAndSetIfChanged(ref _screenSharePicker, value);
    }

    /// <summary>
    /// Whether currently screen sharing.
    /// </summary>
    public bool IsScreenSharing
    {
        get => _isScreenSharing;
        set => this.RaiseAndSetIfChanged(ref _isScreenSharing, value);
    }

    /// <summary>
    /// Current screen share settings (null when not sharing).
    /// </summary>
    public ScreenShareSettings? CurrentSettings => _currentScreenShareSettings;

    /// <summary>
    /// Whether the host allows viewers to draw on the screen share.
    /// </summary>
    public bool IsDrawingAllowedForViewers
    {
        get => _screenAnnotationViewModel?.IsDrawingAllowedForViewers ?? false;
        set
        {
            if (_screenAnnotationViewModel != null)
            {
                _screenAnnotationViewModel.IsDrawingAllowedForViewers = value;
                this.RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// The annotation ViewModel (for sharing with overlay windows).
    /// </summary>
    public ScreenAnnotationViewModel? AnnotationViewModel => _screenAnnotationViewModel;

    #endregion

    #region Commands

    /// <summary>
    /// Command to toggle screen sharing.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleScreenShareCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Toggles screen sharing on/off.
    /// If not sharing, shows the picker. If sharing, stops.
    /// </summary>
    public async Task ToggleScreenShareAsync()
    {
        var channelId = _getCurrentChannelId();
        if (channelId is null) return;

        // If already sharing, stop sharing
        if (IsScreenSharing)
        {
            await StopScreenShareAsync();
            return;
        }

        // Show screen share picker
        ScreenSharePicker = new ScreenSharePickerViewModel(_screenCaptureService, async settings =>
        {
            IsScreenSharePickerOpen = false;
            ScreenSharePicker = null;

            if (settings != null)
            {
                await StartScreenShareWithSettingsAsync(settings);
            }
        });
        IsScreenSharePickerOpen = true;
    }

    /// <summary>
    /// Starts screen sharing with the specified settings.
    /// </summary>
    public async Task StartScreenShareWithSettingsAsync(ScreenShareSettings settings)
    {
        var channelId = _getCurrentChannelId();
        if (channelId is null) return;

        try
        {
            // Update local state BEFORE starting capture so the video stream exists
            // when HardwarePreviewReady fires (otherwise the hardware decoder is dropped)
            IsScreenSharing = true;
            _currentScreenShareSettings = settings;

            // Notify for immediate UI feedback
            var state = new VoiceStateUpdate(IsScreenSharing: true, ScreenShareHasAudio: settings.IncludeAudio, IsCameraOn: false);
            _onLocalStateChanged?.Invoke(_userId, state);

            // Now start the capture (hardware decoder will find the video stream)
            await _webRtc.SetScreenSharingAsync(true, settings);

            // Notify server
            await _signalR.UpdateVoiceStateAsync(channelId.Value, state);

            // Show annotation overlay for monitor (display) sharing only
            if (settings.Source.Type == ScreenCaptureSourceType.Display)
            {
                CreateAnnotationViewModel(channelId.Value);
                if (_screenAnnotationViewModel != null)
                {
                    ShowAnnotationOverlayRequested?.Invoke(settings, _screenAnnotationViewModel);
                }
            }
        }
        catch (Exception ex)
        {
            // Screen share start failure - reset state
            IsScreenSharing = false;
            _currentScreenShareSettings = null;
            ErrorOccurred?.Invoke($"Failed to start screen share: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops screen sharing.
    /// </summary>
    public async Task StopScreenShareAsync()
    {
        var channelId = _getCurrentChannelId();
        if (channelId is null) return;

        try
        {
            // Hide annotation overlay first
            CleanupAnnotationViewModel();
            HideAnnotationOverlayRequested?.Invoke();

            await _webRtc.SetScreenSharingAsync(false);
            IsScreenSharing = false;
            _currentScreenShareSettings = null;

            await _signalR.UpdateVoiceStateAsync(channelId.Value, new VoiceStateUpdate(IsScreenSharing: false, ScreenShareHasAudio: false));

            // Notify for immediate UI feedback
            var state = new VoiceStateUpdate(IsScreenSharing: false, ScreenShareHasAudio: false);
            _onLocalStateChanged?.Invoke(_userId, state);

            // Clear annotations for this screen share
            _annotationService.OnScreenShareEnded(_userId);
        }
        catch
        {
            // Screen share stop failure - ignore
        }
    }

    /// <summary>
    /// Forces stop screen sharing (used when leaving voice channel).
    /// </summary>
    public void ForceStop()
    {
        if (IsScreenSharing)
        {
            CleanupAnnotationViewModel();
            HideAnnotationOverlayRequested?.Invoke();
            IsScreenSharing = false;
            _currentScreenShareSettings = null;
            _annotationService.OnScreenShareEnded(_userId);
        }
    }

    /// <summary>
    /// Called when the annotation toolbar close button is clicked.
    /// Stops screen sharing.
    /// </summary>
    public void OnAnnotationToolbarCloseRequested()
    {
        _ = StopScreenShareAsync();
    }

    /// <summary>
    /// Starts screen sharing from a gaming station command (no picker).
    /// </summary>
    public async Task StartFromStationCommandAsync()
    {
        var channelId = _getCurrentChannelId();
        if (channelId is null) return;

        var displays = _screenCaptureService.GetDisplays();
        if (displays.Count == 0) return;

        // Use first display with gaming-optimized settings
        var settings = new ScreenShareSettings(
            Source: displays[0],
            Resolution: ScreenShareResolution.HD1080,
            Framerate: ScreenShareFramerate.Fps60,
            Quality: ScreenShareQuality.Gaming,
            IncludeAudio: false
        );

        await StartScreenShareWithSettingsAsync(settings);
    }

    private void CreateAnnotationViewModel(Guid channelId)
    {
        _screenAnnotationViewModel = new ScreenAnnotationViewModel(
            _annotationService,
            channelId,
            _userId,
            _username);
        this.RaisePropertyChanged(nameof(IsDrawingAllowedForViewers));
        this.RaisePropertyChanged(nameof(AnnotationViewModel));
    }

    private void CleanupAnnotationViewModel()
    {
        if (_screenAnnotationViewModel != null)
        {
            _screenAnnotationViewModel.Cleanup();
            _screenAnnotationViewModel = null;
            this.RaisePropertyChanged(nameof(IsDrawingAllowedForViewers));
            this.RaisePropertyChanged(nameof(AnnotationViewModel));
        }
    }

    #endregion

    public void Dispose()
    {
        CleanupAnnotationViewModel();
        ToggleScreenShareCommand.Dispose();
    }
}
