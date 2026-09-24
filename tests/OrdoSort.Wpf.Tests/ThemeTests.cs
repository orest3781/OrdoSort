using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>The visual contract: every text pairing the theme ships meets
/// WCAG AA (4.5:1) in BOTH schemes.</summary>
public class ThemeTests
{
    public static IEnumerable<object[]> TextPairs()
    {
        foreach (var s in ThemePalette.Schemes)
        {
            var p = s.Palette;
            yield return new object[] { p.Text, p.WindowBg };
            yield return new object[] { p.Text, p.Surface };
            yield return new object[] { p.SubtleText, p.WindowBg };
            yield return new object[] { p.AccentText, p.Accent };
            yield return new object[] { p.WarningText, p.Warning };
            yield return new object[] { p.DangerText, p.Danger };
            yield return new object[] { p.StatusAmber, p.WindowBg };
            // StatusGreen exists specifically because Success (reused for
            // this pairing before it existed) fails 4.5:1 in dark against
            // both — see ThemePalette.cs's own StatusGreen field comment
            // and HighlightContrastTests' Unlock file-list note tests
            // (status-colour-vocabulary plan, 2026-08-08, Task 1 Step 1).
            yield return new object[] { p.StatusGreen, p.WindowBg };
            yield return new object[] { p.StatusGreen, p.Surface };
            // StatusRed exists for the identical reason, found one step
            // later: Danger AS FOREGROUND TEXT (its shipped job elsewhere is
            // a BACKGROUND, paired with DangerText, unaffected and untested
            // here) fails 4.5:1 in dark against both -- see ThemePalette.cs's
            // StatusRed field comment.
            yield return new object[] { p.StatusRed, p.WindowBg };
            yield return new object[] { p.StatusRed, p.Surface };
            yield return new object[] { p.AccentBronze, p.WindowBg };
            yield return new object[] { p.AccentBronze, p.Surface };
            yield return new object[] { p.Text, p.TileDefaultBg };
            // SurfaceRaised wall (status-colour-vocabulary plan, gap closed
            // 2026-08-09): the toast card's real background is SurfaceRaised,
            // not Surface/WindowBg -- StatusRedRaised exists specifically to
            // clear this pairing (see ThemePalette.cs's StatusRedRaised field
            // comment), and the toast's title/detail text sit on the same
            // background, so Text/SubtleText are enforced against it too.
            yield return new object[] { p.StatusRedRaised, p.SurfaceRaised };
            yield return new object[] { p.Text, p.SurfaceRaised };
            yield return new object[] { p.SubtleText, p.SurfaceRaised };
        }
    }

    [Theory, MemberData(nameof(TextPairs))]
    public void EveryTextPairingMeetsWcagAa(Rgb fg, Rgb bg) =>
        Assert.True(ThemePalette.ContrastRatio(fg, bg) >= 4.5,
            $"{fg} on {bg} = {ThemePalette.ContrastRatio(fg, bg):F2}");

    [Theory]
    [InlineData(46, 125, 50)]    // demo green route
    [InlineData(21, 101, 192)]   // demo blue route
    [InlineData(192, 57, 43)]    // alert red
    [InlineData(255, 255, 255)]
    [InlineData(30, 30, 30)]
    public void IdealForegroundAlwaysMeetsAaOnRealisticBackgrounds(byte r, byte g, byte b)
    {
        var bg = new Rgb(r, g, b);
        var fg = ThemePalette.IdealForeground(bg);
        Assert.True(ThemePalette.ContrastRatio(fg, bg) >= 4.5,
            $"ideal {fg} on {bg} = {ThemePalette.ContrastRatio(fg, bg):F2}");
    }

    [Fact]
    public void ContrastRatioMatchesKnownAnchors()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);
        Assert.Equal(21.0, ThemePalette.ContrastRatio(black, white), 1);
        Assert.Equal(1.0, ThemePalette.ContrastRatio(white, white), 3);
        // symmetry
        Assert.Equal(
            ThemePalette.ContrastRatio(black, white),
            ThemePalette.ContrastRatio(white, black), 6);
    }

    [Fact]
    public void ParseColorHandlesHexNamesAndGarbage()
    {
        Assert.Equal(new Rgb(46, 125, 50), ThemePalette.ParseColor("#2e7d32"));
        Assert.Equal(new Rgb(255, 0, 0), ThemePalette.ParseColor("red"));
        Assert.Null(ThemePalette.ParseColor(""));
        Assert.Null(ThemePalette.ParseColor(null));
        Assert.Null(ThemePalette.ParseColor("not-a-color"));
    }

    // Since the 2026-09 rebrand the registry is exactly one light and one
    // dark scheme; the Appearance tab and the auto pair both depend on that.
    [Fact]
    public void SchemesRegistryIsExactlyLightAndDark()
    {
        Assert.Equal(2, ThemePalette.Schemes.Count);

        var light = ThemePalette.Schemes[0];
        Assert.Equal("light", light.Key);
        Assert.Same(ThemePalette.Light, light.Palette);
        Assert.False(light.IsDark);

        var dark = ThemePalette.Schemes[1];
        Assert.Equal("dark", dark.Key);
        Assert.Same(ThemePalette.Dark, dark.Palette);
        Assert.True(dark.IsDark);
    }

    [Theory]
    [InlineData("light", "light")]
    [InlineData("LIGHT", "light")]
    [InlineData("Dark", "dark")]
    [InlineData("DARK", "dark")]
    public void FindSchemeIsCaseInsensitive(string key, string expectedKey) =>
        Assert.Equal(expectedKey, ThemePalette.FindScheme(key)?.Key);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("graphite")]
    public void FindSchemeReturnsNullForNullOrUnknown(string? key) =>
        Assert.Null(ThemePalette.FindScheme(key));

    // Config (Core) cannot reference the registry, so it validates theme
    // against a literal auto/light/dark. Every registry key must survive a
    // real save and load; if a key is ever renamed on one side only, this
    // fails.
    [Fact]
    public void EveryRegistryKeyPassesConfigValidation()
    {
        foreach (var scheme in ThemePalette.Schemes)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ordonk_themelockstep_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "config.json");
                Config.Save(new Config { Theme = scheme.Key }, path);
                Assert.Equal(scheme.Key, Config.Load(path).Theme);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }
    }

    // 2026-08-04 (Task 7): ThemeManager.Brush allocates and freezes a NEW
    // SolidColorBrush every call; the dashboard re-evaluates RgbToBrushConverter
    // per tile per refresh, so without caching that's a fresh brush per tick.
    // RgbToBrushConverter caches by Rgb — assert the cache actually shares one
    // instance (not just an equal one) and that the shared brush is frozen.
    [Fact]
    public void RgbToBrushConverterCachesAndFreezesTheSameInstance()
    {
        var converter = new RgbToBrushConverter();
        var rgb = new Rgb(46, 125, 50);

        var first = converter.Convert(rgb, typeof(System.Windows.Media.Brush), null,
            System.Globalization.CultureInfo.InvariantCulture);
        var second = converter.Convert(rgb, typeof(System.Windows.Media.Brush), null,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Same(first, second);
        var brush = Assert.IsType<System.Windows.Media.SolidColorBrush>(first);
        Assert.True(brush.IsFrozen);
    }
}

/// <summary>ThemeManager.SetMode: "light"/"dark" pin that scheme's
/// palette/IsDark, anything else follows Windows. Needs a real
/// Application to read back the published "Theme.*" resources, so this joins
/// <see cref="HighlightContrastFixture"/> the same way
/// <see cref="AppearancePreviewTests"/> does (see that class's doc) rather
/// than declaring its own.</summary>
[Collection(HighlightContrastTests.Name)]
public class ThemeManagerSetModeTests
{
    private readonly HighlightContrastFixture _fx;
    public ThemeManagerSetModeTests(HighlightContrastFixture fx) => _fx = fx;

    private Color Brush(string key) =>
        ((SolidColorBrush)_fx.App.Resources[key]).Color;

    private static Color Expect(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    /// <summary>Puts ThemeManager back to "auto" after each test so a pinned
    /// scheme/mode never leaks into another test class sharing this fixture
    /// — same reasoning as ThemeHighContrastTests.ResetSeam.</summary>
    private void Reset() => _fx.Invoke(() => ThemeManager.SetMode(_fx.App, "auto"));

    [Fact]
    public void SetModeDarkPublishesDarkPaletteBrushesAndIsDark() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.SetMode(_fx.App, "dark");

            Assert.True(ThemeManager.IsDark);
            Assert.Same(ThemePalette.Dark, ThemeManager.Current);
            Assert.Equal("dark", ThemeManager.Mode);
            // Spot-check: Theme.WindowBg resolves to Dark.WindowBg.
            Assert.Equal(Expect(ThemePalette.Dark.WindowBg), Brush("Theme.WindowBg"));
        }
        finally { Reset(); }
    });

    [Fact]
    public void SetModeLightPublishesLightPaletteBrushesAndIsNotDark() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.SetMode(_fx.App, "light");

            Assert.False(ThemeManager.IsDark);
            Assert.Same(ThemePalette.Light, ThemeManager.Current);
            Assert.Equal("light", ThemeManager.Mode);
            Assert.Equal(Expect(ThemePalette.Light.WindowBg), Brush("Theme.WindowBg"));
        }
        finally { Reset(); }
    });

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("not-a-registry-scheme")]
    public void SetModeUnknownOrBlankKeyBehavesAsAutoAndDoesNotThrow(string mode) => _fx.Invoke(() =>
    {
        try
        {
            // No throw is the primary assertion: an exception here would
            // propagate out of Invoke and fail the test on its own, but
            // making the "never throws" contract explicit documents intent.
            ThemeManager.SetMode(_fx.App, mode);

            Assert.Equal("auto", ThemeManager.Mode);
            var expected = ThemeManager.IsDark ? ThemePalette.Dark : ThemePalette.Light;
            Assert.Equal(Expect(expected.WindowBg), Brush("Theme.WindowBg"));
        }
        finally { Reset(); }
    });

    [Theory]
    [InlineData("light", false)]
    [InlineData("dark", true)]
    public void SetModeLegacyLightAndDarkAreUnchanged(string mode, bool expectDark) => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.SetMode(_fx.App, mode);

            Assert.Equal(expectDark, ThemeManager.IsDark);
            Assert.Equal(mode, ThemeManager.Mode);
            var expected = expectDark ? ThemePalette.Dark : ThemePalette.Light;
            Assert.Equal(Expect(expected.WindowBg), Brush("Theme.WindowBg"));
        }
        finally { Reset(); }
    });

    [Fact]
    public void SetModeAutoIsUnchanged() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.SetMode(_fx.App, "auto");

            Assert.Equal("auto", ThemeManager.Mode);
            // Whichever the OS preference resolves to, Current/IsDark and the
            // published brush must agree with each other.
            var expected = ThemeManager.IsDark ? ThemePalette.Dark : ThemePalette.Light;
            Assert.Same(expected, ThemeManager.Current);
            Assert.Equal(Expect(expected.WindowBg), Brush("Theme.WindowBg"));
        }
        finally { Reset(); }
    });
}
