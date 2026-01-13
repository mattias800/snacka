using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Snacka.Client.Converters;

/// <summary>
/// Converts item index and selected index to a background brush for highlighting.
/// Values[0] = item (the suggestion)
/// Values[1] = AutocompleteSuggestions collection
/// Values[2] = SelectedAutocompleteIndex
/// </summary>
public class AutocompleteSelectedBackgroundConverter : IMultiValueConverter
{
    public static readonly AutocompleteSelectedBackgroundConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#40444b"));
    private static readonly IBrush NormalBrush = Brushes.Transparent;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3) return NormalBrush;

        var member = values[0];
        var suggestions = values[1] as System.Collections.IList;
        var selectedIndex = values[2] as int?;

        if (member == null || suggestions == null || selectedIndex == null)
            return NormalBrush;

        var itemIndex = suggestions.IndexOf(member);
        return itemIndex == selectedIndex.Value ? SelectedBrush : NormalBrush;
    }
}

/// <summary>
/// Converts HasReacted boolean to a background color for reaction buttons.
/// </summary>
public class ReactionBackgroundConverter : IValueConverter
{
    public static readonly ReactionBackgroundConverter Instance = new();

    private static readonly Color ReactedColor = Color.Parse("#5865f230"); // Discord blue with transparency
    private static readonly Color NormalColor = Color.Parse("#2f3136");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool hasReacted && hasReacted)
            return ReactedColor;
        return NormalColor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to one of two strings based on parameter.
/// Parameter format: "TrueString|FalseString"
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    public static readonly BoolToStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string paramStr)
            return null;

        var parts = paramStr.Split('|');
        if (parts.Length != 2)
            return null;

        return boolValue ? parts[0] : parts[1];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to one of two colors based on parameter.
/// Parameter format: "TrueColor|FalseColor"
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string paramStr)
            return null;

        var parts = paramStr.Split('|');
        if (parts.Length != 2)
            return null;

        var colorStr = boolValue ? parts[0] : parts[1];
        return new SolidColorBrush(Color.Parse(colorStr));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
