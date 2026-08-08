namespace OrdoSort.Wpf.Theme;

/// <summary>A color as plain bytes — no WPF types, so palette logic and the
/// WCAG contrast contract stay unit-testable without a dispatcher.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>The theme token tables (light + dark) and the WCAG 2.1 contrast
/// math. Every text/background pairing shipped here is enforced to >= 4.5:1
/// by ThemeTests.</summary>
public sealed record ThemePalette(
    Rgb WindowBg,      // window background
    Rgb Surface,       // cards, inputs, grids
    Rgb Text,          // primary text on WindowBg/Surface
    Rgb SubtleText,    // secondary text (filenames, hints)
    Rgb Border,        // control borders, splitters
    Rgb Accent,        // primary action (Start, OK)
    Rgb AccentText,    // text on Accent
    Rgb Warning,       // warning banner background
    Rgb WarningText,   // text on Warning
    Rgb Danger,        // alert red (flashing tiles, illegal-name preview)
    Rgb DangerText,    // text on Danger
    Rgb Success,       // positive accents (Done summary)
    Rgb StatusAmber,   // the amber status line, readable on WindowBg
    Rgb StatusGreen,   // the green status line, readable on Surface/WindowBg. Success
                        // (46,125,50) is IDENTICAL in both palettes and measures below
                        // the 4.5:1 floor in dark (2.85:1 vs Surface, 3.33:1 vs WindowBg
                        // -- ThemePalette.ContrastRatio, measured directly, not assumed)
                        // -- so this is its own per-palette pair, the same way StatusAmber
                        // already is, rather than a reuse of Success (status-colour-
                        // vocabulary plan, 2026-08-08, Task 1 Step 1).
    Rgb StatusRed,     // the red status line, readable on Surface/WindowBg. NOT the
                        // same job as Danger/DangerText below (a background, paired
                        // with its own on-Danger text) -- Danger used AS FOREGROUND
                        // TEXT (already shipped: MainWindow.xaml:231,
                        // ProcessingView.xaml:55, ReadyView.xaml:113) measures 2.69:1
                        // vs Dark.Surface / 3.14:1 vs Dark.WindowBg, found while
                        // building the Unlock file list's Unreadable note (Task 1
                        // Step 3) -- the exact Success-shaped problem Step 1 already
                        // fixed once, just discovered a step late and on a different
                        // token. Existing Danger-as-background usage is unaffected and
                        // OUT OF SCOPE here; the other three foreground usages are a
                        // Task 3 Step 2 sweep finding, not fixed by this token.
    Rgb TileDefaultBg, // dashboard tile with no configured color
    Rgb BorderStrong,  // emphasized borders (focus rings, active dividers)
    Rgb AccentBronze)  // warm secondary accent (badges, highlights on graphite)
{
    public static ThemePalette Light { get; } = new(
        WindowBg: new(247, 248, 249),
        Surface: new(255, 255, 255),
        Text: new(23, 26, 31),
        SubtleText: new(84, 90, 99),
        Border: new(186, 192, 200),
        Accent: new(45, 50, 58),
        AccentText: new(255, 255, 255),
        Warning: new(255, 236, 179),
        WarningText: new(102, 60, 0),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(146, 90, 4),
        // Success itself already clears 4.5:1 in light (5.13:1 Surface,
        // 4.82:1 WindowBg) -- reused verbatim rather than inventing a
        // second light green nobody needs.
        StatusGreen: new(46, 125, 50),
        // Danger itself already clears 4.5:1 in light as foreground text
        // (5.44:1 Surface, 5.11:1 WindowBg) -- reused verbatim, same
        // reasoning as StatusGreen's light value above.
        StatusRed: new(192, 57, 43),
        TileDefaultBg: new(228, 230, 233),
        BorderStrong: new(120, 128, 138),
        AccentBronze: new(140, 109, 63));

    public static ThemePalette Dark { get; } = new(
        WindowBg: new(26, 28, 31),
        Surface: new(38, 41, 45),
        Text: new(233, 235, 238),
        SubtleText: new(168, 173, 180),
        Border: new(76, 82, 90),
        Accent: new(205, 210, 218),
        AccentText: new(23, 26, 31),
        Warning: new(84, 62, 8),
        WarningText: new(255, 224, 130),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(240, 173, 78),
        // A lighter green than Success (which fails 4.5:1 here): 7.26:1
        // vs Surface, 8.49:1 vs WindowBg -- comparable margin to
        // StatusAmber's own dark-mode pair (7.51:1 / 8.78:1).
        StatusGreen: new(129, 199, 132),
        // A lighter red than Danger (which fails 4.5:1 here as foreground
        // text): 4.89:1 vs Surface, 5.72:1 vs WindowBg. Red's WCAG-relative
        // luminance ceiling is lower than green's/amber's at any hue that
        // still reads as "red" rather than pink -- this is the brightest
        // red-toned value found that stays clearly red while clearing the
        // floor with real margin, not a token chosen to just barely pass.
        StatusRed: new(229, 115, 115),
        TileDefaultBg: new(54, 58, 63),
        BorderStrong: new(110, 118, 128),
        AccentBronze: new(201, 169, 106));

    // ---------------------------------------------------------- WCAG 2.1 math

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static double Luminance(Rgb c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>WCAG 2.1 contrast ratio, 1..21. AA for normal text is 4.5.</summary>
    public static double ContrastRatio(Rgb a, Rgb b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>Black or white — whichever actually contrasts more against the
    /// background. The single source of truth for text on route buttons and
    /// dashboard tiles.</summary>
    public static Rgb IdealForeground(Rgb bg)
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);
        return ContrastRatio(black, bg) >= ContrastRatio(white, bg) ? black : white;
    }

    /// <summary>Parse a config color string ("#2e7d32" or a CSS name) without
    /// WPF types. Null for blank/invalid — callers fall back to the theme.</summary>
    public static Rgb? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var c = System.Drawing.ColorTranslator.FromHtml(text.Trim());
            return new Rgb(c.R, c.G, c.B);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
