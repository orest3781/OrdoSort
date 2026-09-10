namespace OrdoSort.Wpf.Theme;

/// <summary>The app's typography defaults.
///
/// Lives here rather than on the application class because two executables
/// now share this UI, and a font fallback that only one of them can name is
/// a fallback the other silently does without. <see cref="DefaultChain"/> is
/// what a blank ui_font_family means, and what an unresolvable family name
/// falls back to.</summary>
public static class AppFonts
{
    /// <summary>Segoe UI Variable (the Windows 11 optical font) with a plain
    /// Segoe UI fallback for older Windows.</summary>
    public const string DefaultChain = "Segoe UI Variable Text, Segoe UI";
}
