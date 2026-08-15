using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OrdoSort.Wpf.Views;

/// <summary>Shows an element only once the window is at least
/// <c>ConverterParameter</c> DIPs wide — the header's degradation ladder.
///
/// The header must survive down to EnterCompact's own <c>MinWidth = 400</c>
/// (MainWindow.xaml.cs), and its menu is the one thing in there that cannot
/// usefully shrink: a WPF <see cref="System.Windows.Controls.Menu"/> hosts its
/// items in a WrapPanel, so squeezing it reflows the header onto a second row
/// rather than narrowing it. The toolbar opposite it CAN shrink, because its
/// two captions are redundant — the folders combo repeats its label in its own
/// tooltip, and Refresh keeps its icon and "Rescan the inbox now". So the
/// captions yield first and the menu never has to.
///
/// Thresholds are the window widths the app already works in, not new numbers:
/// 620 is where <see cref="WidthToColumnsConverter"/>'s 3rd tile column arrives
/// (its own comment derives it), and 470 is the compact dashboard's parked
/// width. Bound to the WINDOW's ActualWidth rather than the header's, so the
/// ladder is expressed in the same units EnterCompact sets.
///
/// One-way by design: layout reads width, never writes it.</summary>
public sealed class WidthAtLeastToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value as double? ?? 0d;
        // A window mid-construction reports NaN/0 before its first measure;
        // treat that as "not yet wide enough" so nothing flashes in at 0.
        if (double.IsNaN(width)) width = 0d;
        var threshold = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) => t,
            _ => 0d,
        };
        return width >= threshold ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("width drives visibility, never the reverse");
}
