using OrdoSort.Core;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>PrintPreviewWindow moved into OrdoSort.Ui so BoxLabels.exe can
/// show the same preview OrdoSort does. It used to carry
/// <c>Title="OrdoSort — Print preview"</c> in its XAML, which would have put
/// the name of a product the recipient does not have on a window in an app
/// that otherwise never mentions it.
///
/// The title is the caller's now. These two tests pin both halves of that:
/// a host's title is used verbatim, and the library's own default names no
/// application at all.</summary>
[Collection(HighlightContrastTests.Name)]
public class PrintPreviewTitleTests
{
    private readonly HighlightContrastFixture _fx;
    public PrintPreviewTitleTests(HighlightContrastFixture fx) => _fx = fx;

    private static System.Windows.Documents.FixedDocument OneSheet() =>
        LabelPrinting.BuildDocument(
            BoxLabels.Batch("ABCD", 1, 4, new DateTime(2026, 7, 25), 30));

    [Fact]
    public void TitleIsTheOneTheHostAsksFor() => _fx.Invoke(() =>
    {
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { },
            "Box Labels — Print preview");
        try
        {
            Assert.Equal("Box Labels — Print preview", window.Title);
        }
        finally { window.Close(); }
    });

    /// <summary>The guard that matters for the standalone: a host that passes
    /// nothing must not inherit OrdoSort's branding from the shared library.
    /// Asserting the exact string alone would still pass if someone changed
    /// the default to "OrdoSort — Print preview", so the substring check is
    /// the part carrying the intent.</summary>
    [Fact]
    public void DefaultTitleNamesNoApplication() => _fx.Invoke(() =>
    {
        var window = new PrintPreviewWindow(OneSheet(), "job", _ => { });
        try
        {
            Assert.Equal("Print preview", window.Title);
            Assert.DoesNotContain("OrdoSort", window.Title);
        }
        finally { window.Close(); }
    });
}
