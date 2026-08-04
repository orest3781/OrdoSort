using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Views;

/// <summary>Bridges the view models' WPF-free Rgb colors into brushes.
/// Cached: ThemeManager.Brush allocates and freezes a new brush per call, and
/// the dashboard re-evaluates these bindings per tile per refresh. The brushes
/// are frozen, so sharing one instance across every binding is safe, and the
/// key space is bounded by the palette plus whatever route colours the user
/// has configured.</summary>
public sealed class RgbToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<Rgb, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Rgb c ? Cache.GetOrAdd(c, ThemeManager.Brush) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
