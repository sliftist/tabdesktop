using System.Globalization;
using System.Windows.Data;

namespace TabDesktop;

// Caps an expandable settings section to a fraction of the window height, so a long list scrolls inside its own section instead of stretching the whole page.
public sealed class FractionOfHeightConverter : IValueConverter
{
    public double Fraction { get; set; } = 1;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is double height ? height * Fraction : double.PositiveInfinity;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
