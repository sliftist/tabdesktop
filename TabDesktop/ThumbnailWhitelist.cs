using System.IO;
using System.Text.Json;

namespace TabDesktop;

// Persisted opt-in lists controlling thumbnail resolution, toggled from the tab right-click popup: video thumbnails run only for whitelisted domains (so we don't chase a thumbnail for every open tab), and the window screenshot stands in as the thumbnail for whitelisted executables — the fallback for non-browser apps.
public static class ThumbnailWhitelist
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "thumbnail-whitelist.json");
    // Seeded on first run to preserve the behavior from before the whitelist existed.
    private const string DefaultDomain = "youtube.com";

    private static readonly object gate = new();
    private static readonly HashSet<string> domains = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> screenshotExes = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    static ThumbnailWhitelist()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                domains.Add(DefaultDomain);
                Save();
                return;
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), options) ?? new Config();
            domains.UnionWith(config.Domains);
            screenshotExes.UnionWith(config.ScreenshotExes);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailWhitelist), ex.ToString());
        }
    }

    public static bool IsDomainWhitelisted(string? host)
    {
        if (host is null)
        {
            return false;
        }
        lock (gate)
        {
            return domains.Contains(host);
        }
    }

    public static bool IsScreenshotExe(string? exePath)
    {
        if (exePath is null)
        {
            return false;
        }
        lock (gate)
        {
            return screenshotExes.Contains(exePath);
        }
    }

    // Resolving the tab's domain may need a browser History query, so the toggle runs off-thread.
    public static void ToggleDomainForWindow(string windowTitle)
    {
        Task.Run(() =>
        {
            string? host = null;
            try
            {
                host = TabDomains.Resolve(windowTitle);
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(ThumbnailWhitelist), ex.ToString());
            }
            if (host is null)
            {
                AppLog.Write(nameof(ThumbnailWhitelist), $"Could not determine the domain for \"{windowTitle}\" — no extension report and no History match yet.");
                return;
            }
            ToggleDomain(host);
        });
    }

    public static void ToggleDomain(string host)
    {
        lock (gate)
        {
            if (!domains.Add(host))
            {
                domains.Remove(host);
            }
            Save();
        }
        Changed?.Invoke();
    }

    public static void ToggleScreenshotExe(string exePath)
    {
        lock (gate)
        {
            if (!screenshotExes.Add(exePath))
            {
                screenshotExes.Remove(exePath);
            }
            Save();
        }
        Changed?.Invoke();
    }

    private static void Save()
    {
        try
        {
            var config = new Config { Domains = domains.ToList(), ScreenshotExes = screenshotExes.ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailWhitelist), ex.ToString());
        }
    }

    private sealed class Config
    {
        public List<string> Domains { get; set; } = new();
        public List<string> ScreenshotExes { get; set; } = new();
    }
}
