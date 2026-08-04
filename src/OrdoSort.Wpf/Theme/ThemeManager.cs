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
        r["Theme.TileDefaultBg"] = Brush(p.TileDefaultBg);
        // hover/pressed shades derived once so Styles.xaml stays declarative
        r["Theme.SurfaceHover"] = Brush(Mix(p.Surface, p.Text, 0.08));
        r["Theme.SurfacePressed"] = Brush(Mix(p.Surface, p.Text, 0.16));
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
