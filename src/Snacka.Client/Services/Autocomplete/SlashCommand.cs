namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Represents a slash command definition.
/// </summary>
/// <param name="Name">Command name without the slash (e.g., "gif")</param>
/// <param name="Description">Human-readable description shown in autocomplete</param>
/// <param name="AppendText">Optional text to append to the message (for text face commands)</param>
public record SlashCommand(string Name, string Description, string? AppendText = null);

/// <summary>
/// Static registry of available slash commands.
/// </summary>
public static class SlashCommandRegistry
{
    /// <summary>
    /// Commands that are always available (no API key required).
    /// </summary>
    public static readonly SlashCommand[] BaseCommands =
    [
        // Text faces
        new SlashCommand("shrug", "Append ¯\\_(ツ)_/¯", @"¯\_(ツ)_/¯"),
        new SlashCommand("tableflip", "Flip the table", "(╯°□°)╯︵ ┻━┻"),
        new SlashCommand("unflip", "Put the table back", @"┬─┬ノ( º _ ºノ)"),
        new SlashCommand("lenny", "( ͡° ͜ʖ ͡°)", "( ͡° ͜ʖ ͡°)"),

        // Formatting
        new SlashCommand("me", "Send an action message", null), // Special handling
        new SlashCommand("spoiler", "Hide text behind a spoiler", null), // Special handling
    ];

    /// <summary>
    /// Commands that require GIF API to be enabled.
    /// </summary>
    public static readonly SlashCommand[] GifCommands =
    [
        new SlashCommand("gif", "Search and send a GIF"),
        new SlashCommand("giphy", "Search and send a GIF"),
    ];

    /// <summary>
    /// Gets all available commands based on feature flags.
    /// </summary>
    public static SlashCommand[] GetCommands(bool gifsEnabled)
    {
        if (gifsEnabled)
        {
            return [..BaseCommands, ..GifCommands];
        }
        return BaseCommands;
    }
}
