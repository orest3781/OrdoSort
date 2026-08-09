using System.Windows;
using System.Windows.Controls;

namespace OrdoSort.Wpf.Tests;

/// <summary>Pins the four styles added by the type-scale rename pass (restyle
/// sub-task A1): TileCountText, BodySmallText, EmphasisText and
/// HeadlineCompactText replace hardcoded FontSize values that were scattered,
/// unnamed, across ReadyView/SettingsWindow/MainWindow/TriageWindow. A later
/// "tidy-up" that nudges one of these silently changes every consuming site
/// at once — same rationale as CaptionSizingTests for the caption rung.
///
/// Asserted through a real Application resource lookup and a real applied
/// style, not by reading the style object's setters — a setter can be
/// present and still lose to something with higher precedence.</summary>
[Collection(HighlightContrastTests.Name)]
public class TypeScaleSizingTests
{
    private readonly HighlightContrastFixture _fx;
    public TypeScaleSizingTests(HighlightContrastFixture fx) => _fx = fx;

    private static (double size, FontWeight weight) Resolve(Style style)
    {
        var block = new TextBlock { Text = "sample", Style = style };
        var host = new Border { Child = block };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        return (block.FontSize, block.FontWeight);
    }

    [Fact]
    public void TileCountTextIs27Bold() => _fx.Invoke(() =>
    {
        var style = Assert.IsType<Style>(_fx.App.Resources["TileCountText"]);
        var (size, weight) = Resolve(style);
        Assert.Equal(27d, size);
        Assert.Equal(FontWeights.Bold, weight);
    });

    [Fact]
    public void BodySmallTextIs12() => _fx.Invoke(() =>
    {
        var style = Assert.IsType<Style>(_fx.App.Resources["BodySmallText"]);
        var (size, _) = Resolve(style);
        Assert.Equal(12d, size);
    });

    [Fact]
    public void EmphasisTextIs15() => _fx.Invoke(() =>
    {
        var style = Assert.IsType<Style>(_fx.App.Resources["EmphasisText"]);
        var (size, _) = Resolve(style);
        Assert.Equal(15d, size);
    });

    [Fact]
    public void HeadlineCompactTextIs20BoldLikeHeadlineText() => _fx.Invoke(() =>
    {
        var style = Assert.IsType<Style>(_fx.App.Resources["HeadlineCompactText"]);
        var (size, weight) = Resolve(style);
        Assert.Equal(20d, size);
        Assert.Equal(FontWeights.Bold, weight);
    });
}
