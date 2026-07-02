using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TabDesktop;

// Persists which windows have their browser tabs expanded, mapping hwnd → the extension's window id. The id is resolved by title exactly once (at expand time) and locked in here — the pairing must never be re-derived from titles, which follow the active tab and can transiently match the wrong window. Hwnds belong to the browser process, so both halves of the pair survive TabDesktop restarts (the main use: dev-loop rebuilds) and die together when the browser window closes — a saved hwnd that hasn't been seen all session is dead and gets purged, both to keep the file small and because Windows recycles hwnd values onto unrelated windows.
public static class ExpandedTabWindows
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "expanded-windows.json");
    // Long enough that a slow session start (or a window briefly cloaked on another virtual desktop) isn't mistaken for a dead window.
    private static readonly TimeSpan UnseenPurgeAfter = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<long, int?> saved = new();
    private static readonly HashSet<long> seen = new();
    private static readonly long startedAt = Stopwatch.GetTimestamp();

    static ExpandedTabWindows()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Dictionary<long, int?> loaded = JsonSerializer.Deserialize<Dictionary<long, int?>>(File.ReadAllText(ConfigPath)) ?? new Dictionary<long, int?>();
                foreach ((long hwnd, int? windowId) in loaded)
                {
                    saved[hwnd] = windowId;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExpandedTabWindows), ex.ToString());
        }
    }

    public static List<(long Hwnd, int? WindowId)> GetAll()
    {
        return saved.Select(pair => (pair.Key, pair.Value)).OrderBy(pair => pair.Key).ToList();
    }

    public static bool Contains(IntPtr hwnd)
    {
        return saved.ContainsKey(hwnd.ToInt64());
    }

    public static int? GetWindowId(IntPtr hwnd)
    {
        return saved.GetValueOrDefault(hwnd.ToInt64());
    }

    public static void SetWindowId(IntPtr hwnd, int windowId)
    {
        long id = hwnd.ToInt64();
        if (saved.TryGetValue(id, out int? existing) && existing != windowId)
        {
            saved[id] = windowId;
            Save();
        }
    }

    public static void MarkSeen(IntPtr hwnd)
    {
        seen.Add(hwnd.ToInt64());
    }

    public static void Set(IntPtr hwnd, bool expanded)
    {
        long id = hwnd.ToInt64();
        bool changed = expanded ? saved.TryAdd(id, null) : saved.Remove(id);
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
        List<long> dead = saved.Keys.Where(id => !seen.Contains(id)).ToList();
        if (dead.Count == 0)
        {
            return;
        }
        foreach (long id in dead)
        {
            saved.Remove(id);
        }
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(ExpandedTabWindows), ex.ToString());
        }
    }
}
