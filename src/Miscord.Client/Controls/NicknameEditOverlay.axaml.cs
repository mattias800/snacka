using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Miscord.Client.Controls;

/// <summary>
/// Modal overlay for editing user or member nickname.
/// </summary>
public partial class NicknameEditOverlay : UserControl
{
    public static readonly StyledProperty<bool> IsVisibleOverlayProperty =
        AvaloniaProperty.Register<NicknameEditOverlay, bool>(nameof(IsVisibleOverlay));

    public static readonly StyledProperty<bool> IsEditingMyNicknameProperty =
        AvaloniaProperty.Register<NicknameEditOverlay, bool>(nameof(IsEditingMyNickname));

    public static readonly StyledProperty<string?> EditingNicknameProperty =
        AvaloniaProperty.Register<NicknameEditOverlay, string?>(nameof(EditingNickname));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<NicknameEditOverlay, bool>(nameof(IsLoading));

    public static readonly StyledProperty<ICommand?> SaveNicknameCommandProperty =
        AvaloniaProperty.Register<NicknameEditOverlay, ICommand?>(nameof(SaveNicknameCommand));

    public static readonly StyledProperty<ICommand?> CancelNicknameEditCommandProperty =
        AvaloniaProperty.Register<NicknameEditOverlay, ICommand?>(nameof(CancelNicknameEditCommand));

    public NicknameEditOverlay()
    {
        InitializeComponent();
    }

    public bool IsVisibleOverlay
    {
        get => GetValue(IsVisibleOverlayProperty);
        set => SetValue(IsVisibleOverlayProperty, value);
    }

    public bool IsEditingMyNickname
    {
        get => GetValue(IsEditingMyNicknameProperty);
        set => SetValue(IsEditingMyNicknameProperty, value);
    }

    public string? EditingNickname
    {
        get => GetValue(EditingNicknameProperty);
        set => SetValue(EditingNicknameProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ICommand? SaveNicknameCommand
    {
        get => GetValue(SaveNicknameCommandProperty);
        set => SetValue(SaveNicknameCommandProperty, value);
    }

    public ICommand? CancelNicknameEditCommand
    {
        get => GetValue(CancelNicknameEditCommandProperty);
        set => SetValue(CancelNicknameEditCommandProperty, value);
    }
}
