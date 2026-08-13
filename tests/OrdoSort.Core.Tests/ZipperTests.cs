using System.IO.Compression;
using System.Text;

namespace OrdoSort.Core.Tests;

/// <summary>Task 6 (zip and unzip tools). Fixtures build real zips with
/// ZipFile.Open(path, ZipArchiveMode.Create) — the same idiom ZipMergeTests
/// and XlsxTableTests already use — and plain text-file fixtures for the
/// files/folders CreateZip reads from disk.</summary>
/// <summary>In the AtomicPlace seam collection: SaveAsRidesOutADestination
/// HeldOpenBriefly assigns AtomicPlace.BeforeAttempt, and that field is a
/// single process-wide one — see AtomicPlaceTests.Name.</summary>
[Collection(AtomicPlaceTests.Name)]
public class ZipperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordozipper_" + Guid.NewGuid());
    public ZipperTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private string MakeFile(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string MakeFolder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string MakeZip(string name, params (string EntryName, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            using var s = zip.CreateEntry(entryName).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    // ---------------------------------------------------------- CreateZip

    [Fact]
    public void FilesAreZippedAndTheArchiveContainsExactlyThoseEntries()
    {
        var a = MakeFile("a.txt", "aaa");
        var b = MakeFile("b.txt", "bbb");
        var target = Path.Combine(_dir, "made.zip");

        var r = Zipper.CreateZip(new[] { a, b }, target);

        Assert.Equal("ok", r.Status);
        Assert.Equal(target, r.Output);
        using var zip = ZipFile.OpenRead(target);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "a.txt", "b.txt" }, names);
    }

    [Fact]
    public void DuplicateLooseFileNamesGetACounterSuffixInTheArchive()
    {
        var a1 = MakeFile("a.txt", "one");
        var a2 = MakeFile(Path.Combine("sub", "a.txt"), "two");
        var target = Path.Combine(_dir, "dup.zip");

        var r = Zipper.CreateZip(new[] { a1, a2 }, target);

        Assert.Equal("ok", r.Status);
        using var zip = ZipFile.OpenRead(target);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "a (2).txt", "a.txt" }, names);
    }

    /// <summary>2026-08 review finding: the in-archive dedupe originally only
    /// covered loose files, not folder names — but ZipArchive.CreateEntry
    /// happily writes two entries sharing a FullName even though
    /// ZipFile.ExtractToDirectory later throws IOException on the second one.
    /// Two DIFFERENT top-level folders sharing a name (e.g. from two
    /// different projects) with overlapping relative paths inside must both
    /// survive, under "docs/..." and "docs (2)/...", and the resulting
    /// archive must fully round-trip through this class's own Extract.</summary>
    [Fact]
    public void TwoTopLevelFoldersSharingANameGetACounterSuffixSoTheArchiveRoundTrips()
    {
        var docsA = MakeFolder(Path.Combine("ProjectA", "docs"));
        MakeFile(Path.Combine("ProjectA", "docs", "readme.txt"), "from A");
        var docsB = MakeFolder(Path.Combine("ProjectB", "docs"));
        MakeFile(Path.Combine("ProjectB", "docs", "readme.txt"), "from B");
        var target = Path.Combine(_dir, "collide.zip");

        var r = Zipper.CreateZip(new[] { docsA, docsB }, target);

        Assert.Equal("ok", r.Status);
        using (var zip = ZipFile.OpenRead(target))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "docs (2)/readme.txt", "docs/readme.txt" }, names);
        }

        var extracted = Zipper.Extract(target);
        Assert.Equal("ok", extracted.Status);
        var outDir = extracted.OutputFolder!;
        Assert.Equal("from A", File.ReadAllText(Path.Combine(outDir, "docs", "readme.txt")));
        Assert.Equal("from B", File.ReadAllText(Path.Combine(outDir, "docs (2)", "readme.txt")));
    }

    /// <summary>Same finding as the folder/folder case above, but a loose
    /// file and a top-level folder sharing a root name — the shared
    /// usedRootNames set has to catch the collision regardless of which
    /// kind claims the name first.</summary>
    [Fact]
    public void ALooseFileAndATopLevelFolderSharingANameStillDedupeAndRoundTrip()
    {
        var file = MakeFile("shared.txt", "loose file content");
        // The folder lives under its own parent ("nested\") so its NAME can
        // be "shared.txt" too without an OS-level naming clash against the
        // loose file above — Windows won't allow a file and a directory to
        // share one name in the SAME parent, but that's not what's under
        // test here; the collision under test is the ARCHIVE root name both
        // produce ("shared.txt"), not their real filesystem paths.
        var folder = MakeFolder(Path.Combine("nested", "shared.txt"));
        MakeFile(Path.Combine("nested", "shared.txt", "inside.txt"), "folder content");
        var target = Path.Combine(_dir, "sharedname.zip");

        var r = Zipper.CreateZip(new[] { file, folder }, target);

        Assert.Equal("ok", r.Status);
        using (var zip = ZipFile.OpenRead(target))
        {
            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "shared (2).txt/inside.txt", "shared.txt" }, names);
        }

        var extracted = Zipper.Extract(target);
        Assert.Equal("ok", extracted.Status);
        var outDir = extracted.OutputFolder!;
        Assert.Equal("loose file content", File.ReadAllText(Path.Combine(outDir, "shared.txt")));
        Assert.Equal("folder content", File.ReadAllText(Path.Combine(outDir, "shared (2).txt", "inside.txt")));
    }

    [Fact]
    public void FolderEntriesUseForwardSlashRelativePathsUnderTheFolderNamePrefix()
    {
        var folder = MakeFolder("docs");
        MakeFile(Path.Combine("docs", "top.txt"), "top");
        MakeFile(Path.Combine("docs", "inner", "nested.txt"), "nested");
        var target = Path.Combine(_dir, "folder.zip");

        var r = Zipper.CreateZip(new[] { folder }, target);

        Assert.Equal("ok", r.Status);
        using var zip = ZipFile.OpenRead(target);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "docs/inner/nested.txt", "docs/top.txt" }, names);
    }

    [Fact]
    public void SingleFolderInputDefaultsToTheFoldersNameBesideIt()
    {
        var folder = MakeFolder("photos");
        MakeFile(Path.Combine("photos", "a.jpg"), "x");

        var r = Zipper.CreateZip(new[] { folder });

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, "photos.zip"), r.Output);
        Assert.True(File.Exists(r.Output));
    }

    [Fact]
    public void ADefaultNameCollisionGetsACollisionSuffix()
    {
        var folder = MakeFolder("photos");
        MakeFile(Path.Combine("photos", "a.jpg"), "x");
        File.WriteAllText(Path.Combine(_dir, "photos.zip"), "existing");

        var r = Zipper.CreateZip(new[] { folder });

        Assert.Equal("ok", r.Status);
        Assert.Equal(Path.Combine(_dir, "photos (2).zip"), r.Output);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_dir, "photos.zip")));
    }

    [Fact]
    public void AnExplicitOutputPathOverwritesWhateverWasThere()
    {
        var a = MakeFile("a.txt", "aaa");
        var target = Path.Combine(_dir, "existing.zip");
        File.WriteAllText(target, "old content, not a real zip");

        var r = Zipper.CreateZip(new[] { a }, target);

        Assert.Equal("ok", r.Status);
        Assert.Equal(target, r.Output);
        // The old bytes are actually gone, not just "a zip now parses here"
        // — proves the pre-existing file was replaced, not appended to or
        // reused by ZipArchive opening in some other mode.
        Assert.NotEqual("old content, not a real zip", File.ReadAllText(target));
        using var zip = ZipFile.OpenRead(target);
        Assert.Single(zip.Entries);
        // No temp sibling left behind after a successful Save-As replace.
        Assert.Equal(
            new[] { "a.txt", "existing.zip" },
            Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>2026-08 audit finding: CreateZip used to delete whatever was
    /// at a Save-As <c>outputPath</c> up front, before ever building the new
    /// archive — reasoning that the Save-As dialog already confirmed
    /// overwrite intent. True of the file the user SAW, but the delete ran
    /// after the dialog closed: on the SMB shares this app targets, two
    /// coworkers both Zip -> Save-As to the same filename could race, and
    /// the second one's delete destroyed the first one's just-written
    /// archive unrecoverably — with no elevated access needed, and even if
    /// the SECOND zip then failed to build. This is the regression test for
    /// that: the pre-existing file at the target must survive a zip build
    /// that fails, not just one that succeeds. The failure here is real, not
    /// simulated through a test seam: the input file is opened with
    /// FileShare.None so ZipArchive.CreateEntryFromFile's own internal read
    /// genuinely throws IOException partway through the archive build.</summary>
    [Fact]
    public void AFailedZipBuildLeavesThePreExistingOutputFileIntact()
    {
        var locked = MakeFile("locked.txt", "will fail to read");
        var target = Path.Combine(_dir, "existing.zip");
        var original = Encoding.UTF8.GetBytes("previously saved archive bytes, not touched");
        File.WriteAllBytes(target, original);

        Zipper.ZipResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = Zipper.CreateZip(new[] { locked }, target);
        }

        Assert.Equal("error", r.Status);
        Assert.True(File.Exists(target));
        Assert.Equal(original, File.ReadAllBytes(target));
        // And no orphaned temp sibling left beside it either.
        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(_dir),
            f => f.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenNothingInTheInputStillExistsCreateZipErrorsWithoutWritingAnything()
    {
        var ghost = Path.Combine(_dir, "gone.txt");   // never created

        var r = Zipper.CreateZip(new[] { ghost });

        Assert.Equal("error", r.Status);
        Assert.Equal("nothing to zip", r.Message);
        Assert.Null(r.Output);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    // ------------------------------------------------------------ Extract

    [Fact]
    public void ExtractCreatesASiblingFolderNamedAfterTheZipWithFullContents()
    {
        var zipPath = MakeZip("bundle.zip", ("a.txt", "aaa"), ("sub/b.txt", "bbb"));

        var r = Zipper.Extract(zipPath);

        Assert.Equal("ok", r.Status);
        var outDir = Path.Combine(_dir, "bundle");
        Assert.Equal(outDir, r.OutputFolder);
        Assert.True(Directory.Exists(outDir));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(outDir, "sub", "b.txt")));
    }

    [Fact]
    public void ASecondExtractOfTheSameZipGetsACollisionSuffixedFolder()
    {
        var zipPath = MakeZip("bundle2.zip", ("a.txt", "aaa"));

        var r1 = Zipper.Extract(zipPath);
        var r2 = Zipper.Extract(zipPath);

        Assert.Equal("ok", r1.Status);
        Assert.Equal("ok", r2.Status);
        Assert.Equal(Path.Combine(_dir, "bundle2"), r1.OutputFolder);
        Assert.Equal(Path.Combine(_dir, "bundle2 (2)"), r2.OutputFolder);
    }

    /// <summary>Pins .NET 8's own ZipFile.ExtractToDirectory traversal
    /// protection — see Zipper's class doc comment on why that framework
    /// guarantee is load-bearing here. A crafted entry name that tries to
    /// escape the destination folder must be refused, and must leave no
    /// trace either outside the zip's own directory (evil.txt) or inside it
    /// (the partial output folder this call created before extraction
    /// failed).</summary>
    [Fact]
    public void ZipSlipEntryIsRejectedAndLeavesNoTraceOutsideOrInside()
    {
        var zipPath = Path.Combine(_dir, "slip.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry(@"..\evil.txt").Open();
            var bytes = Encoding.UTF8.GetBytes("pwned");
            s.Write(bytes, 0, bytes.Length);
        }

        var r = Zipper.Extract(zipPath);

        Assert.Equal("error", r.Status);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "slip")));
    }

    [Fact]
    public void CorruptZipIsAReadableErrorAndLeavesNoOutputFolder()
    {
        var path = Path.Combine(_dir, "junk.zip");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is not a zip"));

        var r = Zipper.Extract(path);   // must not throw

        Assert.Equal("error", r.Status);
        Assert.Contains("not a valid zip", r.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "junk")));
    }

    /// <summary>2026-08 review finding: Directory.CreateDirectory is
    /// idempotent — it does NOT throw just because the target already
    /// exists, unlike ZipFile.Open's FileMode.CreateNew for the CreateZip
    /// path — so an earlier version of ExtractCore set `created = true`
    /// unconditionally right after CreateDirectory returned, whether or not
    /// the folder was already there. A subsequent extraction failure then
    /// deleted a directory (and whatever was inside it) this call never
    /// created.
    ///
    /// Collision.FreeDirectory would skip right past a directory that
    /// already exists, so proving the race deterministically needs the same
    /// seam ZipMergeTests.SaveFailureNeverDeletesAFileThisCallDidNotCreate
    /// uses: the internal pickOutputDir overload stands in for "another
    /// process/user claimed this exact folder name first" by resolving
    /// straight to a path that already has real content in it.</summary>
    [Fact]
    public void ExtractFailureNeverDeletesADirectoryThisCallDidNotCreate()
    {
        var path = Path.Combine(_dir, "junk2.zip");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is not a zip"));

        var peerDir = Path.Combine(_dir, "peer");
        Directory.CreateDirectory(peerDir);
        var sentinel = Path.Combine(peerDir, "sentinel.txt");
        File.WriteAllText(sentinel, "not touched");

        var r = Zipper.Extract(path, pickOutputDir: _ => peerDir);

        Assert.Equal("error", r.Status);
        Assert.True(Directory.Exists(peerDir));
        Assert.True(File.Exists(sentinel));
        Assert.Equal("not touched", File.ReadAllText(sentinel));
    }

    /// <summary>Save-As's retry loop had no test and no seam. It was a
    /// byte-for-byte copy of Config.WriteAtomic's loop — whose own retry
    /// behaviour IS proven — and its doc comment said as much ("same shape,
    /// same reasoning"), but nothing anywhere proved that zipping over a file
    /// somebody still has open actually succeeds rather than failing outright.
    /// Sharing one implementation made the seam free, so here it is.
    ///
    /// The reader holds the destination WITHOUT FileShare.Delete, which is
    /// what genuinely blocks File.Replace — the same shape Config.Load uses,
    /// and the realistic case: another station still has the previous archive
    /// open. Releasing on attempt 2 rather than 0 keeps the claim honest, so
    /// the first attempts really do fail and the success is the loop working
    /// rather than a lucky first try.</summary>
    [Fact]
    public void SaveAsRidesOutADestinationHeldOpenBriefly()
    {
        var source = Path.Combine(_dir, "doc.txt");
        File.WriteAllText(source, "contents");
        var dest = Path.Combine(_dir, "archive.zip");
        File.WriteAllText(dest, "the previous archive");

        var hold = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var attemptsSeen = new List<int>();
        AtomicPlace.BeforeAttempt = (destination, attempt) =>
        {
            if (destination != dest) return;   // process-wide seam; not our write
            attemptsSeen.Add(attempt);
            if (attempt == 2) hold.Dispose();
        };
        Zipper.ZipResult r;
        try
        {
            r = Zipper.CreateZip(new[] { source }, dest);
        }
        finally
        {
            AtomicPlace.BeforeAttempt = null;
            hold.Dispose();   // no-op if the callback already ran
        }

        Assert.Equal("ok", r.Status);
        Assert.Equal(dest, r.Output);
        Assert.True(attemptsSeen.Count > 1, "it should have taken more than one attempt to land");

        // The archive really is the new one, not the old file left in place.
        using var archive = System.IO.Compression.ZipFile.OpenRead(dest);
        Assert.Equal("doc.txt", Assert.Single(archive.Entries).FullName);
    }
}
