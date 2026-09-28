using System.Windows;
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
    /// reachable from either executable. A pack URI only parses once WPF has
    /// registered the pack scheme, so it is built on first use rather than
    /// when the class loads: the font list and size rule below must work
    /// before any window exists (Box Labels' settings model reads them).</summary>
    public static Uri FontBaseUri => _fontBaseUri ??= new("pack://application:,,,/OrdoSort.Ui;component/");

    private static Uri? _fontBaseUri;

    /// <summary>The default UI font family.</summary>
    public static FontFamily CreateDefault() => new(FontBaseUri, DefaultChain);

    /// <summary>The family for a ui_font_family value: blank means the
    /// default; anything else is a system family name.</summary>
    /// <param name="name">The configured family name, or blank.</param>
    /// <exception cref="ArgumentException">The name is not a valid font
    /// family string.</exception>
    public static FontFamily Create(string? name) =>
        string.IsNullOrWhiteSpace(name) ? CreateDefault() : new FontFamily(FontBaseUri, name.Trim());

    /// <summary>The fonts Settings offers (OrdoSort's and Box Labels'), as
    /// ui_font_family value → name shown. "" is the bundled default.
    /// KeyValuePair because WPF binds properties, not tuple fields.</summary>
    public static readonly KeyValuePair<string, string>[] Choices =
    {
        new("", "Atkinson Hyperlegible Next (default)"),
        // The pre-rebrand default, for anyone who prefers the Windows look.
        new("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable"),
        new("Segoe UI", "Segoe UI"),
        new("Tahoma", "Tahoma"),
        new("Verdana", "Verdana"),
        new("Consolas", "Consolas"),
        new("Cascadia Mono", "Cascadia Mono"),
    };

    /// <summary>The app text size when none is set.</summary>
    public const double DefaultSize = 14.0;

    /// <summary>"" when <paramref name="text"/> is a usable text size (blank =
    /// default, or 6–72), else what to tell the user. Shared by OrdoSort's and
    /// Box Labels' Settings so the rule and its wording can't differ.</summary>
    public static string SizeProblem(string text) =>
        text.Trim().Length == 0 || (int.TryParse(text.Trim(), out var n) && n is >= 6 and <= 72)
            ? ""
            : "Base text size must be a number from 6 to 72 (or blank for the default).";

    /// <summary>Put a family and size into the resources every window's style
    /// reads (AppFontFamily, AppFontSize). Size 0 is the default.</summary>
    public static void Apply(Application app, string family, int size)
    {
        app.Resources["AppFontFamily"] = Create(family);
        app.Resources["AppFontSize"] = size == 0 ? DefaultSize : (double)size;
    }
}
