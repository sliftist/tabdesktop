using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TabDesktop;

// WPF's TextTrimming only supports trailing ellipsis, so this trims from the middle instead — window titles usually carry the distinguishing part at both ends (document name ... app name). TextBlock seals MeasureOverride, so this renders its own single line rather than subclassing.
public sealed class MiddleEllipsisTextBlock : FrameworkElement
{
    private const string Ellipsis = "…";

    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(nameof(FullText), typeof(string), typeof(MiddleEllipsisTextBlock), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string FullText
    {
        get => (string)GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    private FormattedText? line;

    protected override Size MeasureOverride(Size availableSize)
    {
        string text = FullText;
        if (string.IsNullOrEmpty(text))
        {
            line = null;
            return new Size(0, 0);
        }
        line = Format(FitText(text, availableSize.Width));
        return new Size(Math.Min(line.WidthIncludingTrailingWhitespace, availableSize.Width), line.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (line is not null)
        {
            dc.DrawText(line, new Point(0, 0));
        }
    }

    private string FitText(string text, double maxWidth)
    {
        if (double.IsInfinity(maxWidth) || Format(text).WidthIncludingTrailingWhitespace <= maxWidth)
        {
            return text;
        }
        int low = 0;
        int high = text.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (Format(BuildTrimmed(text, mid)).WidthIncludingTrailingWhitespace <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }
        return BuildTrimmed(text, low);
    }

    private static string BuildTrimmed(string text, int keptChars)
    {
        int front = (keptChars + 1) / 2;
        int back = keptChars / 2;
        return text[..front] + Ellipsis + text[^back..];
    }

    private FormattedText Format(string text)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this), TextElement.GetFontStretch(this));
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection, typeface, TextElement.GetFontSize(this), TextElement.GetForeground(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
