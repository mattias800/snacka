using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Snacka.Client.Services;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Converters;

/// <summary>
/// Compares a channel ID to the current voice channel ID.
/// Returns true if they match (channel has participants to show).
/// </summary>
public class IsCurrentVoiceChannelConverter : IMultiValueConverter
{
    public static readonly IsCurrentVoiceChannelConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;

        var channelId = values[0] as Guid?;
        var currentVoiceChannelId = values[1] as Guid?;

        return channelId.HasValue && currentVoiceChannelId.HasValue && channelId == currentVoiceChannelId;
    }
}

/// <summary>
/// Gets voice participants for a specific channel from the ViewModel.
/// Usage: MultiBinding with channel Id and ViewModel.
/// </summary>
public class ChannelVoiceParticipantsConverter : IMultiValueConverter
{
    public static readonly ChannelVoiceParticipantsConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return null;

        var channelId = values[0] as Guid?;
        var viewModel = values[1] as MainAppViewModel;

        if (channelId.HasValue && viewModel is not null)
        {
            return viewModel.GetChannelVoiceParticipants(channelId.Value);
        }

        return null;
    }
}

/// <summary>
/// Returns true if the channel has any voice participants.
/// </summary>
public class HasVoiceParticipantsConverter : IMultiValueConverter
{
    public static readonly HasVoiceParticipantsConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;

        var channelId = values[0] as Guid?;
        var viewModel = values[1] as MainAppViewModel;

        if (channelId.HasValue && viewModel is not null)
        {
            var participants = viewModel.GetChannelVoiceParticipants(channelId.Value);
            return participants.Count > 0;
        }

        return false;
    }
}

/// <summary>
/// Converts IsSpeaking boolean to foreground brush.
/// White when speaking, gray when not.
/// </summary>
public class SpeakingForegroundConverter : IValueConverter
{
    public static readonly SpeakingForegroundConverter Instance = new();

    private static readonly IBrush SpeakingBrush = new SolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush NotSpeakingBrush = new SolidColorBrush(Color.Parse("#8e9297"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? SpeakingBrush : NotSpeakingBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts IsServerMuted boolean to menu item text.
/// Returns "Remove Server Mute" when true, "Server Mute" when false.
/// </summary>
public class ServerMuteTextConverter : IValueConverter
{
    public static readonly ServerMuteTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Remove Server Mute" : "Server Mute";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts IsServerDeafened boolean to menu item text.
/// Returns "Remove Server Deafen" when true, "Server Deafen" when false.
/// </summary>
public class ServerDeafenTextConverter : IValueConverter
{
    public static readonly ServerDeafenTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Remove Server Deafen" : "Server Deafen";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts IsServerMuted/IsServerDeafened to a color for mute/deafen icon.
/// Orange (#faa61a) for server-muted/deafened, Red (#ed4245) for self-muted/deafened.
/// </summary>
public class MuteIconColorConverter : IMultiValueConverter
{
    public static readonly MuteIconColorConverter Instance = new();

    private static readonly IBrush ServerMutedBrush = new SolidColorBrush(Color.Parse("#faa61a")); // Orange
    private static readonly IBrush SelfMutedBrush = new SolidColorBrush(Color.Parse("#ed4245")); // Red

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return SelfMutedBrush;

        var isSelfMuted = values[0] is true;
        var isServerMuted = values[1] is true;

        // Server muted takes precedence (show orange)
        return isServerMuted ? ServerMutedBrush : SelfMutedBrush;
    }
}

/// <summary>
/// Converts IsDragSource boolean to opacity.
/// Returns 0.3 when true (being dragged), 1.0 when false.
/// </summary>
public class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? 0.3 : 1.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Filters voice channels to exclude the participant's current channel.
/// Values: [0] = VoiceChannels collection, [1] = Participant's current channel ID
/// </summary>
public class OtherVoiceChannelsConverter : IMultiValueConverter
{
    public static readonly OtherVoiceChannelsConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return null;

        var voiceChannels = values[0] as IEnumerable<VoiceChannelViewModel>;
        var currentChannelId = values[1] as Guid?;

        if (voiceChannels is null) return null;

        return voiceChannels.Where(c => c.Id != currentChannelId).ToList();
    }
}

/// <summary>
/// Creates a tuple of (VoiceParticipantViewModel, VoiceChannelViewModel) for the MoveUserToChannel command.
/// Values: [0] = VoiceParticipantViewModel, [1] = VoiceChannelViewModel (target channel)
/// </summary>
public class MoveUserParameterConverter : IMultiValueConverter
{
    public static readonly MoveUserParameterConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return null;

        var participant = values[0] as VoiceParticipantViewModel;
        var targetChannel = values[1] as VoiceChannelViewModel;

        if (participant is null || targetChannel is null) return null;

        return (participant, targetChannel);
    }
}
