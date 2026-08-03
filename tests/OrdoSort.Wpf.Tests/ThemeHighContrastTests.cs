using System.Windows;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-02 audit-remediation, Task 6, pass 2 I5: <c>ThemeManager
/// .Apply</c> used to overwrite the <c>SystemColors.*</c> brush keys
/// unconditionally, so a user who turned on Windows High Contrast — an
/// accessibility setting, not a preference — silently had it overridden by
/// the app's own light/dark palette. The approved decision (design doc,
/// "Windows High Contrast" row): detect and step aside — skip the
/// SystemColors override entirely when High Contrast is on and let the OS
/// palette through, re-evaluating live when the setting changes. Explicitly
/// NOT a bespoke HC theme.
///
/// <see cref="SystemParameters.HighContrast"/> is a static BCL property that
/// reads live OS state and cannot be faked directly, so
/// <see cref="ThemeManager.IsHighContrast"/> is the seam: an internal,
/// test-only-settable <c>Func&lt;bool&gt;</c> indirection (same "settable
/// only by tests" pattern as <see cref="OrdoSort.Wpf.App._crashDir"/>).
/// Production code (<c>ThemeManager.Start</c>) never assigns to it.
///
/// Joins <see cref="HighlightContrastCollection"/> (not its own
/// <c>IClassFixture&lt;&gt;</c>) for the same reason
/// <see cref="DataGridStarColumnTests"/> does — see
/// <see cref="HighlightContrastFixture"/>'s class doc for the two distinct
/// crashes a second, independent instance reproduces.</summary>
[Collection(HighlightContrastTests.Name)]
public class ThemeHighContrastTests
{
    private readonly HighlightContrastFixture _fx;
    public ThemeHighContrastTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Restores the seam to its real production default
    /// (<c>SystemParameters.HighContrast</c>) and re-applies a known-good
    /// (non-HC) state so a test that flips the seam never leaks a stale
    /// override into the shared <see cref="HighlightContrastFixture"/>
    /// Application/Resources that every other test class in this collection
    /// (DataGridStarColumnTests, ProcessingViewImeGuardTests,
    /// HighlightContrastTests) also depends on.</summary>
    private void ResetSeam() => _fx.Invoke(() =>
    {
        ThemeManager.IsHighContrast = () => SystemParameters.HighContrast;
        ThemeManager.Apply(_fx.App, dark: false);
    });

    private static Color BrushColor(object? resource) =>
        resource is SolidColorBrush b ? b.Color : throw new InvalidOperationException(
            $"expected a SolidColorBrush, got {resource?.GetType().ToString() ?? "null"}");

    [Fact]
    public void HighContrastOffAppliesTheSystemColorsOverride() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.IsHighContrast = () => false;
            ThemeManager.Apply(_fx.App, dark: true);

            var p = ThemePalette.Dark;
            Assert.Equal(BrushColor(ThemeManager.Brush(p.Surface)),
                BrushColor(_fx.App.Resources[SystemColors.WindowBrushKey]));
            Assert.Equal(BrushColor(ThemeManager.Brush(p.Text)),
                BrushColor(_fx.App.Resources[SystemColors.WindowTextBrushKey]));
            Assert.Equal(BrushColor(ThemeManager.Brush(p.Accent)),
                BrushColor(_fx.App.Resources[SystemColors.HighlightBrushKey]));
        }
        finally { ResetSeam(); }
    });

    /// <summary>The core step-aside assertion: with the seam reporting High
    /// Contrast ON, <c>Apply</c> must NOT leave any app-palette brush sitting
    /// under a <c>SystemColors.*</c> key — the resource lookup must fall
    /// through to the OS's own High-Contrast SystemColors instead. Checked by
    /// asserting the key is simply absent from <c>Application.Resources</c>
    /// (this app's ONLY source for these overrides — removing them here is
    /// exactly "let the OS palette through").</summary>
    [Fact]
    public void HighContrastOnSkipsTheSystemColorsOverride() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.IsHighContrast = () => true;
            ThemeManager.Apply(_fx.App, dark: true);

            Assert.False(_fx.App.Resources.Contains(SystemColors.WindowBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.WindowTextBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.ControlBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.ControlTextBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.MenuBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.MenuTextBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.MenuBarBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.HighlightBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.HighlightTextBrushKey));
            Assert.False(_fx.App.Resources.Contains(SystemColors.GrayTextBrushKey));
        }
        finally { ResetSeam(); }
    });

    /// <summary>Guards against the narrower, WRONG fix of merely skipping the
    /// assignment when HC is on: a real user turns High Contrast ON mid-
    /// session (Start's SystemEvents.UserPreferenceChanged handler fires
    /// Apply again), while an EARLIER Apply call (HC off, e.g. at app
    /// startup) already wrote the app palette into these same
    /// Application.Resources keys. If Apply merely skipped re-adding them on
    /// the next call instead of removing the stale entry, the OS palette
    /// would never actually show through — this reproduces exactly that
    /// off-then-on transition.</summary>
    [Fact]
    public void HighContrastTurningOnMidSessionRemovesAPriorOverride() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.IsHighContrast = () => false;
            ThemeManager.Apply(_fx.App, dark: true);
            Assert.True(_fx.App.Resources.Contains(SystemColors.WindowBrushKey));   // sanity: override present

            ThemeManager.IsHighContrast = () => true;
            ThemeManager.Apply(_fx.App, dark: true);   // same call Start()'s live watcher makes

            Assert.False(_fx.App.Resources.Contains(SystemColors.WindowBrushKey),
                "a stale app-palette override survived HC turning on mid-session");
        }
        finally { ResetSeam(); }
    });

    /// <summary>The reverse transition: HC turns back OFF mid-session, the
    /// override must reappear (this is NOT a bespoke HC theme — once HC is
    /// off again, the app's ordinary light/dark palette resumes exactly as
    /// before HC was ever turned on).</summary>
    [Fact]
    public void HighContrastTurningOffMidSessionReinstatesTheOverride() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.IsHighContrast = () => true;
            ThemeManager.Apply(_fx.App, dark: false);
            Assert.False(_fx.App.Resources.Contains(SystemColors.WindowBrushKey));   // sanity: stepped aside

            ThemeManager.IsHighContrast = () => false;
            ThemeManager.Apply(_fx.App, dark: false);

            var p = ThemePalette.Light;
            Assert.Equal(BrushColor(ThemeManager.Brush(p.Surface)),
                BrushColor(_fx.App.Resources[SystemColors.WindowBrushKey]));
        }
        finally { ResetSeam(); }
    });

    /// <summary>Not a bespoke HC theme: the app's OWN "Theme.*" resources
    /// (consumed by Styles.xaml's retemplated controls) keep following the
    /// ordinary light/dark palette regardless of High Contrast — only the
    /// native SystemColors.* keys step aside.</summary>
    [Fact]
    public void HighContrastOnDoesNotTouchTheAppsOwnThemeResources() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.IsHighContrast = () => true;
            ThemeManager.Apply(_fx.App, dark: true);

            var p = ThemePalette.Dark;
            Assert.Equal(BrushColor(ThemeManager.Brush(p.Text)),
                BrushColor(_fx.App.Resources["Theme.Text"]));
            Assert.Equal(BrushColor(ThemeManager.Brush(p.WindowBg)),
                BrushColor(_fx.App.Resources["Theme.WindowBg"]));
        }
        finally { ResetSeam(); }
    });
}
