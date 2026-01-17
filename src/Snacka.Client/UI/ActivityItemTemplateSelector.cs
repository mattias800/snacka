using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using Snacka.Client.ViewModels;

namespace Snacka.Client.UI;

/// <summary>
/// Selects the appropriate DataTemplate for different activity types.
/// This allows each activity type to have its own specialized list item component.
/// </summary>
public class ActivityItemTemplateSelector : IDataTemplate
{
    /// <summary>
    /// Template for direct message activities.
    /// </summary>
    [Content]
    public IDataTemplate? DirectMessageTemplate { get; set; }

    /// <summary>
    /// Template for community invite activities.
    /// </summary>
    public IDataTemplate? InviteTemplate { get; set; }

    /// <summary>
    /// Default template for other activity types.
    /// </summary>
    public IDataTemplate? DefaultTemplate { get; set; }

    public Control? Build(object? param)
    {
        if (param is not ActivityItem activity)
            return null;

        var template = activity.Type switch
        {
            ActivityType.DirectMessage => DirectMessageTemplate,
            ActivityType.CommunityInvite => InviteTemplate,
            _ => DefaultTemplate
        };

        return template?.Build(param);
    }

    public bool Match(object? data) => data is ActivityItem;
}
