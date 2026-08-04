using System.Windows;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The Appearance tab's three theme preview cards must show both
/// palettes at once, so they cannot use Theme.* (which follows the ACTIVE
/// theme). They used to hand-write ~22 hex literals instead — and by
/// 2026-08-03 they had drifted a full refresh behind, still advertising the
/// pre-2026-08-01 Material blue accent the app no longer has.
///
/// The fix is structural: ThemeManager publishes both palettes under fixed
/// Light.*/Dark.* keys and the XAML references those. These tests pin the
/// publication — including that it does NOT follow the active theme, which is
/// the whole reason the keys exist.</summary>
[Collection(HighlightContrastTests.Name)]
public class AppearancePreviewTests
{
    private readonly HighlightContrastFixture _fx;
    public AppearancePreviewTests(HighlightContrastFixture fx) => _fx = fx;

    private Color Brush(string key) =>
        ((SolidColorBrush)_fx.App.Resources[key]).Color;

    private static Color Expect(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothPalettesArePublishedRegardlessOfTheActiveTheme(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

        var l = ThemePalette.Light;
        Assert.Equal(Expect(l.WindowBg), Brush("Light.WindowBg"));
        Assert.Equal(Expect(l.Surface), Brush("Light.Surface"));
        Assert.Equal(Expect(l.Border), Brush("Light.Border"));
        Assert.Equal(Expect(l.SubtleText), Brush("Light.SubtleText"));
        Assert.Equal(Expect(l.Accent), Brush("Light.Accent"));

        var d = ThemePalette.Dark;
        Assert.Equal(Expect(d.WindowBg), Brush("Dark.WindowBg"));
        Assert.Equal(Expect(d.Surface), Brush("Dark.Surface"));
        Assert.Equal(Expect(d.Border), Brush("Dark.Border"));
        Assert.Equal(Expect(d.SubtleText), Brush("Dark.SubtleText"));
        Assert.Equal(Expect(d.Accent), Brush("Dark.Accent"));
    });
}
