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
                          // -- StatusRed above was tuned against Surface/WindowBg and
                          // falls short of SurfaceRaised (one step lighter in dark mode)
                          // for graphite/ledger/microfilm: 4.11:1/4.34:1/4.38:1, all
                          // below this app's 4.5 floor (paper 5.44, manila 5.81, carbon
                          // 4.93, blueprint 5.94 already cleared it). paper/manila/
                          // carbon/blueprint reuse StatusRed verbatim below -- same
                          // pattern as light StatusGreen reusing Success. graphite/
                          // ledger/microfilm get a brighter red, (234,130,130) -- found
                          // by stepping StatusRed's (229,115,115) up while keeping R
                          // dominant (so it still reads red, not pink) until
                          // ContrastRatio vs SurfaceRaised cleared 4.55 with real
                          // margin: measured 4.689:1 (graphite), 4.947:1 (ledger),
                          // 4.986:1 (microfilm) -- also clears Surface/WindowBg (bright
                          // on dark only grows further: 5.575-7.973:1 across both).
                          // One shared value for all three rather than three
                          // independently-minimal ones: they share an identical
                          // baseline StatusRed and land close enough in SurfaceRaised
                          // luminance that a single voice reads as more deliberate than
                          // three near-identical reds a pixel apart.
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
    Rgb AccentBronze,  // warm secondary accent (badges, highlights on graphite)
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
        // SurfaceRaised == Surface here (5.44:1 unchanged) -- reused
        // verbatim, same reasoning as StatusGreen/StatusRed's light values
        // above.
        StatusRedRaised: new(192, 57, 43),
        TileDefaultBg: new(228, 230, 233),
        // Light's Surface is already near-white; SurfaceRaised == Surface
        // unchanged (see the field's own comment on the record above).
        SurfaceRaised: new(255, 255, 255),
        BorderStrong: new(120, 128, 138),
        AccentBronze: new(140, 109, 63),
        // Chrome tier, Text-only: darkest safe boundary against Text alone
        // (Text-only, byte search) is v=130 -- these sit well inside it
        // with real margin. Hover 1.729:1 surround-delta vs Surface (Text
        // stays 10.09:1); Pressed 2.649:1 (Text 6.59:1).
        SurfaceHover: new(196, 197, 198),
        SurfacePressed: new(158, 159, 161),
        // Row tier, round 3 (2026-08-08): a CHROMATIC tint, not a neutral
        // grey -- round 2's neutral RowHover held luminance near Surface to
        // protect the five foregrounds and, as a direct result, measured as
        // LESS visible than what it replaced (see ThemeManager.cs's own
        // comment at this field for the full round 1/2/3 CIE76 table).
        // Surface is already pure white, so there is no headroom to
        // *darken* into without re-breaking StatusGreen -- the fix is to
        // hold L* and shift HUE instead: warm, in this app's own bronze
        // family (AccentBronze is Lab hue ~78-85 deg; this sits at ~100
        // deg, still the same warm/gold quadrant), by dropping blue while
        // keeping red/green high, the cheapest channel for WCAG luminance
        // to spend (0.0722 weight vs green's 0.7152). CIE76 vs Surface:
        // 15.07 (round 1 grey measured 8.09, round 2 grey measured only
        // 1.84 -- an order of magnitude fainter than what this round
        // fixes). Legibility (ContrastRatio, unaffected by the metric
        // switch): Text 16.48:1, SubtleText 6.57:1, StatusAmber 5.38:1,
        // StatusGreen 4.84:1, StatusRed 5.14:1 -- all comfortably >=4.5,
        // the tightest (StatusGreen) with a real 0.34 margin, not pinned.
        RowHover: new(255, 249, 220));

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
        // StatusRed above falls short against SurfaceRaised specifically
        // (4.11:1, this scheme's own MainWindowToastIconContrast pin before
        // the fix) -- brighter red found by stepping up while staying
        // clearly red: 4.689:1 vs this palette's SurfaceRaised (51,53,57),
        // real margin over the 4.5 floor. See the field's own comment on
        // the record above for the full search.
        StatusRedRaised: new(234, 130, 130),
        TileDefaultBg: new(54, 58, 63),
        // Mix(Surface, white, 0.06), materialized (byte-identical to the
        // pre-2026-08-09 ThemeManager derivation).
        SurfaceRaised: new(51, 53, 57),
        BorderStrong: new(110, 118, 128),
        AccentBronze: new(201, 169, 106),
        // Chrome tier, Text-only, LIGHTENING (not darkening -- dark mode's
        // Surface (38,41,45) is already near-black, so darkening further
        // caps at only ~1.44:1 no matter what; going toward Text's own
        // near-white luminance has far more real room once SubtleText/
        // StatusColours are off the table). Text-only safe boundary
        // (byte search) is v=106 before Text itself enters its own
        // contrast valley; these sit inside it with margin. Hover 1.780:1
        // surround-delta (Text 6.87:1); Pressed 2.502:1 (Text 4.89:1).
        SurfaceHover: new(77, 79, 83),
        SurfacePressed: new(99, 101, 105),
        // Row tier, round 3 (2026-08-08): same chromatic-not-neutral fix as
        // Light's RowHover above -- round 2's neutral grey here (25,26,28)
        // measured CIE76 7.37 against Surface, weaker than round 1's grey
        // (8.73) despite being the "fixed" version (see ThemeManager.cs's
        // own comment at this field for the full table). Holds L* close to
        // Surface's own (16.4 vs this value's 16.4) rather than darkening
        // further, and shifts hue warm instead -- red up, blue down,
        // green nearly untouched (green carries 0.7152 of WCAG luminance
        // weight, blue only 0.0722, so this is the cheap axis to spend),
        // landing at Lab hue ~70 deg, the same warm/gold quadrant as
        // AccentBronze's ~78-85 deg. CIE76 vs Surface: 15.59. Legibility:
        // Text 12.24:1, SubtleText 6.48:1, StatusAmber 7.51:1, StatusGreen
        // 7.27:1, StatusRed 4.90:1 -- all >=4.5, StatusRed (the tightest,
        // same one round 1 broke at 3.71:1) with a real 0.40 margin.
        RowHover: new(52, 38, 24));

    // Banker's-lamp ledger green with aged gold. Dark. Values validated
    // against the full pairing wall before landing.
    public static ThemePalette Ledger { get; } = new(
        WindowBg: new(21, 26, 22),
        Surface: new(31, 39, 33),
        Text: new(231, 236, 230),
        SubtleText: new(166, 175, 166),
        Border: new(74, 84, 75),
        Accent: new(203, 214, 196),
        AccentText: new(21, 26, 22),
        Warning: new(84, 62, 8),
        WarningText: new(255, 224, 130),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(240, 173, 78),
        StatusGreen: new(129, 199, 132),
        StatusRed: new(229, 115, 115),
        // Same brighter red as Dark/graphite (identical baseline StatusRed,
        // see that palette's comment) -- 4.947:1 vs this palette's
        // SurfaceRaised (44,51,46).
        StatusRedRaised: new(234, 130, 130),
        TileDefaultBg: new(48, 58, 50),
        // Mix(Surface, white, 0.06), materialized.
        SurfaceRaised: new(44, 51, 46),
        BorderStrong: new(108, 120, 109),
        AccentBronze: new(210, 178, 94),
        SurfaceHover: new(70, 82, 72),
        SurfacePressed: new(92, 104, 93),
        RowHover: new(48, 45, 20));

    // Deep archive blue with reader-glow cyan. Dark. Values validated
    // against the full pairing wall before landing.
    public static ThemePalette Microfilm { get; } = new(
        WindowBg: new(21, 25, 34),
        Surface: new(30, 36, 48),
        Text: new(230, 234, 242),
        SubtleText: new(163, 171, 186),
        Border: new(73, 81, 99),
        Accent: new(196, 206, 224),
        AccentText: new(21, 25, 34),
        Warning: new(84, 62, 8),
        WarningText: new(255, 224, 130),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(240, 173, 78),
        StatusGreen: new(129, 199, 132),
        StatusRed: new(229, 115, 115),
        // Same brighter red as Dark/graphite (identical baseline StatusRed,
        // see that palette's comment) -- 4.986:1 vs this palette's
        // SurfaceRaised (43,49,60).
        StatusRedRaised: new(234, 130, 130),
        TileDefaultBg: new(46, 54, 70),
        // Mix(Surface, white, 0.06), materialized.
        SurfaceRaised: new(43, 49, 60),
        BorderStrong: new(106, 116, 136),
        AccentBronze: new(111, 174, 198),
        SurfaceHover: new(72, 80, 98),
        SurfacePressed: new(94, 102, 122),
        RowHover: new(26, 36, 64));

    // The manila folder itself — kraft cream and fountain-ink blue. Light.
    // Values validated against the full pairing wall before landing.
    public static ThemePalette Manila { get; } = new(
        WindowBg: new(244, 238, 225),
        Surface: new(251, 247, 236),
        Text: new(42, 36, 24),
        SubtleText: new(94, 86, 68),
        Border: new(191, 182, 160),
        Accent: new(31, 58, 95),
        AccentText: new(255, 255, 255),
        Warning: new(255, 236, 179),
        WarningText: new(102, 60, 0),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(40, 112, 45),
        StatusAmber: new(134, 82, 2),
        StatusGreen: new(40, 112, 45),
        StatusRed: new(178, 50, 38),
        // SurfaceRaised == Surface here (light, unchanged) -- reused
        // verbatim, same pattern as Light/paper above.
        StatusRedRaised: new(178, 50, 38),
        TileDefaultBg: new(233, 224, 204),
        // Light: SurfaceRaised == Surface unchanged.
        SurfaceRaised: new(251, 247, 236),
        BorderStrong: new(128, 119, 98),
        AccentBronze: new(122, 94, 48),
        SurfaceHover: new(206, 196, 174),
        SurfacePressed: new(174, 162, 134),
        RowHover: new(250, 238, 199));

    // Carbon paper — near-black with carbon-copy violet, OLED-friendly.
    // Dark. Values validated against the full pairing wall before landing.
    public static ThemePalette Carbon { get; } = new(
        WindowBg: new(15, 16, 19),
        Surface: new(26, 27, 31),
        Text: new(242, 242, 245),
        SubtleText: new(176, 177, 186),
        Border: new(70, 72, 82),
        Accent: new(173, 160, 214),
        AccentText: new(15, 16, 19),
        Warning: new(84, 62, 8),
        WarningText: new(255, 224, 130),
        Danger: new(192, 57, 43),
        DangerText: new(255, 255, 255),
        Success: new(46, 125, 50),
        StatusAmber: new(240, 173, 78),
        StatusGreen: new(129, 199, 132),
        StatusRed: new(229, 115, 115),
        // Unlike graphite/ledger/microfilm, StatusRed already clears
        // SurfaceRaised here (4.93:1 -- this palette's near-black Surface
        // (26,27,31) keeps SurfaceRaised dark enough) -- reused verbatim.
        StatusRedRaised: new(229, 115, 115),
        TileDefaultBg: new(40, 41, 48),
        // Mix(Surface, white, 0.06), materialized.
        SurfaceRaised: new(39, 40, 44),
        BorderStrong: new(106, 108, 120),
        AccentBronze: new(142, 130, 184),
        SurfaceHover: new(66, 67, 75),
        SurfacePressed: new(88, 89, 99),
        RowHover: new(38, 32, 54));

    // The drafting blueprint — cool azure paper and cobalt ink. Light.
    // Values validated against the full pairing wall before landing.
    public static ThemePalette Blueprint { get; } = new(
        WindowBg: new(233, 239, 246),
        Surface: new(247, 250, 253),
        Text: new(24, 39, 64),
        SubtleText: new(73, 88, 110),
        Border: new(170, 184, 200),
        Accent: new(35, 83, 143),
        AccentText: new(255, 255, 255),
        Warning: new(255, 236, 179),
        WarningText: new(102, 60, 0),
        Danger: new(186, 54, 41),
        DangerText: new(255, 255, 255),
        Success: new(40, 110, 44),
        StatusAmber: new(140, 86, 3),
        StatusGreen: new(40, 110, 44),
        StatusRed: new(178, 50, 38),
        // SurfaceRaised == Surface here (light, unchanged) -- reused
        // verbatim, same pattern as Light/paper above.
        StatusRedRaised: new(178, 50, 38),
        TileDefaultBg: new(219, 229, 240),
        // Light: SurfaceRaised == Surface unchanged.
        SurfaceRaised: new(247, 250, 253),
        BorderStrong: new(110, 126, 146),
        AccentBronze: new(45, 101, 160),
        SurfaceHover: new(188, 202, 216),
        SurfacePressed: new(152, 170, 190),
        RowHover: new(214, 233, 255));

    // ------------------------------------------------------ scheme registry

    public static IReadOnlyList<ThemeScheme> Schemes { get; } = new[]
    {
        new ThemeScheme("paper",     "Paper",     Light,     IsDark: false),
        new ThemeScheme("graphite",  "Graphite",  Dark,      IsDark: true),
        new ThemeScheme("ledger",    "Ledger",    Ledger,    IsDark: true),
        new ThemeScheme("microfilm", "Microfilm", Microfilm, IsDark: true),
        new ThemeScheme("manila",    "Manila",    Manila,    IsDark: false),
        new ThemeScheme("carbon",    "Carbon",    Carbon,    IsDark: true),
        new ThemeScheme("blueprint", "Blueprint", Blueprint, IsDark: false),
    };

    /// <summary>Case-insensitive lookup by key. Null for null/blank/unknown —
    /// callers fall back to a default scheme.</summary>
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
