using System.Globalization;
using System.Windows;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>An add-feedback line that lives on its own grid row, and this
/// converter is what stops that row taking vertical space when there is
/// nothing to say — a StackPanel giving its last child whatever space is
/// left silently loses a long status line's feedback otherwise.</summary>
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
