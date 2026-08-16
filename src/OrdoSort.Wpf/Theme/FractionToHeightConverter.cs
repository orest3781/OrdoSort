using System.Globalization;
using System.Windows.Data;

namespace OrdoSort.Wpf.Theme;

/// <summary>VM spark fractions (0..1) into pixel heights for the drawn
/// weekly bars — 60px strip, no charting dependency (spec decision 10).</summary>
public sealed class FractionToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double fraction ? Math.Max(2.0, fraction * 60.0) : 2.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
