using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Snacka.Client.Services;

namespace Snacka.Client.Controls;

public partial class GamingStationsView : UserControl
{
    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<GamingStationsView, bool>(nameof(IsLoading));

    public static readonly StyledProperty<bool> HasNoStationsProperty =
        AvaloniaProperty.Register<GamingStationsView, bool>(nameof(HasNoStations));

    public static readonly StyledProperty<bool> HasMyStationsProperty =
        AvaloniaProperty.Register<GamingStationsView, bool>(nameof(HasMyStations));

    public static readonly StyledProperty<bool> HasSharedStationsProperty =
        AvaloniaProperty.Register<GamingStationsView, bool>(nameof(HasSharedStations));

    public static readonly StyledProperty<bool> IsCurrentMachineRegisteredProperty =
        AvaloniaProperty.Register<GamingStationsView, bool>(nameof(IsCurrentMachineRegistered));

    public static readonly StyledProperty<ObservableCollection<GamingStationResponse>?> MyStationsProperty =
        AvaloniaProperty.Register<GamingStationsView, ObservableCollection<GamingStationResponse>?>(nameof(MyStations));

    public static readonly StyledProperty<ObservableCollection<GamingStationResponse>?> SharedStationsProperty =
        AvaloniaProperty.Register<GamingStationsView, ObservableCollection<GamingStationResponse>?>(nameof(SharedStations));

    public static readonly StyledProperty<ICommand?> RegisterStationCommandProperty =
        AvaloniaProperty.Register<GamingStationsView, ICommand?>(nameof(RegisterStationCommand));

    public static readonly StyledProperty<ICommand?> ConnectToStationCommandProperty =
        AvaloniaProperty.Register<GamingStationsView, ICommand?>(nameof(ConnectToStationCommand));

    public static readonly StyledProperty<ICommand?> ManageStationCommandProperty =
        AvaloniaProperty.Register<GamingStationsView, ICommand?>(nameof(ManageStationCommand));

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool HasNoStations
    {
        get => GetValue(HasNoStationsProperty);
        set => SetValue(HasNoStationsProperty, value);
    }

    public bool HasMyStations
    {
        get => GetValue(HasMyStationsProperty);
        set => SetValue(HasMyStationsProperty, value);
    }

    public bool HasSharedStations
    {
        get => GetValue(HasSharedStationsProperty);
        set => SetValue(HasSharedStationsProperty, value);
    }

    public bool IsCurrentMachineRegistered
    {
        get => GetValue(IsCurrentMachineRegisteredProperty);
        set => SetValue(IsCurrentMachineRegisteredProperty, value);
    }

    public ObservableCollection<GamingStationResponse>? MyStations
    {
        get => GetValue(MyStationsProperty);
        set => SetValue(MyStationsProperty, value);
    }

    public ObservableCollection<GamingStationResponse>? SharedStations
    {
        get => GetValue(SharedStationsProperty);
        set => SetValue(SharedStationsProperty, value);
    }

    public ICommand? RegisterStationCommand
    {
        get => GetValue(RegisterStationCommandProperty);
        set => SetValue(RegisterStationCommandProperty, value);
    }

    public ICommand? ConnectToStationCommand
    {
        get => GetValue(ConnectToStationCommandProperty);
        set => SetValue(ConnectToStationCommandProperty, value);
    }

    public ICommand? ManageStationCommand
    {
        get => GetValue(ManageStationCommandProperty);
        set => SetValue(ManageStationCommandProperty, value);
    }

    public GamingStationsView()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Converts StationStatus to a color for the status indicator.
/// </summary>
public class StationStatusColorConverter : IMultiValueConverter
{
    public static readonly StationStatusColorConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count > 0 && values[0] is StationStatus status)
        {
            return status switch
            {
                StationStatus.Online => new SolidColorBrush(Color.Parse("#22c55e")), // Green
                StationStatus.InUse => new SolidColorBrush(Color.Parse("#f59e0b")), // Amber
                StationStatus.Maintenance => new SolidColorBrush(Color.Parse("#ef4444")), // Red
                _ => new SolidColorBrush(Color.Parse("#6b7280")) // Gray for offline
            };
        }
        return new SolidColorBrush(Color.Parse("#6b7280"));
    }
}

/// <summary>
/// Converts StationStatus to display text.
/// </summary>
public class StationStatusTextConverter : IMultiValueConverter
{
    public static readonly StationStatusTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count > 0 && values[0] is StationStatus status)
        {
            return status switch
            {
                StationStatus.Online => "Online",
                StationStatus.InUse => "In Use",
                StationStatus.Maintenance => "Maintenance",
                _ => "Offline"
            };
        }
        return "Unknown";
    }
}

/// <summary>
/// Converts StationPermission to display text.
/// </summary>
public class StationPermissionTextConverter : IMultiValueConverter
{
    public static readonly StationPermissionTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count > 0 && values[0] is StationPermission permission)
        {
            return permission switch
            {
                StationPermission.ViewOnly => "View Only",
                StationPermission.Controller => "Controller",
                StationPermission.FullControl => "Full Control",
                StationPermission.Admin => "Admin",
                _ => "None"
            };
        }
        return "None";
    }
}

/// <summary>
/// Converts StationStatus to boolean indicating if station is online/connectable.
/// </summary>
public class StationOnlineConverter : IValueConverter
{
    public static readonly StationOnlineConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is StationStatus status)
        {
            return status == StationStatus.Online || status == StationStatus.InUse;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
