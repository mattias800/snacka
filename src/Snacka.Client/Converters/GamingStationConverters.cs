using System.Globalization;
using Avalonia.Data.Converters;
using Snacka.Client.Models;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Converters;

/// <summary>
/// Converts a MyGamingStationInfo to a status string for display.
/// Shows: "Available", "In voice channel", "Screen sharing", or "Offline"
/// </summary>
public class GamingStationStatusConverter : IValueConverter
{
    public static readonly GamingStationStatusConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MyGamingStationInfo station)
        {
            if (!station.IsAvailable)
                return "Offline";
            if (station.IsScreenSharing)
                return "Screen sharing";
            if (station.IsInVoiceChannel)
                return "In voice channel";
            return "Available";
        }
        return "Unknown";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
