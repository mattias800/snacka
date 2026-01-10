using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Miscord.Client.Services;

#region MiscordCapture JSON Models

internal record MiscordCaptureSourceList(
    [property: JsonPropertyName("displays")] List<MiscordCaptureDisplay> Displays,
    [property: JsonPropertyName("windows")] List<MiscordCaptureWindow> Windows,
    [property: JsonPropertyName("applications")] List<MiscordCaptureApplication> Applications
);

internal record MiscordCaptureDisplay(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height
);

internal record MiscordCaptureWindow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("appName")] string AppName,
    [property: JsonPropertyName("bundleId")] string? BundleId
);

internal record MiscordCaptureApplication(
    [property: JsonPropertyName("bundleId")] string BundleId,
    [property: JsonPropertyName("name")] string Name
);

#endregion

/// <summary>
/// Cross-platform service for enumerating screen capture sources (displays, windows, and applications).
/// - macOS: Displays, windows, and applications via MiscordCapture (ScreenCaptureKit)
/// - Windows: Displays and windows
/// - Linux: Displays and windows
/// </summary>
public class ScreenCaptureService : IScreenCaptureService
{
    private MiscordCaptureSourceList? _cachedMacOSSources;
    private string? _miscordCapturePath;
    public IReadOnlyList<ScreenCaptureSource> GetAvailableSources()
    {
        // On macOS, try to get all sources at once via MiscordCapture
        if (OperatingSystem.IsMacOS())
        {
            RefreshMacOSSources();
        }

        var sources = new List<ScreenCaptureSource>();
        sources.AddRange(GetDisplays());
        sources.AddRange(GetWindows());
        sources.AddRange(GetApplications());
        return sources;
    }

    public IReadOnlyList<ScreenCaptureSource> GetApplications()
    {
        Console.WriteLine("ScreenCaptureService: Enumerating applications...");

        if (OperatingSystem.IsMacOS())
        {
            return GetApplicationsMacOS();
        }

        // Application capture is currently macOS only via ScreenCaptureKit
        return Array.Empty<ScreenCaptureSource>();
    }

    public IReadOnlyList<ScreenCaptureSource> GetDisplays()
    {
        Console.WriteLine("ScreenCaptureService: Enumerating displays...");

        if (OperatingSystem.IsMacOS())
        {
            return GetDisplaysMacOS();
        }
        if (OperatingSystem.IsWindows())
        {
            return GetDisplaysWindows();
        }
        if (OperatingSystem.IsLinux())
        {
            return GetDisplaysLinux();
        }

        return Array.Empty<ScreenCaptureSource>();
    }

    public IReadOnlyList<ScreenCaptureSource> GetWindows()
    {
        Console.WriteLine("ScreenCaptureService: Enumerating windows...");

        if (OperatingSystem.IsMacOS())
        {
            return GetWindowsMacOS();
        }
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsWindows();
        }
        if (OperatingSystem.IsLinux())
        {
            return GetWindowsLinux();
        }

        return Array.Empty<ScreenCaptureSource>();
    }

    #region macOS Implementation

    /// <summary>
    /// Gets the path to MiscordCapture binary if available.
    /// </summary>
    private string? GetMiscordCapturePath()
    {
        if (_miscordCapturePath != null)
            return _miscordCapturePath;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;

        var searchPaths = new[]
        {
            Path.Combine(appDir, "MiscordCapture"),
            Path.Combine(appDir, "..", "MiscordCapture", ".build", "release", "MiscordCapture"),
            Path.Combine(appDir, "..", "..", "..", "..", "MiscordCapture", ".build", "release", "MiscordCapture"),
            Path.Combine(appDir, "..", "MiscordCapture", ".build", "debug", "MiscordCapture"),
            Path.Combine(appDir, "..", "..", "..", "..", "MiscordCapture", ".build", "debug", "MiscordCapture"),
        };

        foreach (var path in searchPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                Console.WriteLine($"ScreenCaptureService: Found MiscordCapture at {fullPath}");
                _miscordCapturePath = fullPath;
                return fullPath;
            }
        }

        Console.WriteLine("ScreenCaptureService: MiscordCapture not found");
        return null;
    }

    /// <summary>
    /// Refreshes the cached macOS sources by running MiscordCapture list --json.
    /// </summary>
    private void RefreshMacOSSources()
    {
        var miscordPath = GetMiscordCapturePath();
        if (miscordPath == null)
        {
            _cachedMacOSSources = null;
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = miscordPath,
                Arguments = "list --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                _cachedMacOSSources = null;
                return;
            }

            var output = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);

            if (!string.IsNullOrEmpty(stderr))
            {
                Console.WriteLine($"ScreenCaptureService: MiscordCapture stderr: {stderr}");
            }

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"ScreenCaptureService: MiscordCapture exited with code {process.ExitCode}");
                _cachedMacOSSources = null;
                return;
            }

            _cachedMacOSSources = JsonSerializer.Deserialize<MiscordCaptureSourceList>(output);
            Console.WriteLine($"ScreenCaptureService: MiscordCapture found {_cachedMacOSSources?.Displays.Count ?? 0} displays, " +
                              $"{_cachedMacOSSources?.Windows.Count ?? 0} windows, " +
                              $"{_cachedMacOSSources?.Applications.Count ?? 0} applications");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenCaptureService: Failed to get sources via MiscordCapture - {ex.Message}");
            _cachedMacOSSources = null;
        }
    }

    private IReadOnlyList<ScreenCaptureSource> GetDisplaysMacOS()
    {
        // Try to use MiscordCapture if available
        if (_cachedMacOSSources != null)
        {
            var displays = _cachedMacOSSources.Displays.Select(d =>
                new ScreenCaptureSource(
                    ScreenCaptureSourceType.Display,
                    d.Id,
                    $"{d.Name} ({d.Width}x{d.Height})"
                )).ToList();

            foreach (var d in displays)
                Console.WriteLine($"  - {d.Name}");

            Console.WriteLine($"ScreenCaptureService: Found {displays.Count} displays via MiscordCapture (macOS)");
            return displays;
        }

        // Fallback to Swift/CoreGraphics
        return GetDisplaysMacOSFallback();
    }

    private IReadOnlyList<ScreenCaptureSource> GetWindowsMacOS()
    {
        // Use MiscordCapture if available
        if (_cachedMacOSSources != null)
        {
            var windows = _cachedMacOSSources.Windows.Select(w =>
            {
                var displayName = string.IsNullOrEmpty(w.AppName)
                    ? w.Name
                    : $"{w.AppName}: {w.Name}";

                // Truncate long titles
                if (displayName.Length > 60)
                    displayName = displayName.Substring(0, 57) + "...";

                return new ScreenCaptureSource(
                    ScreenCaptureSourceType.Window,
                    w.Id,
                    displayName,
                    w.AppName,
                    w.BundleId
                );
            }).ToList();

            Console.WriteLine($"ScreenCaptureService: Found {windows.Count} windows via MiscordCapture (macOS)");
            return windows;
        }

        // Window enumeration without MiscordCapture is not supported on macOS
        Console.WriteLine("ScreenCaptureService: Window enumeration requires MiscordCapture on macOS");
        return Array.Empty<ScreenCaptureSource>();
    }

    private IReadOnlyList<ScreenCaptureSource> GetApplicationsMacOS()
    {
        // Use MiscordCapture if available
        if (_cachedMacOSSources != null)
        {
            var apps = _cachedMacOSSources.Applications
                .Where(a => !string.IsNullOrEmpty(a.Name))
                .Select(a => new ScreenCaptureSource(
                    ScreenCaptureSourceType.Application,
                    a.BundleId,  // Use bundleId as the ID for application capture
                    a.Name,
                    a.Name,
                    a.BundleId
                ))
                .OrderBy(a => a.Name)
                .ToList();

            Console.WriteLine($"ScreenCaptureService: Found {apps.Count} applications via MiscordCapture (macOS)");
            return apps;
        }

        // Application enumeration without MiscordCapture is not supported
        Console.WriteLine("ScreenCaptureService: Application enumeration requires MiscordCapture on macOS");
        return Array.Empty<ScreenCaptureSource>();
    }

    private IReadOnlyList<ScreenCaptureSource> GetDisplaysMacOSFallback()
    {
        // Try Swift/CoreGraphics
        try
        {
            var swiftCode = @"import CoreGraphics
var displayCount: UInt32 = 0
CGGetActiveDisplayList(0, nil, &displayCount)
var displays = [CGDirectDisplayID](repeating: 0, count: Int(displayCount))
CGGetActiveDisplayList(displayCount, &displays, nil)
for (i, display) in displays.enumerated() {
    let bounds = CGDisplayBounds(display)
    let width = Int(bounds.width)
    let height = Int(bounds.height)
    let main = CGDisplayIsMain(display) != 0 ? "" (Main)"" : """"
    print(""\(i):\(display):\(width)x\(height)\(main)"")
}";

            var tempFile = Path.Combine(Path.GetTempPath(), $"miscord_display_enum_{Guid.NewGuid():N}.swift");
            File.WriteAllText(tempFile, swiftCode);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "swift",
                    Arguments = tempFile,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return GetDisplaysMacOSSimpleFallback();

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(15000);

                var displays = new List<ScreenCaptureSource>();
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var parts = line.Split(':', 3);
                    if (parts.Length >= 3 && int.TryParse(parts[0], out var index))
                    {
                        var resolution = parts[2].Trim();
                        var name = $"Display {index + 1} ({resolution})";
                        displays.Add(new ScreenCaptureSource(
                            ScreenCaptureSourceType.Display,
                            index.ToString(),
                            name
                        ));
                        Console.WriteLine($"  - {name}");
                    }
                }

                Console.WriteLine($"ScreenCaptureService: Found {displays.Count} displays via Swift/CoreGraphics (macOS)");
                return displays;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenCaptureService: macOS display enumeration failed - {ex.Message}");
            return GetDisplaysMacOSSimpleFallback();
        }
    }

    private IReadOnlyList<ScreenCaptureSource> GetDisplaysMacOSSimpleFallback()
    {
        // Simple fallback: assume at least one display exists
        Console.WriteLine("ScreenCaptureService: Using fallback (1 display)");
        return new[]
        {
            new ScreenCaptureSource(ScreenCaptureSourceType.Display, "0", "Display 1")
        };
    }

    #endregion

    #region Windows Implementation

    private IReadOnlyList<ScreenCaptureSource> GetDisplaysWindows()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<ScreenCaptureSource>();

        var displays = new List<ScreenCaptureSource>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, hdcMonitor, lprcMonitor, dwData) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(info);

                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var index = displays.Count;
                    var width = info.rcMonitor.Right - info.rcMonitor.Left;
                    var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
                    var isPrimary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
                    var suffix = isPrimary ? " (Primary)" : "";

                    var name = $"Display {index + 1} ({width}x{height}){suffix}";
                    displays.Add(new ScreenCaptureSource(
                        ScreenCaptureSourceType.Display,
                        index.ToString(),
                        name
                    ));
                    Console.WriteLine($"  - {name}");
                }

                return true;
            }, IntPtr.Zero);

            Console.WriteLine($"ScreenCaptureService: Found {displays.Count} displays via Win32 (Windows)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenCaptureService: Windows display enumeration failed - {ex.Message}");
            // Fallback
            displays.Add(new ScreenCaptureSource(ScreenCaptureSourceType.Display, "0", "Display 1"));
        }

        return displays;
    }

    private IReadOnlyList<ScreenCaptureSource> GetWindowsWindows()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<ScreenCaptureSource>();

        var windows = new List<ScreenCaptureSource>();

        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                // Skip invisible windows
                if (!IsWindowVisible(hWnd))
                    return true;

                // Get window title
                var titleLength = GetWindowTextLength(hWnd);
                if (titleLength == 0)
                    return true;

                var titleBuilder = new StringBuilder(titleLength + 1);
                GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString();

                // Skip empty titles and system windows
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                // Get process name for app identification
                string? appName = null;
                try
                {
                    GetWindowThreadProcessId(hWnd, out var processId);
                    var hProcess = OpenProcess(0x0400 | 0x0010, false, processId); // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
                    if (hProcess != IntPtr.Zero)
                    {
                        try
                        {
                            var exePath = new StringBuilder(260);
                            var size = 260;
                            if (QueryFullProcessImageName(hProcess, 0, exePath, ref size))
                            {
                                appName = Path.GetFileNameWithoutExtension(exePath.ToString());
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                }
                catch { }

                // Use window handle as ID for gdigrab
                var windowId = hWnd.ToString();
                var displayName = string.IsNullOrEmpty(appName) ? title : $"{appName}: {title}";

                // Truncate long titles
                if (displayName.Length > 60)
                    displayName = displayName.Substring(0, 57) + "...";

                windows.Add(new ScreenCaptureSource(
                    ScreenCaptureSourceType.Window,
                    title, // gdigrab uses window title
                    displayName,
                    appName
                ));

                return true;
            }, IntPtr.Zero);

            // Sort by app name
            windows = windows.OrderBy(w => w.AppName ?? w.Name).ToList();

            Console.WriteLine($"ScreenCaptureService: Found {windows.Count} windows via Win32 (Windows)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenCaptureService: Windows window enumeration failed - {ex.Message}");
        }

        return windows;
    }

    // Win32 P/Invoke declarations
    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    #endregion

    #region Linux Implementation

    private IReadOnlyList<ScreenCaptureSource> GetDisplaysLinux()
    {
        var displays = new List<ScreenCaptureSource>();

        try
        {
            // Parse xrandr output to get connected displays
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xrandr",
                Arguments = "--query",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return GetDisplaysLinuxFallback();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // Parse connected displays: "HDMI-1 connected primary 1920x1080+0+0"
            var regex = new Regex(@"(\S+)\s+connected\s*(primary)?\s*(\d+)x(\d+)\+(\d+)\+(\d+)");
            var matches = regex.Matches(output);

            foreach (Match match in matches)
            {
                var outputName = match.Groups[1].Value;
                var isPrimary = !string.IsNullOrEmpty(match.Groups[2].Value);
                var width = match.Groups[3].Value;
                var height = match.Groups[4].Value;
                var x = match.Groups[5].Value;
                var y = match.Groups[6].Value;

                var index = displays.Count;
                var suffix = isPrimary ? " (Primary)" : "";
                var name = $"Display {index + 1} - {outputName} ({width}x{height}){suffix}";

                // For x11grab, we use :0.0+x,y format
                var id = $":0.0+{x},{y}";

                displays.Add(new ScreenCaptureSource(
                    ScreenCaptureSourceType.Display,
                    id,
                    name
                ));
                Console.WriteLine($"  - {name} at {x},{y}");
            }

            if (displays.Count == 0)
            {
                return GetDisplaysLinuxFallback();
            }

            Console.WriteLine($"ScreenCaptureService: Found {displays.Count} displays via xrandr (Linux)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenCaptureService: Linux display enumeration failed - {ex.Message}");
            return GetDisplaysLinuxFallback();
        }

        return displays;
    }

    private IReadOnlyList<ScreenCaptureSource> GetDisplaysLinuxFallback()
    {
        Console.WriteLine("ScreenCaptureService: Using fallback (primary display)");
        return new[]
        {
            new ScreenCaptureSource(ScreenCaptureSourceType.Display, ":0.0", "Display 1")
        };
    }

    private IReadOnlyList<ScreenCaptureSource> GetWindowsLinux()
    {
        var windows = new List<ScreenCaptureSource>();

        try
        {
            // Use wmctrl to list windows
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wmctrl",
                Arguments = "-l -p",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                Console.WriteLine("ScreenCaptureService: wmctrl not available");
                return windows;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // Parse wmctrl output: "0x03000003  0 12345  hostname Window Title"
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { ' ' }, 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    var windowId = parts[0]; // Hex window ID
                    var title = parts[4].Trim();

                    if (string.IsNullOrWhiteSpace(title) || title == "Desktop")
                        continue;

                    // Truncate long titles
                    var displayName = title;
                    if (displayName.Length > 60)
                        displayName = displayName.Substring(0, 57) + "...";

                    windows.Add(new ScreenCaptureSource(
                        ScreenCaptureSourceType.Window,
                        windowId,
                        displayName
                    ));
                }
            }

            Console.WriteLine($"ScreenCaptureService: Found {windows.Count} windows via wmctrl (Linux)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ScreenCaptureService: Linux window enumeration failed - {ex.Message}");
        }

        return windows;
    }

    #endregion
}
