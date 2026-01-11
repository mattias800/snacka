using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Snacka.Client.Services;

namespace Snacka.Client.Controls;

public partial class PendingInvitesContent : UserControl
{
    public static readonly StyledProperty<ObservableCollection<CommunityInviteResponse>?> PendingInvitesProperty =
        AvaloniaProperty.Register<PendingInvitesContent, ObservableCollection<CommunityInviteResponse>?>(nameof(PendingInvites));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<PendingInvitesContent, bool>(nameof(IsLoading));

    public static readonly StyledProperty<bool> HasNoPendingInvitesProperty =
        AvaloniaProperty.Register<PendingInvitesContent, bool>(nameof(HasNoPendingInvites));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<PendingInvitesContent, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<ICommand?> AcceptInviteCommandProperty =
        AvaloniaProperty.Register<PendingInvitesContent, ICommand?>(nameof(AcceptInviteCommand));

    public static readonly StyledProperty<ICommand?> DeclineInviteCommandProperty =
        AvaloniaProperty.Register<PendingInvitesContent, ICommand?>(nameof(DeclineInviteCommand));

    public PendingInvitesContent()
    {
        InitializeComponent();
    }

    public ObservableCollection<CommunityInviteResponse>? PendingInvites
    {
        get => GetValue(PendingInvitesProperty);
        set => SetValue(PendingInvitesProperty, value);
    }

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool HasNoPendingInvites
    {
        get => GetValue(HasNoPendingInvitesProperty);
        set => SetValue(HasNoPendingInvitesProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public ICommand? AcceptInviteCommand
    {
        get => GetValue(AcceptInviteCommandProperty);
        set => SetValue(AcceptInviteCommandProperty, value);
    }

    public ICommand? DeclineInviteCommand
    {
        get => GetValue(DeclineInviteCommandProperty);
        set => SetValue(DeclineInviteCommandProperty, value);
    }
}

/// <summary>
/// Converter to get the first letter of a string for community icons.
/// </summary>
public class FirstLetterConverter : IValueConverter
{
    public static readonly FirstLetterConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
        {
            return s[0].ToString().ToUpperInvariant();
        }
        return "?";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
