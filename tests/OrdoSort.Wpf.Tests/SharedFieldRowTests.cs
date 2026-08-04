using System.Windows;
using System.Windows.Controls;

namespace OrdoSort.Wpf.Tests;

/// <summary>FieldRow/FieldLabel used to live privately inside
/// SettingsWindow.xaml while four other windows hand-rolled the same shape,
/// so the label gap and row rhythm drifted per window. These assert the
/// styles are resolvable app-wide and carry the metrics Settings established
/// — the values are pinned deliberately: a later "tidy-up" that nudges them
/// silently re-lays-out five windows at once.</summary>
[Collection(HighlightContrastTests.Name)]
public class SharedFieldRowTests
{
    private readonly HighlightContrastFixture _fx;
    public SharedFieldRowTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void FieldLabelIsAppLevelAndKeepsSettingsMetrics() => _fx.Invoke(() =>
    {
        var style = _fx.App.Resources["FieldLabel"] as Style;
        Assert.NotNull(style);
        var label = new TextBlock { Text = "Inbox:", Style = style };
        var host = new Border { Child = label };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
        Assert.Equal(new Thickness(0, 0, 10, 0), label.Margin);
    });

    [Fact]
    public void FieldRowIsAppLevelAndKeepsSettingsMetrics() => _fx.Invoke(() =>
    {
        var style = _fx.App.Resources["FieldRow"] as Style;
        Assert.NotNull(style);
        var row = new Grid { Style = style };
        var host = new Border { Child = row };
        host.Measure(new Size(400, 200));
        host.Arrange(new Rect(0, 0, 400, 200));
        Assert.Equal(new Thickness(0, 0, 0, 10), row.Margin);
    });
}
