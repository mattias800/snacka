namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Wraps a CommunityMemberResponse for display in the autocomplete popup.
/// </summary>
public class MentionSuggestion : IAutocompleteSuggestion
{
    public CommunityMemberResponse Member { get; }

    public MentionSuggestion(CommunityMemberResponse member)
    {
        Member = member;
    }

    public string DisplayText => Member.Username;
    public string? SecondaryText => null;
    public string? IconText => Member.Username.Length > 0 ? Member.Username[..1].ToUpperInvariant() : "?";
}
