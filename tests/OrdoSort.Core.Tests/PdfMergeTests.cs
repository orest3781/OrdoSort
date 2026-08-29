using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using ZipFile = System.IO.Compression.ZipFile;

namespace OrdoSort.Core.Tests;

/// <summary>Task 5 (merge PDFs from zip). Fixtures build real zips with
/// ZipFile.Open(path, ZipArchiveMode.Create) (XlsxTableTests' own idiom) whose
/// entries are real PdfSharp-generated PDFs (UnlockTests' own fixture voice)
/// — no fake bytes standing in for either format.</summary>
public class PdfMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordozipmerge_" + Guid.NewGuid());
    public PdfMergeTests() => Directory.CreateDirectory(_dir);
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

    private static readonly string[] NoPasswords = Array.Empty<string>();

    private static string? NeverAsked(PasswordRequest _) =>
        throw new InvalidOperationException("the prompt was reached");

    private string MakePdfFile(string name, int pageCount = 1, double widthPt = 200)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, MakePdfBytes(pageCount, widthPt));
        return path;
    }

    private string MakeEncryptedPdfFile(string name, string userPassword = "secret")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, MakeEncryptedPdfBytes(userPassword));
        return path;
    }

    /// <summary>A password-protected ARCHIVE (AES-256, SharpZipLib's writer)
    /// holding PDFs — as distinct from a plain archive holding a locked PDF.</summary>
    private string MakeLockedZip(string name, string password, params (string EntryName, byte[] Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zos = new ZipOutputStream(fs) { Password = password };
        foreach (var (entryName, content) in entries)
        {
            zos.PutNextEntry(new ZipEntry(entryName) { Size = content.Length, AESKeySize = 256 });
            zos.Write(content, 0, content.Length);
            zos.CloseEntry();
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

        var r = PdfMerge.MergeZip(zip, NoPasswords, null);

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

        var r = PdfMerge.MergeZip(zip, NoPasswords, null);

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

        var r = PdfMerge.MergeZip(zip, NoPasswords, null);

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

        var r = PdfMerge.MergeZip(zip, NoPasswords, null);

        Assert.Equal("no_pdfs", r.Status);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, "empty.pdf")));
    }

    // (e) an encrypted entry nobody can open still fails the WHOLE zip and
    // leaves no output — but as needs_password, naming the entry, so the row
    // can be run again once someone knows the password. Fail-whole is
    // unchanged: no partial file from the entry that merged fine first.
    [Fact]
    public void AnEncryptedEntryNobodyCanOpenIsNeedsPasswordNamingItAndLeavesNoOutput()
    {
        var zip = MakeZip("locked.zip",
            ("a-ok.pdf", MakePdfBytes(1)),
            ("z-bad.pdf", MakeEncryptedPdfBytes()));

        var r = PdfMerge.MergeZip(zip, new[] { "nope" }, _ => null);

        Assert.Equal("needs_password", r.Status);
        Assert.Contains("z-bad.pdf", r.Message);
        Assert.Equal("z-bad.pdf", r.Item);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, "locked.pdf")));
    }

    // (f) a pre-existing <zipname>.pdf gets a " (2)" suffix, never overwritten
    [Fact]
    public void APreExistingOutputNameGetsACollisionSuffix()
    {
        var zip = MakeZip("dup.zip", ("a.pdf", MakePdfBytes(1)));
        File.WriteAllText(Path.Combine(_dir, "dup.pdf"), "existing");

        var r = PdfMerge.MergeZip(zip, NoPasswords, null);

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, "dup (2).pdf"), r.Output);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_dir, "dup.pdf")));
    }

    // (g) nested-folder entries are included and merged
    [Fact]
    public void NestedFolderEntriesAreIncludedAndMerged()
    {
        var zip = MakeZip("nested.zip", ("inner/a.pdf", MakePdfBytes(2)));

        var r = PdfMerge.MergeZip(zip, NoPasswords, null);

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

        var r = PdfMerge.MergeZip(path, NoPasswords, null);   // must not throw

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

        var r = PdfMerge.MergeZip(zip, NoPasswords, null, pickOutput: _ => peerPath);

        Assert.Equal("error", r.Status);
        Assert.Contains("couldn't save", r.Message);
        Assert.True(File.Exists(peerPath));
        Assert.Equal("not touched", File.ReadAllText(peerPath));
    }

    // ------------------------------------------- passwords inside a zip

    [Fact]
    public void ALockedEntryOpensWithACandidateAndNobodyIsAsked()
    {
        var zip = MakeZip("cand.zip", ("a.pdf", MakePdfBytes(1)), ("b.pdf", MakeEncryptedPdfBytes("secret")));

        var r = PdfMerge.MergeZip(zip, new[] { "nope", "secret" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount);
    }

    [Fact]
    public void ALockedEntryAsksWithTheZipAsWhereItLives()
    {
        var zip = MakeZip("asked.zip", ("b.pdf", MakeEncryptedPdfBytes("secret")));
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeZip(zip, NoPasswords, req => { requests.Add(req); return "secret"; });

        Assert.Equal("ok", r.Status);
        var req = Assert.Single(requests);
        Assert.Equal("b.pdf", req.Item);
        Assert.Equal("asked.zip", req.Inside);
    }

    [Fact]
    public void ALockedArchiveOpensWithACandidate()
    {
        var zip = MakeLockedZip("archive.zip", "zippw", ("a.pdf", MakePdfBytes(2)));

        var r = PdfMerge.MergeZip(zip, new[] { "zippw" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(2, merged.PageCount);
    }

    [Fact]
    public void ALockedArchiveSkippedIsNeedsPasswordForTheArchiveItself()
    {
        var zip = MakeLockedZip("archive2.zip", "zippw", ("a.pdf", MakePdfBytes(1)));
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeZip(zip, new[] { "nope" }, req => { requests.Add(req); return null; });

        Assert.Equal("needs_password", r.Status);
        Assert.Equal("archive2.zip", r.Item);
        Assert.Equal("archive2.zip", Assert.Single(requests).Item);
        Assert.Null(Assert.Single(requests).Inside);
        Assert.Null(r.Output);
    }

    /// <summary>The contract the view models' per-unit candidate list rests
    /// on: Core holds the caller's list, it does not snapshot it, so a
    /// password added to that list from inside <c>ask</c> is tried on the
    /// next locked thing in the same call. One archive locked with "same"
    /// holding one PDF locked with "same" is therefore ONE prompt, not two.</summary>
    [Fact]
    public void ATypedArchivePasswordIsReusedForALockedEntryInside()
    {
        var zip = MakeLockedZip("same.zip", "same", ("a.pdf", MakeEncryptedPdfBytes("same")));
        var candidates = new List<string>();
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeZip(zip, candidates, req =>
        {
            requests.Add(req);
            // What ZipListViewModel.AskPassword does to the unit's own list.
            candidates.Remove("same");
            candidates.Insert(0, "same");
            return "same";
        });

        Assert.Equal("ok", r.Status);
        var asked = Assert.Single(requests);
        Assert.Equal("same.zip", asked.Item);   // the archive, and nothing inside it
        Assert.Null(asked.Inside);
    }

    // --------------------------------------------------- loose PDFs

    // "10.pdf" is created first and listed first; a merge that kept input
    // order or sorted lexically would get this backwards.
    [Fact]
    public void LoosePdfsMergeInNaturalOrderOfTheirNames()
    {
        var ten = MakePdfFile("10.pdf", widthPt: 110);
        var two = MakePdfFile("2.pdf", widthPt: 102);

        var r = PdfMerge.MergeFiles(new[] { ten, two }, null, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.Equal(102, merged.Pages[0].Width.Point, 3);
        Assert.Equal(110, merged.Pages[1].Width.Point, 3);
    }

    /// <summary>The same default-name rule Zipper.DefaultName applies to a
    /// zip: the folder CONTAINING the first document, placed beside it.</summary>
    [Fact]
    public void TheDefaultOutputIsNamedAfterTheFolderAndPlacedBesideTheFirst()
    {
        var a = MakePdfFile("a.pdf");
        var b = MakePdfFile("b.pdf");

        var r = PdfMerge.MergeFiles(new[] { b, a }, null, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, Path.GetFileName(_dir) + ".pdf"), r.Output);
        Assert.Equal(Path.GetFileName(_dir) + ".pdf", PdfMerge.DefaultName(new[] { b, a }));
    }

    [Fact]
    public void APreExistingDefaultNameGetsACollisionSuffix()
    {
        var a = MakePdfFile("a.pdf");
        var taken = Path.Combine(_dir, Path.GetFileName(_dir) + ".pdf");
        File.WriteAllText(taken, "existing");

        var r = PdfMerge.MergeFiles(new[] { a }, null, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, Path.GetFileName(_dir) + " (2).pdf"), r.Output);
        Assert.Equal("existing", File.ReadAllText(taken));
    }

    /// <summary>Merge to… is a Save-As, and a Save-As path is an answer the
    /// dialog already asked the user to confirm: the file there is replaced —
    /// through AtomicPlace, so never by deleting it up front, and with no
    /// temp sibling left behind.</summary>
    [Fact]
    public void MergeToReplacesTheChosenPathAndLeavesNoTempSibling()
    {
        var a = MakePdfFile("a.pdf", pageCount: 3);
        var chosen = Path.Combine(_dir, "chosen.pdf");
        File.WriteAllText(chosen, "old content, not a real pdf");

        var r = PdfMerge.MergeFiles(new[] { a }, chosen, NoPasswords, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(chosen, r.Output);
        using (var merged = PdfReader.Open(chosen, PdfDocumentOpenMode.Import))
            Assert.Equal(3, merged.PageCount);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_dir),
            f => f.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ALockedLoosePdfOpensWithACandidate()
    {
        var locked = MakeEncryptedPdfFile("locked.pdf", "secret");
        var plain = MakePdfFile("plain.pdf");

        var r = PdfMerge.MergeFiles(new[] { locked, plain }, null, new[] { "secret" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal(2, r.PdfCount);
    }

    [Fact]
    public void ALockedLoosePdfAsksNamingTheFileWithNowhereInside()
    {
        var locked = MakeEncryptedPdfFile("locked.pdf", "secret");
        var requests = new List<PasswordRequest>();

        var r = PdfMerge.MergeFiles(new[] { locked }, null, NoPasswords, req => { requests.Add(req); return "secret"; });

        Assert.Equal("ok", r.Status);
        var req = Assert.Single(requests);
        Assert.Equal("locked.pdf", req.Item);
        Assert.Null(req.Inside);
    }

    /// <summary>Fail-whole for the loose group: one skipped document merges
    /// nothing, and Item names it so the caller can mark the right row.</summary>
    [Fact]
    public void SkippingALockedLoosePdfMergesNothingAndNamesIt()
    {
        var plain = MakePdfFile("a-plain.pdf");
        var locked = MakeEncryptedPdfFile("z-locked.pdf", "secret");

        var r = PdfMerge.MergeFiles(new[] { plain, locked }, null, new[] { "nope" }, _ => null);

        Assert.Equal("needs_password", r.Status);
        Assert.Equal(locked, r.Item);
        Assert.Equal("needs a password", r.Message);
        Assert.Null(r.Output);
        Assert.False(File.Exists(Path.Combine(_dir, Path.GetFileName(_dir) + ".pdf")));
    }

    [Fact]
    public void AGarbageLoosePdfIsAnErrorNamingItAndNobodyIsAsked()
    {
        var plain = MakePdfFile("a.pdf");
        var junk = Path.Combine(_dir, "junk.pdf");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var r = PdfMerge.MergeFiles(new[] { plain, junk }, null, NoPasswords, NeverAsked);

        Assert.Equal("error", r.Status);
        Assert.Equal(junk, r.Item);
        Assert.StartsWith("couldn't read it", r.Message);
        Assert.Null(r.Output);
    }

    [Fact]
    public void TheMergedOutputIsNotEncryptedEvenWhenEverySourceWas()
    {
        var locked = MakeEncryptedPdfFile("locked.pdf", "secret");

        var r = PdfMerge.MergeFiles(new[] { locked }, null, new[] { "secret" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        using var merged = PdfReader.Open(r.Output!, PdfDocumentOpenMode.Import);
        Assert.False(merged.SecuritySettings.IsEncrypted);
    }

    [Fact]
    public void MergingNothingIsAnErrorNotAThrow()
    {
        var r = PdfMerge.MergeFiles(Array.Empty<string>(), null, NoPasswords, NeverAsked);
        Assert.Equal("error", r.Status);
        Assert.Equal("Merged.pdf", PdfMerge.DefaultName(Array.Empty<string>()));
    }
}
