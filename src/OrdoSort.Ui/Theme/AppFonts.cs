using System.Windows.Media;

namespace OrdoSort.Wpf.Theme;

/// <summary>The app's typography defaults.
///
/// Lives here rather than on the application class because two executables
/// share this UI, and a font fallback that only one of them can name is a
/// fallback the other silently does without. A blank ui_font_family means
/// <see cref="CreateDefault"/>, and an unresolvable family name falls back
/// to it.</summary>
public static class AppFonts
{
    /// <summary>The brand typeface, bundled in this assembly under Fonts/
    /// (SIL Open Font License, see THIRD-PARTY-NOTICES).</summary>
    public const string BrandFamily = "Atkinson Hyperlegible Next";

    /// <summary>The bundled face first, then Segoe UI Variable (the Windows 11
    /// optical font) and plain Segoe UI for glyphs it lacks. The first entry
    /// is relative, so this string only resolves against
    /// <see cref="FontBaseUri"/>; always build it through
    /// <see cref="CreateDefault"/>.</summary>
    public const string DefaultChain = "./Fonts/#" + BrandFamily + ", Segoe UI Variable Text, Segoe UI";

    /// <summary>Where the bundled font files live: this assembly's resources,
    /// reachable from either executable.</summary>
    public static readonly Uri FontBaseUri = new("pack://application:,,,/OrdoSort.Ui;component/");

    /// <summary>The default UI font family.</summary>
    public static FontFamily CreateDefault() => new(FontBaseUri, DefaultChain);

    /// <summary>The family for a ui_font_family value: blank means the
    /// default; anything else is a system family name.</summary>
    /// <param name="name">The configured family name, or blank.</param>
    /// <exception cref="ArgumentException">The name is not a valid font
    /// family string.</exception>
    public static FontFamily Create(string? name) =>
        string.IsNullOrWhiteSpace(name) ? CreateDefault() : new FontFamily(FontBaseUri, name.Trim());
}
