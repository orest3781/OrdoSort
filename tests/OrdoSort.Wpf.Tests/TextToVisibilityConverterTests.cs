using System.Globalization;
using System.Windows;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Turnaround's and Production's add-feedback line is on its own grid
/// row, and this converter is what stops that row taking vertical space when
/// there is nothing to say. The note moved off the source row after a
/// minimum-width render showed the row's StackPanel giving its last child
/// whatever space was left — which, behind a long status line, was none, so
/// the feedback silently vanished in exactly the case it exists for.</summary>
public class TextToVisibilityConverterTests
{
    private static object Convert(object? value) =>
        new TextToVisibilityConverter().Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);

    [Fact]
    public void SomethingToSayIsVisible() =>
        Assert.Equal(Visibility.Visible, Convert("nothing added — 1 already listed"));

    /// <summary>Collapsed, never Hidden: Hidden still reserves the row's
    /// height, which would leave a blank gap above the mapping row whenever
    /// the note is silent — most of the time.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingToSayCollapses(string? quiet) =>
        Assert.Equal(Visibility.Collapsed, Convert(quiet));

    [Fact]
    public void ANonStringIsTreatedAsNothingToSay() =>
        Assert.Equal(Visibility.Collapsed, Convert(42));
}
