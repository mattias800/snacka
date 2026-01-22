using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Snacka.Client.Services;

namespace Snacka.Client.Converters;

/// <summary>
/// Returns "selected" class if the conversation matches the selected conversation.
/// Parameters: [0] = Current conversation, [1] = Selected conversation
/// </summary>
public class IsSelectedConversationConverter : IMultiValueConverter
{
    public static readonly IsSelectedConversationConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return "conversation-btn";

        if (values[0] is not ConversationSummaryResponse current)
            return "conversation-btn";

        if (values[1] is not ConversationSummaryResponse selected)
            return "conversation-btn";

        return current.Id == selected.Id ? "conversation-btn selected" : "conversation-btn";
    }
}

/// <summary>
/// Returns a background brush based on whether the conversation is selected.
/// Parameters: [0] = Current conversation, [1] = Selected conversation
/// </summary>
public class IsSelectedBackgroundConverter : IMultiValueConverter
{
    public static readonly IsSelectedBackgroundConverter Instance = new();

    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#42464d"));
    private static readonly IBrush TransparentBrush = Brushes.Transparent;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return TransparentBrush;

        if (values[0] is not ConversationSummaryResponse current)
            return TransparentBrush;

        if (values[1] is not ConversationSummaryResponse selected)
            return TransparentBrush;

        return current.Id == selected.Id ? SelectedBrush : TransparentBrush;
    }
}
