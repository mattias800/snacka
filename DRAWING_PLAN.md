# Drawing Annotations on Screen Shares - Implementation Plan

## Overview

Add collaborative drawing/annotation capability for screen shares, similar to Slack's annotation feature. This enables real-time visual communication during screen sharing sessions - invaluable for work collaboration, code reviews, and presentations.

---

## Key Design Decisions

### 1. Two Different User Experiences

**Viewers (watching someone's screen share):**
- Draw on the fullscreen video overlay in the Miscord client
- Canvas sits on top of the video stream
- Standard in-app experience

**Sharer (the person sharing their screen):**
- Draw on a transparent fullscreen overlay window on their actual monitor
- This overlay sits above all other windows on the shared monitor
- Drawings appear on the monitor itself, which gets captured by screen share
- Must toggle between "draw mode" and "normal mode" to use their desktop

### 2. Monitor Sharing Only

Drawing annotations are **only supported for full monitor sharing**, not window sharing:
- We can create a fullscreen transparent overlay on a monitor
- We cannot reliably overlay on a specific application window
- Window sharing (not yet implemented) will not support annotations

### 3. Toggle Mode for Sharer

When the sharer enables drawing mode:
- Mouse clicks/drags create drawings instead of interacting with desktop
- A floating toolbar remains clickable to exit draw mode
- All other screen areas respond to drawing input only

---

## Architecture

### Visual Overview

```
VIEWER'S SCREEN (Miscord Client)          SHARER'S SCREEN (Monitor being shared)
┌─────────────────────────────────┐       ┌─────────────────────────────────┐
│ Miscord App                     │       │ Desktop                         │
│ ┌─────────────────────────────┐ │       │                                 │
│ │ Fullscreen Video View       │ │       │  ┌──────────────────────────┐   │
│ │ ┌─────────────────────────┐ │ │       │  │ VS Code / Browser / etc  │   │
│ │ │                         │ │ │       │  │                          │   │
│ │ │   Video from sharer     │ │ │       │  │                          │   │
│ │ │                         │ │ │       │  └──────────────────────────┘   │
│ │ │   ~~~~~ drawings ~~~~~  │ │ │       │                                 │
│ │ │                         │ │ │       │      ~~~~~ drawings ~~~~~       │
│ │ └─────────────────────────┘ │ │       │                                 │
│ │ [Canvas Overlay - drawings] │ │       │  ┌─────────────────────────────┐│
│ └─────────────────────────────┘ │       │  │ Transparent Overlay Window ││
│ [Color][Size][Eraser][Clear][X] │       │  │ (captures drawing input)   ││
└─────────────────────────────────┘       │  └─────────────────────────────┘│
                                          │  [Floating Toolbar]             │
                                          │  [Draw: ON] [Color] [Clear] [X] │
                                          └─────────────────────────────────┘
```

### Data Flow

```
1. User draws (viewer or sharer)
         │
         ▼
2. Capture points as normalized coords (0.0 - 1.0)
         │
         ▼
3. Render locally immediately (optimistic UI)
         │
         ▼
4. Send via SignalR: SendAnnotationAsync(channelId, sharerId, strokeData)
         │
         ▼
5. Server broadcasts to all participants watching this screen share
         │
         ▼
6. Each client receives and renders the stroke
   - Viewers: render on fullscreen canvas overlay
   - Sharer: render on transparent monitor overlay window
```

---

## Components

### Shared (Miscord.Shared)

#### Data Models

```csharp
// A single drawing stroke (one mouse down → drag → mouse up)
public class DrawingStroke
{
    public Guid Id { get; set; }                    // Unique stroke ID
    public Guid UserId { get; set; }                // Who drew it
    public string Username { get; set; }            // For display
    public List<PointF> Points { get; set; }        // Normalized 0.0-1.0 coordinates
    public string Color { get; set; }               // Hex color "#FF0000"
    public float Thickness { get; set; }            // Stroke width (normalized)
    public DateTimeOffset Timestamp { get; set; }
}

// Point with float coordinates (normalized)
public struct PointF
{
    public float X { get; set; }  // 0.0 = left edge, 1.0 = right edge
    public float Y { get; set; }  // 0.0 = top edge, 1.0 = bottom edge
}

// Message sent via SignalR
public class AnnotationMessage
{
    public Guid ChannelId { get; set; }             // Voice channel
    public Guid SharerUserId { get; set; }          // Who is sharing screen
    public string Action { get; set; }              // "stroke", "erase", "clear"
    public DrawingStroke? Stroke { get; set; }      // For "stroke" action
    public Guid? EraseStrokeId { get; set; }        // For "erase" action
}
```

### Server (Miscord.Server)

#### SignalR Hub Methods

```csharp
// Add to CommunityHub.cs

// Client sends annotation
public async Task SendAnnotationAsync(AnnotationMessage message)
{
    // Broadcast to all users in the voice channel
    await Clients.Group($"voice-{message.ChannelId}")
        .SendAsync("ReceiveAnnotation", message);
}

// Clear all annotations for a screen share
public async Task ClearAnnotationsAsync(Guid channelId, Guid sharerUserId)
{
    await Clients.Group($"voice-{message.ChannelId}")
        .SendAsync("ReceiveAnnotation", new AnnotationMessage
        {
            ChannelId = channelId,
            SharerUserId = sharerUserId,
            Action = "clear"
        });
}
```

### Client (Miscord.Client)

#### 1. SignalR Service Additions

```csharp
// ISignalRService.cs
Task SendAnnotationAsync(AnnotationMessage message);
Task ClearAnnotationsAsync(Guid channelId, Guid sharerUserId);
event Action<AnnotationMessage>? AnnotationReceived;

// SignalRService.cs - RegisterHandlers()
_hubConnection.On<AnnotationMessage>("ReceiveAnnotation", msg =>
    AnnotationReceived?.Invoke(msg));
```

#### 2. Annotation Service (New)

Central service to manage drawing state and coordinate between components:

```csharp
public class AnnotationService
{
    // Current strokes for active screen share sessions
    // Key: SharerUserId, Value: list of strokes
    private Dictionary<Guid, List<DrawingStroke>> _strokesBySharer;

    // Drawing settings
    public string CurrentColor { get; set; } = "#FF0000";
    public float CurrentThickness { get; set; } = 3.0f;

    // Events
    public event Action<Guid, DrawingStroke>? StrokeAdded;
    public event Action<Guid>? StrokesCleared;

    // Methods
    public void AddStroke(Guid sharerId, DrawingStroke stroke);
    public void ClearStrokes(Guid sharerId);
    public IReadOnlyList<DrawingStroke> GetStrokes(Guid sharerId);
}
```

#### 3. Viewer UI - Fullscreen Canvas Overlay

Modify `MainAppView.axaml` fullscreen overlay:

```xml
<!-- Video Fullscreen Overlay (existing) -->
<Border Grid.ColumnSpan="4"
        Background="#FF000000"
        IsVisible="{Binding IsVideoFullscreen}">
    <Grid>
        <!-- Video (existing) -->
        <Image Source="{Binding FullscreenStream.VideoBitmap}"
               Stretch="Uniform"
               x:Name="FullscreenVideo"/>

        <!-- NEW: Drawing Canvas Overlay -->
        <Canvas x:Name="AnnotationCanvas"
                Background="Transparent"
                IsVisible="{Binding IsAnnotationEnabled}"
                PointerPressed="OnCanvasPointerPressed"
                PointerMoved="OnCanvasPointerMoved"
                PointerReleased="OnCanvasPointerReleased"/>

        <!-- NEW: Annotation Toolbar -->
        <Border VerticalAlignment="Bottom"
                HorizontalAlignment="Center"
                Margin="0,0,0,60"
                Background="#E0202020"
                CornerRadius="8"
                Padding="8"
                IsVisible="{Binding IsVideoFullscreen}">
            <StackPanel Orientation="Horizontal" Spacing="8">
                <!-- Toggle draw mode -->
                <ToggleButton IsChecked="{Binding IsAnnotationEnabled}"
                              Content="Draw"/>

                <!-- Color buttons -->
                <Button Background="#FF0000" Width="24" Height="24"
                        Command="{Binding SetColorCommand}"
                        CommandParameter="#FF0000"/>
                <Button Background="#00FF00" Width="24" Height="24"
                        Command="{Binding SetColorCommand}"
                        CommandParameter="#00FF00"/>
                <Button Background="#0080FF" Width="24" Height="24"
                        Command="{Binding SetColorCommand}"
                        CommandParameter="#0080FF"/>
                <Button Background="#FFFF00" Width="24" Height="24"
                        Command="{Binding SetColorCommand}"
                        CommandParameter="#FFFF00"/>

                <!-- Clear all -->
                <Button Content="Clear" Command="{Binding ClearAnnotationsCommand}"/>
            </StackPanel>
        </Border>

        <!-- Close button (existing) -->
        <Button VerticalAlignment="Top" HorizontalAlignment="Right" .../>
    </Grid>
</Border>
```

#### 4. Sharer UI - Transparent Fullscreen Overlay Window

A separate window that covers the shared monitor:

```csharp
public class ScreenAnnotationWindow : Window
{
    public ScreenAnnotationWindow(Screen targetScreen)
    {
        // Window properties for transparent overlay
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = WindowTransparencyLevel.Transparent;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;

        // Position to cover the target screen
        Position = new PixelPoint(targetScreen.Bounds.X, targetScreen.Bounds.Y);
        Width = targetScreen.Bounds.Width;
        Height = targetScreen.Bounds.Height;
    }

    // When draw mode is OFF, let input pass through to desktop
    // When draw mode is ON, capture input for drawing
    public bool IsDrawModeEnabled { get; set; }
}
```

**Floating Toolbar (always clickable):**

```xml
<!-- Separate small window or popup that stays on top -->
<Window Title="Drawing Tools"
        Width="300" Height="50"
        Topmost="True"
        SystemDecorations="None">
    <Border Background="#E0202020" CornerRadius="8" Padding="8">
        <StackPanel Orientation="Horizontal" Spacing="8">
            <!-- Toggle - THIS IS THE KEY BUTTON -->
            <ToggleButton x:Name="DrawModeToggle"
                          IsChecked="{Binding IsDrawModeEnabled}"
                          Content="Draw Mode"/>

            <!-- Colors -->
            <Button Background="#FF0000" Width="24" Height="24" .../>
            <Button Background="#00FF00" Width="24" Height="24" .../>

            <!-- Clear -->
            <Button Content="Clear" .../>

            <!-- Close overlay entirely -->
            <Button Content="X" .../>
        </StackPanel>
    </Border>
</Window>
```

---

## Implementation Phases

### Phase 1: Data Models & SignalR (Foundation)

**Files to create/modify:**
- `src/Miscord.Shared/Models/DrawingStroke.cs` (new)
- `src/Miscord.Shared/Models/AnnotationMessage.cs` (new)
- `src/Miscord.Server/Hubs/CommunityHub.cs` (add methods)
- `src/Miscord.Client/Services/ISignalRService.cs` (add interface)
- `src/Miscord.Client/Services/SignalRService.cs` (add implementation)

**Outcome:** SignalR can send/receive annotation messages

### Phase 2: Viewer Drawing Experience

**Files to create/modify:**
- `src/Miscord.Client/Services/AnnotationService.cs` (new)
- `src/Miscord.Client/ViewModels/MainAppViewModel.cs` (add annotation state)
- `src/Miscord.Client/Views/MainAppView.axaml` (add canvas + toolbar)
- `src/Miscord.Client/Views/MainAppView.axaml.cs` (add pointer handlers)

**Outcome:** Viewers can draw on fullscreen video, strokes sync to all viewers

### Phase 3: Sharer Drawing Experience

**Files to create/modify:**
- `src/Miscord.Client/Views/ScreenAnnotationWindow.axaml` (new)
- `src/Miscord.Client/Views/ScreenAnnotationWindow.axaml.cs` (new)
- `src/Miscord.Client/Views/AnnotationToolbarWindow.axaml` (new - floating toolbar)
- `src/Miscord.Client/ViewModels/ScreenAnnotationViewModel.cs` (new)
- `src/Miscord.Client/Services/WebRtcService.cs` (launch overlay when sharing)

**Outcome:** Sharer sees floating toolbar, can toggle draw mode, drawings appear on their screen

### Phase 4: Polish & UX

- Smooth line rendering (Catmull-Rom splines or similar)
- Stroke thickness options
- Eraser tool (click on stroke to remove)
- Per-user colors with username tooltips
- Fade out old strokes after N seconds (optional)
- Keyboard shortcut to toggle draw mode (e.g., Ctrl+D)

---

## Technical Considerations

### Coordinate Normalization

All coordinates stored as 0.0 to 1.0 ratios:

```csharp
// When capturing a point
float normalizedX = pointerPosition.X / canvasWidth;
float normalizedY = pointerPosition.Y / canvasHeight;

// When rendering a point
float renderX = normalizedX * canvasWidth;
float renderY = normalizedY * canvasHeight;
```

This ensures drawings align correctly regardless of:
- Different monitor resolutions between sharer and viewers
- Video scaling (Uniform stretch in fullscreen)
- Window resize

### Input Pass-through on Sharer's Overlay

When draw mode is OFF, the transparent overlay must let clicks pass through to the desktop below. This requires platform-specific native interop:

```csharp
// Windows: SetWindowLong with WS_EX_TRANSPARENT extended window style
// macOS: NSWindow.ignoresMouseEvents = true
// Linux/X11: XShapeCombineRectangles with ShapeInput to set empty input region
// Linux/Wayland: May require layer-shell protocol or compositor-specific handling
```

When draw mode is ON, the overlay captures all input.

**Linux considerations:**
- X11: Well-supported via XShape extension for input pass-through
- Wayland: More restrictive - overlay windows may need special compositor support
- May need to detect display server and adjust behavior accordingly

### Performance

- Batch point updates (send every 50-100ms during drag, not every pixel)
- Use `Polyline` for rendering (single draw call per stroke)
- Limit max points per stroke (simplify long strokes)
- Clear strokes when screen share ends

### Edge Cases

1. **Sharer stops sharing** → Clear all annotations, close overlay window
2. **Viewer leaves fullscreen** → Keep strokes in memory, redraw when re-entering
3. **New viewer joins** → Send current stroke collection (or start fresh)
4. **Sharer changes shared monitor** → Clear strokes, reposition overlay

---

## Open Questions

1. **Persist strokes for late joiners?**
   - Simple: No, new viewers start with blank canvas
   - Complex: Server stores recent strokes, sends on join

2. **Maximum stroke age?**
   - Option A: Strokes persist until cleared manually
   - Option B: Auto-fade after 30 seconds
   - Option C: Keep last N strokes only

3. **Who can clear?**
   - Only the sharer?
   - Anyone can clear all?
   - Each user clears only their own strokes?

4. **Annotation permissions?**
   - Everyone in voice channel can draw?
   - Sharer must enable "allow annotations"?

---

## Dependencies

- Avalonia's `Canvas` and drawing primitives
- SignalR (already in use)
- Platform-specific code for input pass-through on sharer's overlay window:
  - **Windows:** P/Invoke for `SetWindowLong` / `SetWindowLongPtr`
  - **macOS:** ObjC interop for `NSWindow.ignoresMouseEvents`
  - **Linux/X11:** X11 interop for `XShapeCombineRectangles`
  - **Linux/Wayland:** Layer-shell or compositor-specific (may have limitations)

---

## Success Criteria

1. Viewers can draw on fullscreen screen share video
2. Sharer can draw on their actual monitor (with overlay)
3. All drawings sync in real-time to all participants
4. Sharer can toggle between draw mode and normal desktop interaction
5. Drawings are correctly scaled across different screen sizes
6. Performance remains smooth (no frame drops during drawing)
