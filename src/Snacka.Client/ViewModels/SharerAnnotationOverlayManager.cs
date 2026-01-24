using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Snacka.Client.Services;
using Snacka.Client.Views;

namespace Snacka.Client.ViewModels;

/// <summary>
/// Manages the annotation overlay windows for the screen sharer.
/// Creates and positions the ScreenAnnotationWindow (transparent drawing overlay)
/// and AnnotationToolbarWindow (floating toolbar) when display sharing starts.
/// </summary>
public class SharerAnnotationOverlayManager : IDisposable
{
    private ScreenAnnotationWindow? _screenAnnotationWindow;
    private AnnotationToolbarWindow? _annotationToolbarWindow;
    private ScreenAnnotationViewModel? _screenAnnotationViewModel;

    /// <summary>
    /// Raised when the user closes the annotation toolbar, indicating they want to stop sharing.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Shows the annotation overlay for display sharing.
    /// Only called for display (monitor) sharing, not window sharing.
    /// </summary>
    /// <param name="settings">The screen share settings containing the display source.</param>
    /// <param name="annotationViewModel">The view model for the annotation overlay.</param>
    public void Show(ScreenShareSettings settings, ScreenAnnotationViewModel annotationViewModel)
    {
        _screenAnnotationViewModel = annotationViewModel;

        try
        {
            // Find the target screen bounds
            PixelRect? targetBounds = FindTargetScreenBounds(settings);

            // Create and show overlay window
            _screenAnnotationWindow = new ScreenAnnotationWindow();
            _screenAnnotationWindow.DataContext = _screenAnnotationViewModel;
            PositionOverlayWindow(_screenAnnotationWindow, targetBounds);
            _screenAnnotationWindow.Show();

            // Create toolbar window
            _annotationToolbarWindow = new AnnotationToolbarWindow();
            _annotationToolbarWindow.DataContext = _screenAnnotationViewModel;
            _annotationToolbarWindow.SetOverlayWindow(_screenAnnotationWindow);
            PositionToolbarWindow(_annotationToolbarWindow, targetBounds);

            // Note: We don't call Show() here - the toolbar manages its own visibility
            // based on the IsDrawingAllowedForViewers subscription. It will show when
            // the host clicks "Allow Drawing" in the voice panel.
            _annotationToolbarWindow.CloseRequested += OnAnnotationToolbarCloseRequested;
        }
        catch
        {
            // Annotation overlay creation failed - non-critical
            Hide();
        }
    }

    /// <summary>
    /// Hides and closes the annotation overlay and toolbar windows.
    /// </summary>
    public void Hide()
    {
        if (_annotationToolbarWindow != null)
        {
            _annotationToolbarWindow.CloseRequested -= OnAnnotationToolbarCloseRequested;
            _annotationToolbarWindow.Close();
            _annotationToolbarWindow = null;
        }

        if (_screenAnnotationWindow != null)
        {
            _screenAnnotationWindow.Close();
            _screenAnnotationWindow = null;
        }

        // Clear reference (cleanup is handled by ScreenShareViewModel)
        _screenAnnotationViewModel = null;
    }

    private static PixelRect? FindTargetScreenBounds(ScreenShareSettings settings)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var screensService = desktop.MainWindow?.Screens;
            if (screensService != null && int.TryParse(settings.Source.Id, out var displayIndex))
            {
                var allScreens = screensService.All.ToList();
                if (displayIndex < allScreens.Count)
                {
                    return allScreens[displayIndex].Bounds;
                }
            }
        }

        return null;
    }

    private static void PositionOverlayWindow(ScreenAnnotationWindow window, PixelRect? targetBounds)
    {
        if (targetBounds.HasValue)
        {
            window.Position = new PixelPoint(targetBounds.Value.X, targetBounds.Value.Y);
            window.Width = targetBounds.Value.Width;
            window.Height = targetBounds.Value.Height;
        }
        else
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private static void PositionToolbarWindow(AnnotationToolbarWindow window, PixelRect? targetBounds)
    {
        if (targetBounds.HasValue)
        {
            var toolbarX = targetBounds.Value.X + (targetBounds.Value.Width - 380) / 2;
            var toolbarY = targetBounds.Value.Y + targetBounds.Value.Height - 80;
            window.Position = new PixelPoint(toolbarX, toolbarY);
        }
        else
        {
            window.Position = new PixelPoint(400, 700);
        }
    }

    private void OnAnnotationToolbarCloseRequested()
    {
        CloseRequested?.Invoke();
    }

    public void Dispose()
    {
        Hide();
    }
}
