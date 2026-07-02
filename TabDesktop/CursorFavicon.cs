using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace TabDesktop;

// Cursor's title carries the workspace folder name, not its path, so the directory is resolved by probing known code roots for a folder of that name. If it contains a favicon.svg, that becomes the tab icon. SVG support is deliberately minimal — viewBox plus <path> elements with plain fills — which covers typical favicons; anything fancier just falls back to the rule icon.
public static class CursorFavicon
{
    private static readonly string[] DirectoryRoots = { "D:/repos" };
    private static readonly Regex CursorTitle = new(@"^(?:.* - )?(.+) - Cursor$");

    private sealed record CacheEntry(DateTime Mtime, ImageSource? Image);

    private static readonly Dictionary<string, CacheEntry> cache = new();

    public static ImageSource? TryGet(string title)
    {
        Match match = CursorTitle.Match(title);
        if (!match.Success)
        {
            return null;
        }
        string folder = match.Groups[1].Value;
        foreach (string root in DirectoryRoots)
        {
            try
            {
                string path = Path.Combine(root, folder, "favicon.svg");
                if (!File.Exists(path))
                {
                    continue;
                }
                DateTime mtime = File.GetLastWriteTimeUtc(path);
                if (cache.TryGetValue(path, out CacheEntry? entry) && entry.Mtime == mtime)
                {
                    return entry.Image;
                }
                ImageSource? image = ParseSvg(path);
                cache[path] = new CacheEntry(mtime, image);
                return image;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
        }
        return null;
    }

    private static ImageSource? ParseSvg(string path)
    {
        XElement? root = XDocument.Load(path).Root;
        if (root is null)
        {
            return null;
        }
        var group = new DrawingGroup();
        string? viewBox = root.Attribute("viewBox")?.Value;
        if (viewBox is not null)
        {
            double[] bounds = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
            if (bounds.Length == 4)
            {
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(bounds[0], bounds[1], bounds[2], bounds[3]))));
            }
        }
        int pathCount = 0;
        foreach (XElement pathElement in root.Descendants().Where(e => e.Name.LocalName == "path"))
        {
            string? data = pathElement.Attribute("d")?.Value;
            string fill = pathElement.Attribute("fill")?.Value ?? "#000000";
            if (string.IsNullOrEmpty(data) || fill == "none")
            {
                continue;
            }
            Brush brush;
            try
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill));
            }
            catch
            {
                brush = Brushes.Gray;
            }
            try
            {
                group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(data)));
                pathCount++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
        }
        if (pathCount == 0)
        {
            return null;
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
