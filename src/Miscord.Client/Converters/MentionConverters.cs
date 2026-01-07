using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Miscord.Client.Converters;

/// <summary>
/// Converts member index and selected index to a background brush for highlighting.
/// Values[0] = member (the item)
/// Values[1] = MentionSuggestions collection
/// Values[2] = SelectedMentionIndex
/// </summary>
public class MentionSelectedBackgroundConverter : IMultiValueConverter
{
    public static readonly MentionSelectedBackgroundConverter Instance = new();

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
