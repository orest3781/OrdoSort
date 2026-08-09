using System.IO.Compression;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

/// <summary>Task 5 (merge PDFs from zip). Fixtures build real zips with
/// ZipFile.Open(path, ZipArchiveMode.Create) (XlsxTableTests' own idiom) whose
/// entries are real PdfSharp-generated PDFs (UnlockTests' own fixture voice)
/// — no fake bytes standing in for either format.</summary>
public class ZipMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordozipmerge_" + Guid.NewGuid());
    public ZipMergeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    /// <summary>A small in-memory PDF with <paramref name="pageCount"/> pages,
    /// each page's Width distinct (the "page.Width = XUnit.FromPoint(100 + i)"
    /// idiom) so an ordering test can tell merged pages apart by more than
    /// just content.</summary>
    private static byte[] MakePdfBytes(int pageCount, double widthPt = 200)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pageCount; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(widthPt + i);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    /// <summary>Same fixture shape as UnlockTests.MakeEncrypted, but built
    /// straight into a byte[] (an in-memory zip entry source) rather than
    /// saved to a path.</summary>
    private static byte[] MakeEncryptedPdfBytes(string userPassword = "secret")
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPassword;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPassword;
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private string MakeZip(string name, params (string EntryName, byte[] Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            using var s = zip.CreateEntry(entryName).Open();
            s.Write(content, 0, content.Length);
        }
        return path;
    }

    // (a) merged output PageCount == sum of source page counts
    [Fact]
    public void MergedOutputPageCountEqualsSumOfSourcePageCounts()
    {
        var zip = MakeZip("basic.zip",
            ("a.pdf", MakePdfBytes(2)),
            ("b.pdf", MakePdfBytes(3)));

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        Assert.NotNull(r.Output);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(5, merged.PageCount);
    }

    // (b) ordering: 2.pdf before 10.pdf, proven by distinct page widths —
    // "10.pdf" is written into the zip FIRST, so a merge that preserved zip
    // order (or sorted lexically, where "10" < "2") would get this backwards.
    [Fact]
    public void EntriesMergeInNaturalOrderNotZipOrLexicalOrder()
    {
        var zip = MakeZip("order.zip",
            ("10.pdf", MakePdfBytes(1, widthPt: 110)),
            ("2.pdf", MakePdfBytes(1, widthPt: 102)));

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("ok", r.Status);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount);
        Assert.Equal(102, merged.Pages[0].Width.Point, 3);   // 2.pdf first
        Assert.Equal(110, merged.Pages[1].Width.Point, 3);   // 10.pdf second
    }

    // (c) non-PDF entries skipped and counted, merge still ok
    [Fact]
    public void NonPdfEntriesAreSkippedAndCountedButDoNotFailTheMerge()
    {
        var zip = MakeZip("mixed.zip",
            ("a.pdf", MakePdfBytes(1)),
            ("readme.txt", Encoding.UTF8.GetBytes("not a pdf")));

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("ok", r.Status);
        Assert.Equal(1, r.PdfCount);
        Assert.Equal(1, r.SkippedEntries);
    }

    // (d) zero PDF entries -> no_pdfs, nothing written
    [Fact]
    public void AZipWithNoPdfsInsideIsReportedNoPdfsAndWritesNothing()
    {
        var zip = MakeZip("empty.zip",
            ("readme.txt", Encoding.UTF8.GetBytes("nothing here")));

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("no_pdfs", r.Status);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, "empty.pdf")));
    }

    // (e) an encrypted entry fails the WHOLE zip, names the entry, and
    // leaves no output — including no partial file from the entry that
    // merged fine before the encrypted one was reached (natural order puts
    // "a-ok.pdf" before "z-bad.pdf").
    [Fact]
    public void AnEncryptedEntryFailsTheWholeZipAndLeavesNoOutput()
    {
        var zip = MakeZip("locked.zip",
            ("a-ok.pdf", MakePdfBytes(1)),
            ("z-bad.pdf", MakeEncryptedPdfBytes()));

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("error", r.Status);
        Assert.Contains("z-bad.pdf", r.Message);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, "locked.pdf")));
    }

    // (f) a pre-existing <zipname>.pdf gets a " (2)" suffix, never overwritten
    [Fact]
    public void APreExistingOutputNameGetsACollisionSuffix()
    {
        var zip = MakeZip("dup.zip", ("a.pdf", MakePdfBytes(1)));
        File.WriteAllText(Path.Combine(_dir, "dup.pdf"), "existing");

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, "dup (2).pdf"), r.Output);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_dir, "dup.pdf")));
    }

    // (g) nested-folder entries are included and merged
    [Fact]
    public void NestedFolderEntriesAreIncludedAndMerged()
    {
        var zip = MakeZip("nested.zip", ("inner/a.pdf", MakePdfBytes(2)));

        var r = ZipMerge.MergeZip(zip);

        Assert.Equal("ok", r.Status);
        Assert.Equal(1, r.PdfCount);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount);
    }

    // (h) a garbage file with a .zip name is a readable error, never a throw
    [Fact]
    public void AGarbageFileWithAZipExtensionIsAReadableErrorNotAThrow()
    {
        var path = Path.Combine(_dir, "junk.zip");
        File.WriteAllText(path, "this is not a zip");

        var r = ZipMerge.MergeZip(path);   // must not throw

        Assert.Equal("error", r.Status);
        Assert.NotEqual("", r.Message);
    }

    /// <summary>Fix round (review finding, Important): RemoveQuietly used to
    /// fire unconditionally on a save failure, deleting whatever sat at the
    /// picked target name — even a file this call never created. Collision-
    /// free/pickOutput only proves the name was free AT CHECK TIME; another
    /// process can claim that exact name before FileMode.CreateNew runs, in
    /// which case the constructor itself throws and this call must NOT touch
    /// what's there — the exact bug Unlock.PlaceAndSwap's own markCreated
    /// gate exists to close (2026-08 audit finding 1.2).
    ///
    /// The internal pickOutput seam stands in for that race deterministically:
    /// instead of hoping to win a timing race against a second process, this
    /// makes the "collision-free" name resolve straight to a path that
    /// ALREADY has real content on disk, so FileMode.CreateNew is guaranteed
    /// to throw (the name is taken) before a single byte of the merge is
    /// written — proving the `created` gate, not just its intent.</summary>
    [Fact]
    public void SaveFailureNeverDeletesAFileThisCallDidNotCreate()
    {
        var zip = MakeZip("collide.zip", ("a.pdf", MakePdfBytes(1)));
        var peerPath = Path.Combine(_dir, "peer.pdf");
        File.WriteAllText(peerPath, "not touched");

        var r = ZipMerge.MergeZip(zip, pickOutput: _ => peerPath);

        Assert.Equal("error", r.Status);
        Assert.Contains("couldn't save", r.Message);
        Assert.True(File.Exists(peerPath));
        Assert.Equal("not touched", File.ReadAllText(peerPath));
    }
}
