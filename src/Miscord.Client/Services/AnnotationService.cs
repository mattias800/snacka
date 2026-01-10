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
    private readonly Dictionary<Guid, bool> _drawingAllowedBySharer = new(); // Track if drawing is allowed per sharer
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
    public event Action<Guid, bool>? DrawingAllowedChanged; // sharerId, isAllowed

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

            case "stroke_update" when message.Stroke != null:
                // Live update - replace existing stroke with same ID or add new
                UpdateStrokeInternal(message.SharerUserId, message.Stroke);
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

            case "allow_drawing" when message.IsDrawingAllowed.HasValue:
                SetDrawingAllowedInternal(message.SharerUserId, message.IsDrawingAllowed.Value);
                DrawingAllowedChanged?.Invoke(message.SharerUserId, message.IsDrawingAllowed.Value);
                break;
        }
    }

    /// <summary>
    /// Add a completed stroke locally and broadcast it to other users.
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
    /// Update a stroke in progress (live drawing) - broadcasts to other users.
    /// </summary>
    public async Task UpdateStrokeAsync(Guid channelId, Guid sharerId, DrawingStroke stroke)
    {
        // Update locally first
        UpdateStrokeInternal(sharerId, stroke);
        StrokeAdded?.Invoke(sharerId, stroke);

        // Broadcast to others
        var message = new AnnotationMessage
        {
            ChannelId = channelId,
            SharerUserId = sharerId,
            Action = "stroke_update",
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
    /// Set whether drawing is allowed for a screen share session (host only).
    /// </summary>
    public async Task SetDrawingAllowedAsync(Guid channelId, Guid sharerId, bool isAllowed)
    {
        // Set locally first
        SetDrawingAllowedInternal(sharerId, isAllowed);
        DrawingAllowedChanged?.Invoke(sharerId, isAllowed);

        // Broadcast to others
        var message = new AnnotationMessage
        {
            ChannelId = channelId,
            SharerUserId = sharerId,
            Action = "allow_drawing",
            IsDrawingAllowed = isAllowed
        };
        await _signalR.SendAnnotationAsync(message);
    }

    /// <summary>
    /// Check if drawing is allowed for a specific sharer.
    /// </summary>
    public bool IsDrawingAllowed(Guid sharerId)
    {
        lock (_lock)
        {
            var result = _drawingAllowedBySharer.TryGetValue(sharerId, out var allowed) && allowed;
            Console.WriteLine($"AnnotationService.IsDrawingAllowed: sharerId={sharerId}, hasEntry={_drawingAllowedBySharer.ContainsKey(sharerId)}, result={result}");
            return result;
        }
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

    private void UpdateStrokeInternal(Guid sharerId, DrawingStroke stroke)
    {
        lock (_lock)
        {
            if (!_strokesBySharer.ContainsKey(sharerId))
            {
                _strokesBySharer[sharerId] = new List<DrawingStroke>();
            }

            var strokes = _strokesBySharer[sharerId];
            var existingIndex = strokes.FindIndex(s => s.Id == stroke.Id);
            if (existingIndex >= 0)
            {
                // Replace existing stroke
                strokes[existingIndex] = stroke;
            }
            else
            {
                // Add as new stroke
                strokes.Add(stroke);
            }
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

    private void SetDrawingAllowedInternal(Guid sharerId, bool isAllowed)
    {
        Console.WriteLine($"AnnotationService.SetDrawingAllowedInternal: sharerId={sharerId}, isAllowed={isAllowed}");
        lock (_lock)
        {
            _drawingAllowedBySharer[sharerId] = isAllowed;
        }
    }
}
