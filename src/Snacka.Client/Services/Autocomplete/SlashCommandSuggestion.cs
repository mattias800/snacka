namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Wraps a SlashCommand for display in the autocomplete popup.
/// </summary>
public class SlashCommandSuggestion : IAutocompleteSuggestion
{
    public SlashCommand Command { get; }

    public SlashCommandSuggestion(SlashCommand command)
    {
        Command = command;
    }

    public string DisplayText => $"/{Command.Name}";
    public string? SecondaryText => Command.Description;
    public string? IconText => "/";
}
