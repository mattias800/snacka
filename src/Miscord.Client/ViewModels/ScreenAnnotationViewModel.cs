using System.Collections.ObjectModel;
using Avalonia.Threading;
using Miscord.Client.Services;
using Miscord.Shared.Models;
using ReactiveUI;

namespace Miscord.Client.ViewModels;

/// <summary>
/// ViewModel for the screen annotation overlay that appears on the sharer's monitor.
/// Manages drawing state and syncs annotations with other viewers.
/// </summary>
public class ScreenAnnotationViewModel : ViewModelBase
{
    private readonly AnnotationService _annotationService;
    private readonly Guid _channelId;
    private readonly Guid _sharerId; // The local user (sharer)
    private readonly string _sharerUsername;

    private bool _isDrawModeEnabled;
    private string _currentColor = "#FF0000";
    private ObservableCollection<DrawingStroke> _strokes = new();

    public ScreenAnnotationViewModel(
        AnnotationService annotationService,
        Guid channelId,
        Guid sharerId,
        string sharerUsername)
    {
        _annotationService = annotationService;
        _channelId = channelId;
        _sharerId = sharerId;
        _sharerUsername = sharerUsername;

        // Subscribe to annotation events
        _annotationService.StrokeAdded += OnStrokeAdded;
        _annotationService.StrokesCleared += OnStrokesCleared;

        // Load any existing strokes
        foreach (var stroke in _annotationService.GetStrokes(_sharerId))
        {
            _strokes.Add(stroke);
        }
    }

    public bool IsDrawModeEnabled
    {
        get => _isDrawModeEnabled;
        set => this.RaiseAndSetIfChanged(ref _isDrawModeEnabled, value);
    }

    public string CurrentColor
    {
        get => _currentColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentColor, value);
            _annotationService.CurrentColor = value;
        }
    }

    public ObservableCollection<DrawingStroke> Strokes => _strokes;

    public string[] AvailableColors => AnnotationService.AvailableColors;

    private void OnStrokeAdded(Guid sharerId, DrawingStroke stroke)
    {
        // Only handle strokes for our screen share
        if (sharerId != _sharerId) return;

        Dispatcher.UIThread.Post(() =>
        {
            _strokes.Add(stroke);
            this.RaisePropertyChanged(nameof(Strokes));
        });
    }

    private void OnStrokesCleared(Guid sharerId)
    {
        if (sharerId != _sharerId) return;

        Dispatcher.UIThread.Post(() =>
        {
            _strokes.Clear();
            this.RaisePropertyChanged(nameof(Strokes));
        });
    }

    public async Task AddStrokeAsync(DrawingStroke stroke)
    {
        await _annotationService.AddStrokeAsync(_channelId, _sharerId, stroke);
    }

    public async Task ClearStrokesAsync()
    {
        await _annotationService.ClearStrokesAsync(_channelId, _sharerId);
    }

    public Guid SharerId => _sharerId;
    public string SharerUsername => _sharerUsername;

    public void Cleanup()
    {
        _annotationService.StrokeAdded -= OnStrokeAdded;
        _annotationService.StrokesCleared -= OnStrokesCleared;
    }
}
