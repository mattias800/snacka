using System.Globalization;
using Avalonia.Data.Converters;

namespace Miscord.Client.Converters;

/// <summary>
/// Converts an integer to a singular or plural form based on the value.
/// ConverterParameter should be in the format "singular|plural" (e.g., "reply|replies").
/// </summary>
public class PluralizeConverter : IValueConverter
{
    public static readonly PluralizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int count || parameter is not string forms)
            return string.Empty;

        var parts = forms.Split('|');
        if (parts.Length != 2)
            return string.Empty;

        return count == 1 ? parts[0] : parts[1];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
