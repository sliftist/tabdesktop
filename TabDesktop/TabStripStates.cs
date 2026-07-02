using System.IO;
using System.Text.Json;
using System.Windows;

namespace TabDesktop;

// Persists each strip's collapsed/double-height choice keyed by the screen region its group covers: window groups have no stable identity across restarts, but the area they occupy does — a group whose bounds mostly overlap a saved region is the same tabbed interface. The saved rect is refreshed on every toggle so it tracks the group as windows drift. UI-thread only, so no locking.
public static class TabStripStates
{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "strip-states.json");
    // Same "covers most of the smaller rect" notion the window grouping itself uses.
    private const double MatchOverlapFraction = 0.5;
    private const int MaxEntries = 200;

    private static List<Entry> entries = new();

    static TabStripStates()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(ConfigPath)) ?? new List<Entry>();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(TabStripStates), ex.ToString());
        }
    }

    public static (bool Collapsed, bool DoubleHeight)? TryGet(Rect bounds)
    {
        Entry? match = FindMatch(bounds);
        if (match is null)
        {
            return null;
        }
        // Recency only persists on the next Set — not worth a disk write per refresh tick.
        match.LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (match.Collapsed, match.DoubleHeight);
    }

    public static void Set(Rect bounds, bool collapsed, bool doubleHeight)
    {
        Entry? match = FindMatch(bounds);
        if (match is null)
        {
            match = new Entry();
            entries.Add(match);
        }
        match.Left = bounds.X;
        match.Top = bounds.Y;
        match.Width = bounds.Width;
        match.Height = bounds.Height;
        match.Collapsed = collapsed;
        match.DoubleHeight = doubleHeight;
        match.LastUsedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (entries.Count > MaxEntries)
        {
            entries = entries.OrderByDescending(e => e.LastUsedUnixMs).Take(MaxEntries).ToList();
        }
        Save();
    }

    private static Entry? FindMatch(Rect bounds)
    {
        Entry? best = null;
        double bestFraction = MatchOverlapFraction;
        foreach (Entry entry in entries)
        {
            var saved = new Rect(entry.Left, entry.Top, entry.Width, entry.Height);
            Rect intersection = Rect.Intersect(saved, bounds);
            if (intersection.IsEmpty)
            {
                continue;
            }
            double smallerArea = Math.Min(saved.Width * saved.Height, bounds.Width * bounds.Height);
            if (smallerArea <= 0)
            {
                continue;
            }
            double fraction = intersection.Width * intersection.Height / smallerArea;
            if (fraction >= bestFraction)
            {
                bestFraction = fraction;
                best = entry;
            }
        }
        return best;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(TabStripStates), ex.ToString());
        }
    }

    private sealed class Entry
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Collapsed { get; set; }
        public bool DoubleHeight { get; set; }
        public long LastUsedUnixMs { get; set; }
    }
}
