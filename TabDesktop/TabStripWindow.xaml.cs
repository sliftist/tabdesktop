using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TabDesktop.Interop;

namespace TabDesktop;

// One strip per multi-window group, floating just above the group's screen rect. Deliberately a separate small window per group (not one full-screen overlay): the strip only owns the pixels of its own bar, so it can take clicks on tabs without intercepting mouse events anywhere else on the desktop.
public partial class TabStripWindow : Window
{
    private const double HeaderWidth = 32;
    // Tab Border width plus its horizontal margins; used to size the window and as the wheel/button scroll step.
    private const double TabOuterWidth = 202;
    private const double ScrollButtonsWidth = 44;
    private const double ScrollStep = TabOuterWidth * 2;
    private const double DragThreshold = 6;

    private readonly Action<IntPtr> focusRequested;
    private readonly Action<WindowEntry, double> reorderRequested;
    private readonly Action showMainRequested;
    private WindowGroup? currentGroup;
    private WindowGroup? pendingGroup;
    private double currentDpiScale = 1;
    private bool collapsed;
    private WindowEntry? dragEntry;
    private Border? dragBorder;
    private Point dragStart;
    private bool dragging;

    public HashSet<IntPtr> MemberHwnds { get; private set; } = new();

    public TabStripWindow(Action<IntPtr> focusRequested, Action<WindowEntry, double> reorderRequested, Action showMainRequested)
    {
        this.focusRequested = focusRequested;
        this.reorderRequested = reorderRequested;
        this.showMainRequested = showMainRequested;
        InitializeComponent();
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
        // Replacing ItemsSource mid-drag regenerates the tab containers, which kills the captured Border and strands the drag; defer refreshes until the drag ends.
        if (dragEntry is not null)
        {
            pendingGroup = group;
            currentDpiScale = dpiScale;
            return;
        }
        currentGroup = group;
        currentDpiScale = dpiScale;
        MemberHwnds = group.Members.Select(m => m.Hwnd).ToHashSet();
        Tabs.ItemsSource = group.Members;
        CountText.Text = group.Members.Count.ToString();
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (currentGroup is null)
        {
            return;
        }
        Left = currentGroup.ScreenLeft / currentDpiScale;
        Top = Math.Max(SystemParameters.VirtualScreenTop, currentGroup.ScreenTop / currentDpiScale - Height);
        ToggleButton.Content = collapsed ? "»" : "«";
        if (collapsed)
        {
            TabsScroll.Visibility = Visibility.Collapsed;
            ScrollButtons.Visibility = Visibility.Collapsed;
            Width = HeaderWidth;
            return;
        }
        TabsScroll.Visibility = Visibility.Visible;
        double groupWidth = currentGroup.Width / currentDpiScale;
        // Tabs pack into vertical-flow columns of varying height, so the packed width comes from measuring the real panel rather than count × tab width.
        Tabs.Measure(new Size(double.PositiveInfinity, Height));
        double tabsWidth = Tabs.DesiredSize.Width;
        bool overflow = HeaderWidth + tabsWidth > groupWidth;
        ScrollButtons.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        double desired = HeaderWidth + tabsWidth + (overflow ? ScrollButtonsWidth : 0);
        Width = Math.Min(desired, Math.Max(groupWidth, HeaderWidth + TabOuterWidth + ScrollButtonsWidth));
    }

    private void OnToggleCollapse(object sender, RoutedEventArgs e)
    {
        collapsed = !collapsed;
        ApplyLayout();
    }

    private void OnShowMain(object sender, RoutedEventArgs e)
    {
        showMainRequested();
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
            focusRequested(entry.Hwnd);
            return;
        }
        // Compute the drop before applying any deferred group update — it reads the tab containers the drag happened over.
        double newKey = ComputeDropKey(e.GetPosition(Tabs), entry);
        ApplyPendingGroup();
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

    // Returns a key between the slot's neighbors so only the dragged tab's key changes.
    private double ComputeDropKey(Point pos, WindowEntry dragged)
    {
        DropTarget? target = ComputeDropTarget(pos, dragged);
        if (target is null)
        {
            return dragged.OrderKey;
        }
        List<WindowEntry> rest = target.Rest;
        if (target.InsertIndex <= 0)
        {
            return rest[0].OrderKey - 1;
        }
        if (target.InsertIndex >= rest.Count)
        {
            return rest[^1].OrderKey + 1;
        }
        return (rest[target.InsertIndex - 1].OrderKey + rest[target.InsertIndex].OrderKey) / 2;
    }
}
