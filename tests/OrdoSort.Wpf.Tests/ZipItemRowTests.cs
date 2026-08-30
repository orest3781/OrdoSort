using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>ZipItemRow is the union of the three row types the zip tools
/// used to carry one each (PathRow, UnzipRow, ZipRow). These pin the parts
/// that were behaviour rather than plumbing: how a path becomes a Kind, and
/// what each engine result turns into on the row.</summary>
public class ZipItemRowTests
{
    [Theory]
    [InlineData(@"C:\in\a.pdf", "pdf")]
    [InlineData(@"C:\in\a.PDF", "pdf")]
    [InlineData(@"C:\in\a.ZIP", "zip")]
    [InlineData(@"C:\in\a.zip", "zip")]
    // Task 7: KindOf maps every MergeTypes group's own extensions to that
    // group's name too, not just pdf/zip — this is what makes the merge
    // window's Kind column read word/excel/powerpoint/image/text.
    [InlineData(@"C:\in\a.docx", "word")]
    [InlineData(@"C:\in\a.xlsx", "excel")]
    [InlineData(@"C:\in\a.pptx", "powerpoint")]
    [InlineData(@"C:\in\a.jpg", "image")]   // singular — MergeTypes.Images is plural, the column names one file
    [InlineData(@"C:\in\a.txt", "text")]
    // An extension no MergeTypes group recognizes at all still falls back
    // to "file" — the Zip Extract window accepts anything, so this is not
    // just a pdf/zip concern.
    [InlineData(@"C:\in\a.exe", "file")]
    public void KindOfReadsTheExtensionForAnythingThatIsNotADirectory(string path, string expected) =>
        Assert.Equal(expected, ZipItemRow.KindOf(path));

    [Fact]
    public void KindOfCallsAnExistingDirectoryAFolder()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try { Assert.Equal("folder", ZipItemRow.KindOf(dir)); }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void IsZipDrivesOffKindNotTheExtension() =>
        Assert.True(new ZipItemRow(@"C:\in\a.zip", "zip").IsZip);

    /// <summary>A folder row shows the folder's OWN name; Path.GetFileName
    /// would return "" for a trailing separator, which is why PathRow used
    /// DirectoryInfo.Name and this keeps doing so.</summary>
    [Fact]
    public void DisplayUsesTheFolderNameForAFolderRow() =>
        Assert.Equal("scans", new ZipItemRow(@"C:\in\scans\", "folder").Display);

    [Fact]
    public void DisplayUsesTheFileNameForEverythingElse() =>
        Assert.Equal("a.pdf", new ZipItemRow(@"C:\in\a.pdf", "file").Display);

    [Fact]
    public void ApplyingAnOkExtractShowsTheOutputFolder()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "ok", @"C:\in\a"));
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal("→ a", row.Note);
        Assert.Equal(@"C:\in\a", row.Output);
    }

    [Fact]
    public void ApplyingAFailedExtractShowsTheMessageVerbatim()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "error", null, "not a valid zip archive"));
        Assert.Equal(ZipItemRowStatus.Error, row.StatusKind);
        Assert.Equal("not a valid zip archive", row.Note);
    }

    [Fact]
    public void ApplyingAnOkMergeCountsThePdfs()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(@"C:\in\a.zip", "ok", @"C:\in\a.pdf", PdfCount: 3));
        Assert.Equal(ZipItemRowStatus.Ok, row.StatusKind);
        Assert.Equal("→ a.pdf (3 PDFs)", row.Note);
    }

    [Fact]
    public void ApplyingAMergeWithNoPdfsIsItsOwnStatus()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(@"C:\in\a.zip", "no_pdfs", Message: "no PDFs inside"));
        Assert.Equal(ZipItemRowStatus.NoPdfs, row.StatusKind);
        Assert.Equal("no PDFs inside", row.Note);
    }

    /// <summary>Singular/plural on the PDF count — ZipRow got this right and
    /// the union must not lose it.</summary>
    [Fact]
    public void OnePdfIsNotPluralised()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(@"C:\in\a.zip", "ok", @"C:\in\a.pdf", PdfCount: 1));
        Assert.Equal("→ a.pdf (1 PDF)", row.Note);
    }

    [Fact]
    public void ApplyingANeedsPasswordExtractLeavesTheRowRunnable()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "needs_password", null, "needs a password"));
        Assert.Equal(ZipItemRowStatus.NeedsPassword, row.StatusKind);
        Assert.Equal("needs a password", row.Note);
        Assert.True(row.IsRunnable);
        Assert.Null(row.Output);
    }

    [Fact]
    public void ApplyingANeedsPasswordMergeKeepsTheMessageThatNamesTheEntry()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new PdfMerge.MergeResult(@"C:\in\a.zip", "needs_password",
            Message: "'report.pdf' inside needs a password", Item: "report.pdf"));
        Assert.Equal(ZipItemRowStatus.NeedsPassword, row.StatusKind);
        Assert.Equal("'report.pdf' inside needs a password", row.Note);
        Assert.True(row.IsRunnable);
    }

    [Theory]
    [InlineData(ZipItemRowStatus.Pending, true)]
    [InlineData(ZipItemRowStatus.NeedsPassword, true)]
    [InlineData(ZipItemRowStatus.Ok, false)]
    [InlineData(ZipItemRowStatus.NoPdfs, false)]
    [InlineData(ZipItemRowStatus.Error, false)]
    public void OnlyPendingAndNeedsPasswordAreRunnable(ZipItemRowStatus status, bool runnable)
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Mark(status, "");
        Assert.Equal(runnable, row.IsRunnable);
    }

    /// <summary>A probe's verdict, or "not merged — x needs a password" on a
    /// row a culprit held back: status and note only, never Output.</summary>
    [Fact]
    public void MarkSetsStatusAndNoteWithoutTouchingOutput()
    {
        var row = new ZipItemRow(@"C:\in\a.zip", "zip");
        row.Apply(new Zipper.UnzipResult(@"C:\in\a.zip", "ok", @"C:\in\a"));
        row.Mark(ZipItemRowStatus.Pending, "a saved password opens this");
        Assert.Equal(ZipItemRowStatus.Pending, row.StatusKind);
        Assert.Equal("a saved password opens this", row.Note);
        Assert.Equal(@"C:\in\a", row.Output);
    }
}
