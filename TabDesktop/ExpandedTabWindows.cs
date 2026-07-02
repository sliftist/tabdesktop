using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TabDesktop;

// Persists which windows have their browser tabs expanded, keyed by hwnd. Hwnds belong to the browser process, so they survive TabDesktop restarts (the main use: dev-loop rebuilds) but never survive the window itself closing — a saved hwnd that hasn't been seen all session is dead and gets purged, both to keep the file small and because Windows recycles hwnd values onto unrelated windows.
public static class ExpandedTabWindows
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "expanded-windows.json");
    // Long enough that a slow session start (or a window briefly cloaked on another virtual desktop) isn't mistaken for a dead window.
    private static readonly TimeSpan UnseenPurgeAfter = TimeSpan.FromMinutes(5);

    private static readonly HashSet<long> saved = new();
    private static readonly HashSet<long> seen = new();
    private static readonly long startedAt = Stopwatch.GetTimestamp();

    static ExpandedTabWindows()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                saved.UnionWith(JsonSerializer.Deserialize<List<long>>(File.ReadAllText(ConfigPath)) ?? new List<long>());
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExpandedTabWindows), ex.ToString());
        }
    }

    public static bool Contains(IntPtr hwnd)
    {
        return saved.Contains(hwnd.ToInt64());
    }

    public static void MarkSeen(IntPtr hwnd)
    {
        seen.Add(hwnd.ToInt64());
    }

    public static void Set(IntPtr hwnd, bool expanded)
    {
        long id = hwnd.ToInt64();
        bool changed = expanded ? saved.Add(id) : saved.Remove(id);
        if (changed)
        {
            Save();
        }
    }

    public static void PurgeUnseen()
    {
        if (Stopwatch.GetElapsedTime(startedAt) < UnseenPurgeAfter)
        {
            return;
        }
        if (saved.RemoveWhere(id => !seen.Contains(id)) > 0)
        {
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(saved.ToList()));
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExpandedTabWindows), ex.ToString());
        }
    }
}
