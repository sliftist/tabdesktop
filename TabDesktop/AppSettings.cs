using System.IO;
using System.Text.Json;

namespace TabDesktop;

// App-wide user settings, edited from the Settings tab. Basic mode keeps the strip header to count/collapse/home; every other strip button is an advanced-mode extra, and any future strip buttons must be gated behind AdvancedMode the same way.
public static class AppSettings
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "settings.json");

    public static bool AdvancedMode { get; private set; }

    // Off by default: a fullscreen window (movie, game) covering a monitor hides that monitor's strips so our topmost UI doesn't sit on top of it.
    public static bool ShowWhenFullscreen { get; private set; }

    public static event Action? Changed;

    static AppSettings()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
                AdvancedMode = config.AdvancedMode;
                ShowWhenFullscreen = config.ShowWhenFullscreen;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(AppSettings), ex.ToString());
        }
    }

    public static void SetAdvancedMode(bool value)
    {
        if (AdvancedMode == value)
        {
            return;
        }
        AdvancedMode = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetShowWhenFullscreen(bool value)
    {
        if (ShowWhenFullscreen == value)
        {
            return;
        }
        ShowWhenFullscreen = value;
        Save();
        Changed?.Invoke();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config { AdvancedMode = AdvancedMode, ShowWhenFullscreen = ShowWhenFullscreen }));
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(AppSettings), ex.ToString());
        }
    }

    private sealed class Config
    {
        public bool AdvancedMode { get; set; }
        public bool ShowWhenFullscreen { get; set; }
    }
}
