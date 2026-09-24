using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Views;

/// <summary>Config font-family string → FontFamily; blank means the app
/// default (the bundled brand face). Drives the live sample on the Appearance page.</summary>
public sealed class FontFamilyStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = (value as string)?.Trim() ?? "";
        try
        {
            return AppFonts.Create(name);
        }
        catch (ArgumentException)
        {
            return AppFonts.CreateDefault();
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Count → Visible when zero (empty-state hints).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
