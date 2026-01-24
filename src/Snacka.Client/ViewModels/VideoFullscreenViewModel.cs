using Avalonia.Threading;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Services.HardwareVideo;
using Snacka.Client.Services.WebRtc;
using Snacka.Client.Stores;
using Snacka.Shared.Models;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the fullscreen video overlay.
/// Handles fullscreen video display, GPU rendering, annotations, and controller streaming.
/// Reads current voice channel from VoiceStore (Redux-style).
/// </summary>
public class VideoFullscreenViewModel : ReactiveObject, IDisposable
{
    private readonly IWebRtcService _webRtc;
    private readonly AnnotationService _annotationService;
    private readonly IControllerStreamingService _controllerStreamingService;
    private readonly IVoiceStore _voiceStore;
    private readonly Guid _currentUserId;

    private bool _isOpen;
    private VideoStreamViewModel? _stream;
    private bool _isGpuFullscreenActive;
    private IHardwareVideoDecoder? _hardwareDecoder;
    private bool _isAnnotationEnabled;
    private bool _isDrawingAllowedByHost;
    private string _annotationColor = "#FF0000";
    private List<DrawingStroke> _currentStrokes = new();
    private bool _isKeyboardCaptureEnabled;
    private bool _isMouseCaptureEnabled;

    /// <summary>
    /// Event raised when a GPU frame is received for fullscreen rendering.
    /// </summary>
    public event Action<int, int, byte[]>? GpuFrameReceived;

    public VideoFullscreenViewModel(
        IWebRtcService webRtc,
        AnnotationService annotationService,
        IControllerStreamingService controllerStreamingService,
        IVoiceStore voiceStore,
        Guid currentUserId)
    {
        _webRtc = webRtc;
        _annotationService = annotationService;
        _controllerStreamingService = controllerStreamingService;
        _voiceStore = voiceStore;
        _currentUserId = currentUserId;

        // Subscribe to annotation events
        _annotationService.StrokeAdded += OnAnnotationStrokeAdded;
        _annotationService.StrokesCleared += OnAnnotationStrokesCleared;
        _annotationService.DrawingAllowedChanged += OnDrawingAllowedChanged;
    }

    #region Properties

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public VideoStreamViewModel? Stream
    {
        get => _stream;
        private set => this.RaiseAndSetIfChanged(ref _stream, value);
    }

    public bool IsGpuFullscreenActive
    {
        get => _isGpuFullscreenActive;
        private set => this.RaiseAndSetIfChanged(ref _isGpuFullscreenActive, value);
    }

    public IHardwareVideoDecoder? HardwareDecoder
    {
        get => _hardwareDecoder;
        private set => this.RaiseAndSetIfChanged(ref _hardwareDecoder, value);
    }

    /// <summary>
    /// Whether the fullscreen stream is from a gaming station.
    /// </summary>
    public bool IsGamingStation => Stream?.IsGamingStation == true;

    /// <summary>
    /// The machine ID of the gaming station being viewed in fullscreen.
    /// </summary>
    public string? GamingStationMachineId => Stream?.GamingStationMachineId;

    #endregion

    #region Annotation Properties

    public bool IsAnnotationEnabled
    {
        get => _isAnnotationEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAnnotationEnabled, value);
    }

    public bool IsDrawingAllowedByHost
    {
        get => _isDrawingAllowedByHost;
        private set => this.RaiseAndSetIfChanged(ref _isDrawingAllowedByHost, value);
    }

    public string AnnotationColor
    {
        get => _annotationColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _annotationColor, value);
            _annotationService.CurrentColor = value;
        }
    }

    public string[] AvailableAnnotationColors => AnnotationService.AvailableColors;

    public AnnotationService AnnotationService => _annotationService;

    public List<DrawingStroke> CurrentAnnotationStrokes => _currentStrokes;

    #endregion

    #region Input Capture Properties

    public bool IsKeyboardCaptureEnabled
    {
        get => _isKeyboardCaptureEnabled;
        set => this.RaiseAndSetIfChanged(ref _isKeyboardCaptureEnabled, value);
    }

    public bool IsMouseCaptureEnabled
    {
        get => _isMouseCaptureEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMouseCaptureEnabled, value);
    }

    #endregion

    #region Controller Streaming Properties

    /// <summary>
    /// Whether we're currently streaming controller input to anyone.
    /// </summary>
    public bool IsControllerStreaming => _controllerStreamingService.IsStreaming;

    /// <summary>
    /// The host user ID we're streaming controller to, if any.
    /// </summary>
    public Guid? ControllerStreamingHostUserId => _controllerStreamingService.StreamingHostUserId;

    /// <summary>
    /// Check if we're currently streaming controller input to a specific user.
    /// </summary>
    public bool IsStreamingControllerTo(Guid userId)
    {
        return _controllerStreamingService.IsStreaming && _controllerStreamingService.StreamingHostUserId == userId;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Opens fullscreen view for the specified video stream.
    /// </summary>
    public void Open(VideoStreamViewModel stream)
    {
        Stream = stream;
        IsOpen = true;

        // Check if stream is using hardware decoding (zero-copy GPU pipeline)
        if (stream.IsUsingHardwareDecoder)
        {
            // Hardware decoding: frames go directly to native view, no software path needed
            HardwareDecoder = stream.HardwareDecoder;
            IsGpuFullscreenActive = false;
        }
        else if (_webRtc.IsGpuRenderingAvailable)
        {
            // Software decoding with GPU rendering
            IsGpuFullscreenActive = true;
            _webRtc.Nv12VideoFrameReceived += OnNv12VideoFrameReceived;
        }
        else
        {
            // Pure software rendering (bitmap)
            IsGpuFullscreenActive = false;
        }

        // Load existing strokes for this screen share
        _currentStrokes = _annotationService.GetStrokes(stream.UserId).ToList();
        this.RaisePropertyChanged(nameof(CurrentAnnotationStrokes));

        // Check if drawing is allowed for this screen share
        IsDrawingAllowedByHost = _annotationService.IsDrawingAllowed(stream.UserId);
    }

    /// <summary>
    /// Closes the fullscreen view.
    /// </summary>
    public void Close()
    {
        // Unsubscribe from NV12 frames
        if (IsGpuFullscreenActive)
        {
            _webRtc.Nv12VideoFrameReceived -= OnNv12VideoFrameReceived;
            IsGpuFullscreenActive = false;
        }

        // Store reference to stream before clearing
        var stream = Stream;
        var decoder = stream?.HardwareDecoder;

        // Clear fullscreen hardware decoder reference first - this releases the native view
        HardwareDecoder = null;

        // Force the tile to reclaim the decoder by re-triggering its binding
        // The native view can only be embedded in one NativeControlHost at a time,
        // so we need to explicitly detach it from the fullscreen parent first.
        if (stream != null && decoder != null)
        {
            stream.HardwareDecoder = null;

            // Explicitly detach the native view from its current parent (fullscreen)
            decoder.DetachView();

            // Use a delay to ensure the native view is fully released from fullscreen
            // The NativeControlHost needs time to process the removal
            _ = Task.Run(async () =>
            {
                await Task.Delay(150); // Give native view time to be fully released
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    stream.HardwareDecoder = decoder;
                });
            });
        }

        IsOpen = false;
        Stream = null;
        IsAnnotationEnabled = false; // Disable drawing when exiting fullscreen

        // Disable gaming station input capture when exiting fullscreen
        IsKeyboardCaptureEnabled = false;
        IsMouseCaptureEnabled = false;
    }

    /// <summary>
    /// Called when a user leaves voice - close fullscreen if it was their stream.
    /// </summary>
    public void OnUserLeftVoice(Guid userId)
    {
        if (IsOpen && Stream?.UserId == userId)
        {
            Close();
        }
    }

    /// <summary>
    /// Called when a user stops screen sharing - close fullscreen if it was their stream.
    /// </summary>
    public void OnScreenShareEnded(Guid userId)
    {
        if (IsOpen && Stream?.UserId == userId)
        {
            Close();
        }
    }

    /// <summary>
    /// Toggle controller access for the current fullscreen stream.
    /// </summary>
    public async Task ToggleControllerAccessAsync()
    {
        if (Stream == null) return;
        await ToggleControllerAccessAsync(Stream);
    }

    /// <summary>
    /// Toggle controller access for a user who is sharing their screen.
    /// </summary>
    public async Task ToggleControllerAccessAsync(VideoStreamViewModel stream)
    {
        var channelId = _voiceStore.GetCurrentChannelId();
        if (channelId == null) return;
        if (stream.StreamType != VideoStreamType.ScreenShare) return;
        if (stream.UserId == _currentUserId) return;

        // Check if already streaming to this host - if so, stop
        if (IsStreamingControllerTo(stream.UserId))
        {
            await _controllerStreamingService.StopStreamingAsync();
            return;
        }

        await _controllerStreamingService.RequestAccessAsync(channelId.Value, stream.UserId);
    }

    #endregion

    #region Annotation Methods

    public async Task AddAnnotationStrokeAsync(DrawingStroke stroke)
    {
        var channelId = _voiceStore.GetCurrentChannelId();
        if (channelId == null || Stream == null) return;

        await _annotationService.AddStrokeAsync(channelId.Value, Stream.UserId, stroke);
    }

    public async Task UpdateAnnotationStrokeAsync(DrawingStroke stroke)
    {
        var channelId = _voiceStore.GetCurrentChannelId();
        if (channelId == null || Stream == null) return;

        await _annotationService.UpdateStrokeAsync(channelId.Value, Stream.UserId, stroke);
    }

    public async Task ClearAnnotationsAsync()
    {
        var channelId = _voiceStore.GetCurrentChannelId();
        if (channelId == null || Stream == null) return;

        await _annotationService.ClearStrokesAsync(channelId.Value, Stream.UserId);
    }

    #endregion

    #region Private Event Handlers

    private void OnNv12VideoFrameReceived(Guid userId, VideoStreamType streamType, int width, int height, byte[] nv12Data)
    {
        // Only render frames from the fullscreen stream
        if (Stream?.UserId == userId && Stream?.StreamType == streamType)
        {
            GpuFrameReceived?.Invoke(width, height, nv12Data);
        }
    }

    private void OnAnnotationStrokeAdded(Guid sharerId, DrawingStroke stroke)
    {
        // Only update if we're viewing this sharer's screen in fullscreen
        if (Stream?.UserId == sharerId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentStrokes = _annotationService.GetStrokes(sharerId).ToList();
                this.RaisePropertyChanged(nameof(CurrentAnnotationStrokes));
            });
        }
    }

    private void OnAnnotationStrokesCleared(Guid sharerId)
    {
        // Only update if we're viewing this sharer's screen in fullscreen
        if (Stream?.UserId == sharerId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentStrokes.Clear();
                this.RaisePropertyChanged(nameof(CurrentAnnotationStrokes));
            });
        }
    }

    private void OnDrawingAllowedChanged(Guid sharerId, bool isAllowed)
    {
        // Only update if we're viewing this sharer's screen in fullscreen
        if (Stream?.UserId == sharerId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsDrawingAllowedByHost = isAllowed;
                // If drawing was disabled by host, turn off our drawing mode
                if (!isAllowed && IsAnnotationEnabled)
                {
                    IsAnnotationEnabled = false;
                }
            });
        }
    }

    #endregion

    public void Dispose()
    {
        _annotationService.StrokeAdded -= OnAnnotationStrokeAdded;
        _annotationService.StrokesCleared -= OnAnnotationStrokesCleared;
        _annotationService.DrawingAllowedChanged -= OnDrawingAllowedChanged;

        if (IsGpuFullscreenActive)
        {
            _webRtc.Nv12VideoFrameReceived -= OnNv12VideoFrameReceived;
        }
    }
}
