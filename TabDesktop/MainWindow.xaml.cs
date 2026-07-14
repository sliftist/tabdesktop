using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    // The focused window is the one that moves; polling just its rect between full refreshes keeps the strips tracking it responsively without multiplying the cost of the full scan.
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(400);
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
    // Keeps the Thumbnails tab responsive against a 100k-row snapshot: only this many matches render (and decode images) at once.
    private const int MaxThumbnailResults = 200;

    private List<ThumbnailRow>? thumbnailRows;
    private bool thumbnailRefreshRunning;
    private int thumbnailSnapshotDiskVersion = -1;
    private int thumbnailSnapshotUrlsVersion = -1;

    private readonly ObservableCollection<WindowEntry> windows = new();
    private readonly ObservableCollection<WindowGroup> groups = new();
    // Settings-tab rows persist across refreshes (matched to groups by shared hwnds) so their expanders don't collapse every tick.
    private readonly ObservableCollection<GroupRow> settingsGroups = new();
    private ExtensionInstallWindow? installWindow;
    private SearchWindow? searchWindow;
    private GlobalHotkey? searchHotkey;
    private readonly List<TabStripWindow> tabStrips = new();
    private readonly Dictionary<IntPtr, long> previewCapturedAt = new();
    private IntPtr lastForeground;
    // Keyed by DisplayTitle rather than hwnd so the order survives app restarts and window recreation; for apps like Cursor the display title (the folder) is the stable identity even though the full title changes with every file switch.
    private readonly Dictionary<string, double> tabOrder;
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
        RestorePlacement();
        Closing += (_, _) => SavePlacement();

        WindowList.ItemsSource = windows;
        LayoutItems.ItemsSource = groups;
        LogList.ItemsSource = AppLog.Entries;
        SettingsGroups.ItemsSource = settingsGroups;
        BuildVersionRun.Text = $"TabDesktop v{AppInfo.Version}";
        BuildTimeRun.Text = $" ({AppInfo.BuiltAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown"})";
        RebuildRestartButton.Visibility = TabStripWindow.CanRebuildRestart ? Visibility.Visible : Visibility.Collapsed;
        AdvancedModeCheck.IsChecked = AppSettings.AdvancedMode;
        ShowWhenFullscreenCheck.IsChecked = AppSettings.ShowWhenFullscreen;
        SearchEnabledCheck.IsChecked = AppSettings.SearchEnabled;
        SearchHotkeyBox.Text = AppSettings.SearchHotkey;
        RunOnStartupCheck.IsChecked = Installer.IsAutoStartEnabled();
        UpdateStartupStatus();
        UpdateInstallStatus();
        ApplySearchHotkey();
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
        // Rebuild the expanded-tab layout the moment the browser confirms a tab change (activate/move/close), so dragging a tab in the strip snaps into place instead of waiting out the refresh tick.
        ExtensionThumbnails.TabsChanged += () => Dispatcher.BeginInvoke(RecomputeGroups);
        refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        refreshTimer.Tick += (_, _) => RefreshWindows();
        refreshTimer.Start();
        var foregroundPollTimer = new DispatcherTimer { Interval = ForegroundPollInterval };
        foregroundPollTimer.Tick += (_, _) => FastPollForeground();
        foregroundPollTimer.Start();
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
        UpdateThumbnailStaleness();
    }

    // GatherBasics reads only user32-cached state, so unlike the worker-pool polls this is safe on the UI thread even against a hung process. ApplyResults (and the group recompute behind it) only runs when the rect actually changed, so an idle foreground window costs two cheap native calls per tick.
    private void FastPollForeground()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (!entriesByHwnd.TryGetValue(foreground, out WindowEntry? entry))
        {
            return;
        }
        WindowData? data = WindowScanner.GatherBasics(foreground);
        if (data is null || data.IsMinimized)
        {
            return;
        }
        double screenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        double screenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        bool moved = entry.CanvasLeft != data.Left - screenLeft || entry.CanvasTop != data.Top - screenTop || entry.LayoutWidth != data.Width || entry.LayoutHeight != data.Height;
        if (!moved)
        {
            return;
        }
        ApplyResults(new List<WindowData> { data });
    }

    private string? windowListSortProperty;
    private ListSortDirection windowListSortDirection;

    // Sorts the List tab by the clicked column; clicking the same header again flips the direction. Columns with a display binding sort by its property; the templated Title column's header text matches its property name.
    private void OnWindowListHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column is null)
        {
            return;
        }
        string? property = header.Column.DisplayMemberBinding is Binding binding ? binding.Path.Path : header.Column.Header as string;
        if (string.IsNullOrEmpty(property))
        {
            return;
        }
        // The Order column displays a decorated string; sorting lexicographically would cluster the "min …" rows apart from plain keys, so sort it by the numeric key.
        if (property == nameof(WindowEntry.OrderText))
        {
            property = nameof(WindowEntry.OrderKey);
        }
        windowListSortDirection = windowListSortProperty == property && windowListSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        windowListSortProperty = property;
        ICollectionView view = CollectionViewSource.GetDefaultView(WindowList.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(property, windowListSortDirection));
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
            WindowEntry? lastFocused = members.Where(m => m.LastFocusedAt != 0).OrderByDescending(m => m.LastFocusedAt).FirstOrDefault();
            // Every entry — window or sub-tab — has its own persistent order key; sub-tabs of one window additionally share a block key (the minimum key among that window's tabs).
            var flat = new List<(WindowEntry Entry, double BlockKey, int SubTab)>();
            foreach (WindowEntry member in members)
            {
                member.IsGroupLastFocused = member == lastFocused;
                List<BrowserTab>? tabs = member.ExpandTabs ? GetTabsForExpandedWindow(member) : null;
                if (tabs is null || tabs.Count == 0)
                {
                    double key = EnsureOrder(member);
                    member.OrderText = $"{key}";
                    flat.Add((member, key, 0));
                    continue;
                }
                List<WindowEntry> tabEntries = BuildBrowserTabEntries(member, tabs);
                double blockKey = tabEntries.Min(EnsureOrder);
                member.OrderText = $"min {blockKey}";
                foreach (WindowEntry tabEntry in tabEntries)
                {
                    flat.Add((tabEntry, blockKey, 1));
                }
            }
            // Four consecutive stable sorts. 1: own key. 2: full windows before sub-tabs. 3: block key — the primary — which clumps a window's tabs (identical block key) and interleaves blocks with windows. 4: within each consecutive same-block-key run, back to each entry's own key.
            List<(WindowEntry Entry, double BlockKey, int SubTab)> pass = flat.OrderBy(x => x.Entry.OrderKey).ToList();
            pass = pass.OrderBy(x => x.BlockKey).ToList();
            pass = pass.OrderBy(x => x.SubTab).ToList();
            var display = new List<WindowEntry>();
            int runStart = 0;
            while (runStart < pass.Count)
            {
                int runEnd = runStart + 1;
                while (runEnd < pass.Count && pass[runEnd].BlockKey == pass[runStart].BlockKey)
                {
                    runEnd++;
                }
                display.AddRange(pass.Skip(runStart).Take(runEnd - runStart).OrderBy(x => x.Entry.OrderKey).Select(x => x.Entry));
                runStart = runEnd;
            }
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
                ZIndex = members.Max(m => m.ZIndex),
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
        // Same >=2 filter as the rows below, so the header counts exactly what's listed.
        List<WindowGroup> stripGroups = groups.Where(g => g.Members.Count >= 2).ToList();
        TabGroupsHeader.Text = $"Tab groups ({stripGroups.Count} groups | {stripGroups.Sum(g => g.Members.Count)} tabs)";
        var unmatched = new List<GroupRow>(settingsGroups);
        foreach (WindowGroup group in stripGroups)
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

    private void OnShowWhenFullscreenChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.SetShowWhenFullscreen(ShowWhenFullscreenCheck.IsChecked ?? false);
    }

    private void OnSearchEnabledChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.SetSearchEnabled(SearchEnabledCheck.IsChecked ?? false);
        ApplySearchHotkey();
    }

    private static readonly Brush HotkeyErrorBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xCC, 0x00, 0x00));

    // Fallback for when the keyboard hook couldn't install: WPF-visible combos still capture, though shell-owned ones (Win+A) never reach the app this way.
    private void OnSearchHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }
        HotkeyCombo? combo = HotkeyCombo.FromKeyEvent(e);
        if (combo is null)
        {
            return;
        }
        SearchHotkeyBox.Text = combo.ToString();
        AppSettings.SetSearchHotkey(combo.ToString());
        ApplySearchHotkey();
    }

    // While the box is focused the hook swallows every keystroke system-wide and feeds it here — the only way shell-owned combos like Win+A can be captured instead of opening Quick Settings.
    private void OnSearchHotkeyBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        EnsureSearchHotkeyHook();
        if (!searchHotkey!.IsInstalled)
        {
            return;
        }
        SearchHotkeyStatus.Foreground = Brushes.Gray;
        SearchHotkeyStatus.Text = "Press the key combination you want — Esc or click elsewhere to finish";
        searchHotkey.BeginCapture(
            combo => Dispatcher.BeginInvoke(() =>
            {
                SearchHotkeyBox.Text = combo.ToString();
                AppSettings.SetSearchHotkey(combo.ToString());
            }),
            () => Dispatcher.BeginInvoke(Keyboard.ClearFocus));
    }

    private void OnSearchHotkeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        searchHotkey?.EndCapture();
        ApplySearchHotkey();
    }

    private void ApplySearchHotkey()
    {
        SearchHotkeyStatus.Foreground = HotkeyErrorBrush;
        SearchHotkeyStatus.Text = "";
        HotkeyCombo? combo = AppSettings.SearchEnabled ? HotkeyCombo.TryParse(AppSettings.SearchHotkey) : null;
        if (combo is null)
        {
            if (AppSettings.SearchEnabled)
            {
                SearchHotkeyStatus.Text = "Unrecognized hotkey — click the box and press a new combination";
            }
            // The hook intercepts every keystroke system-wide, so it only exists while a hotkey is wanted or the capture box is in use.
            if (searchHotkey is not null && !searchHotkey.IsCapturing)
            {
                searchHotkey.Dispose();
                searchHotkey = null;
            }
            else
            {
                searchHotkey?.SetCombo(null);
            }
            return;
        }
        EnsureSearchHotkeyHook();
        searchHotkey!.SetCombo(combo);
        if (!searchHotkey.IsInstalled)
        {
            SearchHotkeyStatus.Text = "Couldn't install the keyboard hook — the hotkey won't work";
            AppLog.Write(nameof(MainWindow), SearchHotkeyStatus.Text);
        }
    }

    private void EnsureSearchHotkeyHook()
    {
        // The hook callback fires mid-keystroke and must return immediately; the actual toggle runs on the next dispatcher pass.
        searchHotkey ??= new GlobalHotkey(() => Dispatcher.BeginInvoke(ToggleSearchWindow));
    }

    // Non-modal singleton, same pattern as the extension-install window; pressing the hotkey again dismisses it.
    private void ToggleSearchWindow()
    {
        if (searchWindow is not null)
        {
            searchWindow.Close();
            return;
        }
        searchWindow = new SearchWindow(GetSearchEntries, ActivateEntry);
        searchWindow.Closed += (_, _) => searchWindow = null;
        searchWindow.Show();
        searchWindow.Activate();
    }

    // Everything the strips can show — including expanded browser-tab pseudo-entries — plus minimized windows, which have no layout group but are still tabs the user may want to jump to.
    private List<WindowEntry> GetSearchEntries()
    {
        var result = groups.SelectMany(g => g.Members).ToList();
        var shown = new HashSet<IntPtr>(result.Select(e => e.Hwnd));
        result.AddRange(windows.Where(w => !shown.Contains(w.Hwnd) && w.Title != LoadingTitle));
        return result;
    }

    private void ActivateEntry(WindowEntry entry)
    {
        if (entry.BrowserTab is not null)
        {
            ExtensionThumbnails.ActivateTab(entry.BrowserTab);
        }
        FocusWindow(entry.Hwnd);
        PredictFocus(entry);
    }

    // An explicit activation reliably ends with that window foreground, but the scanner only notices on its next poll — waiting for it makes clicks feel sluggish. Apply the expected state immediately; the poll converges to reality if the activation actually failed. lastForeground is deliberately left alone so the poll still treats this as a focus change and captures a fresh preview.
    private void PredictFocus(WindowEntry entry)
    {
        long now = Stopwatch.GetTimestamp();
        foreach (WindowEntry window in windows)
        {
            window.IsForeground = window.Hwnd == entry.Hwnd;
        }
        if (entriesByHwnd.TryGetValue(entry.Hwnd, out WindowEntry? parent))
        {
            parent.LastFocusedAt = now;
            parent.IdleOpacity = 1;
        }
        entry.LastFocusedAt = now;
        entry.IdleOpacity = 1;
        // Recomputing rederives IsGroupLastFocused from the bumped focus time and, for expanded windows, rebuilds the tab entries from the optimistically switched active-tab flags.
        RecomputeGroups();
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
        UpdateStartupStatus();
    }

    private void UpdateStartupStatus()
    {
        string? registered = Installer.GetAutoStartTarget();
        string current = Environment.ProcessPath!;
        if (registered is null)
        {
            StartupStatusText.Text = $"Would run this exe: {current}";
        }
        else if (string.Equals(registered, current, StringComparison.OrdinalIgnoreCase))
        {
            StartupStatusText.Text = $"Startup runs this exe: {registered}";
        }
        else
        {
            StartupStatusText.Text = $"Startup runs: {registered} — not this exe ({current}); re-check the box to switch startup to this one";
        }
    }

    private void UpdateInstallStatus()
    {
        (string ExePath, string Version)? installed = Installer.GetInstalledInfo();
        if (installed is null)
        {
            InstallAppButton.Content = "Install TabDesktop";
            InstallStatusText.Text = "Not installed";
            return;
        }
        InstallAppButton.Content = "Reinstall / update";
        string versionNote = installed.Value.Version == AppInfo.Version ? "" : $" (this build is v{AppInfo.Version})";
        InstallStatusText.Text = $"Installed: v{installed.Value.Version}{versionNote} — {installed.Value.ExePath}";
    }

    private void OnInstallApp(object sender, RoutedEventArgs e)
    {
        // Checking the box first lets its changed handler fire (targeting this exe) before Install overwrites the batch with the installed exe, which is the copy that should own startup.
        RunOnStartupCheck.IsChecked = true;
        try
        {
            Installer.Install();
        }
        catch (Exception ex)
        {
            AppLog.Write(nameof(Installer), ex.ToString());
            return;
        }
        UpdateStartupStatus();
        UpdateInstallStatus();
        InstallAppButton.Content = "Installed!";
        var revert = new DispatcherTimer { Interval = InstallFeedbackDuration };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            UpdateInstallStatus();
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
        foreach (string domain in ThumbnailWhitelist.GetBlockedDomains())
        {
            rows.Add(new SavedStateRow("Blacklisted thumbnail domain", domain, () => ThumbnailWhitelist.ToggleBlockedDomain(domain)));
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

    // SelectionChanged bubbles from every inner ListView too, so only react to the TabControl's own selection.
    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs))
        {
            return;
        }
        if (ThumbnailsTab.IsSelected)
        {
            RefreshThumbnailRows();
        }
    }

    // Filtering the in-memory snapshot is instant; the rebuild (when needed) lands afterwards and re-applies the filter.
    private void OnThumbnailSearch(object sender, TextChangedEventArgs e)
    {
        ApplyThumbnailFilter();
        if (IsThumbnailSnapshotStale())
        {
            RefreshThumbnailRows();
        }
    }

    private void OnThumbnailRefresh(object sender, RoutedEventArgs e)
    {
        RefreshThumbnailRows();
    }

    // Deletes ALL matches of the current search (not just the rows shown under the display cap); an empty search deletes everything. No confirmation — it's a regenerable cache.
    private void OnThumbnailDelete(object sender, RoutedEventArgs e)
    {
        if (thumbnailRows is null)
        {
            return;
        }
        string query = ThumbnailSearchBox.Text.Trim();
        List<ThumbnailRow> matched = query.Length == 0
            ? thumbnailRows
            : thumbnailRows.Where(r => r.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
        {
            return;
        }
        ThumbnailDiskCache.DeleteMany(matched.Where(r => r.FilePath is not null).Select(r => r.Hash));
        ExtensionThumbnails.ForgetTitles(matched.Select(r => r.Title).Where(t => t.Length > 0));
        RefreshThumbnailRows();
    }

    private bool IsThumbnailSnapshotStale()
    {
        return thumbnailSnapshotDiskVersion != ThumbnailDiskCache.Version || thumbnailSnapshotUrlsVersion != ExtensionThumbnails.SavedUrlsVersion;
    }

    // Piggybacks on the 2 s refresh tick so the indicator appears without user action while the tab sits open.
    private void UpdateThumbnailStaleness()
    {
        ThumbnailStaleText.Visibility = thumbnailRows is not null && IsThumbnailSnapshotStale() ? Visibility.Visible : Visibility.Collapsed;
    }

    // The join of the persisted title→URL map with the on-disk cache can span 100k files, so the snapshot builds off-thread; search keystrokes filter the in-memory snapshot only, with staleness tracked via the sources' version counters.
    private void RefreshThumbnailRows()
    {
        if (thumbnailRefreshRunning)
        {
            return;
        }
        thumbnailRefreshRunning = true;
        // Versions are captured before the data reads, so a write racing the rebuild still leaves the new snapshot marked stale.
        int diskVersion = ThumbnailDiskCache.Version;
        int urlsVersion = ExtensionThumbnails.SavedUrlsVersion;
        List<(string Title, string? Url)> titleUrls = ExtensionThumbnails.SnapshotSavedUrls();
        Task.Run(() =>
        {
            try
            {
                List<ThumbnailDiskCache.SavedThumbnail> files = ThumbnailDiskCache.ListSaved();
                var byHash = new Dictionary<string, ThumbnailDiskCache.SavedThumbnail>();
                foreach (ThumbnailDiskCache.SavedThumbnail file in files)
                {
                    byHash[file.Hash] = file;
                }
                var claimed = new HashSet<string>();
                var rows = new List<ThumbnailRow>();
                foreach ((string title, string? url) in titleUrls)
                {
                    ThumbnailDiskCache.SavedThumbnail? file = null;
                    if (url is not null && byHash.TryGetValue(ThumbnailDiskCache.HashFor(url), out ThumbnailDiskCache.SavedThumbnail? matched))
                    {
                        file = matched;
                        claimed.Add(matched.Hash);
                    }
                    rows.Add(new ThumbnailRow(title, url, file));
                }
                // Cache files whose URL is no longer in the title map (evicted titles, YouTube thumbnail URLs) still count as rows; the disk cache's URL index recovers their URL where it can.
                foreach (ThumbnailDiskCache.SavedThumbnail file in files)
                {
                    if (!claimed.Contains(file.Hash))
                    {
                        rows.Add(new ThumbnailRow("", ThumbnailDiskCache.TryGetUrlForHash(file.Hash), file));
                    }
                }
                rows.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
                Dispatcher.BeginInvoke(() =>
                {
                    thumbnailRefreshRunning = false;
                    thumbnailSnapshotDiskVersion = diskVersion;
                    thumbnailSnapshotUrlsVersion = urlsVersion;
                    thumbnailRows = rows;
                    ApplyThumbnailFilter();
                    UpdateThumbnailStaleness();
                });
            }
            catch (Exception ex)
            {
                AppLog.Write(nameof(MainWindow), $"Thumbnail snapshot rebuild failed: {ex}");
                Dispatcher.BeginInvoke(() => thumbnailRefreshRunning = false);
            }
        });
    }

    private void ApplyThumbnailFilter()
    {
        if (thumbnailRows is null)
        {
            return;
        }
        string query = ThumbnailSearchBox.Text.Trim();
        List<ThumbnailRow> matched = query.Length == 0
            ? thumbnailRows
            : thumbnailRows.Where(r => r.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        List<ThumbnailRow> shown = matched.Take(MaxThumbnailResults).ToList();
        ThumbnailList.ItemsSource = shown;
        ThumbnailCountText.Text = shown.Count == matched.Count
            ? $"{matched.Count} of {thumbnailRows.Count} rows"
            : $"showing first {shown.Count} of {matched.Count} matches ({thumbnailRows.Count} rows)";
        LoadThumbnailImages(shown);
    }

    // Images decode lazily for the rows actually shown (the result cap keeps this small); WPF marshals the resulting property-change notifications to the UI thread itself.
    private void LoadThumbnailImages(List<ThumbnailRow> rows)
    {
        List<ThumbnailRow> missing = rows.Where(r => r.Image is null && r.FilePath is not null && !r.ImageLoadFailed).ToList();
        if (missing.Count == 0)
        {
            return;
        }
        Task.Run(() =>
        {
            foreach (ThumbnailRow row in missing)
            {
                try
                {
                    row.Image = BrowserFavicon.DecodeImage(File.ReadAllBytes(row.FilePath!));
                }
                catch
                {
                    row.ImageLoadFailed = true;
                }
            }
        });
    }

    private sealed class ThumbnailRow : INotifyPropertyChanged
    {
        public ThumbnailRow(string title, string? url, ThumbnailDiskCache.SavedThumbnail? file)
        {
            Title = title;
            Url = url;
            FilePath = file?.Path;
            Hash = file?.Hash ?? (url is not null ? ThumbnailDiskCache.HashFor(url) : "");
            LastUsedUtc = file?.LastUsedUtc ?? DateTime.MinValue;
            LastUsedText = file is null ? "" : file.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            SizeText = file is null ? "" : $"{file.SizeBytes / 1024.0:0.0} KB";
            SearchText = string.Join("\n", Title, Url ?? "", Hash, LastUsedText, SizeText);
        }

        public string Title { get; }
        public string? Url { get; }
        public string? FilePath { get; }
        public string Hash { get; }
        public DateTime LastUsedUtc { get; }
        public string LastUsedText { get; }
        public string SizeText { get; }
        public string SearchText { get; }
        public bool ImageLoadFailed;

        private ImageSource? image;
        public ImageSource? Image
        {
            get => image;
            set
            {
                image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

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

    // A name that already has a persisted key keeps it (manual drags write midpoints here, and they must survive restarts); a new name defaults to its process creation time so order is stable no matter when TabDesktop first sees the window.
    private double EnsureOrder(WindowEntry entry)
    {
        if (entry.OrderKey != 0)
        {
            return entry.OrderKey;
        }
        if (entry.Title == LoadingTitle)
        {
            // Not cached or persisted: the real name hasn't arrived yet, so the name→order mapping can't be recorded.
            return GetDefaultOrder(entry);
        }
        if (tabOrder.TryGetValue(entry.DisplayTitle, out double existing))
        {
            entry.OrderKey = existing;
            return existing;
        }
        double assigned = GetDefaultOrder(entry);
        entry.OrderKey = assigned;
        tabOrder[entry.DisplayTitle] = assigned;
        tabOrderDirty = true;
        return assigned;
    }

    // Whole seconds, so same-second processes tie here and the sort's pid tiebreak dedupes them. Unqueryable processes (elevated) fall back to first-seen time.
    private static double GetDefaultOrder(WindowEntry entry)
    {
        return ProcessDirectory.GetCreationUnixSeconds(entry.Pid) ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
        AppLog.Write("TabDrag", $"ReorderTab \"{entry.DisplayTitle}\" {entry.OrderKey} -> {newKey}");
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

    // Monitors fully covered by a normal window (rect contains the whole monitor, taskbar included) — the borderless-fullscreen signature of movies and games. The desktop shell's own windows also span monitors, so they're excluded by class.
    private List<Rect> FindFullscreenMonitors()
    {
        double screenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        double screenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var fullscreen = new List<Rect>();
        foreach (Rect monitor in WindowScanner.GetMonitorRects())
        {
            foreach (WindowEntry entry in windows)
            {
                if (!entry.ShowInLayout)
                {
                    continue;
                }
                var windowRect = new Rect(entry.CanvasLeft + screenLeft, entry.CanvasTop + screenTop, entry.LayoutWidth, entry.LayoutHeight);
                if (windowRect.Contains(monitor) && !IsShellWindow(entry.Hwnd))
                {
                    fullscreen.Add(monitor);
                    break;
                }
            }
        }
        return fullscreen;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        string className = name.ToString();
        return className == "Progman" || className == "WorkerW";
    }

    private void SyncTabStrips()
    {
        List<Rect> fullscreenMonitors = AppSettings.ShowWhenFullscreen ? new List<Rect>() : FindFullscreenMonitors();
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
                best = new TabStripWindow(ActivateEntry, ReorderTab, ShowMainWindow, ToggleDirectoryTitle, ToggleExpandTabs);
                tabStrips.Add(best);
                best.Update(group, dpiScale);
                best.Show();
            }
            else
            {
                unmatched.Remove(best);
                best.Update(group, dpiScale);
            }
            var groupRect = new Rect(group.ScreenLeft, group.ScreenTop, group.Width, group.Height);
            best.SetSuppressed(fullscreenMonitors.Any(monitor => monitor.IntersectsWith(groupRect)));
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
