using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TabDesktop.Interop;

namespace TabDesktop;

// One strip per multi-window group, floating just above the group's screen rect. Deliberately a separate small window per group (not one full-screen overlay): the strip only owns the pixels of its own bar, so it can take clicks on tabs without intercepting mouse events anywhere else on the desktop.
public partial class TabStripWindow : Window
{
    private const double ButtonColumnWidth = 32;
    private const double StripHeight = 68;
    // Tab Border outer width (border included, no margins); used as the wheel/button scroll step.
    private const double TabOuterWidth = 200;
    private const double ScrollButtonsWidth = 44;
    private const double ScrollStep = TabOuterWidth * 2;
    private const double DragThreshold = 6;
    // A dark-green chip keeps the puzzle icon readable on top while still reading as clearly "on" from across the screen.
    private static readonly Brush ExtensionConnectedBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private const double DisconnectedIconOpacity = 0.4;

    private readonly Action<WindowEntry> activateRequested;
    private readonly Action<WindowEntry, double> reorderRequested;
    private readonly Action showMainRequested;
    private readonly Action<WindowEntry> directoryTitleToggleRequested;
    private readonly Action<WindowEntry> expandTabsToggleRequested;
    private WindowGroup? currentGroup;
    private WindowGroup? pendingGroup;
    private double currentDpiScale = 1;
    private bool collapsed;
    private bool doubleHeight;
    private WindowEntry? dragEntry;
    private Border? dragBorder;
    private Point dragStart;
    private bool dragging;
    private ExtensionInstallWindow? installWindow;
    // The strip's ItemsSource, mutated in place on every refresh: replacing the ItemsSource wholesale regenerates all tab containers, which flashes the hover overlay and eats a click whose press landed on a container recycled before the release.
    private readonly ObservableCollection<WindowEntry> members = new();

    public HashSet<IntPtr> MemberHwnds { get; private set; } = new();

    public TabStripWindow(Action<WindowEntry> activateRequested, Action<WindowEntry, double> reorderRequested, Action showMainRequested, Action<WindowEntry> directoryTitleToggleRequested, Action<WindowEntry> expandTabsToggleRequested)
    {
        this.activateRequested = activateRequested;
        this.reorderRequested = reorderRequested;
        this.showMainRequested = showMainRequested;
        this.directoryTitleToggleRequested = directoryTitleToggleRequested;
        this.expandTabsToggleRequested = expandTabsToggleRequested;
        InitializeComponent();
        Height = StripHeight;
        Tabs.ItemsSource = members;
        TabActionsPopup.Closed += (_, _) => ApplyPendingGroup();
        // Tool-window ex-style keeps the strip out of alt-tab and out of our own scanner's candidate filter, so strips never form groups over themselves.
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));
        };
    }

    // dpiScale converts the scanner's physical pixels to WPF's device-independent units; on mixed-DPI setups this is only exact on monitors matching the system DPI.
    public void Update(WindowGroup group, double dpiScale)
    {
        UpdateExtensionIndicator();
        ReassertTopmost();
        // Reordering the tab containers mid-drag would strand the captured Border, and mid-popup would yank the placement target; defer refreshes until the drag ends or the popup closes.
        if (dragEntry is not null || TabActionsPopup.IsOpen)
        {
            pendingGroup = group;
            currentDpiScale = dpiScale;
            return;
        }
        currentGroup = group;
        currentDpiScale = dpiScale;
        // Reapplying the persisted state on every update is idempotent: toggles save immediately, so the lookup always agrees with the user's latest choice for this region.
        (bool Collapsed, bool DoubleHeight)? savedState = TabStripStates.TryGet(GroupBounds(group));
        if (savedState is not null)
        {
            collapsed = savedState.Value.Collapsed;
            doubleHeight = savedState.Value.DoubleHeight;
        }
        MemberHwnds = group.Members.Select(m => m.Hwnd).ToHashSet();
        SyncMembers(group.Members);
        CountText.Text = group.Members.Count.ToString();
        ApplyLayout();
    }

    // WindowEntry objects persist across refreshes (cached per hwnd / per tab id in MainWindow), so syncing by reference keeps each unchanged tab's container alive.
    private void SyncMembers(List<WindowEntry> target)
    {
        for (int i = members.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(members[i]))
            {
                members.RemoveAt(i);
            }
        }
        for (int i = 0; i < target.Count; i++)
        {
            int current = members.IndexOf(target[i]);
            if (current < 0)
            {
                members.Insert(i, target[i]);
            }
            else if (current != i)
            {
                members.Move(current, i);
            }
        }
    }

    // Topmost is a z-band position, not a sticky flag: any window made topmost later stacks above the strip, and some fullscreen transitions strip the bit outright. Re-asserting on every refresh keeps the strip on top; SWP_NOACTIVATE so it never steals focus doing so.
    private void ReassertTopmost()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void ApplyLayout()
    {
        if (currentGroup is null)
        {
            return;
        }
        // Basic mode shows only the count/collapse/home column; every other strip button lives in the advanced column, and future buttons must too.
        bool advanced = AppSettings.AdvancedMode;
        AdvancedButtons.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        RebuildButton.Visibility = CanRebuildRestart ? Visibility.Visible : Visibility.Collapsed;
        double headerWidth = advanced ? 2 * ButtonColumnWidth : ButtonColumnWidth;
        HeaderBorder.Width = headerWidth;
        Height = doubleHeight ? StripHeight * 2 : StripHeight;
        ToggleButton.Content = collapsed ? "»" : "«";
        HeightButton.Content = doubleHeight ? "⇓" : "⇑";
        if (collapsed)
        {
            TabsScroll.Visibility = Visibility.Collapsed;
            ScrollButtons.Visibility = Visibility.Collapsed;
            Width = headerWidth;
        }
        else
        {
            TabsScroll.Visibility = Visibility.Visible;
            double groupWidth = currentGroup.Width / currentDpiScale;
            // Tabs pack into vertical-flow columns of varying height, so the packed width comes from measuring the real panel rather than count × tab width.
            Tabs.Measure(new Size(double.PositiveInfinity, Height));
            double tabsWidth = Tabs.DesiredSize.Width;
            bool overflow = headerWidth + tabsWidth > groupWidth;
            ScrollButtons.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            double desired = headerWidth + tabsWidth + (overflow ? ScrollButtonsWidth : 0);
            // The group-width floor keeps a minimum usable width even when the group is mostly pushed off the side of the screen.
            Width = Math.Min(desired, Math.Max(groupWidth, headerWidth + TabOuterWidth + ScrollButtonsWidth));
        }
        // The strip must always be fully in view: clamp its rect into the virtual screen after the size is known, so a group dragged past an edge (or above the top) keeps its strip visible at the boundary instead of rendering off-screen.
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double maxLeft = Math.Max(screenLeft, screenLeft + SystemParameters.VirtualScreenWidth - Width);
        double maxTop = Math.Max(screenTop, screenTop + SystemParameters.VirtualScreenHeight - Height);
        Left = Math.Clamp(currentGroup.ScreenLeft / currentDpiScale, screenLeft, maxLeft);
        Top = Math.Clamp(currentGroup.ScreenTop / currentDpiScale - Height, screenTop, maxTop);
    }

    // Hidden (not closed) while a fullscreen window covers the group's monitor, so the strip's state and identity survive the movie ending.
    public void SetSuppressed(bool value)
    {
        Visibility target = value ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != target)
        {
            Visibility = target;
        }
    }

    private void OnToggleCollapse(object sender, RoutedEventArgs e)
    {
        collapsed = !collapsed;
        SaveStripState();
        ApplyLayout();
    }

    private void OnToggleDoubleHeight(object sender, RoutedEventArgs e)
    {
        doubleHeight = !doubleHeight;
        SaveStripState();
        ApplyLayout();
    }

    private static Rect GroupBounds(WindowGroup group)
    {
        return new Rect(group.ScreenLeft, group.ScreenTop, group.Width, group.Height);
    }

    private void SaveStripState()
    {
        if (currentGroup is not null)
        {
            TabStripStates.Set(GroupBounds(currentGroup), collapsed, doubleHeight);
        }
    }

    private void OnShowMain(object sender, RoutedEventArgs e)
    {
        showMainRequested();
    }

    // The bat only ships with dev builds (publish excludes it), so its presence doubles as "this machine has the source".
    public static readonly bool CanRebuildRestart = File.Exists(Path.Combine(AppContext.BaseDirectory, "build-and-run.bat"));

    // Launches the batch file that ships beside the exe; it kills this process, rebuilds, and starts the new build — the cmd child survives its parent being killed.
    public static void LaunchRebuildRestart()
    {
        string batPath = Path.Combine(AppContext.BaseDirectory, "build-and-run.bat");
        if (!File.Exists(batPath))
        {
            Trace.WriteLine($"build-and-run.bat not found at {batPath}");
            return;
        }
        Process.Start(new ProcessStartInfo(batPath) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }

    private void OnRebuildRestart(object sender, RoutedEventArgs e)
    {
        LaunchRebuildRestart();
    }

    // Rides the regular refresh cycle, so the indicator also dims again when the extension stops reporting (browser closed, extension removed).
    private void UpdateExtensionIndicator()
    {
        bool connected = ExtensionThumbnails.IsConnected;
        ExtensionIcon.Opacity = connected ? 1.0 : DisconnectedIconOpacity;
        ExtensionIconChip.Background = connected ? ExtensionConnectedBrush : Brushes.Transparent;
        ExtensionButton.ToolTip = connected
            ? "Browser extension connected and reporting — click for install info"
            : "Install the browser extension — video thumbnails straight from your tabs, including logged-in sites";
    }

    // Non-modal on purpose: the app spans every monitor, so a blocking dialog would lock out the whole desktop's strips.
    private void OnInstallExtension(object sender, RoutedEventArgs e)
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

    private void OnScrollLeft(object sender, RoutedEventArgs e)
    {
        TabsScroll.ScrollToHorizontalOffset(TabsScroll.HorizontalOffset - ScrollStep);
    }

    private void OnScrollRight(object sender, RoutedEventArgs e)
    {
        TabsScroll.ScrollToHorizontalOffset(TabsScroll.HorizontalOffset + ScrollStep);
    }

    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        TabsScroll.ScrollToHorizontalOffset(TabsScroll.HorizontalOffset - Math.Sign(e.Delta) * TabOuterWidth);
        e.Handled = true;
    }

    // Right-click opens the action popup above the tab. The toggle button reads its entry from the popup's DataContext, so state (filled vs outline folder) follows the entry's DirectoryTitle binding live.
    private void OnTabRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not WindowEntry entry)
        {
            return;
        }
        TabActionsPopup.DataContext = entry;
        TabActionsPopup.PlacementTarget = border;
        TabActionsPopup.IsOpen = true;
        e.Handled = true;
    }

    private void OnToggleFolderTitle(object sender, RoutedEventArgs e)
    {
        if (TabActionsPopup.DataContext is WindowEntry entry)
        {
            directoryTitleToggleRequested(entry);
        }
    }

    private void OnToggleScreenshotThumbnail(object sender, RoutedEventArgs e)
    {
        if (TabActionsPopup.DataContext is not WindowEntry entry)
        {
            return;
        }
        if (entry.IsBrowserTab)
        {
            ThumbnailWhitelist.ToggleScreenshotForWindow(entry.Title);
        }
        else if (entry.ExePath is not null)
        {
            ThumbnailWhitelist.ToggleScreenshotExe(entry.ExePath);
        }
    }

    private void OnToggleThumbnailBlacklist(object sender, RoutedEventArgs e)
    {
        if (TabActionsPopup.DataContext is WindowEntry entry)
        {
            ThumbnailWhitelist.ToggleBlockedForWindow(entry.Title);
        }
    }

    private void OnToggleExpandTabs(object sender, RoutedEventArgs e)
    {
        if (TabActionsPopup.DataContext is WindowEntry entry)
        {
            expandTabsToggleRequested(entry);
        }
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not WindowEntry entry)
        {
            return;
        }
        dragEntry = entry;
        dragBorder = border;
        dragStart = e.GetPosition(this);
        dragging = false;
        border.CaptureMouse();
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (dragEntry is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        if (!dragging && (e.GetPosition(this) - dragStart).Length > DragThreshold)
        {
            dragging = true;
            dragEntry.IsDragging = true;
        }
        if (dragging)
        {
            UpdateDropIndicator(e.GetPosition(Tabs));
        }
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragEntry is null)
        {
            return;
        }
        WindowEntry entry = dragEntry;
        Border border = dragBorder!;
        bool wasDragging = dragging;
        ClearDragState(entry);
        border.ReleaseMouseCapture();
        if (!wasDragging)
        {
            ApplyPendingGroup();
            activateRequested(entry);
            return;
        }
        // Compute the drop before applying any deferred group update — it reads the tab containers the drag happened over.
        Point dropPos = e.GetPosition(Tabs);
        DropTarget? target = ComputeDropTarget(dropPos, entry);
        ApplyPendingGroup();
        if (target is null)
        {
            return;
        }
        double newKey = ComputeDropKey(target, entry);
        if (newKey != entry.OrderKey)
        {
            reorderRequested(entry, newKey);
        }
    }

    private void OnTabLostCapture(object sender, MouseEventArgs e)
    {
        if (dragEntry is null)
        {
            return;
        }
        WindowEntry entry = dragEntry;
        ClearDragState(entry);
        ApplyPendingGroup();
    }

    private void ClearDragState(WindowEntry entry)
    {
        entry.IsDragging = false;
        DropIndicator.Visibility = Visibility.Collapsed;
        dragEntry = null;
        dragBorder = null;
        dragging = false;
    }

    private void ApplyPendingGroup()
    {
        if (pendingGroup is not null)
        {
            WindowGroup group = pendingGroup;
            pendingGroup = null;
            Update(group, currentDpiScale);
        }
    }

    private sealed record DropTarget(int InsertIndex, Rect AnchorRect, bool Before, List<WindowEntry> Rest);

    // Maps the pointer to an insertion slot among the other tabs (column-major order: left-to-right columns, top-to-bottom within a column). Before/After is decided by where the mouse is relative to the nearest tab, so hovering under a column's last tab anchors the indicator below it while hovering above the next column's first tab anchors above — the same insertion slot, previewed at whichever edge the mouse is near.
    private DropTarget? ComputeDropTarget(Point pos, WindowEntry dragged)
    {
        List<WindowEntry> rest = currentGroup!.Members.Where(m => m != dragged).ToList();
        if (rest.Count == 0)
        {
            return null;
        }
        int bestIndex = -1;
        double bestDistance = double.MaxValue;
        Rect bestRect = Rect.Empty;
        for (int i = 0; i < rest.Count; i++)
        {
            if (Tabs.ItemContainerGenerator.ContainerFromItem(rest[i]) is not UIElement container)
            {
                continue;
            }
            var rect = new Rect(container.TransformToAncestor(Tabs).Transform(new Point(0, 0)), container.RenderSize);
            var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            double distance = (pos - center).Length;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
                bestRect = rect;
            }
        }
        if (bestIndex < 0)
        {
            return null;
        }
        bool before = pos.X < bestRect.Left || (pos.X <= bestRect.Right && pos.Y < bestRect.Y + bestRect.Height / 2);
        return new DropTarget(before ? bestIndex : bestIndex + 1, bestRect, before, rest);
    }

    private void UpdateDropIndicator(Point pos)
    {
        DropTarget? target = ComputeDropTarget(pos, dragEntry!);
        if (target is null)
        {
            DropIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        double y = target.Before ? target.AnchorRect.Top - DropIndicator.Height : target.AnchorRect.Bottom;
        Point origin = Tabs.TransformToVisual(IndicatorCanvas).Transform(new Point(target.AnchorRect.X, y));
        Canvas.SetLeft(DropIndicator, origin.X);
        Canvas.SetTop(DropIndicator, origin.Y);
        DropIndicator.Width = target.AnchorRect.Width;
        DropIndicator.Visibility = Visibility.Visible;
    }

    // Display order isn't monotonic in the keys (blocks sort by their minimum tab key), so the immediate neighbors' keys can't be trusted. The dropped entry's key goes between the minimum key before the slot and the minimum key after it — beating every block minimum that follows guarantees the entry (and via the block minimum, its whole window) lands at the drop position. When the minimum before is greater than the minimum after (non-linear keys), the lower bound falls back to zero.
    private static double ComputeDropKey(DropTarget target, WindowEntry dragged)
    {
        List<WindowEntry> rest = target.Rest;
        if (target.InsertIndex >= rest.Count)
        {
            return rest.Max(m => m.OrderKey) + 1;
        }
        double minBefore = target.InsertIndex > 0 ? rest.Take(target.InsertIndex).Min(m => m.OrderKey) : 0;
        double minAfter = rest.Skip(target.InsertIndex).Min(m => m.OrderKey);
        if (minBefore > minAfter)
        {
            minBefore = 0;
        }
        return (minBefore + minAfter) / 2;
    }
}
