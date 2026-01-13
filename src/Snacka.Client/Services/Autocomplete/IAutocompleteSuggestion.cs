namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Common interface for autocomplete suggestions (mentions, slash commands, emojis, etc.)
/// </summary>
public interface IAutocompleteSuggestion
{
    /// <summary>
    /// Primary display text (e.g., "@john" or "/gif")
    /// </summary>
    string DisplayText { get; }

    /// <summary>
    /// Secondary/description text (e.g., "Search and send a GIF")
    /// </summary>
    string? SecondaryText { get; }

    /// <summary>
    /// Icon text for the avatar circle (e.g., first letter or "/")
    /// </summary>
    string? IconText { get; }
}
