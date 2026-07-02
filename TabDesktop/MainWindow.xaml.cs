using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TabDesktop.Interop;

namespace TabDesktop;

public partial class MainWindow : Window
{
    private static readonly string PlacementPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "window-placement.json");
    private static readonly string TabOrderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "tab-order.json");
    private const string LoadingTitle = "(loading)";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    // A window joins a group when the intersection covers this fraction of the smaller of the two rects — min (not union) so a small window sitting on top of a big one still counts as "mostly overlapping".
    private const double GroupOverlapThreshold = 0.5;
    private static readonly TimeSpan PreviewMaxAge = TimeSpan.FromSeconds(15);

    private readonly ObservableCollection<WindowEntry> windows = new();
    private readonly ObservableCollection<WindowGroup> groups = new();
    private readonly List<TabStripWindow> tabStrips = new();
    private readonly Dictionary<IntPtr, long> previewCapturedAt = new();
    private IntPtr lastForeground;
    // Keyed by DisplayTitle rather than hwnd so the order survives app restarts and window recreation; for apps like Cursor the display title (the folder) is the stable identity even though the full title changes with every file switch.
    private readonly Dictionary<string, double> tabOrder;
    private double lastAssignedOrder;
    private bool tabOrderDirty;
    private readonly Dictionary<IntPtr, WindowEntry> entriesByHwnd = new();
    private readonly ProcessWorkerPool workerPool = new();
    private readonly DispatcherTimer refreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        tabOrder = LoadTabOrder();
        lastAssignedOrder = tabOrder.Count > 0 ? tabOrder.Values.Max() : 0;
        RestorePlacement();
        Closing += (_, _) => SavePlacement();

        WindowList.ItemsSource = windows;
        LayoutItems.ItemsSource = groups;
        refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        refreshTimer.Tick += (_, _) => RefreshWindows();
        refreshTimer.Start();
        Loaded += (_, _) => RefreshWindows();
    }

    private void RefreshWindows()
    {
        if (TitleRules.EnsureLoaded())
        {
            foreach (WindowEntry entry in windows)
            {
                entry.RefreshDerived();
            }
        }

        List<(IntPtr Hwnd, uint Pid, bool IsMinimized)> candidates = WindowScanner.FindCandidateWindows();

        DesktopBounds.Width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        DesktopBounds.Height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        var present = new HashSet<IntPtr>(candidates.Select(c => c.Hwnd));
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(windows[i].Hwnd))
            {
                entriesByHwnd.Remove(windows[i].Hwnd);
                previewCapturedAt.Remove(windows[i].Hwnd);
                windows.RemoveAt(i);
            }
        }

        // EnumWindows returns topmost-first; invert so the topmost window gets the highest canvas z-index.
        for (int i = 0; i < candidates.Count; i++)
        {
            (IntPtr hwnd, uint pid, _) = candidates[i];
            if (!entriesByHwnd.TryGetValue(hwnd, out WindowEntry? entry))
            {
                entry = new WindowEntry { Hwnd = hwnd, Pid = pid, Title = LoadingTitle };
                entriesByHwnd[hwnd] = entry;
                windows.Add(entry);
            }
            entry.ZIndex = candidates.Count - i;
        }

        var livePids = new HashSet<uint>(candidates.Select(c => c.Pid));
        workerPool.PruneExcept(livePids);

        // Minimized windows don't need a worker or a screenshot — their whole poll is the cheap cached-state read (mostly "is it visible yet"), done inline.
        var cheapResults = new List<WindowData>();
        foreach ((IntPtr hwnd, _, _) in candidates.Where(c => c.IsMinimized))
        {
            WindowData? data = WindowScanner.GatherBasics(hwnd);
            if (data is not null)
            {
                cheapResults.Add(data);
            }
        }
        ApplyResults(cheapResults);

        // Previews are captured only for the foreground window, and only when focus just switched to it or its cached preview has gone stale — a focused window is the one whose content is changing, and everything else keeps its last-focused snapshot.
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        IntPtr captureTarget = IntPtr.Zero;
        if (entriesByHwnd.ContainsKey(foreground))
        {
            bool stale = !previewCapturedAt.TryGetValue(foreground, out long capturedAt) || Stopwatch.GetElapsedTime(capturedAt) > PreviewMaxAge;
            if (foreground != lastForeground || stale)
            {
                captureTarget = foreground;
            }
        }
        lastForeground = foreground;

        foreach (WindowEntry entry in windows)
        {
            entry.IsForeground = entry.Hwnd == foreground;
        }
        if (entriesByHwnd.TryGetValue(foreground, out WindowEntry? focusedEntry))
        {
            focusedEntry.LastFocusedAt = Stopwatch.GetTimestamp();
        }

        foreach (IGrouping<uint, (IntPtr Hwnd, uint Pid, bool IsMinimized)> group in candidates.Where(c => !c.IsMinimized).GroupBy(c => c.Pid))
        {
            uint pid = group.Key;
            IntPtr[] hwnds = group.Select(c => c.Hwnd).ToArray();
            IntPtr pidCaptureTarget = hwnds.Contains(captureTarget) ? captureTarget : IntPtr.Zero;
            string description = $"{hwnds.Length} window(s): {string.Join(", ", hwnds.Select(h => entriesByHwnd[h].Title))}";
            bool scheduled = workerPool.TrySchedule(pid, description, () =>
            {
                var results = new List<WindowData>();
                foreach (IntPtr hwnd in hwnds)
                {
                    WindowData? data = WindowScanner.GatherOnWorkerThread(hwnd, hwnd == pidCaptureTarget);
                    if (data is not null)
                    {
                        results.Add(data);
                    }
                }
                Dispatcher.BeginInvoke(() => ApplyResults(results));
            });
            if (!scheduled && workerPool.IsHung(pid))
            {
                foreach (IntPtr hwnd in hwnds)
                {
                    if (entriesByHwnd.TryGetValue(hwnd, out WindowEntry? entry))
                    {
                        entry.Status = "Unresponsive";
                    }
                }
            }
        }

        UpdateWorkerTab();
    }

    private void UpdateWorkerTab()
    {
        List<WorkerStatus> statuses = workerPool.SnapshotStatus();
        int stalled = statuses.Count(s => s.State == "Stalled");
        (double pollsPerSecond, double pollMsPerSecond) = workerPool.GetRecentPollRate();
        WorkerSummary.Text = $"{statuses.Count} worker threads — {stalled} stalled — last 15 s: {pollsPerSecond:0.0} polls/s, {pollMsPerSecond:0.0} ms/s spent polling";
        WorkerList.ItemsSource = statuses.Select(s => new WorkerRow(s.Pid, s.State, FormatTook(s), s.LastPoll)).ToList();
    }

    private static string FormatTook(WorkerStatus status)
    {
        if (status.BusyForMs is double busyMs)
        {
            return $"{busyMs / 1000:0.0} s (running)";
        }
        if (status.LastPollMs is double lastMs)
        {
            return $"{lastMs:0} ms";
        }
        return "";
    }

    private sealed record WorkerRow(uint Pid, string State, string Took, string Poll);

    private void ApplyResults(List<WindowData> results)
    {
        double screenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        double screenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        foreach (WindowData data in results)
        {
            if (!entriesByHwnd.TryGetValue(data.Hwnd, out WindowEntry? entry))
            {
                continue;
            }
            entry.Title = data.Title;
            entry.PositionText = $"{data.Left}, {data.Top}";
            entry.SizeText = $"{data.Width} × {data.Height}";
            entry.Status = data.IsMinimized ? "Minimized" : "OK";
            if (data.Thumbnail is not null)
            {
                entry.Thumbnail = data.Thumbnail;
                previewCapturedAt[data.Hwnd] = Stopwatch.GetTimestamp();
            }
            entry.CanvasLeft = data.Left - screenLeft;
            entry.CanvasTop = data.Top - screenTop;
            entry.LayoutWidth = data.Width;
            entry.LayoutHeight = data.Height;
            entry.ShowInLayout = !data.IsMinimized;
        }
        RecomputeGroups();
    }

    private void RecomputeGroups()
    {
        var boxes = windows
            .Where(w => w.ShowInLayout && w.LayoutWidth > 0 && w.LayoutHeight > 0)
            .Select(w => (Bounds: new Rect(w.CanvasLeft, w.CanvasTop, w.LayoutWidth, w.LayoutHeight), Members: new List<WindowEntry> { w }))
            .ToList();

        // Merging can enlarge a group's bounds enough to newly overlap other boxes, so restart until a full pass produces no merge.
        bool mergedAny = true;
        while (mergedAny)
        {
            mergedAny = false;
            for (int i = 0; i < boxes.Count && !mergedAny; i++)
            {
                for (int j = i + 1; j < boxes.Count && !mergedAny; j++)
                {
                    if (MostlyOverlaps(boxes[i].Bounds, boxes[j].Bounds))
                    {
                        boxes[i].Members.AddRange(boxes[j].Members);
                        boxes[i] = (Rect.Union(boxes[i].Bounds, boxes[j].Bounds), boxes[i].Members);
                        boxes.RemoveAt(j);
                        mergedAny = true;
                    }
                }
            }
        }

        double screenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        double screenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        groups.Clear();
        foreach ((Rect bounds, List<WindowEntry> members) in boxes)
        {
            List<WindowEntry> ordered = members.OrderBy(EnsureOrder).ThenByDescending(m => m.ZIndex).ToList();
            WindowEntry? lastFocused = ordered.Where(m => m.LastFocusedAt != 0).OrderByDescending(m => m.LastFocusedAt).FirstOrDefault();
            foreach (WindowEntry member in ordered)
            {
                member.IsGroupLastFocused = member == lastFocused;
            }
            groups.Add(new WindowGroup
            {
                CanvasLeft = bounds.X,
                CanvasTop = bounds.Y,
                ScreenLeft = bounds.X + screenLeft,
                ScreenTop = bounds.Y + screenTop,
                Width = bounds.Width,
                Height = bounds.Height,
                CountText = ordered.Count.ToString(),
                Members = ordered,
                ZIndex = ordered.Max(m => m.ZIndex),
            });
        }
        if (tabOrderDirty)
        {
            SaveTabOrder();
            tabOrderDirty = false;
        }

        SyncTabStrips();
    }

    private double EnsureOrder(WindowEntry entry)
    {
        if (entry.OrderKey != 0)
        {
            return entry.OrderKey;
        }
        if (entry.Title == LoadingTitle)
        {
            return 0;
        }
        if (tabOrder.TryGetValue(entry.DisplayTitle, out double existing))
        {
            entry.OrderKey = existing;
            return existing;
        }
        double nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double assigned = Math.Max(nowSeconds, lastAssignedOrder + 1);
        lastAssignedOrder = assigned;
        entry.OrderKey = assigned;
        tabOrder[entry.DisplayTitle] = assigned;
        tabOrderDirty = true;
        return assigned;
    }

    private void ReorderTab(WindowEntry entry, double newKey)
    {
        entry.OrderKey = newKey;
        if (entry.Title != LoadingTitle)
        {
            tabOrder[entry.DisplayTitle] = newKey;
            SaveTabOrder();
        }
        RecomputeGroups();
    }

    private Dictionary<string, double> LoadTabOrder()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(TabOrderPath)) ?? new Dictionary<string, double>();
        }
        catch
        {
            return new Dictionary<string, double>();
        }
    }

    private void SaveTabOrder()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TabOrderPath)!);
            File.WriteAllText(TabOrderPath, JsonSerializer.Serialize(tabOrder));
        }
        catch
        {
        }
    }

    private void SyncTabStrips()
    {
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var unmatched = new List<TabStripWindow>(tabStrips);
        foreach (WindowGroup group in groups.Where(g => g.Members.Count >= 2))
        {
            // Reuse the strip sharing the most windows with this group so strips update in place instead of being torn down and recreated (which flickers) every refresh.
            TabStripWindow? best = null;
            int bestShared = 0;
            foreach (TabStripWindow strip in unmatched)
            {
                int shared = group.Members.Count(m => strip.MemberHwnds.Contains(m.Hwnd));
                if (shared > bestShared)
                {
                    bestShared = shared;
                    best = strip;
                }
            }
            if (best is null)
            {
                best = new TabStripWindow(FocusWindow, ReorderTab, ShowMainWindow);
                tabStrips.Add(best);
                best.Update(group, dpiScale);
                best.Show();
            }
            else
            {
                unmatched.Remove(best);
                best.Update(group, dpiScale);
            }
        }
        foreach (TabStripWindow leftover in unmatched)
        {
            tabStrips.Remove(leftover);
            leftover.Close();
        }
    }

    private void ShowMainWindow()
    {
        WindowState = WindowState.Normal;
        Left = SystemParameters.VirtualScreenLeft + (SystemParameters.VirtualScreenWidth - Width) / 2;
        Top = SystemParameters.VirtualScreenTop + (SystemParameters.VirtualScreenHeight - Height) / 2;
        Show();
        Activate();
    }

    private static void FocusWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        }
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private static bool MostlyOverlaps(Rect a, Rect b)
    {
        Rect intersection = Rect.Intersect(a, b);
        if (intersection.IsEmpty)
        {
            return false;
        }
        double smallerArea = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smallerArea > 0 && intersection.Width * intersection.Height / smallerArea >= GroupOverlapThreshold;
    }

    private record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    private void RestorePlacement()
    {
        Placement? placement;
        try
        {
            placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath));
        }
        catch
        {
            return;
        }
        if (placement is null || placement.Width <= 0 || placement.Height <= 0)
        {
            return;
        }
        // Clamp to the virtual screen so the window doesn't restore off-screen after a monitor is removed or rearranged.
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        Width = Math.Min(placement.Width, SystemParameters.VirtualScreenWidth);
        Height = Math.Min(placement.Height, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(placement.Left, screenLeft, Math.Max(screenLeft, screenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Clamp(placement.Top, screenTop, Math.Max(screenTop, screenTop + SystemParameters.VirtualScreenHeight - Height));
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        // When maximized (or minimized), Left/Top/Width/Height describe the maximized rect, so persist the normal-state bounds instead.
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
        {
            return;
        }
        var placement = new Placement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementPath)!);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(placement));
        }
        catch
        {
        }
    }
}
