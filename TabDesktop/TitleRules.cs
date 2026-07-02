using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace TabDesktop;

// User-editable title simplification: each rule is a match regex, a replacement (regex substitution), and an optional icon given as SVG path data — WPF's Geometry.Parse consumes SVG "d" syntax directly, so simple brand icons need no SVG library. The file is re-read whenever its mtime changes, so edits apply without restarting.
public static class TitleRules
{
    private static readonly string RulesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabDesktop", "title-rules.json");

    private const string DefaultRulesJson = """
[
  {
    "match": "^(?:.* - )?(.+) - Cursor$",
    "replace": "$1",
    "icon": {
      "viewBox": "0 0 24 24",
      "paths": [
        { "d": "M12 1 L22 6.5 L12 12 L2 6.5 Z", "fill": "#E8E8E8" },
        { "d": "M2 6.5 L12 12 L12 23 L2 17.5 Z", "fill": "#8A8A8A" },
        { "d": "M22 6.5 L12 12 L12 23 L22 17.5 Z", "fill": "#C0C0C0" }
      ]
    }
  },
  {
    "match": "^(.*) - YouTube\\b.*$",
    "replace": "$1",
    "icon": {
      "viewBox": "0 0 24 24",
      "paths": [
        { "d": "M23.498 6.186a3.016 3.016 0 0 0-2.122-2.136C19.505 3.545 12 3.545 12 3.545s-7.505 0-9.377.505A3.017 3.017 0 0 0 .502 6.186C0 8.07 0 12 0 12s0 3.93.502 5.814a3.016 3.016 0 0 0 2.122 2.136c1.871.505 9.376.505 9.376.505s7.505 0 9.377-.505a3.015 3.015 0 0 0 2.122-2.136C24 15.93 24 12 24 12s0-3.93-.502-5.814zM9.545 15.568V8.432L15.818 12l-6.273 3.568z", "fill": "#FF0000" }
      ]
    }
  },
  {
    "match": "^(?:.* [-–] )?Gemini\\b.*$",
    "replace": "Gemini",
    "icon": {
      "viewBox": "0 0 24 24",
      "paths": [
        { "d": "M12 24C12 17.373 6.627 12 0 12 6.627 12 12 6.627 12 0c0 6.627 5.373 12 12 12-6.627 0-12 5.373-12 12z", "fill": "#8E75B2" }
      ]
    }
  }
]
""";

    private sealed class RuleConfig
    {
        public string Match { get; set; } = "";
        public string Replace { get; set; } = "";
        public IconConfig? Icon { get; set; }
    }

    private sealed class IconConfig
    {
        public string ViewBox { get; set; } = "0 0 24 24";
        public List<IconPathConfig> Paths { get; set; } = new();
    }

    private sealed class IconPathConfig
    {
        public string D { get; set; } = "";
        public string Fill { get; set; } = "#FFFFFF";
    }

    private sealed record CompiledRule(Regex Match, string Replace, DrawingImage? Icon);

    private static List<CompiledRule> rules = new();
    private static DateTime loadedMtime;

    // Returns true when the rules actually (re)loaded, so callers know to re-raise derived bindings.
    public static bool EnsureLoaded()
    {
        try
        {
            if (!File.Exists(RulesPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RulesPath)!);
                File.WriteAllText(RulesPath, DefaultRulesJson);
            }
            DateTime mtime = File.GetLastWriteTimeUtc(RulesPath);
            if (mtime == loadedMtime)
            {
                return false;
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            List<RuleConfig> configs = JsonSerializer.Deserialize<List<RuleConfig>>(File.ReadAllText(RulesPath), options) ?? new List<RuleConfig>();
            var compiled = new List<CompiledRule>();
            foreach (RuleConfig config in configs)
            {
                compiled.Add(new CompiledRule(new Regex(config.Match), config.Replace, CompileIcon(config.Icon)));
            }
            rules = compiled;
            loadedMtime = mtime;
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return false;
        }
    }

    public static string Simplify(string title)
    {
        foreach (CompiledRule rule in rules)
        {
            if (rule.Match.IsMatch(title))
            {
                return rule.Match.Replace(title, rule.Replace);
            }
        }
        return title;
    }

    public static ImageSource? GetIcon(string title)
    {
        foreach (CompiledRule rule in rules)
        {
            if (rule.Match.IsMatch(title))
            {
                return rule.Icon;
            }
        }
        return null;
    }

    private static DrawingImage? CompileIcon(IconConfig? icon)
    {
        if (icon is null || icon.Paths.Count == 0)
        {
            return null;
        }
        var group = new DrawingGroup();
        double[] viewBox = icon.ViewBox.Split(' ').Select(double.Parse).ToArray();
        if (viewBox.Length == 4)
        {
            // A transparent rect spanning the viewBox makes the image bounds match the SVG canvas, preserving the intended padding and aspect ratio.
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(viewBox[0], viewBox[1], viewBox[2], viewBox[3]))));
        }
        foreach (IconPathConfig path in icon.Paths)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(path.Fill));
            group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(path.D)));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
