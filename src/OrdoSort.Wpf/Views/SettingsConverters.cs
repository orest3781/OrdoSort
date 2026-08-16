using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Views;

/// <summary>Config color string ("#2e7d32" / "red") → brush; transparent for
/// blank or invalid. Used for the color chips in the settings lists.</summary>
public sealed class ColorStringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemePalette.ParseColor(value as string) is { } c
            ? ThemeManager.Brush(c)
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Color string → the black/white brush that contrasts with it —
/// the ✓ on the selected swatch stays readable on any swatch color.</summary>
public sealed class ColorStringToForeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemePalette.ParseColor(value as string) is { } c
            ? ThemeManager.Brush(ThemePalette.IdealForeground(c))
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Config font-family string → FontFamily; blank means the app
/// default (Segoe UI). Drives the live sample on the Appearance page.</summary>
public sealed class FontFamilyStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = (value as string)?.Trim() ?? "";
        try
        {
            return new FontFamily(name.Length == 0 ? App.DefaultFontChain : name);
        }
        catch (ArgumentException)
        {
            return new FontFamily(App.DefaultFontChain);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The size textbox's text → a preview font size; anything invalid
/// falls back to the app default (14) so the sample never explodes.</summary>
public sealed class FontSizeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        int.TryParse((value as string)?.Trim(), out var size) && size is >= 6 and <= 72
            ? (double)size
            : 14.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A saved password's stored value ("dpapi:…" or plaintext) →
/// status text for the Manage saved… dialog, which binds straight to the
/// Core SavedPassword records rather than a wrapping edit view model.
/// Plaintext is the normal, intended state now (portable-saved-passwords,
/// 2026-08-08) — a "dpapi:" value is a leftover from before that change,
/// and is converted to plaintext the next time ANY saved-password change
/// persists (add or remove — see
/// UnlockViewModel.MigrateProtectedToPlaintext), not specifically "on next
/// save" — the wording here must keep matching that, and the
/// ManageSavedWindow note above the list.</summary>
public sealed class PasswordStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && PasswordVault.IsProtected(s)
            ? "protected to this computer — becomes plain text automatically"
            : "plain text";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Full path → just the filename (lists show names; the tooltip
/// carries the full path).</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? System.IO.Path.GetFileName(s) : "";

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

/// <summary>Text → Visible when it says something, Collapsed when it's empty.
///
/// For a line that only exists when there's something to report, so it takes
/// no vertical space the rest of the time.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>[swatch color, currently chosen color] → "✓" when they match —
/// marks the selected swatch in the palette strip.</summary>
public sealed class SwatchCheckConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var swatch = (values.ElementAtOrDefault(0) as string)?.Trim() ?? "";
        var chosen = (values.ElementAtOrDefault(1) as string)?.Trim() ?? "";
        return swatch.Length > 0 && swatch.Equals(chosen, StringComparison.OrdinalIgnoreCase)
            ? "✓" : "";
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
