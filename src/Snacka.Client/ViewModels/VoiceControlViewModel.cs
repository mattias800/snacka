using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using Snacka.Client.Services;
using Snacka.Client.Stores;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for voice control state (mute, deafen, speaking).
/// Encapsulates voice control logic and state, delegating to stores and services.
/// </summary>
public class VoiceControlViewModel : ReactiveObject, IDisposable
{
    private readonly IVoiceStore _voiceStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IWebRtcService _webRtc;
    private readonly ISignalRService _signalR;
    private readonly Guid _userId;
    private readonly Func<Guid?> _getCurrentChannelId;
    private readonly Action<Guid, VoiceStateUpdate>? _onLocalStateChanged;
    private readonly CompositeDisposable _subscriptions = new();

    private bool _isMuted;
    private bool _isDeafened;
    private bool _isSpeaking;
    private bool _isCameraOn;
    private bool _isScreenSharing;
    private VoiceConnectionStatus _voiceConnectionStatus = VoiceConnectionStatus.Disconnected;

    /// <summary>
    /// Creates a new VoiceControlViewModel.
    /// </summary>
    /// <param name="voiceStore">Voice state store for reactive state.</param>
    /// <param name="settingsStore">Settings store for persisting mute/deafen state.</param>
    /// <param name="webRtc">WebRTC service for applying audio/video state.</param>
    /// <param name="signalR">SignalR service for broadcasting state changes.</param>
    /// <param name="userId">Current user ID.</param>
    /// <param name="getCurrentChannelId">Function to get current voice channel ID.</param>
    /// <param name="onLocalStateChanged">Optional callback for immediate UI feedback when local state changes.</param>
    public VoiceControlViewModel(
        IVoiceStore voiceStore,
        ISettingsStore settingsStore,
        IWebRtcService webRtc,
        ISignalRService signalR,
        Guid userId,
        Func<Guid?> getCurrentChannelId,
        Action<Guid, VoiceStateUpdate>? onLocalStateChanged = null)
    {
        _voiceStore = voiceStore;
        _settingsStore = settingsStore;
        _webRtc = webRtc;
        _signalR = signalR;
        _userId = userId;
        _getCurrentChannelId = getCurrentChannelId;
        _onLocalStateChanged = onLocalStateChanged;

        // Initialize local state from settings
        _isMuted = _settingsStore.Settings.IsMuted;
        _isDeafened = _settingsStore.Settings.IsDeafened;

        // Create commands
        ToggleMuteCommand = ReactiveCommand.CreateFromTask(ToggleMuteAsync);
        ToggleDeafenCommand = ReactiveCommand.CreateFromTask(ToggleDeafenAsync);
        ToggleCameraCommand = ReactiveCommand.CreateFromTask(ToggleCameraAsync);

        // Subscribe to store state changes
        SetupStoreSubscriptions();
    }

    private void SetupStoreSubscriptions()
    {
        // Subscribe to mute state from store
        _subscriptions.Add(
            _voiceStore.IsMuted
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(muted =>
                {
                    if (_isMuted != muted)
                    {
                        _isMuted = muted;
                        this.RaisePropertyChanged(nameof(IsMuted));
                    }
                }));

        // Subscribe to deafen state from store
        _subscriptions.Add(
            _voiceStore.IsDeafened
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(deafened =>
                {
                    if (_isDeafened != deafened)
                    {
                        _isDeafened = deafened;
                        this.RaisePropertyChanged(nameof(IsDeafened));
                    }
                }));

        // Subscribe to speaking state from store
        _subscriptions.Add(
            _voiceStore.IsSpeaking
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(speaking =>
                {
                    if (_isSpeaking != speaking)
                    {
                        _isSpeaking = speaking;
                        this.RaisePropertyChanged(nameof(IsSpeaking));
                    }
                }));

        // Subscribe to camera state from store
        _subscriptions.Add(
            _voiceStore.IsCameraOn
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(cameraOn =>
                {
                    if (_isCameraOn != cameraOn)
                    {
                        _isCameraOn = cameraOn;
                        this.RaisePropertyChanged(nameof(IsCameraOn));
                    }
                }));

        // Subscribe to screen sharing state from store
        _subscriptions.Add(
            _voiceStore.IsScreenSharing
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(sharing =>
                {
                    if (_isScreenSharing != sharing)
                    {
                        _isScreenSharing = sharing;
                        this.RaisePropertyChanged(nameof(IsScreenSharing));
                    }
                }));

        // Subscribe to connection status from store
        _subscriptions.Add(
            _voiceStore.ConnectionStatus
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(status =>
                {
                    if (_voiceConnectionStatus != status)
                    {
                        _voiceConnectionStatus = status;
                        this.RaisePropertyChanged(nameof(VoiceConnectionStatus));
                        this.RaisePropertyChanged(nameof(VoiceConnectionStatusText));
                        this.RaisePropertyChanged(nameof(IsVoiceConnecting));
                        this.RaisePropertyChanged(nameof(IsVoiceConnected));
                        this.RaisePropertyChanged(nameof(IsInVoiceChannel));
                    }
                }));
    }

    #region Properties

    /// <summary>
    /// Whether the local user is muted.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            this.RaiseAndSetIfChanged(ref _isMuted, value);
        }
    }

    /// <summary>
    /// Whether the local user is deafened.
    /// </summary>
    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            if (_isDeafened == value) return;
            this.RaiseAndSetIfChanged(ref _isDeafened, value);
        }
    }

    /// <summary>
    /// Whether the local user is currently speaking.
    /// </summary>
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            this.RaiseAndSetIfChanged(ref _isSpeaking, value);
        }
    }

    /// <summary>
    /// Whether the local user's camera is on.
    /// </summary>
    public bool IsCameraOn
    {
        get => _isCameraOn;
        set
        {
            if (_isCameraOn == value) return;
            this.RaiseAndSetIfChanged(ref _isCameraOn, value);
        }
    }

    /// <summary>
    /// Whether the local user is screen sharing.
    /// </summary>
    public bool IsScreenSharing
    {
        get => _isScreenSharing;
        set
        {
            if (_isScreenSharing == value) return;
            this.RaiseAndSetIfChanged(ref _isScreenSharing, value);
        }
    }

    /// <summary>
    /// Current voice connection status.
    /// </summary>
    public VoiceConnectionStatus VoiceConnectionStatus
    {
        get => _voiceConnectionStatus;
        set
        {
            if (_voiceConnectionStatus == value) return;
            this.RaiseAndSetIfChanged(ref _voiceConnectionStatus, value);
            this.RaisePropertyChanged(nameof(VoiceConnectionStatusText));
            this.RaisePropertyChanged(nameof(IsVoiceConnecting));
            this.RaisePropertyChanged(nameof(IsVoiceConnected));
            this.RaisePropertyChanged(nameof(IsInVoiceChannel));
        }
    }

    /// <summary>
    /// Human-readable voice connection status text.
    /// </summary>
    public string VoiceConnectionStatusText => VoiceConnectionStatus switch
    {
        VoiceConnectionStatus.Connected => "Voice Connected",
        VoiceConnectionStatus.Connecting => "Connecting...",
        _ => ""
    };

    /// <summary>
    /// Whether currently connecting to a voice channel.
    /// </summary>
    public bool IsVoiceConnecting => VoiceConnectionStatus == VoiceConnectionStatus.Connecting;

    /// <summary>
    /// Whether connected to a voice channel.
    /// </summary>
    public bool IsVoiceConnected => VoiceConnectionStatus == VoiceConnectionStatus.Connected;

    /// <summary>
    /// Whether the user is in a voice channel.
    /// </summary>
    public bool IsInVoiceChannel => _getCurrentChannelId() is not null;

    #endregion

    #region Commands

    /// <summary>
    /// Command to toggle mute state.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; }

    /// <summary>
    /// Command to toggle deafen state.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleDeafenCommand { get; }

    /// <summary>
    /// Command to toggle camera.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ToggleCameraCommand { get; }

    #endregion

    #region Methods

    /// <summary>
    /// Toggles mute state. Checks server-muted state before allowing unmute.
    /// </summary>
    public async Task ToggleMuteAsync()
    {
        var channelId = _getCurrentChannelId();

        // Check if server-muted (cannot unmute if server-muted)
        if (channelId is not null && !IsMuted)
        {
            var participant = _voiceStore.GetLocalParticipant(_userId);
            if (participant?.IsServerMuted == true) return;
        }

        IsMuted = !IsMuted;

        // Persist to settings
        _settingsStore.Settings.IsMuted = IsMuted;
        _settingsStore.Save();

        // Update voice store
        _voiceStore.SetLocalMuted(IsMuted);

        // If in a voice channel, apply immediately
        if (channelId is not null)
        {
            _webRtc.SetMuted(IsMuted);
            await _signalR.UpdateVoiceStateAsync(channelId.Value, new VoiceStateUpdate(IsMuted: IsMuted));

            // Notify for immediate UI feedback
            var state = new VoiceStateUpdate(IsMuted: IsMuted);
            _onLocalStateChanged?.Invoke(_userId, state);
        }
    }

    /// <summary>
    /// Toggles deafen state. Checks server-deafened state before allowing undeafen.
    /// </summary>
    public async Task ToggleDeafenAsync()
    {
        var channelId = _getCurrentChannelId();

        // Check if server-deafened (cannot undeafen if server-deafened)
        if (channelId is not null && !IsDeafened)
        {
            var participant = _voiceStore.GetLocalParticipant(_userId);
            if (participant?.IsServerDeafened == true) return;
        }

        IsDeafened = !IsDeafened;

        // If deafening, also mute
        if (IsDeafened && !IsMuted)
        {
            IsMuted = true;
            _settingsStore.Settings.IsMuted = IsMuted;
        }

        // Persist to settings
        _settingsStore.Settings.IsDeafened = IsDeafened;
        _settingsStore.Save();

        // Update voice store
        _voiceStore.SetLocalMuted(IsMuted);
        _voiceStore.SetLocalDeafened(IsDeafened);

        // If in a voice channel, apply immediately
        if (channelId is not null)
        {
            _webRtc.SetMuted(IsMuted);
            _webRtc.SetDeafened(IsDeafened);
            await _signalR.UpdateVoiceStateAsync(channelId.Value, new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened));

            // Notify for immediate UI feedback
            var state = new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened);
            _onLocalStateChanged?.Invoke(_userId, state);
        }
    }

    /// <summary>
    /// Toggles camera on/off.
    /// </summary>
    public async Task ToggleCameraAsync()
    {
        var channelId = _getCurrentChannelId();
        if (channelId is null) return;

        try
        {
            var newState = !IsCameraOn;
            await _webRtc.SetCameraAsync(newState);
            IsCameraOn = newState;

            // Update voice store
            _voiceStore.SetLocalCameraOn(newState);

            await _signalR.UpdateVoiceStateAsync(channelId.Value, new VoiceStateUpdate(IsCameraOn: newState));

            // Notify for immediate UI feedback
            var state = new VoiceStateUpdate(IsCameraOn: newState);
            _onLocalStateChanged?.Invoke(_userId, state);
        }
        catch
        {
            // Camera toggle failure - ignore
        }
    }

    /// <summary>
    /// Sets the muted state directly (used for push-to-talk).
    /// </summary>
    public async Task SetMutedAsync(bool muted)
    {
        if (IsMuted == muted) return;

        IsMuted = muted;

        // Persist to settings
        _settingsStore.Settings.IsMuted = IsMuted;
        _settingsStore.Save();

        // Update voice store
        _voiceStore.SetLocalMuted(IsMuted);

        var channelId = _getCurrentChannelId();
        if (channelId is not null)
        {
            _webRtc.SetMuted(IsMuted);
            await _signalR.UpdateVoiceStateAsync(channelId.Value, new VoiceStateUpdate(IsMuted: IsMuted));

            var state = new VoiceStateUpdate(IsMuted: IsMuted);
            _onLocalStateChanged?.Invoke(_userId, state);
        }
    }

    /// <summary>
    /// Updates speaking state from WebRTC.
    /// </summary>
    public void UpdateSpeakingState(bool isSpeaking)
    {
        IsSpeaking = isSpeaking;
        _voiceStore.SetLocalSpeaking(isSpeaking);
    }

    /// <summary>
    /// Handles server-initiated mute/deafen state changes.
    /// </summary>
    public void HandleServerVoiceStateUpdate(bool? isServerMuted, bool? isServerDeafened)
    {
        if (isServerMuted == true && !IsMuted)
        {
            // Server muted the user, sync local state
            IsMuted = true;
            _settingsStore.Settings.IsMuted = true;
            _settingsStore.Save();
            _voiceStore.SetLocalMuted(true);
        }

        if (isServerDeafened == true && !IsDeafened)
        {
            // Server deafened the user, sync local state
            IsDeafened = true;
            IsMuted = true;
            _settingsStore.Settings.IsDeafened = true;
            _settingsStore.Settings.IsMuted = true;
            _settingsStore.Save();
            _voiceStore.SetLocalMuted(true);
            _voiceStore.SetLocalDeafened(true);
        }
    }

    /// <summary>
    /// Applies persisted mute/deafen state when joining a voice channel.
    /// </summary>
    public async Task ApplyPersistedStateAsync(Guid channelId)
    {
        _webRtc.SetMuted(IsMuted);
        _webRtc.SetDeafened(IsDeafened);

        if (IsMuted || IsDeafened)
        {
            await _signalR.UpdateVoiceStateAsync(channelId, new VoiceStateUpdate(IsMuted: IsMuted, IsDeafened: IsDeafened));
        }
    }

    /// <summary>
    /// Resets transient state when leaving a voice channel.
    /// Note: IsMuted and IsDeafened are persisted and NOT reset.
    /// </summary>
    public void ResetTransientState()
    {
        IsSpeaking = false;
        IsCameraOn = false;
        IsScreenSharing = false;

        _voiceStore.SetLocalSpeaking(false);
        _voiceStore.SetLocalCameraOn(false);
        _voiceStore.SetLocalScreenSharing(false);
    }

    #endregion

    public void Dispose()
    {
        _subscriptions.Dispose();
        ToggleMuteCommand.Dispose();
        ToggleDeafenCommand.Dispose();
        ToggleCameraCommand.Dispose();
    }
}
