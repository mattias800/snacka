using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Miscord.Client.Converters;

/// <summary>
/// Converts selected category string to background brush for settings menu items.
/// Returns selected color if current category matches parameter, transparent otherwise.
/// </summary>
public class SettingsCategoryBackgroundConverter : IValueConverter
{
    public static readonly SettingsCategoryBackgroundConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#42464d"));
    private static readonly IBrush UnselectedBrush = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string selectedCategory && parameter is string categoryName)
        {
            return selectedCategory == categoryName ? SelectedBrush : UnselectedBrush;
        }
        return UnselectedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts selected category string to foreground brush for settings menu items.
/// Returns white if current category matches parameter, gray otherwise.
/// </summary>
public class SettingsCategoryForegroundConverter : IValueConverter
{
    public static readonly SettingsCategoryForegroundConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush UnselectedBrush = new SolidColorBrush(Color.Parse("#b9bbbe"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string selectedCategory && parameter is string categoryName)
        {
            return selectedCategory == categoryName ? SelectedBrush : UnselectedBrush;
        }
        return UnselectedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a float (0-1) to a width for the audio level indicator.
/// </summary>
public class AudioLevelToWidthConverter : IValueConverter
{
    public static readonly AudioLevelToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float level && parameter is string maxWidthStr && double.TryParse(maxWidthStr, out var maxWidth))
        {
            return Math.Max(0, Math.Min(maxWidth, level * maxWidth));
        }
        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts IsAboveGate bool to "Open"/"Closed" status text.
/// </summary>
public class BoolToGateStatusConverter : IValueConverter
{
    public static readonly BoolToGateStatusConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isAboveGate)
        {
            return isAboveGate ? "Open" : "Closed";
        }
        return "Closed";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts IsAboveGate bool to color (green when open, yellow/orange when closed).
/// </summary>
public class BoolToGateColorConverter : IValueConverter
{
    public static readonly BoolToGateColorConverter Instance = new();

    private static readonly Color OpenColor = Color.Parse("#3ba55c");    // Green
    private static readonly Color ClosedColor = Color.Parse("#faa61a"); // Orange/Yellow

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isAboveGate)
        {
            return isAboveGate ? OpenColor : ClosedColor;
        }
        return ClosedColor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts gate threshold (0-0.5) to left margin for the threshold marker.
/// The parameter is the max width of the container.
/// </summary>
public class GateThresholdToMarginConverter : IValueConverter
{
    public static readonly GateThresholdToMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is float threshold && parameter is string maxWidthStr && double.TryParse(maxWidthStr, out var maxWidth))
        {
            // Threshold is 0-0.5, but we display 0-1 range, so multiply by 2
            var normalizedThreshold = Math.Min(1.0, threshold * 2);
            var leftMargin = normalizedThreshold * maxWidth;
            return new Avalonia.Thickness(leftMargin, 0, 0, 0);
        }
        return new Avalonia.Thickness(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts bool to foreground brush - white when true, gray when false.
/// Used for tab selection indicators.
/// </summary>
public class BoolToForegroundConverter : IValueConverter
{
    public static readonly BoolToForegroundConverter Instance = new();

    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush InactiveBrush = new SolidColorBrush(Color.Parse("#72767d"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isActive)
        {
            return isActive ? ActiveBrush : InactiveBrush;
        }
        return InactiveBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts SelectedSource to background brush for source selection items.
/// Returns highlighted background if the item is selected, dark otherwise.
/// </summary>
public class SelectedSourceBackgroundConverter : IValueConverter
{
    public static readonly SelectedSourceBackgroundConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#5865f2"));
    private static readonly IBrush UnselectedBrush = new SolidColorBrush(Color.Parse("#40444b"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // This is a simplified approach - the actual binding would need comparison
        // For now, return unselected brush; the selection handling will be done in code-behind
        return UnselectedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts selected annotation color to border brush.
/// Returns white border if color matches parameter (selected), transparent otherwise.
/// Used to highlight the currently selected color button.
/// </summary>
public class ColorToBorderConverter : IValueConverter
{
    public static readonly ColorToBorderConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush UnselectedBrush = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string selectedColor && parameter is string buttonColor)
        {
            return string.Equals(selectedColor, buttonColor, StringComparison.OrdinalIgnoreCase)
                ? SelectedBrush
                : UnselectedBrush;
        }
        return UnselectedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
