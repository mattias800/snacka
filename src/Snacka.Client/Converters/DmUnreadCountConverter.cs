using System.Globalization;
using Avalonia.Data.Converters;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Converters;

/// <summary>
/// Multi-value converter that looks up the DM unread count for a user.
/// Binding[0]: UserId (Guid)
/// Binding[1]: MembersListViewModel
/// Returns the count by default, or a boolean (count > 0) if parameter is "visibility".
/// </summary>
public class DmUnreadCountConverter : IMultiValueConverter
{
    public static readonly DmUnreadCountConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 &&
            values[0] is Guid userId &&
            values[1] is MembersListViewModel viewModel)
        {
            var count = viewModel.GetDmUnreadCount(userId);

            // Return visibility boolean if parameter is "visibility"
            if (parameter is string param && param == "visibility")
                return count > 0;

            return count;
        }

        if (parameter is string p && p == "visibility")
            return false;

        return 0;
    }
}
