using System.Windows;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The bundled brand typeface really loads. WPF never reports a
/// missing font: a family chain whose first entry cannot be found just draws
/// in the next one. Without these tests a broken resource path would quietly
/// ship the app in Segoe UI. Joins <see cref="HighlightContrastFixture"/> for
/// a live Application, which pack URIs need.</summary>
[Collection(HighlightContrastTests.Name)]
public class AppFontsTests
{
    private readonly HighlightContrastFixture _fx;
    public AppFontsTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData(400, "AtkinsonHyperlegibleNext-Regular.ttf")]
    [InlineData(600, "AtkinsonHyperlegibleNext-SemiBold.ttf")]
    [InlineData(700, "AtkinsonHyperlegibleNext-Bold.ttf")]
    public void DefaultFamilyResolvesEachWeightToTheBundledFile(int weight, string file) => _fx.Invoke(() =>
    {
        var typeface = new Typeface(AppFonts.CreateDefault(), FontStyles.Normal,
            FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);

        Assert.True(typeface.TryGetGlyphTypeface(out var glyphs),
            "the bundled font did not load, so WPF would fall back to Segoe UI");
        Assert.EndsWith(file, glyphs.FontUri.OriginalString, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void BlankConfigValueMeansTheDefault() => _fx.Invoke(() =>
        Assert.Equal(AppFonts.CreateDefault().Source, AppFonts.Create("  ").Source));

    [Fact]
    public void NamedFamilyIsUsedAsGiven() => _fx.Invoke(() =>
        Assert.Equal("Consolas", AppFonts.Create(" Consolas ").Source));
}
