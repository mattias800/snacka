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

    /// <summary>
    /// Processes slash commands in the message content.
    /// Returns the modified content with commands replaced.
    /// </summary>
    public static string ProcessContent(string content)
    {
        // Text face commands - append text (can have other content before)
        // Format: "/shrug" or "some text /shrug" or "/shrug some text"
        foreach (var cmd in BaseCommands)
        {
            if (cmd.AppendText == null) continue;

            var cmdPattern = $"/{cmd.Name}";

            // Check if message is just the command
            if (content.Equals(cmdPattern, StringComparison.OrdinalIgnoreCase) ||
                content.Equals($"{cmdPattern} ", StringComparison.OrdinalIgnoreCase))
            {
                return cmd.AppendText;
            }

            // Check if command is at start with text after: "/shrug hello" -> "hello ¯\_(ツ)_/¯"
            if (content.StartsWith($"{cmdPattern} ", StringComparison.OrdinalIgnoreCase))
            {
                var textAfter = content.Substring(cmdPattern.Length + 1).Trim();
                return $"{textAfter} {cmd.AppendText}";
            }

            // Check if command is at end: "hello /shrug" -> "hello ¯\_(ツ)_/¯"
            if (content.EndsWith($" {cmdPattern}", StringComparison.OrdinalIgnoreCase))
            {
                var textBefore = content.Substring(0, content.Length - cmdPattern.Length - 1).Trim();
                return $"{textBefore} {cmd.AppendText}";
            }
        }

        // /me command - format as action (italic)
        if (content.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
        {
            var action = content.Substring(4).Trim();
            if (!string.IsNullOrWhiteSpace(action))
            {
                return $"*{action}*";
            }
        }

        // /spoiler command - wrap in spoiler tags
        if (content.StartsWith("/spoiler ", StringComparison.OrdinalIgnoreCase))
        {
            var spoilerText = content.Substring(9).Trim();
            if (!string.IsNullOrWhiteSpace(spoilerText))
            {
                return $"||{spoilerText}||";
            }
        }

        return content;
    }
}
