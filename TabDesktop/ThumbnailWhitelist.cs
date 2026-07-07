using System.IO;
using System.Text.Json;

namespace TabDesktop;

// Persisted opt-in lists controlling screenshot thumbnails, toggled from the tab right-click popup: the window screenshot stands in as the thumbnail for whitelisted executables (non-browser apps) or whitelisted site domains (browser tabs). Page thumbnails (poster/og:image) need no opt-in — the extension checks every focused page. Also holds the per-domain blacklist: some sites report thumbnails that are useless (generic banners, logos), and blacklisting the domain suppresses every thumbnail source for its tabs.
public static class ThumbnailWhitelist
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "thumbnail-whitelist.json");

    private static readonly object gate = new();
    private static readonly HashSet<string> screenshotExes = new(StringComparer.OrdinalIgnoreCase);
    // Screenshot opt-in for browser tabs is per-domain — whitelisting the browser exe would drag every site along.
    private static readonly HashSet<string> screenshotDomains = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> blockedDomains = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    static ThumbnailWhitelist()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            Config config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), options) ?? new Config();
            screenshotExes.UnionWith(config.ScreenshotExes);
            screenshotDomains.UnionWith(config.ScreenshotDomains);
            blockedDomains.UnionWith(config.BlockedDomains);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ThumbnailWhitelist), ex.ToString());
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

    public static bool IsScreenshotDomainWhitelisted(string? host)
    {
        if (host is null)
        {
            return false;
        }
        lock (gate)
        {
            return screenshotDomains.Contains(host);
        }
    }

    public static bool IsDomainBlocked(string? host)
    {
        if (host is null)
        {
            return false;
        }
        lock (gate)
        {
            return blockedDomains.Contains(host);
        }
    }

    public static void ToggleScreenshotForWindow(string windowTitle)
    {
        ToggleDomainForWindow(windowTitle, ToggleScreenshotDomain);
    }

    public static void ToggleBlockedForWindow(string windowTitle)
    {
        ToggleDomainForWindow(windowTitle, ToggleBlockedDomain);
    }

    private static void ToggleDomainForWindow(string windowTitle, Action<string> toggle)
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
            toggle(host);
        });
    }

    public static void ToggleScreenshotDomain(string host)
    {
        lock (gate)
        {
            if (!screenshotDomains.Add(host))
            {
                screenshotDomains.Remove(host);
            }
            Save();
        }
        Changed?.Invoke();
    }

    public static void ToggleBlockedDomain(string host)
    {
        lock (gate)
        {
            if (!blockedDomains.Add(host))
            {
                blockedDomains.Remove(host);
            }
            Save();
        }
        Changed?.Invoke();
    }

    public static List<string> GetScreenshotDomains()
    {
        lock (gate)
        {
            return screenshotDomains.OrderBy(d => d).ToList();
        }
    }

    public static List<string> GetBlockedDomains()
    {
        lock (gate)
        {
            return blockedDomains.OrderBy(d => d).ToList();
        }
    }

    public static List<string> GetScreenshotExes()
    {
        lock (gate)
        {
            return screenshotExes.OrderBy(e => e).ToList();
        }
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
            var config = new Config { ScreenshotExes = screenshotExes.ToList(), ScreenshotDomains = screenshotDomains.ToList(), BlockedDomains = blockedDomains.ToList() };
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
        public List<string> ScreenshotExes { get; set; } = new();
        public List<string> ScreenshotDomains { get; set; } = new();
        public List<string> BlockedDomains { get; set; } = new();
    }
}
