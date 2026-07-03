using System.IO;
using System.Text.Json;

namespace TabDesktop;

// App-wide user settings, edited from the Settings tab. Basic mode keeps the strip header to count/collapse/home; every other strip button is an advanced-mode extra, and any future strip buttons must be gated behind AdvancedMode the same way.
public static class AppSettings
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "settings.json");

    public const string DefaultSearchHotkey = "Win+A";

    public static bool AdvancedMode { get; private set; }

    // Off by default: a fullscreen window (movie, game) covering a monitor hides that monitor's strips so our topmost UI doesn't sit on top of it.
    public static bool ShowWhenFullscreen { get; private set; }

    // Off by default: the search hotkey is global, so it must be an explicit opt-in rather than silently shadowing a combination the user already relies on.
    public static bool SearchEnabled { get; private set; }

    public static string SearchHotkey { get; private set; } = DefaultSearchHotkey;

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
                SearchEnabled = config.SearchEnabled;
                SearchHotkey = string.IsNullOrWhiteSpace(config.SearchHotkey) ? DefaultSearchHotkey : config.SearchHotkey;
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

    public static void SetSearchEnabled(bool value)
    {
        if (SearchEnabled == value)
        {
            return;
        }
        SearchEnabled = value;
        Save();
        Changed?.Invoke();
    }

    public static void SetSearchHotkey(string value)
    {
        if (SearchHotkey == value)
        {
            return;
        }
        SearchHotkey = value;
        Save();
        Changed?.Invoke();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new Config { AdvancedMode = AdvancedMode, ShowWhenFullscreen = ShowWhenFullscreen, SearchEnabled = SearchEnabled, SearchHotkey = SearchHotkey }));
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
        public bool SearchEnabled { get; set; }
        public string? SearchHotkey { get; set; }
    }
}
