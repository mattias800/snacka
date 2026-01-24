using System.Collections.ObjectModel;
using ReactiveUI;
using Snacka.Client.Services;

namespace Snacka.Client.ViewModels;

/// <summary>
/// ViewModel for the GIF panel that displays trending/search results.
/// This is separate from GifPickerViewModel which handles the inline /gif command preview.
/// </summary>
public class GifPanelViewModel : ReactiveObject
{
    private readonly IApiClient _apiClient;

    private string _searchQuery = string.Empty;
    private bool _isLoading;
    private string? _nextPos;

    /// <summary>
    /// Raised when user selects a GIF to send. The parent should handle actually sending the message.
    /// </summary>
    public event Action<GifResult>? GifSelected;

    public GifPanelViewModel(IApiClient apiClient)
    {
        _apiClient = apiClient;
        Results = new ObservableCollection<GifResult>();
    }

    public ObservableCollection<GifResult> Results { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool HasMore => !string.IsNullOrEmpty(_nextPos);

    /// <summary>
    /// Loads trending GIFs.
    /// </summary>
    public async Task LoadTrendingAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        Results.Clear();
        _nextPos = null;

        try
        {
            var result = await _apiClient.GetTrendingGifsAsync(24);
            if (result.Success && result.Data != null)
            {
                foreach (var gif in result.Data.Results)
                {
                    Results.Add(gif);
                }
                _nextPos = result.Data.NextPos;
                this.RaisePropertyChanged(nameof(HasMore));
            }
        }
        catch
        {
            // GIF loading failures are non-critical
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Searches for GIFs matching the current query.
    /// </summary>
    public async Task SearchAsync()
    {
        if (IsLoading || string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsLoading = true;
        Results.Clear();
        _nextPos = null;

        try
        {
            var result = await _apiClient.SearchGifsAsync(SearchQuery.Trim(), 24);
            if (result.Success && result.Data != null)
            {
                foreach (var gif in result.Data.Results)
                {
                    Results.Add(gif);
                }
                _nextPos = result.Data.NextPos;
                this.RaisePropertyChanged(nameof(HasMore));
            }
        }
        catch
        {
            // GIF search failures are non-critical
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Loads more results (pagination).
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (IsLoading || string.IsNullOrEmpty(_nextPos)) return;

        IsLoading = true;

        try
        {
            var result = string.IsNullOrWhiteSpace(SearchQuery)
                ? await _apiClient.GetTrendingGifsAsync(24, _nextPos)
                : await _apiClient.SearchGifsAsync(SearchQuery.Trim(), 24, _nextPos);

            if (result.Success && result.Data != null)
            {
                foreach (var gif in result.Data.Results)
                {
                    Results.Add(gif);
                }
                _nextPos = result.Data.NextPos;
                this.RaisePropertyChanged(nameof(HasMore));
            }
        }
        catch
        {
            // GIF loading failures are non-critical
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Called when user selects a GIF.
    /// </summary>
    public void SelectGif(GifResult gif)
    {
        GifSelected?.Invoke(gif);
    }

    /// <summary>
    /// Clears results and search query.
    /// </summary>
    public void Clear()
    {
        Results.Clear();
        SearchQuery = string.Empty;
        _nextPos = null;
        this.RaisePropertyChanged(nameof(HasMore));
    }
}
