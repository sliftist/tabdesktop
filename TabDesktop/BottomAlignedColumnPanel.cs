using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TabDesktop;

// Vertical-flow panel like a vertical WrapPanel, except each column hangs from the bottom edge (items keep top-to-bottom order within the column). Adjacent items overlap by 1px so their uniform borders collapse into a single shared line, and the outer boundary (edges facing the desktop rather than another tab) is drawn 1px thicker via OnRender segments in a reserved inset ring — interior edges stay thin while the silhouette gets a heavier outline.
public sealed class BottomAlignedColumnPanel : Panel
{
    private const double BorderCollapse = 1;
    private const double OuterBorder = 1;
    private static readonly SolidColorBrush OuterBorderBrush = new(Color.FromRgb(0x10, 0x10, 0x10));

    static BottomAlignedColumnPanel()
    {
        OuterBorderBrush.Freeze();
    }

    private readonly List<Rect> columnRects = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxHeight = double.IsInfinity(availableSize.Height) ? availableSize.Height : availableSize.Height - 2 * OuterBorder;
        double x = 0;
        double y = 0;
        double columnWidth = 0;
        double usedHeight = 0;
        bool any = false;
        foreach (UIElement child in InternalChildren)
        {
            any = true;
            child.Measure(new Size(double.PositiveInfinity, maxHeight));
            Size size = child.DesiredSize;
            if (y > 0 && y + size.Height - BorderCollapse > maxHeight)
            {
                x += columnWidth - BorderCollapse;
                y = 0;
                columnWidth = 0;
            }
            y += y > 0 ? size.Height - BorderCollapse : size.Height;
            columnWidth = Math.Max(columnWidth, size.Width);
            usedHeight = Math.Max(usedHeight, y);
        }
        if (!any)
        {
            return new Size(0, 0);
        }
        double width = x + columnWidth + 2 * OuterBorder;
        return new Size(width, double.IsInfinity(availableSize.Height) ? usedHeight + 2 * OuterBorder : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        columnRects.Clear();
        double contentHeight = finalSize.Height - 2 * OuterBorder;
        var columns = new List<List<UIElement>>();
        var current = new List<UIElement>();
        double y = 0;
        foreach (UIElement child in InternalChildren)
        {
            Size size = child.DesiredSize;
            if (y > 0 && y + size.Height - BorderCollapse > contentHeight)
            {
                columns.Add(current);
                current = new List<UIElement>();
                y = 0;
            }
            current.Add(child);
            y += y > 0 ? size.Height - BorderCollapse : size.Height;
        }
        if (current.Count > 0)
        {
            columns.Add(current);
        }
        double x = OuterBorder;
        double bottom = finalSize.Height - OuterBorder;
        foreach (List<UIElement> column in columns)
        {
            double columnWidth = column.Max(c => c.DesiredSize.Width);
            double columnHeight = column.Sum(c => c.DesiredSize.Height) - (column.Count - 1) * BorderCollapse;
            double startY = Math.Max(OuterBorder, bottom - columnHeight);
            columnRects.Add(new Rect(x, startY, columnWidth, bottom - startY));
            foreach (UIElement child in column)
            {
                child.Arrange(new Rect(x, startY, columnWidth, child.DesiredSize.Height));
                startY += child.DesiredSize.Height - BorderCollapse;
            }
            x += columnWidth - BorderCollapse;
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (columnRects.Count == 0)
        {
            return;
        }
        Rect first = columnRects[0];
        Rect last = columnRects[^1];
        double bottom = first.Bottom;
        dc.DrawRectangle(OuterBorderBrush, null, new Rect(first.Left - OuterBorder, bottom, last.Right - first.Left + 2 * OuterBorder, OuterBorder));
        for (int i = 0; i < columnRects.Count; i++)
        {
            Rect rect = columnRects[i];
            dc.DrawRectangle(OuterBorderBrush, null, new Rect(rect.Left - OuterBorder, rect.Top - OuterBorder, rect.Width + 2 * OuterBorder, OuterBorder));
            if (i == 0)
            {
                dc.DrawRectangle(OuterBorderBrush, null, new Rect(rect.Left - OuterBorder, rect.Top - OuterBorder, OuterBorder, rect.Height + 2 * OuterBorder));
            }
            else if (rect.Top < columnRects[i - 1].Top)
            {
                dc.DrawRectangle(OuterBorderBrush, null, new Rect(rect.Left - OuterBorder, rect.Top - OuterBorder, OuterBorder, columnRects[i - 1].Top - rect.Top + OuterBorder));
            }
            if (i == columnRects.Count - 1)
            {
                dc.DrawRectangle(OuterBorderBrush, null, new Rect(rect.Right, rect.Top - OuterBorder, OuterBorder, rect.Height + 2 * OuterBorder));
            }
            else if (rect.Top < columnRects[i + 1].Top)
            {
                dc.DrawRectangle(OuterBorderBrush, null, new Rect(rect.Right, rect.Top - OuterBorder, OuterBorder, columnRects[i + 1].Top - rect.Top + OuterBorder));
            }
        }
    }
}
