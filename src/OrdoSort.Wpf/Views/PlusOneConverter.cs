using System.Globalization;
using System.Windows.Data;

namespace OrdoSort.Wpf.Views;

/// <summary>DataGrid's AlternationIndex is zero-based; the "#" column people
/// read is one-based. Used only by FilenameListWindow's row-number column,
/// whose value cannot come from a property because FileRow has no index.</summary>
public sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
