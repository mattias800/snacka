using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Snacka.Client.Converters;

/// <summary>
/// Converts IsRead boolean to a foreground brush.
/// IsRead = false (unread) = White, IsRead = true (read) = Muted gray (same as channel text).
/// </summary>
public class IsReadToForegroundConverter : IValueConverter
{
    public static readonly IsReadToForegroundConverter Instance = new();

    private static readonly IBrush UnreadBrush = new SolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush ReadBrush = new SolidColorBrush(Color.Parse("#949ba4"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isRead)
            return isRead ? ReadBrush : UnreadBrush;
        return ReadBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
