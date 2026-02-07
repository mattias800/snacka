using System.Text.Json;
using Snacka.Client.ViewModels;

namespace Snacka.Client.Services;

public interface ISettingsStore
{
    UserSettings Settings { get; }
    void Save();
    void Load();
}

public class UserSettings
{
    // Audio settings
    public string? AudioInputDevice { get; set; }  // null = default device
    public string? AudioOutputDevice { get; set; } // null = default device
    public float InputVolume { get; set; } = 1.0f;
    public float OutputVolume { get; set; } = 1.0f;
    public float InputGain { get; set; } = 1.0f;   // 0.0 to 3.0 (0% to 300%), manual adjustment on top of AGC
    public float GateThreshold { get; set; } = 0.02f; // 0.0 to 0.5 (normalized RMS threshold)
    public bool GateEnabled { get; set; } = true;

    /// <summary>
    /// Enable AI-powered noise suppression for microphone input.
    /// Reduces background noise like fans, keyboards, and ambient sounds.
    /// </summary>
    public bool NoiseSuppression { get; set; } = true;

    /// <summary>
    /// Enable OS-level acoustic echo cancellation (AEC).
    /// Removes speaker audio from microphone input to prevent feedback.
    /// On macOS: Uses VoiceProcessingIO audio unit.
    /// On Windows: Uses Voice Capture DSP.
    /// On Linux: Users should configure PipeWire/PulseAudio module-echo-cancel.
    /// </summary>
    public bool EchoCancellation { get; set; } = true;

    // Voice channel state (persisted across sessions)
    public bool IsMuted { get; set; } = false;
    public bool IsDeafened { get; set; } = false;

    // Push-to-talk settings
    public bool PushToTalkEnabled { get; set; } = false;

    // Video settings
    public string? VideoDevice { get; set; }  // null = default/first camera

    // Camera quality settings
    public int CameraHeight { get; set; } = 360;               // 360, 720, 1080 (width calculated assuming 16:9)
    public int CameraFramerate { get; set; } = 15;              // 15, 30
    public int CameraBitrateMbps { get; set; } = 2;             // 1, 2, 4

    // Per-user volume settings (key: UserId as string, value: volume 0.0-3.0)
    public Dictionary<string, float> UserVolumes { get; set; } = new();

    // Quick Switcher recent items
    public List<RecentQuickSwitcherItem> RecentQuickSwitcherItems { get; set; } = new();

    // Panel widths (for sidebar resizing)
    public double ChannelListWidth { get; set; } = 260;
    public double MembersListWidth { get; set; } = 260;

    // Activity panel ratio (0.0 to 1.0, where 0.5 = 50% for activity, 50% for members)
    public double ActivityPanelRatio { get; set; } = 0.5;

    // Controller settings
    public bool ControllerRumbleEnabled { get; set; } = true;

    // Gaming Station settings
    public bool IsGamingStationEnabled { get; set; } = false;
    public string GamingStationDisplayName { get; set; } = "";  // Empty = use machine name

    // Onboarding flags
    public bool HasSeenWelcome { get; set; } = false;

    // Window position and size (null = use default/center)
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; } = false;
}

public class SettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private UserSettings _settings = new();

    public UserSettings Settings => _settings;

    public SettingsStore(string? profileName = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var baseDir = Path.Combine(appData, "Snacka");

        if (!string.IsNullOrEmpty(profileName))
        {
            baseDir = Path.Combine(baseDir, $"profile-{profileName}");
        }

        Directory.CreateDirectory(baseDir);
        _settingsPath = Path.Combine(baseDir, "settings.json");

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
                Console.WriteLine($"SettingsStore: Loaded settings from {_settingsPath}");
                Console.WriteLine($"  AudioInputDevice: {_settings.AudioInputDevice ?? "(default)"}");
                Console.WriteLine($"  AudioOutputDevice: {_settings.AudioOutputDevice ?? "(default)"}");
                Console.WriteLine($"  VideoDevice: {_settings.VideoDevice ?? "(default)"}");
                Console.WriteLine($"  CameraHeight: {_settings.CameraHeight}p");
                Console.WriteLine($"  CameraFramerate: {_settings.CameraFramerate}");
                Console.WriteLine($"  CameraBitrateMbps: {_settings.CameraBitrateMbps}");
            }
            else
            {
                Console.WriteLine("SettingsStore: No settings file found, using defaults");
                _settings = new UserSettings();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsStore: Failed to load settings - {ex.Message}");
            _settings = new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_settings, options);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SettingsStore: Failed to save settings - {ex.Message}");
        }
    }
}
