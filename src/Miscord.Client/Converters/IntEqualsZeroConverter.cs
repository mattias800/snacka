using System.Globalization;
using Avalonia.Data.Converters;

namespace Miscord.Client.Converters;

/// <summary>
/// Converts an integer to a boolean indicating if it equals zero.
/// </summary>
public class IntEqualsZeroConverter : IValueConverter
{
    public static readonly IntEqualsZeroConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
            return intValue == 0;
        return true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
