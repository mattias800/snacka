using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Controls;

/// <summary>
/// Content for the quick switcher popup.
/// </summary>
public partial class QuickSwitcherContent : UserControl
{
    public static readonly StyledProperty<QuickSwitcherViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<QuickSwitcherContent, QuickSwitcherViewModel?>(nameof(ViewModel));

    private TextBox? _searchInput;

    public QuickSwitcherContent()
    {
        InitializeComponent();

        // Focus search input when control is attached
        AttachedToVisualTree += OnAttachedToVisualTree;
        KeyDown += OnKeyDown;
    }

    public QuickSwitcherViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _searchInput = this.FindControl<TextBox>("SearchInput");
        if (_searchInput is not null)
        {
            // Focus the search input after a short delay to ensure it's ready
            Dispatcher.UIThread.Post(() =>
            {
                _searchInput.Focus();
                _searchInput.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null) return;

        switch (e.Key)
        {
            case Key.Up:
                ViewModel.MoveUp();
                e.Handled = true;
                break;

            case Key.Down:
                ViewModel.MoveDown();
                e.Handled = true;
                break;

            case Key.Enter:
                ViewModel.SelectCurrent();
                e.Handled = true;
                break;

            case Key.Escape:
                ViewModel.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnResultItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: QuickSwitcherItem item } && ViewModel is not null)
        {
            ViewModel.SelectItem(item);
            e.Handled = true;
        }
    }
}
