using OrdoSort.Core;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>QC-15: with box labels, Copies in the print preview numbers each
/// copy on its own rather than repeating barcodes. The preview says so
/// before anything prints; the numbering itself is covered by
/// LabelMakerViewModelTests.</summary>
[Collection(HighlightContrastTests.Name)]
public class PrintPreviewCopiesTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public PrintPreviewCopiesTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private static System.Windows.Documents.FixedDocument OneSheet() =>
        LabelPrinting.BuildDocument(
            BoxLabels.Batch("ABCD", 1, 4, new DateTime(2026, 7, 25), 30));

    [Fact]
    public void MoreThanOneCopySaysEachExtraCopyGetsItsOwnNumbers() => _fx.Invoke(() =>
    {
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { },
            extraCopies: _ => Task.FromResult<System.Windows.Documents.FixedDocument?>(null));
        try
        {
            // A machine with no printers shows that note instead; give it one.
            if (window.Printers.Items.Count == 0) window.Printers.Items.Add("Test printer");

            window.Copies.Text = "3";
            Assert.Equal("Each extra copy gets its own box numbers when you print.", window.PrintNote.Text);

            window.Copies.Text = "1";
            Assert.Equal("", window.PrintNote.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WithoutNumberingCopiesAddsNoNote() => _fx.Invoke(() =>
    {
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { });
        try
        {
            var before = window.PrintNote.Text;

            window.Copies.Text = "3";

            Assert.Equal(before, window.PrintNote.Text);
        }
        finally { window.Close(); }
    });
}
