using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The date prompt behind IDialogService.AskDate. Built through the
/// same internal Build seam PasswordWindow exposes, for the same reason: the
/// real window is constructed and shown off-screen without entering the
/// modal loop Ask would then have to escape.</summary>
[Collection(HighlightContrastTests.Name)]
public class StandardiseDateWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public StandardiseDateWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static StandardiseDateWindow Show(string defaultDate, int fileCount)
    {
        var w = StandardiseDateWindow.Build(null, defaultDate, fileCount);
        w.Left = -20000; w.Top = 0; w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Show();
        w.UpdateLayout();
        OverflowProbe.PumpRender();
        w.UpdateLayout();
        return w;
    }

    [Fact]
    public void OneFileIsNamedSingularAndTheBoxIsPreFilledWithTheDefault() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show("20260115", 1);
        try
        {
            Assert.Equal("Enter the date for this file.", w.MessageText.Text);
            Assert.Equal("20260115", w.DateBox.Text);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void SeveralFilesAreNamedByCount() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show("20260115", 12);
        try { Assert.Equal("Enter the date for these 12 files.", w.MessageText.Text); }
        finally { w.Close(); }
    });

    [Fact]
    public void RenameAnswersWithTheTypedDateAndIsTheDefaultButton() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show("20260115", 1);
        try
        {
            Assert.True(w.RenameButton.IsDefault, "Enter must mean Rename");
            w.DateBox.Text = "20260220";
            w.RenameButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("20260220", w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void RenameWithNonsenseStaysOpenAndExplainsWhy() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show("20260115", 1);
        try
        {
            w.DateBox.Text = "not a date";
            Assert.False(w.FailedText.IsVisible);
            w.RenameButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(w.IsVisible, "an invalid date must not close the window");
            Assert.Null(w.Answer);
            Assert.True(w.FailedText.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void CancelAnswersNullAndDiscardsWhatWasTyped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show("20260115", 1);
        try
        {
            w.DateBox.Text = "20260220";
            w.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    [Fact]
    public void EscapeIsACancelEvenWithADateTyped() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var w = Show("20260115", 1);
        try
        {
            w.DateBox.Text = "20260220";
            var source = PresentationSource.FromVisual(w)!;
            InputManager.Current.ProcessInput(
                new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.Null(w.Answer);
            Assert.False(w.IsVisible);
        }
        finally { if (w.IsVisible) w.Close(); }
    });

    // ---------------------------------------------------- IsValidDate table

    /// <summary>Pure, no window needed: "refuse nonsense" has to reject
    /// wrong lengths, non-digits AND calendar-impossible dates alike, not
    /// merely "8 characters that happen to be digits" — TryParseExact
    /// catches all three in one call, which is exactly what this table
    /// exists to pin rather than assume.</summary>
    [Theory]
    [InlineData("20260115", true)]
    [InlineData("20240229", true)]    // 2024 is a leap year
    [InlineData("20260229", false)]   // 2026 is NOT a leap year
    [InlineData("20261301", false)]   // month 13
    [InlineData("20260132", false)]   // day 32
    [InlineData("2026011", false)]    // 7 digits
    [InlineData("202601155", false)]  // 9 digits
    [InlineData("2026-01-15", false)] // not bare digits
    [InlineData("banana", false)]
    [InlineData("", false)]
    [InlineData("        ", false)]
    public void IsValidDateAcceptsOnlyARealCalendarDateAsExactlyEightDigits(string text, bool expected) =>
        Assert.Equal(expected, StandardiseDateWindow.IsValidDate(text));
}
