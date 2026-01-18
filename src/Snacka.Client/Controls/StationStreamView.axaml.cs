using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Snacka.Client.Services;
using Snacka.Client.Services.HardwareVideo;

namespace Snacka.Client.Controls;

public partial class StationStreamView : UserControl
{
    public static readonly StyledProperty<Guid?> StationIdProperty =
        AvaloniaProperty.Register<StationStreamView, Guid?>(nameof(StationId));

    public static readonly StyledProperty<string> StationNameProperty =
        AvaloniaProperty.Register<StationStreamView, string>(nameof(StationName), "Gaming Station");

    public static readonly StyledProperty<string> ConnectionStatusProperty =
        AvaloniaProperty.Register<StationStreamView, string>(nameof(ConnectionStatus), "Disconnected");

    public static readonly StyledProperty<bool> IsConnectingProperty =
        AvaloniaProperty.Register<StationStreamView, bool>(nameof(IsConnecting));

    public static readonly StyledProperty<bool> IsViewOnlyModeProperty =
        AvaloniaProperty.Register<StationStreamView, bool>(nameof(IsViewOnlyMode), true);

    public static readonly StyledProperty<bool> IsControllerModeProperty =
        AvaloniaProperty.Register<StationStreamView, bool>(nameof(IsControllerMode));

    public static readonly StyledProperty<bool> IsFullInputModeProperty =
        AvaloniaProperty.Register<StationStreamView, bool>(nameof(IsFullInputMode));

    public static readonly StyledProperty<IHardwareVideoDecoder?> HardwareDecoderProperty =
        AvaloniaProperty.Register<StationStreamView, IHardwareVideoDecoder?>(nameof(HardwareDecoder));

    public static readonly StyledProperty<Bitmap?> VideoBitmapProperty =
        AvaloniaProperty.Register<StationStreamView, Bitmap?>(nameof(VideoBitmap));

    public static readonly StyledProperty<int> ConnectedUserCountProperty =
        AvaloniaProperty.Register<StationStreamView, int>(nameof(ConnectedUserCount));

    public static readonly StyledProperty<int> LatencyProperty =
        AvaloniaProperty.Register<StationStreamView, int>(nameof(Latency));

    public static readonly StyledProperty<string> ResolutionProperty =
        AvaloniaProperty.Register<StationStreamView, string>(nameof(Resolution), "—");

    public static readonly StyledProperty<int?> PlayerSlotProperty =
        AvaloniaProperty.Register<StationStreamView, int?>(nameof(PlayerSlot));

    public static readonly StyledProperty<ICommand?> DisconnectCommandProperty =
        AvaloniaProperty.Register<StationStreamView, ICommand?>(nameof(DisconnectCommand));

    public static readonly StyledProperty<ICommand?> ToggleFullscreenCommandProperty =
        AvaloniaProperty.Register<StationStreamView, ICommand?>(nameof(ToggleFullscreenCommand));

    // Events for input forwarding
    public event Action<StationKeyboardInput>? KeyboardInputReceived;
    public event Action<StationMouseInput>? MouseInputReceived;

    public Guid? StationId
    {
        get => GetValue(StationIdProperty);
        set => SetValue(StationIdProperty, value);
    }

    public string StationName
    {
        get => GetValue(StationNameProperty);
        set => SetValue(StationNameProperty, value);
    }

    public string ConnectionStatus
    {
        get => GetValue(ConnectionStatusProperty);
        set => SetValue(ConnectionStatusProperty, value);
    }

    public bool IsConnecting
    {
        get => GetValue(IsConnectingProperty);
        set => SetValue(IsConnectingProperty, value);
    }

    public bool IsViewOnlyMode
    {
        get => GetValue(IsViewOnlyModeProperty);
        set => SetValue(IsViewOnlyModeProperty, value);
    }

    public bool IsControllerMode
    {
        get => GetValue(IsControllerModeProperty);
        set => SetValue(IsControllerModeProperty, value);
    }

    public bool IsFullInputMode
    {
        get => GetValue(IsFullInputModeProperty);
        set => SetValue(IsFullInputModeProperty, value);
    }

    public IHardwareVideoDecoder? HardwareDecoder
    {
        get => GetValue(HardwareDecoderProperty);
        set => SetValue(HardwareDecoderProperty, value);
    }

    public Bitmap? VideoBitmap
    {
        get => GetValue(VideoBitmapProperty);
        set => SetValue(VideoBitmapProperty, value);
    }

    public int ConnectedUserCount
    {
        get => GetValue(ConnectedUserCountProperty);
        set => SetValue(ConnectedUserCountProperty, value);
    }

    public int Latency
    {
        get => GetValue(LatencyProperty);
        set => SetValue(LatencyProperty, value);
    }

    public string Resolution
    {
        get => GetValue(ResolutionProperty);
        set => SetValue(ResolutionProperty, value);
    }

    public int? PlayerSlot
    {
        get => GetValue(PlayerSlotProperty);
        set => SetValue(PlayerSlotProperty, value);
    }

    public ICommand? DisconnectCommand
    {
        get => GetValue(DisconnectCommandProperty);
        set => SetValue(DisconnectCommandProperty, value);
    }

    public ICommand? ToggleFullscreenCommand
    {
        get => GetValue(ToggleFullscreenCommandProperty);
        set => SetValue(ToggleFullscreenCommandProperty, value);
    }

    public StationStreamView()
    {
        InitializeComponent();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsFullInputMode || StationId is null) return;

        var input = new StationKeyboardInput(
            StationId.Value,
            e.Key.ToString(),
            IsDown: true,
            Ctrl: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            Alt: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            Meta: e.KeyModifiers.HasFlag(KeyModifiers.Meta)
        );

        KeyboardInputReceived?.Invoke(input);
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!IsFullInputMode || StationId is null) return;

        var input = new StationKeyboardInput(
            StationId.Value,
            e.Key.ToString(),
            IsDown: false,
            Ctrl: e.KeyModifiers.HasFlag(KeyModifiers.Control),
            Alt: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            Meta: e.KeyModifiers.HasFlag(KeyModifiers.Meta)
        );

        KeyboardInputReceived?.Invoke(input);
        e.Handled = true;
    }

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsFullInputMode || StationId is null) return;

        var point = e.GetCurrentPoint(sender as Visual);
        var position = GetNormalizedPosition(point.Position, sender as Visual);

        var input = new StationMouseInput(
            StationId.Value,
            StationMouseInputType.Down,
            position.X,
            position.Y,
            Button: (int)point.Properties.PointerUpdateKind,
            DeltaX: null,
            DeltaY: null
        );

        MouseInputReceived?.Invoke(input);
        e.Handled = true;
    }

    private void OnVideoPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsFullInputMode || StationId is null) return;

        var point = e.GetCurrentPoint(sender as Visual);
        var position = GetNormalizedPosition(point.Position, sender as Visual);

        var input = new StationMouseInput(
            StationId.Value,
            StationMouseInputType.Move,
            position.X,
            position.Y,
            Button: null,
            DeltaX: null,
            DeltaY: null
        );

        MouseInputReceived?.Invoke(input);
    }

    private void OnVideoPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsFullInputMode || StationId is null) return;

        var point = e.GetCurrentPoint(sender as Visual);
        var position = GetNormalizedPosition(point.Position, sender as Visual);

        var input = new StationMouseInput(
            StationId.Value,
            StationMouseInputType.Up,
            position.X,
            position.Y,
            Button: (int)point.Properties.PointerUpdateKind,
            DeltaX: null,
            DeltaY: null
        );

        MouseInputReceived?.Invoke(input);
        e.Handled = true;
    }

    private void OnVideoPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsFullInputMode || StationId is null) return;

        var point = e.GetCurrentPoint(sender as Visual);
        var position = GetNormalizedPosition(point.Position, sender as Visual);

        var input = new StationMouseInput(
            StationId.Value,
            StationMouseInputType.Wheel,
            position.X,
            position.Y,
            Button: null,
            DeltaX: e.Delta.X,
            DeltaY: e.Delta.Y
        );

        MouseInputReceived?.Invoke(input);
        e.Handled = true;
    }

    /// <summary>
    /// Normalize mouse position to 0-1 range based on the video area bounds.
    /// </summary>
    private static Point GetNormalizedPosition(Point position, Visual? visual)
    {
        if (visual is null) return new Point(0, 0);

        var bounds = visual.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new Point(0, 0);

        return new Point(
            Math.Clamp(position.X / bounds.Width, 0, 1),
            Math.Clamp(position.Y / bounds.Height, 0, 1)
        );
    }
}

/// <summary>
/// Converts latency value to a color (green/yellow/red).
/// </summary>
public class LatencyColorConverter : IMultiValueConverter
{
    public static readonly LatencyColorConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count > 0 && values[0] is int latency)
        {
            return latency switch
            {
                < 50 => new SolidColorBrush(Color.Parse("#22c55e")), // Green
                < 100 => new SolidColorBrush(Color.Parse("#f59e0b")), // Amber
                _ => new SolidColorBrush(Color.Parse("#ef4444")) // Red
            };
        }
        return new SolidColorBrush(Color.Parse("#6b7280")); // Gray
    }
}
