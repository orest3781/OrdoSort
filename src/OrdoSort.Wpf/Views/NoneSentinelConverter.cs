using System.Globalization;
using System.Windows.Data;

namespace OrdoSort.Wpf.Views;

/// <summary>Display-only: "" (no category picked) reads as "(none)" in the
/// Turn-around Time report's category combo. One-way — SelectedItem binds
/// straight to TurnaroundViewModel.CategoryColumn, a plain string, so ""
/// already round-trips with no converter needed there; this only dresses up
/// how the empty entry LOOKS in the dropdown's ItemTemplate.</summary>
public sealed class NoneSentinelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: 0 } or null ? "(none)" : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
