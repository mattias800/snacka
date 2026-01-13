namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Data source for autocomplete suggestions.
/// Each source handles a specific trigger character (e.g., @ for mentions, / for commands).
/// </summary>
public interface IAutocompleteSource
{
    /// <summary>
    /// The character that triggers this autocomplete (e.g., '@', '/', ':')
    /// </summary>
    char TriggerCharacter { get; }

    /// <summary>
    /// Checks if the trigger character at the given index is in a valid position.
    /// For example, @ is valid after whitespace, / is only valid at position 0.
    /// </summary>
    bool IsValidTriggerPosition(string text, int triggerIndex);

    /// <summary>
    /// Returns filtered suggestions based on the text after the trigger character.
    /// </summary>
    IEnumerable<IAutocompleteSuggestion> GetSuggestions(string filterText);

    /// <summary>
    /// Returns the text to insert when a suggestion is selected.
    /// For mentions: "@username "
    /// For executable commands: empty string (command is executed instead)
    /// </summary>
    string GetInsertText(IAutocompleteSuggestion suggestion);

    /// <summary>
    /// Executes the command if applicable.
    /// Returns true if a command was executed (no text insertion needed).
    /// Returns false for non-executable suggestions like mentions.
    /// </summary>
    bool TryExecuteCommand(IAutocompleteSuggestion suggestion, out object? commandData);
}
