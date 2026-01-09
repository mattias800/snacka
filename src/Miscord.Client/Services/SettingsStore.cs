using System.Text.Json;
using Miscord.Client.ViewModels;

namespace Miscord.Client.Services;

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
    public float InputGain { get; set; } = 1.0f;   // 0.0 to 3.0 (0% to 300%)
    public float GateThreshold { get; set; } = 0.02f; // 0.0 to 0.5 (normalized RMS threshold)
    public bool GateEnabled { get; set; } = true;

    // Voice channel state (persisted across sessions)
    public bool IsMuted { get; set; } = false;
    public bool IsDeafened { get; set; } = false;

    // Push-to-talk settings
    public bool PushToTalkEnabled { get; set; } = false;

    // Video settings
    public string? VideoDevice { get; set; }  // null = default/first camera

    // Per-user volume settings (key: UserId as string, value: volume 0.0-2.0)
    public Dictionary<string, float> UserVolumes { get; set; } = new();

    // Quick Switcher recent items
    public List<RecentQuickSwitcherItem> RecentQuickSwitcherItems { get; set; } = new();
}

public class SettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private UserSettings _settings = new();

    public UserSettings Settings => _settings;

    public SettingsStore(string? profileName = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var baseDir = Path.Combine(appData, "Miscord");

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
