namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Autocomplete source for @ mentions.
/// Triggered by '@' after whitespace or at message start.
/// </summary>
public class MentionAutocompleteSource : IAutocompleteSource
{
    private readonly Func<IEnumerable<CommunityMemberResponse>> _getMembersFunc;
    private readonly Guid _currentUserId;

    public MentionAutocompleteSource(Func<IEnumerable<CommunityMemberResponse>> getMembersFunc, Guid currentUserId)
    {
        _getMembersFunc = getMembersFunc;
        _currentUserId = currentUserId;
    }

    public char TriggerCharacter => '@';

    public bool IsValidTriggerPosition(string text, int triggerIndex)
    {
        // Valid if at start or preceded by whitespace
        return triggerIndex == 0 || char.IsWhiteSpace(text[triggerIndex - 1]);
    }

    public IEnumerable<IAutocompleteSuggestion> GetSuggestions(string filterText)
    {
        return _getMembersFunc()
            .Where(m => m.UserId != _currentUserId)
            .Where(m => string.IsNullOrEmpty(filterText) ||
                        m.Username.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(m => new MentionSuggestion(m));
    }

    public string GetInsertText(IAutocompleteSuggestion suggestion)
    {
        if (suggestion is MentionSuggestion mention)
        {
            return $"@{mention.Member.Username} ";
        }
        return string.Empty;
    }

    public bool TryExecuteCommand(IAutocompleteSuggestion suggestion, out object? commandData)
    {
        // Mentions don't execute commands, they just insert text
        commandData = null;
        return false;
    }
}
