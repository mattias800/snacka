using System.Collections.ObjectModel;
using ReactiveUI;

namespace Snacka.Client.Services.Autocomplete;

/// <summary>
/// Manages autocomplete state and logic for multiple trigger sources.
/// Handles the popup lifecycle, filtering, keyboard navigation, and selection.
/// </summary>
public class AutocompleteManager : ReactiveObject
{
    private readonly List<IAutocompleteSource> _sources = new();
    private IAutocompleteSource? _activeSource;
    private int _triggerStartIndex = -1;
    private string _filterText = string.Empty;
    private string _lastProcessedText = string.Empty;

    private bool _isPopupOpen;
    private int _selectedIndex;

    /// <summary>
    /// Fired when an executable command is selected (e.g., /gif).
    /// The object contains command-specific data (e.g., SlashCommand).
    /// </summary>
    public event Action<object>? CommandExecuted;

    public AutocompleteManager()
    {
        Suggestions = new ObservableCollection<IAutocompleteSuggestion>();
    }

    /// <summary>
    /// Whether the autocomplete popup is currently open.
    /// </summary>
    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        private set => this.RaiseAndSetIfChanged(ref _isPopupOpen, value);
    }

    /// <summary>
    /// The filtered suggestions to display.
    /// </summary>
    public ObservableCollection<IAutocompleteSuggestion> Suggestions { get; }

    /// <summary>
    /// The currently selected suggestion index.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    /// <summary>
    /// Registers an autocomplete source (e.g., mentions, slash commands).
    /// </summary>
    public void RegisterSource(IAutocompleteSource source)
    {
        _sources.Add(source);
    }

    /// <summary>
    /// Handles text changes and triggers autocomplete detection.
    /// Call this from the MessageInput property setter.
    /// </summary>
    public void HandleTextChange(string newText)
    {
        // Skip if text hasn't actually changed (e.g., caret movement)
        if (newText == _lastProcessedText)
            return;

        _lastProcessedText = newText;

        if (string.IsNullOrEmpty(newText))
        {
            Close();
            return;
        }

        // Find the most recent trigger character that's in a valid position
        var foundTrigger = FindActiveTrigger(newText);

        if (foundTrigger.HasValue)
        {
            var (source, triggerIndex) = foundTrigger.Value;
            var filterText = newText.Substring(triggerIndex + 1);

            // Close if there's a space in the filter text (completion done)
            if (filterText.Contains(' '))
            {
                Close();
                return;
            }

            _activeSource = source;
            _triggerStartIndex = triggerIndex;
            _filterText = filterText;

            UpdateSuggestions();
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// Finds the most recent trigger character that's in a valid position.
    /// Returns the source and trigger index, or null if none found.
    /// </summary>
    private (IAutocompleteSource source, int triggerIndex)? FindActiveTrigger(string text)
    {
        // Scan backwards from the end to find trigger characters
        for (int i = text.Length - 1; i >= 0; i--)
        {
            // Stop at whitespace (no trigger can be before this point)
            if (char.IsWhiteSpace(text[i]))
            {
                // But check position i+1 in case trigger is right after whitespace
                if (i + 1 < text.Length)
                {
                    var afterWhitespace = text[i + 1];
                    foreach (var source in _sources)
                    {
                        if (afterWhitespace == source.TriggerCharacter &&
                            source.IsValidTriggerPosition(text, i + 1))
                        {
                            return (source, i + 1);
                        }
                    }
                }
                break;
            }

            // Check if current position is a trigger
            foreach (var source in _sources)
            {
                if (text[i] == source.TriggerCharacter &&
                    source.IsValidTriggerPosition(text, i))
                {
                    return (source, i);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Updates the suggestions based on current filter text.
    /// </summary>
    private void UpdateSuggestions()
    {
        if (_activeSource == null)
        {
            Close();
            return;
        }

        var newSuggestions = _activeSource.GetSuggestions(_filterText).ToList();

        Suggestions.Clear();
        foreach (var suggestion in newSuggestions)
        {
            Suggestions.Add(suggestion);
        }

        IsPopupOpen = Suggestions.Count > 0;
        SelectedIndex = 0;
    }

    /// <summary>
    /// Navigates to the previous suggestion (wraps around).
    /// </summary>
    public void NavigateUp()
    {
        if (Suggestions.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + Suggestions.Count) % Suggestions.Count;
    }

    /// <summary>
    /// Navigates to the next suggestion (wraps around).
    /// </summary>
    public void NavigateDown()
    {
        if (Suggestions.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % Suggestions.Count;
    }

    /// <summary>
    /// Selects the currently highlighted suggestion.
    /// Returns (newText, cursorPosition) or null if no selection.
    /// </summary>
    public (string newText, int cursorPosition)? SelectCurrent(string currentText)
    {
        if (SelectedIndex < 0 || SelectedIndex >= Suggestions.Count)
            return null;

        return Select(Suggestions[SelectedIndex], currentText);
    }

    /// <summary>
    /// Selects a specific suggestion.
    /// Returns (newText, cursorPosition) or null if the command was executed.
    /// </summary>
    public (string newText, int cursorPosition)? Select(IAutocompleteSuggestion suggestion, string currentText)
    {
        if (_activeSource == null || _triggerStartIndex < 0)
        {
            Close();
            return null;
        }

        // Check if this is an executable command
        if (_activeSource.TryExecuteCommand(suggestion, out var commandData))
        {
            Close();
            if (commandData != null)
            {
                CommandExecuted?.Invoke(commandData);
            }
            // Return empty text since command was executed
            return (string.Empty, 0);
        }

        // Get the text to insert
        var insertText = _activeSource.GetInsertText(suggestion);

        // Replace the trigger + filter with the insert text
        var beforeTrigger = currentText.Substring(0, _triggerStartIndex);
        var afterFilter = _triggerStartIndex + 1 + _filterText.Length < currentText.Length
            ? currentText.Substring(_triggerStartIndex + 1 + _filterText.Length)
            : string.Empty;

        var newText = beforeTrigger + insertText + afterFilter;
        var cursorPosition = beforeTrigger.Length + insertText.Length;

        Close();

        // Set _lastProcessedText to the new text so that when the binding
        // updates MessageInput, HandleTextChange will skip processing
        _lastProcessedText = newText;

        return (newText, cursorPosition);
    }

    /// <summary>
    /// Closes the autocomplete popup and resets state.
    /// </summary>
    public void Close()
    {
        IsPopupOpen = false;
        _activeSource = null;
        _triggerStartIndex = -1;
        _filterText = string.Empty;
        _lastProcessedText = string.Empty;
        Suggestions.Clear();
        SelectedIndex = 0;
    }
}
