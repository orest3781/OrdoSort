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

    // Scheme registry scaffold: Schemes is the single enumeration every
    // scheme-aware test iterates going forward, so pin its exact shape here
    // (keys, palette identity, IsDark) plus FindScheme's lookup contract.
    // Extended alongside each new scheme landing (ledger, microfilm, manila,
    // carbon, blueprint) rather than left pinned at paper/graphite.
    [Fact]
    public void SchemesRegistryHasExpectedKeysAndPalettes()
    {
        Assert.Equal(6, ThemePalette.Schemes.Count);

        var paper = ThemePalette.Schemes[0];
        Assert.Equal("paper", paper.Key);
        Assert.Same(ThemePalette.Light, paper.Palette);
        Assert.False(paper.IsDark);

        var graphite = ThemePalette.Schemes[1];
        Assert.Equal("graphite", graphite.Key);
        Assert.Same(ThemePalette.Dark, graphite.Palette);
        Assert.True(graphite.IsDark);

        var ledger = ThemePalette.Schemes[2];
        Assert.Equal("ledger", ledger.Key);
        Assert.Same(ThemePalette.Ledger, ledger.Palette);
        Assert.True(ledger.IsDark);

        var microfilm = ThemePalette.Schemes[3];
        Assert.Equal("microfilm", microfilm.Key);
        Assert.Same(ThemePalette.Microfilm, microfilm.Palette);
        Assert.True(microfilm.IsDark);

        var manila = ThemePalette.Schemes[4];
        Assert.Equal("manila", manila.Key);
        Assert.Same(ThemePalette.Manila, manila.Palette);
        Assert.False(manila.IsDark);

        var carbon = ThemePalette.Schemes[5];
        Assert.Equal("carbon", carbon.Key);
        Assert.Same(ThemePalette.Carbon, carbon.Palette);
        Assert.True(carbon.IsDark);
    }

    [Theory]
    [InlineData("paper", "paper")]
    [InlineData("PAPER", "paper")]
    [InlineData("Graphite", "graphite")]
    [InlineData("GRAPHITE", "graphite")]
    public void FindSchemeIsCaseInsensitive(string key, string expectedKey) =>
        Assert.Equal(expectedKey, ThemePalette.FindScheme(key)?.Key);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public void FindSchemeReturnsNullForNullOrUnknown(string? key) =>
        Assert.Null(ThemePalette.FindScheme(key));

    // OrdoSort.Core.Config.SchemeKeys mirrors this registry BY HAND (Core
    // cannot reference Wpf, so there is no compile-time link) — see
    // Config.SchemeKeys' own doc comment. This is the drift guard: every
    // registry key here must pass Config's "theme" validation, and every
    // key Config's whitelist accepts must resolve to a real registry entry.
    // Whichever direction fails is exactly the signal that someone added a
    // scheme to one list and forgot the other.
    [Fact]
    public void ConfigSchemeWhitelistAndRegistryStayInLockstep()
    {
        foreach (var scheme in ThemePalette.Schemes)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ordonk_themelockstep_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "config.json");
                // A minimal valid Config, same shape the Core-side round-trip
                // tests build — only Theme varies. Config.Load runs the real
                // validation path, so this throws ConfigException the moment
                // Config.SchemeKeys stops accepting a registry key.
                Config.Save(new Config { Theme = scheme.Key }, path);
                var loaded = Config.Load(path);
                Assert.Equal(scheme.Key, loaded.Theme);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }

        foreach (var key in Config.SchemeKeys)
            Assert.NotNull(ThemePalette.FindScheme(key));
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

/// <summary>ThemeManager.SetMode's scheme-key support (2026-08-08): a
/// registry scheme key (e.g. "graphite") pins that scheme's palette/IsDark,
/// additive alongside the legacy "auto"/"light"/"dark" trio, which keeps
/// resolving to the paper/graphite auto pair exactly as before. Needs a real
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
    public void SetModeGraphitePublishesDarkPaletteBrushesAndIsDark() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.SetMode(_fx.App, "graphite");

            Assert.True(ThemeManager.IsDark);
            Assert.Same(ThemePalette.Dark, ThemeManager.Current);
            Assert.Equal("graphite", ThemeManager.Mode);
            // Spot-check: Theme.WindowBg resolves to Dark.WindowBg.
            Assert.Equal(Expect(ThemePalette.Dark.WindowBg), Brush("Theme.WindowBg"));
        }
        finally { Reset(); }
    });

    [Fact]
    public void SetModePaperPublishesLightPaletteBrushesAndIsNotDark() => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.SetMode(_fx.App, "paper");

            Assert.False(ThemeManager.IsDark);
            Assert.Same(ThemePalette.Light, ThemeManager.Current);
            Assert.Equal("paper", ThemeManager.Mode);
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
