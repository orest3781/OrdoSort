using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>TrimmedTextTooltip directly, on a bare TextBlock — table-rules
/// Rule 4's "only when trimmed" half, isolated from any specific window or
/// grid the same way DataGridColumnCapTests isolates DataGridColumnCap's
/// own arithmetic on a bare grid built in code. Every fact here constrains
/// a real, off-screen TextBlock to a real pixel width and reads its OWN
/// realized ToolTip back — the same FormattedText-vs-ActualWidth
/// measurement DataGridColumnCap.ContentWidths already uses for content
/// width, applied here to trimmed-ness instead (see TrimmedTextTooltip's
/// own doc comment for why: WPF's TextBlock has no IsTextTrimmed property
/// to read directly).
///
/// AutoFitColumnTests/HistoryWindowXamlTests already prove the POSITIVE
/// case — a long value inside a real DataGrid column gets a tooltip — on
/// several real windows; what they do not prove, because none of their own
/// facts need a value short enough to fit, is the negative: a cell that
/// isn't actually cut off must not show a tooltip at all ("a tooltip that
/// repeats fully-visible text is noise," requirements.md). That gap is
/// this file's whole reason to exist.</summary>
[Collection(HighlightContrastTests.Name)]
public class TrimmedTextTooltipTests
{
    private readonly HighlightContrastFixture _fx;
    public TrimmedTextTooltipTests(HighlightContrastFixture fx) => _fx = fx;

    private const string LongText =
        "A-Very-Long-Value-That-Cannot-Possibly-Fit-In-Sixty-Pixels-Of-Width.pdf";

    private static (Window host, TextBlock text) BuildTextBlock(string content, double width, bool enabled)
    {
        var text = new TextBlock { Text = content, TextTrimming = TextTrimming.CharacterEllipsis, Width = width };
        TrimmedTextTooltip.SetEnabled(text, enabled);
        var host = new Window
        {
            Content = text,
            Width = width + 40, Height = 80,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        host.Show();
        host.UpdateLayout();
        return (host, text);
    }

    [Fact]
    public void ATextBlockShortEnoughToFitGetsNoTooltip() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (host, text) = BuildTextBlock("short", 200, enabled: true);
        try
        {
            Assert.Null(text.ToolTip);
        }
        finally { host.Close(); }
    });

    [Fact]
    public void ATextBlockTooLongToFitGetsATooltipOfItsOwnFullText() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (host, text) = BuildTextBlock(LongText, 60, enabled: true);
        try
        {
            Assert.Equal(LongText, text.ToolTip as string);
        }
        finally { host.Close(); }
    });

    /// <summary>The opt-out every full-PATH-tooltip column uses (ZipTools/
    /// MergePdfs/PageCounts' Item/File, FilenameListWindow's Pages): with
    /// Enabled="False" this class must never touch ToolTip at all, even for
    /// a value that would otherwise clearly trigger it.</summary>
    [Fact]
    public void EnabledFalseNeverSetsATooltipEvenWhenTrimmed() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (host, text) = BuildTextBlock(LongText, 60, enabled: false);
        try
        {
            Assert.Null(text.ToolTip);
        }
        finally { host.Close(); }
    });

    /// <summary>A resize that goes from fitting to not fitting must pick up
    /// a tooltip live, not just at construction — DataGridColumnCap's own
    /// cap changes are exactly this shape: a live width change on an
    /// EXISTING cell, driven by SizeChanged the same way this class's own
    /// doc comment explains it must be (a Binding to ActualWidth would not
    /// reliably refresh here).</summary>
    [Fact]
    public void ATextBlockThatStopsFittingAfterAResizePicksUpATooltip() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (host, text) = BuildTextBlock(LongText, 2000, enabled: true);
        try
        {
            Assert.Null(text.ToolTip);   // plenty of room at 2000px — nothing trimmed yet
            text.Width = 60;
            host.UpdateLayout();
            Assert.Equal(LongText, text.ToolTip as string);
        }
        finally { host.Close(); }
    });

    /// <summary>The reverse of the resize fact above: a cell that WAS
    /// trimmed and gains room again (a sibling column shrinking, say) must
    /// lose its tooltip, not keep showing stale text nothing hides any
    /// more.</summary>
    [Fact]
    public void ATextBlockThatStartsFittingAfterAResizeLosesItsTooltip() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (host, text) = BuildTextBlock(LongText, 60, enabled: true);
        try
        {
            Assert.Equal(LongText, text.ToolTip as string);   // precondition: trimmed, tooltip present
            text.Width = 2000;
            host.UpdateLayout();
            Assert.Null(text.ToolTip);
        }
        finally { host.Close(); }
    });
}
