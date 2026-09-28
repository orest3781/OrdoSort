using OrdoSort.Core;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>How the print preview finds its printers: off the UI thread, and
/// saying which of two different problems it met when it lists none.</summary>
[Collection(HighlightContrastTests.Name)]
public class PrintPreviewPrintersTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public PrintPreviewPrintersTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private static System.Windows.Documents.FixedDocument OneSheet() =>
        LabelPrinting.BuildDocument(
            BoxLabels.Batch("ABCD", 1, 4, new DateTime(2026, 7, 25), 30));

    /// <summary>Q2-22: the preview asked Windows for every print queue inside
    /// its constructor, so an offline network printer held the whole app
    /// still before the preview even appeared. The window now opens at once,
    /// says it is looking, and fills the list when the answer comes.</summary>
    [Fact]
    public void ThePreviewOpensBeforeThePrinterListArrives() => _fx.Invoke(() =>
    {
        var answer = new TaskCompletionSource<PrinterList>();
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { }, findPrinters: () => answer.Task);
        try
        {
            Assert.Equal("Finding printers…", window.PrintNote.Text);
            Assert.False(window.PrintButton.IsEnabled);

            answer.SetResult(new PrinterList(new[] { "Office", "Labels" }, Default: "Labels"));
            PumpUntil(() => window.Printers.Items.Count == 2, "the printer list never arrived");

            Assert.Equal("Labels", window.Printers.SelectedItem);
            Assert.True(window.PrintButton.IsEnabled);
            Assert.Equal("", window.PrintNote.Text);
        }
        finally { window.Close(); }
    });

    /// <summary>DW-42: a stopped print spooler said "No printers found.", the
    /// same as a PC with no printers, so the user went looking for a printer
    /// that was there all along. The spooler's own failure is now named.</summary>
    [Fact]
    public void AStoppedSpoolerIsNamedRatherThanReportedAsNoPrinters() => _fx.Invoke(() =>
    {
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { },
            findPrinters: () => Task.FromResult(new PrinterList(Array.Empty<string>(), null,
                SpoolerError: "The RPC server is unavailable.")));
        try
        {
            PumpUntil(() => window.PrintNote.Text != "Finding printers…", "the printer check never finished");

            Assert.Contains("Print Spooler", window.PrintNote.Text);
            Assert.Contains("The RPC server is unavailable.", window.PrintNote.Text);
            Assert.False(window.PrintButton.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void APcWithNoPrintersStillSaysSo() => _fx.Invoke(() =>
    {
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { },
            findPrinters: () => Task.FromResult(new PrinterList(Array.Empty<string>(), null)));
        try
        {
            PumpUntil(() => window.PrintNote.Text != "Finding printers…", "the printer check never finished");

            Assert.Equal("No printers found.", window.PrintNote.Text);
            Assert.False(window.PrintButton.IsEnabled);
        }
        finally { window.Close(); }
    });
}
