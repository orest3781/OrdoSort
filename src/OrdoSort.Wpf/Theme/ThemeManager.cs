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
    /// <summary>Reflects the APPLIED scheme's <c>IsDark</c> — the legacy
    /// auto/light/dark pair resolves to the paper/graphite schemes below, so
    /// this stays correct for those too. Feeds TitleBar's DWM dark-titlebar
    /// call.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The applied scheme's palette. Backed by a field (not derived
    /// from <see cref="IsDark"/>) so a future non-paper/graphite scheme
    /// resolves to ITS OWN palette rather than being folded into just
    /// Light/Dark.</summary>
    public static ThemePalette Current { get; private set; } = ThemePalette.Light;

    /// <summary>"auto" (follow Windows), "light", "dark", or any
    /// <see cref="ThemePalette.Schemes"/> key (case-insensitive as typed;
    /// normalized to the registry's own casing) — the config's theme key.
    /// Only "auto" reacts to the OS preference changing; a pinned scheme key
    /// behaves like "light"/"dark" in that respect (fixed until SetMode is
    /// called again).</summary>
    public static string Mode { get; private set; } = "auto";

    // The auto pair's two endpoints, as registry schemes — Apply(app, bool)
    // below is a thin wrapper over Apply(app, ThemeScheme) so there is
    // exactly one brush-publication loop for both the legacy bool entry
    // point and arbitrary scheme keys. Resolved via FindScheme rather than
    // referencing ThemePalette.Light/Dark directly so this stays correct if
    // the registry ever reassigns which scheme is "the" light/dark default.
    private static readonly ThemeScheme LightScheme = ThemePalette.FindScheme("paper")!;
    private static readonly ThemeScheme DarkScheme = ThemePalette.FindScheme("graphite")!;

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
                // the CURRENT scheme as-is (not derived again from the OS
                // light/dark preference, and NOT collapsed to the auto
                // pair) so the SystemColors step-aside gate re-evaluates
                // live, regardless of whether Mode is "auto", pinned to a
                // fixed light/dark choice, or pinned to an arbitrary
                // registry scheme key.
                app.Dispatcher.BeginInvoke(() => Reapply(app));
            else if (Mode == "auto" &&
                e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                // Only "auto" ever reaches here — a pinned scheme key (or a
                // pinned legacy "light"/"dark") sets Mode to something other
                // than "auto", so the OS preference changing never re-themes
                // a pinned choice. Same gate that already protected legacy
                // "light"/"dark"; scheme keys get it for free.
                app.Dispatcher.BeginInvoke(() => Apply(app, ReadOsPrefersDark()));
        };
    }

    /// <summary>Accepts "auto", "light", "dark", or any case-insensitive
    /// <see cref="ThemePalette.Schemes"/> key (e.g. "graphite"). Unknown or
    /// blank keys fall back to "auto" rather than throwing — Config
    /// validates the key upstream, so this is a defensive default, not the
    /// primary guard.</summary>
    public static void SetMode(Application app, string mode)
    {
        var scheme = ThemePalette.FindScheme(mode);
        if (scheme is not null)
        {
            Mode = scheme.Key;
            Apply(app, scheme);
            return;
        }

        Mode = mode is "light" or "dark" ? mode : "auto";
        Apply(app, Mode == "dark" || (Mode == "auto" && ReadOsPrefersDark()));
    }

    /// <summary>The auto pair, fixed: dark → the graphite scheme, light →
    /// paper. Kept as its own overload (rather than folded into the
    /// <see cref="ThemeScheme"/> overload) because two test files
    /// (AppearancePreviewTests, ThemeHighContrastTests) call it directly by
    /// this exact bool signature — routes through the same scheme-aware core
    /// as everything else, so there is no second publication loop to drift
    /// out of sync.</summary>
    public static void Apply(Application app, bool dark) =>
        Apply(app, dark ? DarkScheme : LightScheme);

    /// <summary>Pins the app to an explicit registry scheme (e.g. the
    /// "graphite" entry SetMode resolves a matching config key to).</summary>
    public static void Apply(Application app, ThemeScheme scheme) =>
        ApplyCore(app, scheme.Palette, scheme.IsDark);

    /// <summary>Re-publishes whichever scheme is already active
    /// (<see cref="Current"/>/<see cref="IsDark"/>) without changing it —
    /// used by the High Contrast listener above, which must re-evaluate the
    /// SystemColors step-aside gate without touching which scheme is
    /// applied.</summary>
    private static void Reapply(Application app) => ApplyCore(app, Current, IsDark);

    /// <summary>The single brush-publication loop — every entry point above
    /// (the legacy bool overload, the scheme overload, and Reapply) routes
    /// through here so there is exactly one place that writes
    /// "Theme.*"/SystemColors.* resources.</summary>
    private static void ApplyCore(Application app, ThemePalette p, bool dark)
    {
        IsDark = dark;
        Current = p;
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
        // Hover/pressed/row tints -- three rounds of the same review
        // (2026-08-08), each correcting the last:
        //
        // Round 1: a single shared Mix(Surface, Text, amount) formula,
        // raised from 0.08 to 0.10. Moving the background toward Text
        // simultaneously grows the WCAG surround-delta AND erodes every
        // non-Text foreground that can sit on it -- two status colours
        // (StatusGreen light, StatusRed dark) were already below 4.5:1 at
        // the OLD 0.08, before round 1 touched anything.
        //
        // Round 2: a parallel audit found Match & Merge/Bulk rename/
        // History/Triage had NO DataGridRow hover at all (added this
        // round, see Styles.xaml), and split the single token into two
        // per-palette tiers on ThemePalette (SurfaceHover/SurfacePressed
        // for chrome -- MenuItem/TabItem/ChipButton, Text-only, free to be
        // strong; RowHover for grid/list rows, which can show StatusAmber/
        // Green/Red/SubtleText). RowHover was tuned as a NEUTRAL grey held
        // close to Surface's own luminance to keep all five foregrounds
        // legible -- which is real, correct WCAG reasoning that led
        // somewhere wrong: ContrastRatio is a pure luminance ratio, so
        // "hold luminance near Surface" is indistinguishable, by that
        // metric, from "make the tint barely visible". Measured directly
        // (CIE76 below): round 2's grey was FAINTER than round 1's, in
        // light mode by almost 5x.
        //
        // Round 3: still-per-palette, still ThemePalette constants, but
        // RowHover is now a CHROMATIC tint (warm, this app's own
        // AccentBronze family) instead of a neutral grey -- shifting HUE
        // at near-constant lightness is nearly free in WCAG terms (hue
        // doesn't move the luminance ratio at all) but reads as plainly
        // visible to the eye, which is exactly the gap ContrastRatio
        // alone can't see. Measured with ThemePalette.DeltaE76 (CIE76,
        // Lab space) against the real Surface each sits on, both palettes:
        //
        //   Light   Round 1 grey (231,232,232): dE76  8.09
        //           Round 2 grey (249,250,250): dE76  1.84  <- the regression
        //           Round 3 warm (255,249,220): dE76 15.07
        //   Dark    Round 1 grey ( 57, 60, 64): dE76  8.73
        //           Round 2 grey ( 25, 26, 28): dE76  7.37  <- also fainter
        //           Round 3 warm ( 52, 38, 24): dE76 15.59
        //
        // Legibility (ContrastRatio, the metric that never changed) holds
        // throughout round 3 -- all five foregrounds >=4.5:1 in both
        // palettes, no exceptions, no pinning. See ThemePalette.cs's own
        // comment at SurfaceHover/SurfacePressed/RowHover for the exact
        // per-colour numbers and why the chromatic direction is nearly
        // free (blue carries only 0.0722 of WCAG's luminance weight vs
        // green's 0.7152, so dropping blue buys hue distance at almost no
        // luminance cost).
        r["Theme.SurfaceHover"] = Brush(p.SurfaceHover);
        r["Theme.SurfacePressed"] = Brush(p.SurfacePressed);
        r["Theme.RowHover"] = Brush(p.RowHover);
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
