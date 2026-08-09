using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The caption rung of the type ramp. Two styles, deliberately:
/// CaptionText is small AND de-emphasised (the overwhelmingly common case —
/// hints, notes, counts beside a control); CaptionTextOnSurface is small at
/// full text weight, for the handful of captions that carry real content the
/// user is meant to read, not skim. Before this task both were spelled
/// FontSize="11" by hand at 20 call sites, so the difference between them was
/// invisible in the XAML and drifted.
///
/// Asserted through a real Application resource lookup and a real applied
/// style, not by reading the style object's setters — a setter can be present
/// and still lose to something with higher precedence.</summary>
[Collection(HighlightContrastTests.Name)]
public class CaptionSizingTests
{
    private readonly HighlightContrastFixture _fx;
    public CaptionSizingTests(HighlightContrastFixture fx) => _fx = fx;

    private (double size, Color fore) Resolve(string styleKey)
    {
        var block = new TextBlock { Text = "sample" };
        block.Style = (Style)_fx.App.Resources[styleKey];
        var host = new Border { Child = block };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        return (block.FontSize, ((SolidColorBrush)block.Foreground).Color);
    }

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void CaptionTextIsSmallAndDeEmphasised(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (size, fore) = Resolve("CaptionText");
        Assert.Equal(11d, size);
        Assert.Equal(Color.FromRgb(p.SubtleText.R, p.SubtleText.G, p.SubtleText.B), fore);
    });

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void CaptionTextOnSurfaceIsSmallAtFullTextWeight(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);
        var (size, fore) = Resolve("CaptionTextOnSurface");
        Assert.Equal(11d, size);
        // the whole point of the second style: NOT SubtleText
        Assert.Equal(Color.FromRgb(p.Text.R, p.Text.G, p.Text.B), fore);
    });
}
