using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Snacka.Client.Services;
using Snacka.Client.Services.Autocomplete;
using Snacka.Shared.Models;
using CommunityMemberResponse = Snacka.Client.Services.CommunityMemberResponse;

namespace Snacka.Client.Tests;

/// <summary>
/// Tests for autocomplete caret positioning.
/// When a user selects an autocomplete suggestion, the caret should be positioned
/// at the end of the inserted text (after the trailing space).
/// </summary>
public class AutocompleteCaretTests
{
    #region Unit Tests - AutocompleteManager cursor position calculation

    [Fact]
    public void Select_MentionSuggestion_ReturnsCursorPositionAfterInsertedText()
    {
        // Arrange
        var manager = new AutocompleteManager();
        var members = new List<CommunityMemberResponse>
        {
            CreateMember("bob"),
            CreateMember("alice")
        };
        manager.RegisterSource(new MentionAutocompleteSource(() => members, Guid.Empty));

        // Simulate typing "@"
        manager.HandleTextChange("@");

        // Act - select the first suggestion (bob)
        var suggestion = manager.Suggestions.First();
        var result = manager.Select(suggestion, "@");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("@bob ", result.Value.newText);
        Assert.Equal(5, result.Value.cursorPosition); // "@bob " = 5 characters
    }

    [Fact]
    public void Select_MentionSuggestion_WithFilterText_ReturnsCursorPositionAfterInsertedText()
    {
        // Arrange
        var manager = new AutocompleteManager();
        var members = new List<CommunityMemberResponse>
        {
            CreateMember("bob"),
            CreateMember("bobby")
        };
        manager.RegisterSource(new MentionAutocompleteSource(() => members, Guid.Empty));

        // Simulate typing "@bo"
        manager.HandleTextChange("@bo");

        // Act - select "bobby" (DisplayText is just the username without "@")
        var suggestion = manager.Suggestions.First(s => s.DisplayText == "bobby");
        var result = manager.Select(suggestion, "@bo");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("@bobby ", result.Value.newText);
        Assert.Equal(7, result.Value.cursorPosition); // "@bobby " = 7 characters
    }

    [Fact]
    public void Select_SlashCommand_ReturnsCursorPositionAfterInsertedText()
    {
        // Arrange
        var manager = new AutocompleteManager();
        manager.RegisterSource(new SlashCommandAutocompleteSource());

        // Simulate typing "/"
        manager.HandleTextChange("/");

        // Act - select /shrug (always available, unlike /gif which requires gifsEnabled)
        var suggestion = manager.Suggestions.First(s => s.DisplayText == "/shrug");
        var result = manager.Select(suggestion, "/");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("/shrug ", result.Value.newText);
        Assert.Equal(7, result.Value.cursorPosition); // "/shrug " = 7 characters
    }

    [Fact]
    public void Select_SlashCommand_WithFilterText_ReturnsCursorPositionAfterInsertedText()
    {
        // Arrange
        var manager = new AutocompleteManager();
        manager.RegisterSource(new SlashCommandAutocompleteSource());

        // Simulate typing "/sh" (partial match for /shrug)
        manager.HandleTextChange("/sh");

        // Act - select /shrug (always available, unlike /gif which requires gifsEnabled)
        var suggestion = manager.Suggestions.First(s => s.DisplayText == "/shrug");
        var result = manager.Select(suggestion, "/sh");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("/shrug ", result.Value.newText);
        Assert.Equal(7, result.Value.cursorPosition); // "/shrug " = 7 characters
    }

    [Fact]
    public void Select_MentionInMiddleOfText_ReturnsCursorPositionAfterInsertedText()
    {
        // Arrange
        var manager = new AutocompleteManager();
        var members = new List<CommunityMemberResponse>
        {
            CreateMember("bob")
        };
        manager.RegisterSource(new MentionAutocompleteSource(() => members, Guid.Empty));

        // Simulate typing "hello @" (mention after text)
        manager.HandleTextChange("hello @");

        // Act - select bob
        var suggestion = manager.Suggestions.First();
        var result = manager.Select(suggestion, "hello @");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("hello @bob ", result.Value.newText);
        Assert.Equal(11, result.Value.cursorPosition); // "hello @bob " = 11 characters
    }

    #endregion

    #region Integration Tests - TextBox caret positioning

    [AvaloniaFact]
    public void TextBox_SetTextAndCaretIndex_CaretIsAtCorrectPosition()
    {
        // Arrange
        var window = new Window();
        var textBox = new TextBox();
        window.Content = textBox;
        window.Show();

        // Act - set text and caret directly (simulating our fix)
        textBox.Text = "@bob ";
        textBox.CaretIndex = 5;

        // Assert
        Assert.Equal("@bob ", textBox.Text);
        Assert.Equal(5, textBox.CaretIndex);
    }

    [AvaloniaFact]
    public void TextBox_SetTextThenCaretIndex_CaretIsAtEnd()
    {
        // Arrange
        var window = new Window();
        var textBox = new TextBox();
        window.Content = textBox;
        window.Show();

        // Act - simulate the autocomplete selection flow
        textBox.Focus();
        textBox.Text = "/gif ";
        textBox.CaretIndex = 5;

        // Assert
        Assert.Equal("/gif ", textBox.Text);
        Assert.Equal(5, textBox.CaretIndex);
    }

    [AvaloniaFact]
    public void TextBox_SimulateTypingAndSelection_CaretIsAtEnd()
    {
        // Arrange
        var window = new Window();
        var textBox = new TextBox();
        window.Content = textBox;
        window.Show();
        textBox.Focus();

        // Simulate typing "@"
        textBox.Text = "@";
        textBox.CaretIndex = 1;

        // Now simulate autocomplete selection (replacing text and setting caret)
        textBox.Text = "@bob ";
        textBox.CaretIndex = 5;

        // Assert
        Assert.Equal("@bob ", textBox.Text);
        Assert.Equal(5, textBox.CaretIndex);
    }

    [AvaloniaFact]
    public void TextBox_WithBinding_SetTextViaBoundProperty_ThenSetCaret_CaretIsAtEnd()
    {
        // Arrange - create a simple view model with a bindable property
        var viewModel = new TestViewModel { Text = "@" };
        var window = new Window();
        var textBox = new TextBox();

        // Set up two-way binding
        textBox.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("Text")
        {
            Source = viewModel,
            Mode = Avalonia.Data.BindingMode.TwoWay
        });

        window.Content = textBox;
        window.Show();
        textBox.Focus();

        // Verify initial state
        Assert.Equal("@", textBox.Text);

        // Act - simulate what the ViewModel does: set the bound property
        viewModel.Text = "@bob ";

        // Then set caret directly on TextBox (what the View does)
        textBox.CaretIndex = 5;

        // Assert
        Assert.Equal("@bob ", textBox.Text);
        Assert.Equal(5, textBox.CaretIndex);
    }

    [AvaloniaFact]
    public void TextBox_WithBinding_SetTextViaBoundPropertyAndDirectly_ThenSetCaret_CaretIsAtEnd()
    {
        // Arrange - this simulates what actually happens in the app
        var viewModel = new TestViewModel { Text = "@" };
        var window = new Window();
        var textBox = new TextBox();

        // Set up two-way binding
        textBox.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("Text")
        {
            Source = viewModel,
            Mode = Avalonia.Data.BindingMode.TwoWay
        });

        window.Content = textBox;
        window.Show();
        textBox.Focus();

        // Act - simulate the full flow:
        // 1. ViewModel sets MessageInput (triggers binding)
        viewModel.Text = "@bob ";

        // 2. View also sets TextBox.Text directly (same value)
        textBox.Text = "@bob ";

        // 3. View sets CaretIndex
        textBox.CaretIndex = 5;

        // Assert
        Assert.Equal("@bob ", textBox.Text);
        Assert.Equal(5, textBox.CaretIndex);
    }

    [AvaloniaFact]
    public void TextBox_WithBinding_UseDispatcherPost_CaretIsAtEnd()
    {
        // Arrange
        var viewModel = new TestViewModel { Text = "@" };
        var window = new Window();
        var textBox = new TextBox();

        textBox.Bind(TextBox.TextProperty, new Avalonia.Data.Binding("Text")
        {
            Source = viewModel,
            Mode = Avalonia.Data.BindingMode.TwoWay
        });

        window.Content = textBox;
        window.Show();
        textBox.Focus();

        // Act - simulate the actual code path with Dispatcher.Post
        viewModel.Text = "@bob ";
        textBox.Text = "@bob ";

        // Use Dispatcher.Post with Background priority like the actual code does
        var caretSet = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (textBox.Text == "@bob ")
            {
                textBox.CaretIndex = 5;
                caretSet = true;
            }
        }, Avalonia.Threading.DispatcherPriority.Background);

        // Process pending dispatcher operations
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Assert
        Assert.True(caretSet, "Dispatcher.Post callback should have executed");
        Assert.Equal("@bob ", textBox.Text);
        Assert.Equal(5, textBox.CaretIndex);
    }

    #endregion

    #region Test ViewModel

    private class TestViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _text = string.Empty;

        public string Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    #endregion

    #region Helpers

    private static CommunityMemberResponse CreateMember(string username)
    {
        return new CommunityMemberResponse(
            UserId: Guid.NewGuid(),
            Username: username,
            DisplayName: null,
            DisplayNameOverride: null,
            EffectiveDisplayName: username,
            Avatar: null,
            IsOnline: true,
            Role: UserRole.Member,
            JoinedAt: DateTime.UtcNow
        );
    }

    #endregion
}
