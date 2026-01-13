namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Autocomplete source for slash commands.
/// Triggered by '/' only at the start of a message.
/// Commands are inserted as text (e.g., "/gif ") and processed when the message is sent.
/// </summary>
public class SlashCommandAutocompleteSource : IAutocompleteSource
{
    public char TriggerCharacter => '/';

    public bool IsValidTriggerPosition(string text, int triggerIndex)
    {
        // Slash commands are only valid at the very start of the message
        return triggerIndex == 0;
    }

    public IEnumerable<IAutocompleteSuggestion> GetSuggestions(string filterText)
    {
        return SlashCommandRegistry.Commands
            .Where(c => string.IsNullOrEmpty(filterText) ||
                        c.Name.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(c => new SlashCommandSuggestion(c));
    }

    public string GetInsertText(IAutocompleteSuggestion suggestion)
    {
        // Insert the command with a trailing space so user can continue typing
        if (suggestion is SlashCommandSuggestion cmd)
        {
            return $"/{cmd.Command.Name} ";
        }

        return string.Empty;
    }

    public bool TryExecuteCommand(IAutocompleteSuggestion suggestion, out object? commandData)
    {
        // Slash commands are never executed immediately - they're processed when the message is sent
        commandData = null;
        return false;
    }
}
