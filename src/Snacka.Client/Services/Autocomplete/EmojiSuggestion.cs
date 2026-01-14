namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Represents an emoji suggestion in the autocomplete popup.
/// Shows the emoji character as the icon and the shortcode as the display text.
/// </summary>
public class EmojiSuggestion : IAutocompleteSuggestion
{
    public EmojiSuggestion(string shortcode, string emoji)
    {
        Shortcode = shortcode;
        Emoji = emoji;
    }

    /// <summary>
    /// The shortcode (e.g., ":+1:", ":fire:")
    /// </summary>
    public string Shortcode { get; }

    /// <summary>
    /// The emoji character (e.g., "👍", "🔥")
    /// </summary>
    public string Emoji { get; }

    /// <summary>
    /// Display text shows the shortcode (e.g., ":fire:")
    /// </summary>
    public string DisplayText => Shortcode;

    /// <summary>
    /// No secondary text needed for emojis
    /// </summary>
    public string? SecondaryText => null;

    /// <summary>
    /// Icon shows the emoji itself
    /// </summary>
    public string? IconText => Emoji;
}
