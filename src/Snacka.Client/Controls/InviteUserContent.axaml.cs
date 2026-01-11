using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Snacka.Client.Services;

namespace Snacka.Client.Controls;

public partial class InviteUserContent : UserControl
{
    public static readonly StyledProperty<string?> CommunityNameProperty =
        AvaloniaProperty.Register<InviteUserContent, string?>(nameof(CommunityName));

    public static readonly StyledProperty<string?> SearchQueryProperty =
        AvaloniaProperty.Register<InviteUserContent, string?>(nameof(SearchQuery));

    public static readonly StyledProperty<bool> IsSearchingProperty =
        AvaloniaProperty.Register<InviteUserContent, bool>(nameof(IsSearching));

    public static readonly StyledProperty<ObservableCollection<UserSearchResult>?> SearchResultsProperty =
        AvaloniaProperty.Register<InviteUserContent, ObservableCollection<UserSearchResult>?>(nameof(SearchResults));

    public static readonly StyledProperty<bool> HasNoResultsProperty =
        AvaloniaProperty.Register<InviteUserContent, bool>(nameof(HasNoResults));

    public static readonly StyledProperty<string?> StatusMessageProperty =
        AvaloniaProperty.Register<InviteUserContent, string?>(nameof(StatusMessage));

    public static readonly StyledProperty<bool> IsStatusErrorProperty =
        AvaloniaProperty.Register<InviteUserContent, bool>(nameof(IsStatusError));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<InviteUserContent, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<ICommand?> InviteUserCommandProperty =
        AvaloniaProperty.Register<InviteUserContent, ICommand?>(nameof(InviteUserCommand));

    public static readonly StyledProperty<ICommand?> SearchCommandProperty =
        AvaloniaProperty.Register<InviteUserContent, ICommand?>(nameof(SearchCommand));

    public InviteUserContent()
    {
        InitializeComponent();
    }

    public string? CommunityName
    {
        get => GetValue(CommunityNameProperty);
        set => SetValue(CommunityNameProperty, value);
    }

    public string? SearchQuery
    {
        get => GetValue(SearchQueryProperty);
        set => SetValue(SearchQueryProperty, value);
    }

    public bool IsSearching
    {
        get => GetValue(IsSearchingProperty);
        set => SetValue(IsSearchingProperty, value);
    }

    public ObservableCollection<UserSearchResult>? SearchResults
    {
        get => GetValue(SearchResultsProperty);
        set => SetValue(SearchResultsProperty, value);
    }

    public bool HasNoResults
    {
        get => GetValue(HasNoResultsProperty);
        set => SetValue(HasNoResultsProperty, value);
    }

    public string? StatusMessage
    {
        get => GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public bool IsStatusError
    {
        get => GetValue(IsStatusErrorProperty);
        set => SetValue(IsStatusErrorProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public ICommand? InviteUserCommand
    {
        get => GetValue(InviteUserCommandProperty);
        set => SetValue(InviteUserCommandProperty, value);
    }

    public ICommand? SearchCommand
    {
        get => GetValue(SearchCommandProperty);
        set => SetValue(SearchCommandProperty, value);
    }

    public event EventHandler<string>? SearchRequested;

    private void OnSearchBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchCommand?.Execute(SearchQuery);
            SearchRequested?.Invoke(this, SearchQuery ?? string.Empty);
        }
    }

    public void FocusSearchBox()
    {
        var searchBox = this.FindControl<TextBox>("SearchBox");
        searchBox?.Focus();
    }
}

/// <summary>
/// Converter to change text color based on error status
/// </summary>
public class BoolToStatusColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isError)
        {
            return isError
                ? new SolidColorBrush(Color.Parse("#f23f43")) // DangerBrush
                : new SolidColorBrush(Color.Parse("#23a559")); // SuccessBrush
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
