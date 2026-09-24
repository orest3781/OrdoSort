namespace OrdoSort.Wpf.Theme;

/// <summary>A color as plain bytes — no WPF types, so palette logic and the
/// WCAG contrast contract stay unit-testable without a dispatcher.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>A named, user-selectable palette. The registry below is the single
/// enumeration every scheme-aware test iterates — adding a scheme here automatically
/// puts it behind the 4.5:1 contrast wall in ThemeTests.</summary>
public sealed record ThemeScheme(string Key, string DisplayName, ThemePalette Palette, bool IsDark);

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
                       // TEXT measures 2.69:1 vs Dark.Surface / 3.14:1 vs
                       // Dark.WindowBg, found while building the Unlock file list's
                       // Unreadable note (Task 1 Step 3) -- the exact Success-shaped
                       // problem Step 1 already fixed once, just discovered a step
                       // late and on a different token. Existing Danger-as-background
                       // usage is unaffected and OUT OF SCOPE here. The other three
                       // foreground usages Task 1 found (MainWindow.xaml:231,
                       // ProcessingView.xaml:55, ReadyView.xaml:113) were switched to
                       // StatusRed by Task 3 Part B: ProcessingView/ReadyView sit on
                       // WindowBg and now clear 4.5:1 in both palettes (the pairing
                       // already covered by ThemeTests.TextPairs' {StatusRed,
                       // WindowBg} entry). MainWindow.xaml:231 is the one exception --
                       // its real background is Theme.SurfaceRaised, not
                       // Surface/WindowBg, and StatusRed was never tuned against that
                       // (one step lighter than Surface in dark mode). Measured there:
                       // 4.11:1 dark / 5.44:1 light -- an improvement over Danger's
                       // 2.26:1 but still short of this app's 4.5 floor in dark,
                       // though it clears WCAG's 3:1 non-text/icon floor. Left as a
                       // known, open gap (see MainWindow.xaml's own comment at that
                       // site and HighlightContrastTests' MainWindowToastIconContrast)
                       // rather than adding a token tuned for one call site.
                       //
                       // GAP CLOSED 2026-08-09 via StatusRedRaised (below) + the newly
                       // materialized SurfaceRaised field: the toast icon now binds
                       // Theme.StatusRedRaised instead of Theme.StatusRed. See
                       // StatusRedRaised's own comment for the replacement values.
    Rgb StatusRedRaised, // the red status voice, readable on SurfaceRaised specifically
                         // -- StatusRed was tuned against Surface/WindowBg and can fall
                         // short of SurfaceRaised (one step lighter in dark mode).
    Rgb TileDefaultBg, // dashboard tile with no configured color
    Rgb SurfaceRaised, // floating surfaces (the alert toast's card, Processing's
                       // running-file chip, Match & Merge's side panel) -- one
                       // step LIGHTER than Surface in dark mode, unchanged from
                       // Surface in light (light's Surface is already near-white;
                       // the shadow does the lifting there instead). Materialized
                       // 2026-08-09 (byte-identical to the Mix(Surface, white,
                       // 0.06) derivation ThemeManager.cs used to compute this at
                       // publish time) so a per-scheme StatusRedRaised, tuned
                       // against the REAL background one call site actually
                       // paints on, has something concrete to be tuned against.
    Rgb BorderStrong,  // emphasized borders (focus rings, active dividers)
    Rgb AccentBronze,  // the brand accent: focus rings, badges, selected tabs
                       // ------------------------------------------------------- hover/pressed
                       // Hover-tint strength review, round 2 (2026-08-08). Round 1 raised a
                       // SINGLE derived Mix(Surface, Text, amount) shared by every hover/
                       // pressed surface in the app. A parallel audit found that mechanism
                       // fundamentally can't work: Mix moves the background toward Text, which
                       // simultaneously (a) grows the surround-delta and (b) shrinks contrast
                       // for every OTHER foreground that can sit on it -- and two status
                       // colours were found to ALREADY be below the 4.5:1 floor at even the
                       // old, barely-there 0.08 amount (StatusGreen light 4.343:1, StatusRed
                       // dark 3.945:1), which no test had ever caught. These three fields
                       // replace that shared formula with hand-tuned, per-palette constants --
                       // same pattern as StatusAmber/StatusGreen/StatusRed above -- split into
                       // two tiers by what can actually render underneath them, verified by
                       // reading every real IsMouseOver/IsSubmenuOpen consumer in Styles.xaml
                       // and every window, not assumed:
                       //
                       //   CHROME tier (SurfaceHover/SurfacePressed) -- MenuItem (all three
                       //   templates), TabItem/SectionTab, ChipButton's resting fill, and
                       //   MainWindow's Rescan button. None of these ever paints anything but
                       //   Theme.Text underneath while hovered/pressed in THIS app: the one
                       //   theoretical exception (IsEnabled="False" combined with a hover/
                       //   highlight state, which would show SubtleText instead) is provably
                       //   unreachable here -- grep confirms zero MenuItem/TabItem in
                       //   src/OrdoSort.Wpf/**/*.xaml ever binds or sets IsEnabled. Free to be
                       //   strong: bounded only by Theme.Text, which has enormous headroom
                       //   (>=6.5:1 at every value chosen here, both palettes).
                       //
                       //   ROW tier (RowHover) -- DataGridRow (BulkRename/MatchMerge/History/
                       //   Triage, newly added this round -- Styles.xaml had NO DataGridRow
                       //   style at all before this, confirmed by the parallel audit),
                       //   ListBoxItem (RouteList/LabelMaker/ManageSaved/UnlockWindow -- whose
                       //   FileList Note column is exactly where StatusGreen/StatusRed were
                       //   found broken), the Calendar family's day/month/nav buttons (whose
                       //   IsInactive state pairs SubtleText with the SAME hover trigger), and
                       //   ReadyView's "open inbox" button (whose BigCount can render
                       //   StatusRed mid-hover when CountAlertOn is set). Bounded by the
                       //   TIGHTEST of Text/SubtleText/StatusAmber/StatusGreen/StatusRed in
                       //   each palette -- verified by brute-force byte search, not estimated:
                       //   in light mode Surface is already pure white (255,255,255), so
                       //   there is NO headroom to lighten further, and StatusGreen's own
                       //   luminance (0.1548) caps how far this can darken before contrast
                       //   against it drops below 4.5 -- the safe zone is only
                       //   RGB 241-254, and even PURE BLACK does not recover it (WCAG's
                       //   (Lfg+0.05)/(Lbg+0.05) tops out at 4.10 for StatusGreen against
                       //   black, still short of 4.5). No "pressed" variant exists: none of
                       //   this tier's consumers have a WPF IsPressed concept (DataGridRow and
                       //   ListBoxItem have none; the Calendar/inbox buttons' existing styles
                       //   never gained one).
                       //
                       // Every consumer verified against ITS OWN real rendered background by
                       // HighlightContrastTests (resolved brushes, not the resource value
                       // read directly) -- see that file's Hover/Pressed-strength region.
    Rgb SurfaceHover,   // chrome hover (Text-only)
    Rgb SurfacePressed, // chrome pressed (Text-only), stronger than SurfaceHover
    Rgb RowHover)       // row/list hover (Text+SubtleText+StatusAmber+StatusGreen+StatusRed)
{
    // Brand palette "ink & bronze" (2026-09 rebrand): warm paper and ink in
    // light, graphite and brass in dark. Every text pairing is enforced to
    // >= 4.5:1 by ThemeTests.TextPairs; the tightest in light is StatusGreen
    // on WindowBg at 4.62:1. Status, warning and danger colours are
    // unchanged from the pre-rebrand palettes, which the hover-tint and
    // status reviews above tuned.
    public static ThemePalette Light { get; } = new(
        WindowBg: new(245, 243, 238),   // warm paper
        Surface: new(255, 255, 255),
        Text: new(28, 31, 36),          // ink, 14.9:1 on WindowBg
        SubtleText: new(88, 93, 102),
        Border: new(195, 189, 176),
        Accent: new(36, 40, 46),        // primary action: an ink button
        AccentText: new(255, 255, 255),
        Warning: new(255, 236, 179),
        WarningText: new(102, 60, 0),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(146, 90, 4),
        // Success itself clears 4.5:1 here (4.62:1 WindowBg, 5.13:1
        // Surface) -- reused verbatim.
        StatusGreen: new(46, 125, 50),
        // Danger clears 4.5:1 as foreground text here -- reused verbatim.
        StatusRed: new(192, 57, 43),
        // SurfaceRaised == Surface in light, so StatusRed already clears it.
        StatusRedRaised: new(192, 57, 43),
        TileDefaultBg: new(232, 229, 222),
        // Light's Surface is already white; the shadow does the lifting.
        SurfaceRaised: new(255, 255, 255),
        BorderStrong: new(122, 116, 104),
        // Brand bronze, 5.71:1 on WindowBg.
        AccentBronze: new(122, 90, 38),
        // Chrome tier (Text-only): the same lightness as before the rebrand,
        // tinted warm to sit on paper.
        SurfaceHover: new(198, 196, 191),
        SurfacePressed: new(160, 158, 153),
        RowHover: new(255, 249, 220));

    public static ThemePalette Dark { get; } = new(
        WindowBg: new(26, 28, 31),
        Surface: new(36, 39, 43),
        Text: new(236, 234, 229),        // warm off-white
        SubtleText: new(169, 173, 179),
        Border: new(74, 78, 85),
        Accent: new(214, 210, 202),      // primary action: a paper button
        AccentText: new(26, 28, 31),
        Warning: new(84, 62, 8),
        WarningText: new(255, 224, 130),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(240, 173, 78),
        // A lighter green than Success, which fails 4.5:1 on dark surfaces.
        StatusGreen: new(129, 199, 132),
        // A lighter red than Danger, which fails 4.5:1 as dark-mode text.
        StatusRed: new(229, 115, 115),
        // Brighter again for SurfaceRaised, one step lighter than Surface.
        StatusRedRaised: new(234, 130, 130),
        TileDefaultBg: new(52, 56, 61),
        // Mix(Surface, white, 0.06).
        SurfaceRaised: new(49, 52, 56),
        BorderStrong: new(110, 116, 124),
        // Brand brass, 8.14:1 on WindowBg.
        AccentBronze: new(210, 174, 107),
        // Chrome tier lightens toward Text in dark mode (darkening has no
        // room left below a near-black Surface).
        SurfaceHover: new(77, 79, 83),
        SurfacePressed: new(99, 101, 105),
        RowHover: new(52, 38, 24));

    // ------------------------------------------------------ scheme registry

    public static IReadOnlyList<ThemeScheme> Schemes { get; } = new[]
    {
        new ThemeScheme("light", "Light", Light, IsDark: false),
        new ThemeScheme("dark", "Dark", Dark, IsDark: true),
    };

    /// <summary>Case-insensitive lookup by key ("light"/"dark"). Null for
    /// null/blank/unknown (including "auto"): callers fall back to a default.</summary>
    public static ThemeScheme? FindScheme(string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : Schemes.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

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

    // --------------------------------------------------------- CIE Lab math
    //
    // Hover-tint strength review, round 3 (2026-08-08): ContrastRatio above
    // is a pure LUMINANCE ratio -- it cannot see hue at all, which is
    // exactly why round 2's neutral-grey Theme.RowHover, tuned by holding
    // luminance near Surface to protect the five foregrounds, measured as
    // FAINTER than round 1's shared value even though round 1 was the
    // complaint being fixed (round 1 grey 8.09:1 light / 8.73 dark on the
    // scale below; round 2 grey only 1.84 / 7.37). A chromatic tint that
    // shifts hue at near-constant luminance is plainly visible while
    // costing almost nothing in WCAG contrast -- this is that measurement.
    // CIE76 (D65 white point, the standard illuminant WCAG's own linear-RGB
    // math already assumes): ~1-2 is the classic "just noticeable
    // difference" for a trained eye under ideal viewing conditions; this
    // codebase's own hover tints target well above that floor. See
    // ThemeManager.cs's comment at Theme.RowHover for the full table this
    // round measured before choosing a value.

    private static (double X, double Y, double Z) ToXyz(Rgb c)
    {
        var (r, g, b) = (Linear(c.R), Linear(c.G), Linear(c.B));
        return (
            r * 0.4124564 + g * 0.3575761 + b * 0.1804375,
            r * 0.2126729 + g * 0.7151522 + b * 0.0721750,
            r * 0.0193339 + g * 0.1191920 + b * 0.9503041);
    }

    // D65 reference white, the same illuminant sRGB (and this file's own
    // WCAG Linear/Luminance) is defined against.
    private const double XN = 0.95047, YN = 1.0, ZN = 1.08883;

    private static double LabF(double t)
    {
        const double delta = 6.0 / 29.0;
        return t > delta * delta * delta ? Math.Cbrt(t) : t / (3 * delta * delta) + 4.0 / 29.0;
    }

    /// <summary>CIE L*a*b* (D65), the perceptual space ContrastRatio cannot
    /// see into: L* tracks lightness (roughly what WCAG luminance also
    /// tracks), a*/b* track the hue/chroma a chromatic tint actually
    /// spends.</summary>
    public static (double L, double A, double B) Lab(Rgb c)
    {
        var (x, y, z) = ToXyz(c);
        var (fx, fy, fz) = (LabF(x / XN), LabF(y / YN), LabF(z / ZN));
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIE76 perceptual colour distance between two RGB colours --
    /// the Euclidean distance in Lab space. NOT a substitute for
    /// ContrastRatio (that still governs text legibility); this measures
    /// whether two BACKGROUNDS read as visibly different from each other,
    /// which a luminance-only ratio structurally cannot when the two
    /// colours are held close in lightness on purpose.</summary>
    public static double DeltaE76(Rgb a, Rgb b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }
}
