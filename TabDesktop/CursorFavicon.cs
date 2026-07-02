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
        // Gradient fills collapse to their first stop color — at icon size a flat approximation is indistinguishable and avoids implementing SVG gradient geometry.
        var gradientColors = new Dictionary<string, Color>();
        foreach (XElement gradient in root.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient"))
        {
            string? id = gradient.Attribute("id")?.Value;
            string? stopColor = gradient.Descendants().FirstOrDefault(e => e.Name.LocalName == "stop")?.Attribute("stop-color")?.Value;
            if (id is null || stopColor is null)
            {
                continue;
            }
            try
            {
                gradientColors[id] = (Color)ColorConverter.ConvertFromString(stopColor);
            }
            catch
            {
            }
        }
        var group = new DrawingGroup();
        string? viewBox = root.Attribute("viewBox")?.Value;
        if (viewBox is not null)
        {
            double[] bounds = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
            if (bounds.Length == 4)
            {
                var viewBoxRect = new Rect(bounds[0], bounds[1], bounds[2], bounds[3]);
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(viewBoxRect)));
                group.ClipGeometry = new RectangleGeometry(viewBoxRect);
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
            Brush brush = ResolveFill(fill, gradientColors);
            try
            {
                Drawing drawing = new GeometryDrawing(brush, null, Geometry.Parse(data));
                Matrix? transform = ParseTransform(pathElement.Attribute("transform")?.Value);
                if (transform is Matrix matrix)
                {
                    drawing = new DrawingGroup { Transform = new MatrixTransform(matrix), Children = { drawing } };
                }
                group.Children.Add(drawing);
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

    private static Brush ResolveFill(string fill, Dictionary<string, Color> gradientColors)
    {
        Match urlMatch = Regex.Match(fill, @"^url\(#(.+)\)$");
        if (urlMatch.Success)
        {
            return gradientColors.TryGetValue(urlMatch.Groups[1].Value, out Color gradientColor) ? new SolidColorBrush(gradientColor) : Brushes.Gray;
        }
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill));
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    private static Matrix? ParseTransform(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        Match match = Regex.Match(value, @"^\s*(matrix|translate|scale)\(([^)]+)\)\s*$");
        if (!match.Success)
        {
            return null;
        }
        double[] args = match.Groups[2].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray();
        return match.Groups[1].Value switch
        {
            "matrix" when args.Length == 6 => new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]),
            "translate" when args.Length >= 1 => new Matrix(1, 0, 0, 1, args[0], args.Length > 1 ? args[1] : 0),
            "scale" when args.Length >= 1 => new Matrix(args[0], 0, 0, args.Length > 1 ? args[1] : args[0], 0, 0),
            _ => null,
        };
    }
}
