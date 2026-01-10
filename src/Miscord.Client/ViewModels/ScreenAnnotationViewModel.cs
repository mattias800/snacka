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
    private bool _isDrawingAllowedForViewers;
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

    /// <summary>
    /// Whether viewers are allowed to draw on this screen share.
    /// Only the host (sharer) can change this setting.
    /// </summary>
    public bool IsDrawingAllowedForViewers
    {
        get => _isDrawingAllowedForViewers;
        set
        {
            Console.WriteLine($"ScreenAnnotationViewModel.IsDrawingAllowedForViewers setter: old={_isDrawingAllowedForViewers}, new={value}");
            if (this.RaiseAndSetIfChanged(ref _isDrawingAllowedForViewers, value) != value) return;
            // Broadcast the change to all viewers
            Console.WriteLine($"ScreenAnnotationViewModel: Broadcasting IsDrawingAllowedForViewers={value}");
            _ = SetDrawingAllowedAsync(value);
        }
    }

    private async Task SetDrawingAllowedAsync(bool isAllowed)
    {
        await _annotationService.SetDrawingAllowedAsync(_channelId, _sharerId, isAllowed);

        // Clear all drawings when disabling - useful if someone draws something inappropriate
        if (!isAllowed)
        {
            await ClearStrokesAsync();
        }
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
        Console.WriteLine($"ScreenAnnotationViewModel: OnStrokeAdded - sharerId={sharerId}, _sharerId={_sharerId}, match={sharerId == _sharerId}, strokePoints={stroke.Points.Count}");

        // Only handle strokes for our screen share
        if (sharerId != _sharerId) return;

        Dispatcher.UIThread.Post(() =>
        {
            _strokes.Add(stroke);
            Console.WriteLine($"ScreenAnnotationViewModel: Added stroke, total strokes now: {_strokes.Count}");
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

    public async Task UpdateStrokeAsync(DrawingStroke stroke)
    {
        await _annotationService.UpdateStrokeAsync(_channelId, _sharerId, stroke);
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
