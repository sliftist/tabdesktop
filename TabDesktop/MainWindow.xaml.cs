using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private static readonly TimeSpan TitleChangeCaptureMinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleFadeStart = TimeSpan.FromHours(24);
    private static readonly TimeSpan IdleFadeEnd = TimeSpan.FromHours(72);
    private const double MaxIdleTransparency = 0.25;
    // The fade moves imperceptibly slowly, so a coarse recompute cadence is plenty.
    private static readonly TimeSpan IdleFadeRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstallFeedbackDuration = TimeSpan.FromSeconds(1.5);
    // Expanded browser-tab entries slot between their parent and the next real window without disturbing persisted order keys.
    private const double BrowserTabOrderStep = 0.001;

    private readonly ObservableCollection<WindowEntry> windows = new();
    private readonly ObservableCollection<WindowGroup> groups = new();
    // Settings-tab rows persist across refreshes (matched to groups by shared hwnds) so their expanders don't collapse every tick.
    private readonly ObservableCollection<GroupRow> settingsGroups = new();
    private ExtensionInstallWindow? installWindow;
    private readonly List<TabStripWindow> tabStrips = new();
    private readonly Dictionary<IntPtr, long> previewCapturedAt = new();
    private IntPtr lastForeground;
    // Keyed by DisplayTitle rather than hwnd so the order survives app restarts and window recreation; for apps like Cursor the display title (the folder) is the stable identity even though the full title changes with every file switch.
    private readonly Dictionary<string, double> tabOrder;
    private double lastAssignedOrder;
    private bool tabOrderDirty;
    private readonly Dictionary<IntPtr, WindowEntry> entriesByHwnd = new();
    private readonly HashSet<IntPtr> pendingTitleCaptures = new();
    // Pseudo-entries for expanded browser windows, cached per (hwnd, tab id) so per-tab state — most importantly the screenshot captured while that tab was selected — survives refreshes and tab switches.
    private readonly Dictionary<(IntPtr Hwnd, int TabId), WindowEntry> browserTabEntries = new();
    // Once a window has matched an extension window id, remember it: during a tab switch the window title and the reported active tab briefly disagree, and without this the expansion would flicker closed.
    private readonly Dictionary<IntPtr, int> browserWindowIdByHwnd = new();
    private readonly ProcessWorkerPool workerPool = new();
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer idleFadeTimer;
    // Baseline for windows never focused while the app has been running: their true last-focus time is unknowable, so idle is measured from app start.
    private static readonly long appStartedAt = Stopwatch.GetTimestamp();

    public MainWindow()
    {
        InitializeComponent();
        tabOrder = LoadTabOrder();
        lastAssignedOrder = tabOrder.Count > 0 ? tabOrder.Values.Max() : 0;
        RestorePlacement();
        Closing += (_, _) => SavePlacement();

        WindowList.ItemsSource = windows;
        LayoutItems.ItemsSource = groups;
        LogList.ItemsSource = AppLog.Entries;
        SettingsGroups.ItemsSource = settingsGroups;
        AdvancedModeCheck.IsChecked = AppSettings.AdvancedMode;
        RunOnStartupCheck.IsChecked = Installer.IsAutoStartEnabled();
        // Strips re-read AdvancedMode inside their layout pass; a full refresh pushes the change to every strip immediately.
        AppSettings.Changed += () => Dispatcher.BeginInvoke(RefreshWindows);
        // Extension reports and whitelist toggles happen off the UI thread; re-raise the derived bindings so tabs pick up new thumbnails and toggle states.
        Action refreshDerived = () => Dispatcher.BeginInvoke(() =>
        {
            foreach (WindowEntry entry in windows)
            {
                entry.RefreshDerived();
            }
            foreach (WindowEntry entry in browserTabEntries.Values)
            {
                entry.RefreshDerived();
            }
        });
        ExtensionThumbnails.Updated += refreshDerived;
        ThumbnailWhitelist.Changed += refreshDerived;
        refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        refreshTimer.Tick += (_, _) => RefreshWindows();
        refreshTimer.Start();
        idleFadeTimer = new DispatcherTimer { Interval = IdleFadeRefreshInterval };
        idleFadeTimer.Tick += (_, _) => UpdateIdleOpacity();
        idleFadeTimer.Start();
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
                IntPtr gone = windows[i].Hwnd;
                entriesByHwnd.Remove(gone);
                previewCapturedAt.Remove(gone);
                pendingTitleCaptures.Remove(gone);
                browserWindowIdByHwnd.Remove(gone);
                foreach ((IntPtr Hwnd, int TabId) key in browserTabEntries.Keys.Where(k => k.Hwnd == gone).ToList())
                {
                    browserTabEntries.Remove(key);
                }
                windows.RemoveAt(i);
            }
        }

        // EnumWindows returns topmost-first; invert so the topmost window gets the highest canvas z-index.
        for (int i = 0; i < candidates.Count; i++)
        {
            (IntPtr hwnd, uint pid, _) = candidates[i];
            if (!entriesByHwnd.TryGetValue(hwnd, out WindowEntry? entry))
            {
                entry = new WindowEntry { Hwnd = hwnd, Pid = pid, Title = LoadingTitle, ExePath = ProcessDirectory.GetExecutablePath(pid) };
                entry.ExpandTabs = ExpandedTabWindows.Contains(hwnd);
                ExpandedTabWindows.MarkSeen(hwnd);
                entriesByHwnd[hwnd] = entry;
                windows.Add(entry);
            }
            entry.ZIndex = candidates.Count - i;
        }

        ApplyDirectoryTitles();
        ExpandedTabWindows.PurgeUnseen();

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

        // Previews are captured for the foreground window when focus just switched to it or its cached preview has gone stale (a focused window is the one whose content is changing), plus any screenshot-whitelisted window whose title changed — a new title usually means new content (next video/track), throttled via TitleChangeCaptureMinInterval.
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var captureTargets = new HashSet<IntPtr>();
        if (entriesByHwnd.ContainsKey(foreground))
        {
            bool stale = !previewCapturedAt.TryGetValue(foreground, out long capturedAt) || Stopwatch.GetElapsedTime(capturedAt) > PreviewMaxAge;
            if (foreground != lastForeground || stale)
            {
                captureTargets.Add(foreground);
            }
        }
        lastForeground = foreground;
        pendingTitleCaptures.RemoveWhere(hwnd =>
        {
            if (!entriesByHwnd.ContainsKey(hwnd))
            {
                return true;
            }
            bool throttled = previewCapturedAt.TryGetValue(hwnd, out long at) && Stopwatch.GetElapsedTime(at) < TitleChangeCaptureMinInterval;
            if (throttled)
            {
                return false;
            }
            captureTargets.Add(hwnd);
            return true;
        });

        foreach (WindowEntry entry in windows)
        {
            entry.IsForeground = entry.Hwnd == foreground;
        }
        if (entriesByHwnd.TryGetValue(foreground, out WindowEntry? focusedEntry))
        {
            focusedEntry.LastFocusedAt = Stopwatch.GetTimestamp();
            // Un-fade immediately on focus rather than waiting out the slow fade timer.
            focusedEntry.IdleOpacity = 1;
        }

        foreach (IGrouping<uint, (IntPtr Hwnd, uint Pid, bool IsMinimized)> group in candidates.Where(c => !c.IsMinimized).GroupBy(c => c.Pid))
        {
            uint pid = group.Key;
            IntPtr[] hwnds = group.Select(c => c.Hwnd).ToArray();
            string description = $"{hwnds.Length} window(s): {string.Join(", ", hwnds.Select(h => entriesByHwnd[h].Title))}";
            bool scheduled = workerPool.TrySchedule(pid, description, () =>
            {
                var results = new List<WindowData>();
                foreach (IntPtr hwnd in hwnds)
                {
                    WindowData? data = WindowScanner.GatherOnWorkerThread(hwnd, captureTargets.Contains(hwnd));
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
        ExtensionStatusText.Text = ExtensionThumbnails.IsConnected
            ? "Connected — the browser extension is reporting. Tab thumbnails, per-tab expansion, and tab switching are available."
            : "Not connected — no reports from the browser extension. Install it below, or check that the browser is running and the extension is enabled.";
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
            string previousTitle = entry.Title;
            entry.Title = data.Title;
            if (previousTitle != data.Title && previousTitle != LoadingTitle && entry.IsScreenshotThumbnailEnabled)
            {
                pendingTitleCaptures.Add(data.Hwnd);
            }
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
            var display = new List<WindowEntry>();
            // Expanded browser tabs always sort after every real window, so a fan of tabs can't bury the actual windows in the strip.
            var expandedTail = new List<WindowEntry>();
            foreach (WindowEntry member in ordered)
            {
                member.IsGroupLastFocused = member == lastFocused;
                List<BrowserTab>? tabs = member.ExpandTabs ? GetTabsForExpandedWindow(member) : null;
                if (tabs is null || tabs.Count == 0)
                {
                    display.Add(member);
                    continue;
                }
                expandedTail.AddRange(BuildBrowserTabEntries(member, tabs));
            }
            display.AddRange(expandedTail);
            groups.Add(new WindowGroup
            {
                CanvasLeft = bounds.X,
                CanvasTop = bounds.Y,
                ScreenLeft = bounds.X + screenLeft,
                ScreenTop = bounds.Y + screenTop,
                Width = bounds.Width,
                Height = bounds.Height,
                CountText = display.Count.ToString(),
                Members = display,
                ZIndex = ordered.Max(m => m.ZIndex),
            });
        }
        if (tabOrderDirty)
        {
            SaveTabOrder();
            tabOrderDirty = false;
        }

        SyncTabStrips();
        SyncSettingsGroups();
    }

    private void SyncSettingsGroups()
    {
        var unmatched = new List<GroupRow>(settingsGroups);
        foreach (WindowGroup group in groups.Where(g => g.Members.Count >= 2))
        {
            var hwnds = group.Members.Select(m => m.Hwnd).ToHashSet();
            GroupRow? best = null;
            int bestShared = 0;
            foreach (GroupRow row in unmatched)
            {
                int shared = row.MemberHwnds.Count(hwnds.Contains);
                if (shared > bestShared)
                {
                    bestShared = shared;
                    best = row;
                }
            }
            if (best is null)
            {
                best = new GroupRow();
                settingsGroups.Add(best);
            }
            else
            {
                unmatched.Remove(best);
            }
            UpdateGroupRow(best, group);
        }
        foreach (GroupRow leftover in unmatched)
        {
            settingsGroups.Remove(leftover);
        }
    }

    private static void UpdateGroupRow(GroupRow row, WindowGroup group)
    {
        row.MemberHwnds = group.Members.Select(m => m.Hwnd).ToHashSet();
        row.Bounds = new Rect(group.ScreenLeft, group.ScreenTop, group.Width, group.Height);
        row.Header = $"{group.Members.Count} tabs — {group.Width:0}×{group.Height:0} at {group.ScreenLeft:0}, {group.ScreenTop:0}";
        (bool Collapsed, bool DoubleHeight)? state = TabStripStates.TryGet(row.Bounds);
        row.SizeButtonText = SizeButtonLabel(state is not null && state.Value.DoubleHeight);
        List<string> titles = group.Members.Select(m => m.DisplayTitle).ToList();
        if (!titles.SequenceEqual(row.Titles))
        {
            row.Titles = titles;
        }
    }

    private static string SizeButtonLabel(bool tall)
    {
        return tall ? "Tall — make normal" : "Normal — make tall";
    }

    private void OnAdvancedModeChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.SetAdvancedMode(AdvancedModeCheck.IsChecked ?? false);
    }

    private void OnRunOnStartupChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            Installer.SetAutoStart(RunOnStartupCheck.IsChecked ?? false);
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(Installer), ex.ToString());
        }
    }

    private void OnInstallApp(object sender, RoutedEventArgs e)
    {
        try
        {
            Installer.Install();
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(Installer), ex.ToString());
            return;
        }
        RunOnStartupCheck.IsChecked = true;
        InstallAppButton.Content = "Installed!";
        var revert = new DispatcherTimer { Interval = InstallFeedbackDuration };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            InstallAppButton.Content = "Install TabDesktop";
        };
        revert.Start();
    }

    private void OnOpenExtensionInstall(object sender, RoutedEventArgs e)
    {
        if (installWindow is null)
        {
            installWindow = new ExtensionInstallWindow();
            installWindow.Closed += (_, _) => installWindow = null;
            installWindow.Show();
        }
        else
        {
            installWindow.Activate();
        }
    }

    private void OnRebuildRestartClick(object sender, RoutedEventArgs e)
    {
        TabStripWindow.LaunchRebuildRestart();
    }

    private void OnToggleGroupSize(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not GroupRow row)
        {
            return;
        }
        (bool Collapsed, bool DoubleHeight)? state = TabStripStates.TryGet(row.Bounds);
        bool collapsed = state is not null && state.Value.Collapsed;
        bool tall = state is not null && state.Value.DoubleHeight;
        TabStripStates.Set(row.Bounds, collapsed, !tall);
        row.SizeButtonText = SizeButtonLabel(!tall);
        e.Handled = true;
    }

    private void OnSavedStateExpanded(object sender, RoutedEventArgs e)
    {
        RefreshSavedState();
    }

    private void OnSavedStateSearch(object sender, TextChangedEventArgs e)
    {
        RefreshSavedState();
    }

    private void OnRemoveSavedState(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is SavedStateRow row)
        {
            row.Remove();
            RefreshSavedState();
        }
    }

    private void RefreshSavedState()
    {
        string query = SavedStateSearch.Text.Trim();
        var rows = new List<SavedStateRow>();
        foreach (string domain in ThumbnailWhitelist.GetScreenshotDomains())
        {
            rows.Add(new SavedStateRow("Screenshot domain", domain, () => ThumbnailWhitelist.ToggleScreenshotDomain(domain)));
        }
        foreach (string exe in ThumbnailWhitelist.GetScreenshotExes())
        {
            rows.Add(new SavedStateRow("Screenshot program", exe, () => ThumbnailWhitelist.ToggleScreenshotExe(exe)));
        }
        foreach (string exe in DirectoryTitles.GetEnabled())
        {
            rows.Add(new SavedStateRow("Directory title program", exe, () => DirectoryTitles.Toggle(exe)));
        }
        foreach ((long hwnd, int? windowId) in ExpandedTabWindows.GetAll())
        {
            string value = $"hwnd {hwnd}" + (windowId is int id ? $" → browser window {id}" : "");
            rows.Add(new SavedStateRow("Expanded browser window", value, () => ExpandedTabWindows.Set(new IntPtr(hwnd), false)));
        }
        if (query.Length > 0)
        {
            rows = rows.Where(r => r.Category.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        SavedStateList.ItemsSource = rows;
    }

    private sealed record SavedStateRow(string Category, string Value, Action Remove);

    private sealed class GroupRow : INotifyPropertyChanged
    {
        public HashSet<IntPtr> MemberHwnds = new();
        public Rect Bounds;

        private string header = "";
        public string Header
        {
            get => header;
            set
            {
                if (header != value)
                {
                    header = value;
                    Raise(nameof(Header));
                }
            }
        }

        private string sizeButtonText = "";
        public string SizeButtonText
        {
            get => sizeButtonText;
            set
            {
                if (sizeButtonText != value)
                {
                    sizeButtonText = value;
                    Raise(nameof(SizeButtonText));
                }
            }
        }

        private List<string> titles = new();
        public List<string> Titles
        {
            get => titles;
            set
            {
                titles = value;
                Raise(nameof(Titles));
            }
        }

        private bool isExpanded;
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded != value)
                {
                    isExpanded = value;
                    Raise(nameof(IsExpanded));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // The extension window id is resolved by title exactly once — right after the user expands the window, when its title is fresh — then locked in (in memory and on disk). Re-matching by title on every refresh is wrong: window titles follow the active tab, so a stale or duplicated title can pair the expansion with whichever window happens to share it.
    private List<BrowserTab>? GetTabsForExpandedWindow(WindowEntry member)
    {
        if (!browserWindowIdByHwnd.TryGetValue(member.Hwnd, out int windowId))
        {
            int? persisted = ExpandedTabWindows.GetWindowId(member.Hwnd);
            if (persisted is null)
            {
                List<BrowserTab>? matched = ExtensionThumbnails.TryGetTabsForWindow(member.Title);
                if (matched is null || matched.Count == 0)
                {
                    return null;
                }
                persisted = matched[0].WindowId;
                ExpandedTabWindows.SetWindowId(member.Hwnd, persisted.Value);
            }
            windowId = persisted.Value;
            browserWindowIdByHwnd[member.Hwnd] = windowId;
        }
        return ExtensionThumbnails.TryGetTabsByWindowId(windowId);
    }

    private List<WindowEntry> BuildBrowserTabEntries(WindowEntry parent, List<BrowserTab> tabs)
    {
        // The composed title ("Tab Title - Brave") lets every existing title-keyed pipeline — favicons, extension thumbnails, YouTube, title rules — work on tab entries unchanged.
        string suffix = BrowserFavicon.GetBrowserSuffix(parent.Title) ?? "";
        var result = new List<WindowEntry>();
        var live = new HashSet<(IntPtr, int)>();
        for (int i = 0; i < tabs.Count; i++)
        {
            BrowserTab tab = tabs[i];
            (IntPtr, int) key = (parent.Hwnd, tab.Id);
            live.Add(key);
            if (!browserTabEntries.TryGetValue(key, out WindowEntry? entry))
            {
                entry = new WindowEntry { Hwnd = parent.Hwnd, Pid = parent.Pid, ExePath = parent.ExePath };
                browserTabEntries[key] = entry;
            }
            entry.BrowserTab = tab;
            // Mirrors the parent's expanded state so the popup's expand icon shows lit on tab entries.
            entry.ExpandTabs = true;
            entry.Title = tab.Title + suffix;
            entry.OrderKey = parent.OrderKey + (i + 1) * BrowserTabOrderStep;
            entry.IsForeground = parent.IsForeground && tab.Active;
            entry.IsGroupLastFocused = parent.IsGroupLastFocused && tab.Active;
            // The active tab shares the window's focus recency; inactive tabs keep the time from when they were last the active one, fading independently.
            if (tab.Active)
            {
                entry.LastFocusedAt = parent.LastFocusedAt;
                entry.IdleOpacity = 1;
            }
            // The window screenshot can only ever show the selected tab, so it's assigned to (and cached on) the active tab's entry alone; inactive tabs keep the shot from when they were last selected.
            if (tab.Active && parent.Thumbnail is not null)
            {
                entry.Thumbnail = parent.Thumbnail;
            }
            result.Add(entry);
        }
        foreach ((IntPtr, int) key in browserTabEntries.Keys.Where(k => k.Hwnd == parent.Hwnd && !live.Contains(k)).ToList())
        {
            browserTabEntries.Remove(key);
        }
        return result;
    }

    // Toggling from a tab pseudo-entry routes to the real window entry it belongs to.
    private void UpdateIdleOpacity()
    {
        foreach (WindowEntry entry in windows)
        {
            entry.IdleOpacity = ComputeIdleOpacity(entry.LastFocusedAt);
        }
        foreach (WindowEntry entry in browserTabEntries.Values)
        {
            entry.IdleOpacity = ComputeIdleOpacity(entry.LastFocusedAt);
        }
    }

    private static double ComputeIdleOpacity(long lastFocusedAt)
    {
        TimeSpan idle = Stopwatch.GetElapsedTime(lastFocusedAt != 0 ? lastFocusedAt : appStartedAt);
        if (idle <= IdleFadeStart)
        {
            return 1;
        }
        double progress = Math.Min(1, (idle - IdleFadeStart) / (IdleFadeEnd - IdleFadeStart));
        return 1 - MaxIdleTransparency * progress;
    }

    private void ToggleExpandTabs(WindowEntry entry)
    {
        WindowEntry target = entry.BrowserTab is not null && entriesByHwnd.TryGetValue(entry.Hwnd, out WindowEntry? parent) ? parent : entry;
        target.ExpandTabs = !target.ExpandTabs;
        ExpandedTabWindows.Set(target.Hwnd, target.ExpandTabs);
        // Collapsing forgets the pairing so a later re-expand resolves fresh against the window's then-current title.
        if (!target.ExpandTabs)
        {
            browserWindowIdByHwnd.Remove(target.Hwnd);
        }
        RecomputeGroups();
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

    // Re-resolved on every refresh (not just on toggle) so the shown directory tracks the process as it changes its working directory.
    private void ApplyDirectoryTitles()
    {
        Dictionary<uint, List<uint>>? childrenByParent = null;
        Dictionary<IntPtr, HashSet<uint>>? tabShellsByWindow = null;
        // Trees are cached by their root pid: the window's own process for ordinary apps, or each conpty shell for terminal windows. The shells must be walked as roots in their own right — with the default-terminal handoff they're frequently not process-children of the terminal at all.
        var treesByRoot = new Dictionary<uint, List<ProcessDirectory.TreeDirectory>>();
        List<ProcessDirectory.TreeDirectory> GetTree(uint rootPid)
        {
            if (!treesByRoot.TryGetValue(rootPid, out List<ProcessDirectory.TreeDirectory>? tree))
            {
                childrenByParent ??= ProcessDirectory.SnapshotChildren();
                tree = ProcessDirectory.GetTreeDirectories(rootPid, childrenByParent);
                treesByRoot[rootPid] = tree;
            }
            return tree;
        }
        foreach (WindowEntry entry in windows)
        {
            if (!DirectoryTitles.IsEnabled(entry.ExePath))
            {
                entry.DirectoryTitle = null;
                continue;
            }
            tabShellsByWindow ??= ProcessDirectory.SnapshotTabShells();
            List<ProcessDirectory.TreeDirectory> tree = tabShellsByWindow.TryGetValue(entry.Hwnd, out HashSet<uint>? windowTabShells)
                ? windowTabShells.SelectMany(GetTree).ToList()
                : GetTree(entry.Pid);
            entry.DirectoryTitle = ProcessDirectory.ChooseBestDirectory(tree, entry.Title) ?? ProcessDirectory.GetCurrentDirectory(entry.Pid) ?? Path.GetDirectoryName(entry.ExePath);
        }
    }

    private void ToggleDirectoryTitle(WindowEntry entry)
    {
        if (entry.ExePath is null)
        {
            return;
        }
        DirectoryTitles.Toggle(entry.ExePath);
        ApplyDirectoryTitles();
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
                best = new TabStripWindow(FocusWindow, ReorderTab, ShowMainWindow, ToggleDirectoryTitle, ToggleExpandTabs);
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
