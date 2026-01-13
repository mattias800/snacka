namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Represents a slash command definition.
/// </summary>
/// <param name="Name">Command name without the slash (e.g., "gif")</param>
/// <param name="Description">Human-readable description shown in autocomplete</param>
public record SlashCommand(string Name, string Description);

/// <summary>
/// Static registry of available slash commands.
/// </summary>
public static class SlashCommandRegistry
{
    public static readonly SlashCommand[] Commands =
    [
        new SlashCommand("gif", "Search and send a GIF"),
        new SlashCommand("giphy", "Search and send a GIF"),
    ];
}
