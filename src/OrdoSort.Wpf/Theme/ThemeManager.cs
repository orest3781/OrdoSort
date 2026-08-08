using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace OrdoSort.Wpf.Theme;

/// <summary>Applies a <see cref="ThemePalette"/> to the running app as
/// "Theme.*" brush resources (consumed by Styles.xaml via DynamicResource),
/// follows the OS light/dark preference, and re-applies live when the user
/// changes it. Steps aside from overriding the native SystemColors.* keys
/// entirely while Windows High Contrast is on, live-reevaluated the same
/// way.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static ThemePalette Current => IsDark ? ThemePalette.Dark : ThemePalette.Light;

    /// <summary>"auto" (follow Windows), "light", or "dark" — the config's
    /// theme key. Only auto reacts to the OS preference changing.</summary>
    public static string Mode { get; private set; } = "auto";

    /// <summary>Indirection over <c>SystemParameters.HighContrast</c> — a
    /// static BCL property that can't be faked directly — so tests can
    /// simulate Windows High Contrast ON/OFF without touching real OS
    /// accessibility settings. Internal (not private) only so tests can
    /// override it via <c>InternalsVisibleTo</c>; production code never
    /// assigns to this field. Same "settable only by tests" seam pattern as
    /// <see cref="App._crashDir"/>.</summary>
    internal static Func<bool> IsHighContrast = () => SystemParameters.HighContrast;

    public static void Start(Application app, string mode = "auto")
    {
        TitleBar.Hook();
        SetMode(app, mode);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.Accessibility)
                // High Contrast (SPI_SETHIGHCONTRAST) — and any other
                // accessibility toggle — lands in this category. Re-apply
                // with the CURRENT dark/light state (not derived again from
                // the OS preference) so the SystemColors step-aside gate
                // re-evaluates live, regardless of whether Mode is "auto" or
                // pinned to a fixed light/dark choice.
                app.Dispatcher.BeginInvoke(() => Apply(app, IsDark));
            else if (Mode == "auto" &&
                e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                app.Dispatcher.BeginInvoke(() => Apply(app, ReadOsPrefersDark()));
        };
    }

    public static void SetMode(Application app, string mode)
    {
        Mode = mode is "light" or "dark" ? mode : "auto";
        Apply(app, Mode == "dark" || (Mode == "auto" && ReadOsPrefersDark()));
    }

    public static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        var p = Current;
        var r = app.Resources;
        r["Theme.WindowBg"] = Brush(p.WindowBg);
        r["Theme.Surface"] = Brush(p.Surface);
        r["Theme.Text"] = Brush(p.Text);
        r["Theme.SubtleText"] = Brush(p.SubtleText);
        r["Theme.Border"] = Brush(p.Border);
        r["Theme.BorderStrong"] = Brush(p.BorderStrong);
        r["Theme.Accent"] = Brush(p.Accent);
        r["Theme.AccentBronze"] = Brush(p.AccentBronze);
        // Bronze is a flat 100% fill everywhere it's used (its WCAG margin is
        // pinned to that), so anything drawn ON bronze (the enter-target
        // badge's plate, e.g.) needs its own WCAG-picked foreground rather
        // than whatever color happens to sit behind the plate — same
        // IdealForeground contract TileViewModel/RouteButtonViewModel use for
        // tiles and route buttons, just precomputed once here since bronze
        // itself never varies per-route, only per light/dark theme.
        r["Theme.AccentBronzeText"] = Brush(ThemePalette.IdealForeground(p.AccentBronze));
        r["Theme.AccentText"] = Brush(p.AccentText);
        r["Theme.Warning"] = Brush(p.Warning);
        r["Theme.WarningText"] = Brush(p.WarningText);
        r["Theme.Danger"] = Brush(p.Danger);
        r["Theme.DangerText"] = Brush(p.DangerText);
        r["Theme.Success"] = Brush(p.Success);
        r["Theme.StatusAmber"] = Brush(p.StatusAmber);
        r["Theme.StatusGreen"] = Brush(p.StatusGreen);
        r["Theme.StatusRed"] = Brush(p.StatusRed);
        r["Theme.TileDefaultBg"] = Brush(p.TileDefaultBg);
        // hover/pressed shades derived once so Styles.xaml stays declarative.
        //
        // Raised from 0.08/0.16 (hover-tint strength review, 2026-08-08):
        // the owner's own words were "too light... lose track of what's
        // hovered or selected", explicitly choosing "make them stronger"
        // over "tone them down". 0.08 moved Light.Surface (255,255,255) only
        // to ~(236,236,237) -- a surround-delta (ContrastRatio against the
        // plain Surface it sits on) of just 1.181:1 in light, 1.240:1 in
        // dark -- imperceptible at a glance, which is exactly the report.
        //
        // The ceiling here is NOT taste, it's text sitting on top of the
        // tint: this Mix formula moves the background TOWARD Theme.Text, so
        // raising the amount simultaneously helps the surround-delta and
        // HURTS every foreground colour that isn't Text itself (their
        // fixed luminance sits between Surface's and Text's, so the
        // background sliding toward Text erodes their contrast headroom).
        // Measured every candidate amount against the app's real hover
        // pairings (ThemePalette.ContrastRatio, both palettes) before
        // picking one:
        //   * Theme.Text on Hover: huge margin regardless (>=9:1 up to at
        //     least 0.25) -- never the binding constraint.
        //   * Theme.SubtleText on Hover: the real pairing is RouteList/
        //     LabelMaker/ManageSaved's caption text on an unselected,
        //     hovered row, AND BulkRenameWindow's "New name" column on a
        //     NeedsName row (whose DataGridRow.Background is this same
        //     SurfaceHover -- see that window's RowStyle). Crosses below
        //     4.5:1 in dark between amount 0.12 (4.616:1) and 0.13
        //     (4.474:1).
        //   * Theme.StatusAmber on Hover: the real pairing is that SAME
        //     BulkRenameWindow NeedsName row's Note column -- NeedsName
        //     implies NoteIsProblem in BulkRenameViewModel.ApplyPlans, so a
        //     NeedsName row's Note is ALWAYS StatusAmber, never Subtle.
        //     Crosses below 4.5:1 in light between 0.11 (4.525:1) and 0.12
        //     (4.442:1) -- the tightest of the three "must-hold" pairings.
        //   * Theme.StatusGreen / Theme.StatusRed on Hover: real pairing is
        //     UnlockWindow's FileList Note column, unselected but hovered
        //     (Styles.xaml's ListBoxItem IsMouseOver trigger paints the SAME
        //     Bd.Background regardless of which status coloured the Note).
        //     These were ALREADY below the floor at the OLD 0.08: StatusRed
        //     measured 3.945:1 in dark and StatusGreen 4.343:1 in light,
        //     neither ever asserted by any test before this change --
        //     "SurfaceHover" and "SurfacePressed" have zero prior hits
        //     anywhere in tests/. StatusRed in light was passing but paper-
        //     thin (4.606:1, a 0.106 margin) and does cross below 4.5 at the
        //     new amount (4.430:1 at 0.10) -- a small, deliberate, measured
        //     regression on an already-marginal pairing, not something this
        //     change could avoid without abandoning the strengthening the
        //     owner asked for. See HighlightContrastTests' hover/pressed
        //     coverage (added alongside this change) for the full, current
        //     pass/fail table across both palettes -- pinned with real
        //     numbers rather than left to silently drift.
        //
        // 0.10/0.20 keeps StatusAmber and SubtleText (the two pairings with
        // an unambiguous live DataGrid/ListBox call site AND real baseline
        // margin) at or above 4.5:1 in BOTH palettes at Hover, roughly
        // doubling the pre-existing margin most call sites had, while
        // giving a real, measured surround-delta gain: Hover
        // 1.181:1->1.228:1 light, 1.240:1->1.318:1 dark. Pressed
        // (0.16->0.20) has no live SubtleText/status-colour pairing at all
        // today -- its only real call site is MenuTopLevelHeader's
        // IsSubmenuOpen trigger, whose header text is always Theme.Text --
        // so it was raised further for "pressed visibly stronger than
        // hover" (surround-delta 1.400:1->1.529:1 light, 1.587:1->1.780:1
        // dark) without that headroom being spent on a pairing that doesn't
        // exist in this app.
        r["Theme.SurfaceHover"] = Brush(Mix(p.Surface, p.Text, 0.10));
        r["Theme.SurfacePressed"] = Brush(Mix(p.Surface, p.Text, 0.20));
        // Unchanged: computed but never consumed by any Theme.AccentHover
        // DynamicResource lookup anywhere in src/OrdoSort.Wpf/**/*.xaml or
        // *.cs (confirmed by search, 2026-08-08) -- dead since it was added,
        // so raising it would not move a single rendered pixel. Left as-is
        // rather than "fixed" as part of a task about pixels the owner can
        // actually see; flagged here so a future reader doesn't assume its
        // value means anything today.
        r["Theme.AccentHover"] = Brush(Mix(p.Accent, new Rgb(255, 255, 255), 0.12));
        // floating surfaces sit a step lighter in the dark (light mode's
        // Surface is already near-white; the shadow does the lifting there)
        r["Theme.SurfaceRaised"] = Brush(
            dark ? Mix(p.Surface, new Rgb(255, 255, 255), 0.06) : p.Surface);

        // The Appearance tab's preview cards show BOTH palettes side by side,
        // so they cannot use Theme.* — those follow the active theme. These
        // keys are palette-fixed and identical in every theme. Published here
        // rather than as XAML literals because the literals drifted a whole
        // refresh behind (2026-08-03: the cards still showed the pre-refresh
        // Material blue accent).
        foreach (var (prefix, pal) in new[] { ("Light", ThemePalette.Light), ("Dark", ThemePalette.Dark) })
        {
            r[$"{prefix}.WindowBg"] = Brush(pal.WindowBg);
            r[$"{prefix}.Surface"] = Brush(pal.Surface);
            r[$"{prefix}.Border"] = Brush(pal.Border);
            r[$"{prefix}.SubtleText"] = Brush(pal.SubtleText);
            r[$"{prefix}.Accent"] = Brush(pal.Accent);
        }

        // Native-templated controls (menus, scrollbars, dialogs) read the
        // SystemColors brush keys — override them so dark mode reaches the
        // parts we don't retemplate. BUT: a user who has turned on Windows
        // High Contrast has done so as an accessibility need, not a cosmetic
        // preference — silently overriding it with our own palette would
        // defeat the OS setting. Step aside entirely in that case (do not
        // substitute a bespoke HC theme): REMOVE any override rather than
        // merely skip re-adding one, because Apply() re-runs live when HC is
        // toggled ON while the app is already running with a PRIOR override
        // sitting in this same ResourceDictionary — skipping the assignment
        // would leave that stale app-palette brush in place instead of
        // letting the real SystemColors resolve through.
        if (IsHighContrast())
        {
            r.Remove(SystemColors.WindowBrushKey);
            r.Remove(SystemColors.WindowTextBrushKey);
            r.Remove(SystemColors.ControlBrushKey);
            r.Remove(SystemColors.ControlTextBrushKey);
            r.Remove(SystemColors.MenuBrushKey);
            r.Remove(SystemColors.MenuTextBrushKey);
            r.Remove(SystemColors.MenuBarBrushKey);
            r.Remove(SystemColors.HighlightBrushKey);
            r.Remove(SystemColors.HighlightTextBrushKey);
            r.Remove(SystemColors.GrayTextBrushKey);
        }
        else
        {
            r[SystemColors.WindowBrushKey] = Brush(p.Surface);
            r[SystemColors.WindowTextBrushKey] = Brush(p.Text);
            r[SystemColors.ControlBrushKey] = Brush(p.WindowBg);
            r[SystemColors.ControlTextBrushKey] = Brush(p.Text);
            r[SystemColors.MenuBrushKey] = Brush(p.Surface);
            r[SystemColors.MenuTextBrushKey] = Brush(p.Text);
            r[SystemColors.MenuBarBrushKey] = Brush(p.WindowBg);
            r[SystemColors.HighlightBrushKey] = Brush(p.Accent);
            r[SystemColors.HighlightTextBrushKey] = Brush(p.AccentText);
            r[SystemColors.GrayTextBrushKey] = Brush(p.SubtleText);
        }

        TitleBar.ApplyAll(app);   // window chrome follows the theme too
    }

    public static SolidColorBrush Brush(Rgb c)
    {
        var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>Blend <paramref name="amount"/> of <paramref name="into"/>
    /// into <paramref name="baseColor"/> — cheap hover/pressed derivation.</summary>
    private static Rgb Mix(Rgb baseColor, Rgb into, double amount) => new(
        (byte)(baseColor.R + (into.R - baseColor.R) * amount),
        (byte)(baseColor.G + (into.G - baseColor.G) * amount),
        (byte)(baseColor.B + (into.B - baseColor.B) * amount));

    private static bool ReadOsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;   // no signal -> light
        }
    }
}
