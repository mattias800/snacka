using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Miscord.Client.Services;

namespace Miscord.Client.Controls;

/// <summary>
/// GIF picker content showing search box and results grid.
/// </summary>
public partial class GifPickerContent : UserControl
{
    public static readonly StyledProperty<string?> GifSearchQueryProperty =
        AvaloniaProperty.Register<GifPickerContent, string?>(nameof(GifSearchQuery));

    public static readonly StyledProperty<bool> IsLoadingGifsProperty =
        AvaloniaProperty.Register<GifPickerContent, bool>(nameof(IsLoadingGifs));

    public static readonly StyledProperty<IEnumerable<GifResult>?> GifResultsProperty =
        AvaloniaProperty.Register<GifPickerContent, IEnumerable<GifResult>?>(nameof(GifResults));

    public static readonly StyledProperty<ICommand?> SearchGifsCommandProperty =
        AvaloniaProperty.Register<GifPickerContent, ICommand?>(nameof(SearchGifsCommand));

    public GifPickerContent()
    {
        InitializeComponent();
    }

    public string? GifSearchQuery
    {
        get => GetValue(GifSearchQueryProperty);
        set => SetValue(GifSearchQueryProperty, value);
    }

    public bool IsLoadingGifs
    {
        get => GetValue(IsLoadingGifsProperty);
        set => SetValue(IsLoadingGifsProperty, value);
    }

    public IEnumerable<GifResult>? GifResults
    {
        get => GetValue(GifResultsProperty);
        set => SetValue(GifResultsProperty, value);
    }

    public ICommand? SearchGifsCommand
    {
        get => GetValue(SearchGifsCommandProperty);
        set => SetValue(SearchGifsCommandProperty, value);
    }

    /// <summary>
    /// Event raised when a GIF is selected.
    /// </summary>
    public event EventHandler<GifResult>? GifSelected;

    /// <summary>
    /// Event raised when Enter is pressed in the search box.
    /// </summary>
    public event EventHandler? SearchRequested;

    private void GifSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchGifsCommand?.Execute(null);
            SearchRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void GifPreview_GifClicked(object? sender, GifResult gif)
    {
        GifSelected?.Invoke(this, gif);
    }

    /// <summary>
    /// Focuses the search box.
    /// </summary>
    public void FocusSearchBox()
    {
        GifSearchBox?.Focus();
    }
}
