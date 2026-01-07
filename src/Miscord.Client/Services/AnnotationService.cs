using System.Collections.ObjectModel;
using Miscord.Shared.Models;

namespace Miscord.Client.Services;

/// <summary>
/// Service to manage drawing annotation state and coordinate between components.
/// Maintains strokes for active screen share sessions and provides drawing settings.
/// </summary>
public class AnnotationService
{
    private readonly ISignalRService _signalR;
    private readonly Dictionary<Guid, List<DrawingStroke>> _strokesBySharer = new();
    private readonly object _lock = new();

    // Drawing settings
    public string CurrentColor { get; set; } = "#FF0000"; // Red default
    public float CurrentThickness { get; set; } = 3.0f;

    // Available colors for the toolbar
    public static readonly string[] AvailableColors =
    [
        "#FF0000", // Red
        "#00FF00", // Green
        "#0080FF", // Blue
        "#FFFF00", // Yellow
        "#FF00FF", // Magenta
        "#00FFFF", // Cyan
        "#FFFFFF", // White
        "#000000"  // Black
    ];

    // Events
    public event Action<Guid, DrawingStroke>? StrokeAdded;
    public event Action<Guid, Guid>? StrokeErased; // sharerId, strokeId
    public event Action<Guid>? StrokesCleared; // sharerId

    public AnnotationService(ISignalRService signalR)
    {
        _signalR = signalR;
        _signalR.AnnotationReceived += OnAnnotationReceived;
    }

    private void OnAnnotationReceived(AnnotationMessage message)
    {
        switch (message.Action)
        {
            case "stroke" when message.Stroke != null:
                AddStrokeInternal(message.SharerUserId, message.Stroke);
                StrokeAdded?.Invoke(message.SharerUserId, message.Stroke);
                break;

            case "erase" when message.EraseStrokeId.HasValue:
                EraseStrokeInternal(message.SharerUserId, message.EraseStrokeId.Value);
                StrokeErased?.Invoke(message.SharerUserId, message.EraseStrokeId.Value);
                break;

            case "clear":
                ClearStrokesInternal(message.SharerUserId);
                StrokesCleared?.Invoke(message.SharerUserId);
                break;
        }
    }

    /// <summary>
    /// Add a stroke locally and broadcast it to other users.
    /// </summary>
    public async Task AddStrokeAsync(Guid channelId, Guid sharerId, DrawingStroke stroke)
    {
        // Add locally first (optimistic UI)
        AddStrokeInternal(sharerId, stroke);
        StrokeAdded?.Invoke(sharerId, stroke);

        // Broadcast to others
        var message = new AnnotationMessage
        {
            ChannelId = channelId,
            SharerUserId = sharerId,
            Action = "stroke",
            Stroke = stroke
        };
        await _signalR.SendAnnotationAsync(message);
    }

    /// <summary>
    /// Clear all strokes for a screen share session and broadcast.
    /// </summary>
    public async Task ClearStrokesAsync(Guid channelId, Guid sharerId)
    {
        // Clear locally first
        ClearStrokesInternal(sharerId);
        StrokesCleared?.Invoke(sharerId);

        // Broadcast to others
        await _signalR.ClearAnnotationsAsync(channelId, sharerId);
    }

    /// <summary>
    /// Get all strokes for a specific sharer.
    /// </summary>
    public IReadOnlyList<DrawingStroke> GetStrokes(Guid sharerId)
    {
        lock (_lock)
        {
            if (_strokesBySharer.TryGetValue(sharerId, out var strokes))
            {
                return strokes.AsReadOnly();
            }
            return Array.Empty<DrawingStroke>();
        }
    }

    /// <summary>
    /// Clear all strokes when a screen share ends.
    /// </summary>
    public void OnScreenShareEnded(Guid sharerId)
    {
        ClearStrokesInternal(sharerId);
    }

    private void AddStrokeInternal(Guid sharerId, DrawingStroke stroke)
    {
        lock (_lock)
        {
            if (!_strokesBySharer.ContainsKey(sharerId))
            {
                _strokesBySharer[sharerId] = new List<DrawingStroke>();
            }
            _strokesBySharer[sharerId].Add(stroke);
        }
    }

    private void EraseStrokeInternal(Guid sharerId, Guid strokeId)
    {
        lock (_lock)
        {
            if (_strokesBySharer.TryGetValue(sharerId, out var strokes))
            {
                strokes.RemoveAll(s => s.Id == strokeId);
            }
        }
    }

    private void ClearStrokesInternal(Guid sharerId)
    {
        lock (_lock)
        {
            if (_strokesBySharer.ContainsKey(sharerId))
            {
                _strokesBySharer[sharerId].Clear();
            }
        }
    }
}
