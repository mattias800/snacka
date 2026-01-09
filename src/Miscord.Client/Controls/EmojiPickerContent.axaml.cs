using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Miscord.Client.Controls;

/// <summary>
/// Content for emoji picker popup showing reaction emojis.
/// </summary>
public partial class EmojiPickerContent : UserControl
{
    public EmojiPickerContent()
    {
        InitializeComponent();
    }

    // Event for when an emoji is selected
    public event EventHandler<string>? EmojiSelected;

    private void OnEmojiClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string emoji)
        {
            EmojiSelected?.Invoke(this, emoji);
        }
    }
}
