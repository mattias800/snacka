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
