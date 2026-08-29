using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using ZipFile = System.IO.Compression.ZipFile;

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

    private static readonly string[] NoPasswords = Array.Empty<string>();

    /// <summary>An <c>ask</c> that must never be reached: a fact passing a
    /// working candidate proves nothing if the prompt quietly rescued it.</summary>
    private static string? NeverAsked(PasswordRequest _) =>
        throw new InvalidOperationException("the prompt was reached");

    /// <summary>A locked zip through SharpZipLib's own writer — the only
    /// writer in reach that encrypts. ZipCrypto when <paramref name="aesKeySize"/>
    /// is 0, WinZip AES otherwise. Entries here are deflated; see
    /// MakeStoredLockedZip for the stored variant the check-byte fact needs.</summary>
    private string MakeLockedZip(string name, string password, int aesKeySize,
        params (string EntryName, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zos = new ZipOutputStream(fs) { Password = password };
        foreach (var (entryName, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new ZipEntry(entryName) { Size = bytes.Length, AESKeySize = aesKeySize };
            zos.PutNextEntry(entry);
            zos.Write(bytes, 0, bytes.Length);
            zos.CloseEntry();
        }
        return path;
    }

    /// <summary>One STORED ZipCrypto entry — no Deflate to choke on garbage,
    /// so a password that slips past the 1-byte header check hands back
    /// garbage silently and only the CRC can tell (measured 2026-08-28).</summary>
    private string MakeStoredLockedZip(string name, string password, string content)
    {
        var path = Path.Combine(_dir, name);
        var bytes = Encoding.UTF8.GetBytes(content);
        var crc = new Crc32();
        crc.Update(bytes);
        using var fs = File.Create(path);
        using var zos = new ZipOutputStream(fs) { Password = password };
        var entry = new ZipEntry("s.txt")
        {
            Size = bytes.Length, Crc = crc.Value, CompressionMethod = CompressionMethod.Stored, AESKeySize = 0,
        };
        zos.PutNextEntry(entry);
        zos.Write(bytes, 0, bytes.Length);
        zos.CloseEntry();
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

        var extracted = Zipper.Extract(target, NoPasswords, null);
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

        var extracted = Zipper.Extract(target, NoPasswords, null);
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

        var r = Zipper.Extract(zipPath, NoPasswords, null);

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

        var r1 = Zipper.Extract(zipPath, NoPasswords, null);
        var r2 = Zipper.Extract(zipPath, NoPasswords, null);

        Assert.Equal("ok", r1.Status);
        Assert.Equal("ok", r2.Status);
        Assert.Equal(Path.Combine(_dir, "bundle2"), r1.OutputFolder);
        Assert.Equal(Path.Combine(_dir, "bundle2 (2)"), r2.OutputFolder);
    }

    /// <summary>The ZipSlip guard is Zipper's own since the SharpZipLib move
    /// (2026-08-28) — see the class doc comment. Written with
    /// System.IO.Compression on purpose: that writer keeps entry names
    /// verbatim, while SharpZipLib's own writer cleans them (measured:
    /// "C:\drive.txt" became "drive.txt"), so a SharpZipLib-built fixture
    /// would never reach the guard at all. A crafted name that would land
    /// outside the destination must be refused and leave no trace either
    /// outside the zip's own directory or inside it (the partial output
    /// folder this call created before extraction failed).</summary>
    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData(@"C:\evil.txt")]
    public void ZipSlipEntryIsRejectedAndLeavesNoTraceOutsideOrInside(string entryName)
    {
        var zipPath = Path.Combine(_dir, "slip.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry(entryName).Open();
            var bytes = Encoding.UTF8.GetBytes("pwned");
            s.Write(bytes, 0, bytes.Length);
        }

        var r = Zipper.Extract(zipPath, NoPasswords, null);

        Assert.Equal("error", r.Status);
        Assert.Contains("outside", r.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetPathRoot(Path.GetFullPath(_dir))!, "evil.txt")));
        Assert.False(File.Exists(@"C:\evil.txt"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "slip")));
    }

    [Fact]
    public void CorruptZipIsAReadableErrorAndLeavesNoOutputFolder()
    {
        var path = Path.Combine(_dir, "junk.zip");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is not a zip"));

        var r = Zipper.Extract(path, NoPasswords, null);   // must not throw

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

        var r = Zipper.Extract(path, NoPasswords, null, pickOutputDir: _ => peerDir);

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

    // ------------------------------------------------------ passwords

    [Theory]
    [InlineData(0)]     // ZipCrypto
    [InlineData(256)]   // WinZip AES
    public void ALockedZipExtractsWithTheRightCandidateAndNeverAsks(int aesKeySize)
    {
        var zipPath = MakeLockedZip("locked.zip", "right", aesKeySize, ("a.txt", "aaa"), ("sub/b.txt", "bbb"));

        var r = Zipper.Extract(zipPath, new[] { "nope", "right" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        var outDir = Path.Combine(_dir, "locked");
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(outDir, "sub", "b.txt")));
    }

    [Fact]
    public void WhenNoCandidateOpensItThePromptIsAskedForTheArchiveItself()
    {
        var zipPath = MakeLockedZip("asked.zip", "right", 0, ("a.txt", "aaa"));
        var requests = new List<PasswordRequest>();

        var r = Zipper.Extract(zipPath, new[] { "nope" }, req => { requests.Add(req); return "right"; });

        Assert.Equal("ok", r.Status);
        var req = Assert.Single(requests);
        Assert.Equal("asked.zip", req.Item);
        Assert.Null(req.Inside);
        Assert.False(req.PreviousAttemptFailed);
    }

    [Fact]
    public void AWrongTypedPasswordIsAskedAgainWithTheFailedFlag()
    {
        var zipPath = MakeLockedZip("twice.zip", "right", 0, ("a.txt", "aaa"));
        var answers = new Queue<string?>(new[] { "bad", "right" });
        var flags = new List<bool>();

        var r = Zipper.Extract(zipPath, NoPasswords, req => { flags.Add(req.PreviousAttemptFailed); return answers.Dequeue(); });

        Assert.Equal("ok", r.Status);
        Assert.Equal(new[] { false, true }, flags);
    }

    /// <summary>Both key sizes (2026-08-28 review finding): AES entries carry
    /// no CRC at all, so a wrong AES password is only ever caught by
    /// SharpZipLib's own end-of-stream authentication failing inside
    /// <see cref="Decrypts"/> — nothing in the ZipCrypto-only version of this
    /// fact exercised that branch.</summary>
    [Theory]
    [InlineData(0)]     // ZipCrypto
    [InlineData(256)]   // WinZip AES
    public void SkippingThePromptIsNeedsPasswordAndLeavesNoFolder(int aesKeySize)
    {
        var zipPath = MakeLockedZip($"skipped-{aesKeySize}.zip", "right", aesKeySize, ("a.txt", "aaa"));

        var r = Zipper.Extract(zipPath, new[] { "nope" }, _ => null);

        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.OutputFolder);
        Assert.False(Directory.Exists(Path.Combine(_dir, $"skipped-{aesKeySize}")));
    }

    [Fact]
    public void WithNoPromptALockedZipNobodyCanOpenIsNeedsPassword()
    {
        var zipPath = MakeLockedZip("noask.zip", "right", 0, ("a.txt", "aaa"));
        var r = Zipper.Extract(zipPath, new[] { "nope" }, ask: null);
        Assert.Equal("needs_password", r.Status);
        Assert.False(Directory.Exists(Path.Combine(_dir, "noask")));
    }

    /// <summary>The correctness rule behind the CRC check. ZipCrypto's header
    /// check is one byte, so about 1 wrong password in 256 passes it — and on
    /// a STORED entry there is no Deflate to choke on the garbage: measured
    /// 2026-08-28, "wrong147" read 39 bytes silently with the CRC wrong.
    /// The header's 12 random bytes make the colliding password different
    /// every time the fixture is built, so the test finds one at runtime by
    /// asking SharpZipLib directly, then proves Zipper still refuses it.</summary>
    [Fact]
    public void AWrongPasswordThatPassesTheCheckByteIsStillRejected()
    {
        var zipPath = MakeStoredLockedZip("collide.zip", "right", "stored zipcrypto entry with a known crc");

        string? collider = null;
        using (var zip = new SzlZipFile(zipPath))
        {
            var entry = zip[0];
            for (var i = 0; i < 20000 && collider is null; i++)
            {
                zip.Password = "wrong" + i;
                try
                {
                    using var s = zip.GetInputStream(entry);   // throws "Invalid password" unless the check byte matches
                    collider = "wrong" + i;
                }
                catch (ZipException) { }
            }
        }
        Assert.NotNull(collider);   // (255/256)^20000 — a miss here means the fixture is not ZipCrypto

        var extracted = Zipper.Extract(zipPath, new[] { collider! }, ask: null);
        Assert.Equal("needs_password", extracted.Status);
        Assert.False(Directory.Exists(Path.Combine(_dir, "collide")));

        var probed = Zipper.Probe(zipPath, new[] { collider! });
        Assert.Equal("needs_password", probed.Status);
    }

    /// <summary>A second way verification can be quietly defeated (2026-08-28
    /// review finding), independent of the check-byte collision above: a
    /// 0-byte encrypted entry decrypts to 0 bytes under ANY password, so its
    /// CRC (0) always matches the archive's recorded CRC (also 0) — if
    /// picked as the probe entry, every wrong password would read as
    /// "opened". The empty entry is deliberately the FIRST and smallest-by-size
    /// entry in the archive; <see cref="SmallestEncryptedEntry"/> must still
    /// pick the non-empty one after it, for both Probe and Extract, and must
    /// still resolve to the empty entry once the real password is known so
    /// it round-trips like any other entry.</summary>
    [Fact]
    public void AnEmptyEncryptedEntryIsNeverPickedAsTheProbeOverANonEmptyOne()
    {
        var zipPath = MakeLockedZip("empty-first.zip", "right", 0, ("empty.txt", ""), ("a.txt", "aaa"));

        var probedWrong = Zipper.Probe(zipPath, new[] { "wrong" });
        Assert.Equal("needs_password", probedWrong.Status);

        var extractedWrong = Zipper.Extract(zipPath, new[] { "wrong" }, ask: null);
        Assert.Equal("needs_password", extractedWrong.Status);
        Assert.Null(extractedWrong.OutputFolder);
        Assert.False(Directory.Exists(Path.Combine(_dir, "empty-first")));

        var extractedRight = Zipper.Extract(zipPath, new[] { "right" }, ask: null);
        Assert.Equal("ok", extractedRight.Status);
        var outDir = Path.Combine(_dir, "empty-first");
        Assert.Equal("", File.ReadAllText(Path.Combine(outDir, "empty.txt")));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(outDir, "a.txt")));
    }

    [Fact]
    public void MixedEncryptedAndPlainEntriesExtractTogether()
    {
        var zipPath = Path.Combine(_dir, "mixed.zip");
        using (var fs = File.Create(zipPath))
        using (var zos = new ZipOutputStream(fs))
        {
            void Put(string name, string content, string? password)
            {
                zos.Password = password;
                var bytes = Encoding.UTF8.GetBytes(content);
                zos.PutNextEntry(new ZipEntry(name) { Size = bytes.Length, AESKeySize = 0 });
                zos.Write(bytes, 0, bytes.Length);
                zos.CloseEntry();
            }
            Put("plain.txt", "plain", null);
            Put("locked.txt", "locked", "right");
        }

        var r = Zipper.Extract(zipPath, new[] { "right" }, NeverAsked);

        Assert.Equal("ok", r.Status);
        Assert.Equal("plain", File.ReadAllText(Path.Combine(_dir, "mixed", "plain.txt")));
        Assert.Equal("locked", File.ReadAllText(Path.Combine(_dir, "mixed", "locked.txt")));
    }

    /// <summary>One password per archive: the password that opens the
    /// smallest encrypted entry is used for all of them, and an entry that
    /// rejects it fails the zip naming that entry — never a half-extracted
    /// folder left behind.</summary>
    [Fact]
    public void ALaterEntryWithADifferentPasswordFailsTheZipNamingIt()
    {
        var zipPath = Path.Combine(_dir, "two-passwords.zip");
        using (var fs = File.Create(zipPath))
        using (var zos = new ZipOutputStream(fs))
        {
            void Put(string name, string content, string password)
            {
                zos.Password = password;
                var bytes = Encoding.UTF8.GetBytes(content);
                zos.PutNextEntry(new ZipEntry(name) { Size = bytes.Length, AESKeySize = 0 });
                zos.Write(bytes, 0, bytes.Length);
                zos.CloseEntry();
            }
            Put("small.txt", "s", "right");                              // the smallest — the probe entry
            Put("other.txt", "a much longer entry body here", "different");
        }

        var r = Zipper.Extract(zipPath, new[] { "right" }, NeverAsked);

        Assert.Equal("error", r.Status);
        Assert.Contains("other.txt", r.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "two-passwords")));
    }

    // ---------------------------------------------------------- Probe

    [Fact]
    public void ProbeReportsNotEncryptedForAPlainZip()
    {
        var zipPath = MakeZip("plain.zip", ("a.txt", "aaa"));
        var r = Zipper.Probe(zipPath, new[] { "irrelevant" });
        Assert.Equal("not_encrypted", r.Status);
        Assert.Null(r.MatchedIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void ProbeReportsReadyWithTheIndexOfTheCandidateThatOpensIt(int aesKeySize)
    {
        var zipPath = MakeLockedZip("ready.zip", "right", aesKeySize, ("a.txt", "aaa"));
        var r = Zipper.Probe(zipPath, new[] { "nope", "right" });
        Assert.Equal("ready", r.Status);
        Assert.Equal(1, r.MatchedIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void ProbeReportsNeedsPasswordWhenNoCandidateOpensIt(int aesKeySize)
    {
        var zipPath = MakeLockedZip("needs.zip", "right", aesKeySize, ("a.txt", "aaa"));
        var r = Zipper.Probe(zipPath, new[] { "nope" });
        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.MatchedIndex);
    }

    [Fact]
    public void ProbeReportsUnreadableForSomethingThatIsNotAZip()
    {
        var path = Path.Combine(_dir, "junk.zip");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is not a zip"));
        var r = Zipper.Probe(path, new[] { "x" });
        Assert.Equal("unreadable", r.Status);
        Assert.Contains("not a valid zip", r.Message);
    }

    /// <summary>The probe writes nothing, anywhere — the same promise
    /// UnlockProbeWritesNothingTests holds Unlock.ProbeReadiness to, proven
    /// the same way: names, sizes and mtimes of the fixture directory before
    /// and after. No %TEMP% assertion here (2026-08-28 review finding):
    /// docs/known-flakes.md records that exact check flaking on
    /// UnlockProbeWritesNothingTests because a concurrently-running unlock
    /// test writes its own working copy into %TEMP% mid-window, fixed there
    /// by sharing UnlockNeverOverwritesTests' collection — but ZipperTests
    /// runs in AtomicPlaceTests' collection instead, so it cannot join that
    /// fix, and the check has nothing to prove here anyway: Zipper.Probe has
    /// no %TEMP% code path at all.</summary>
    [Fact]
    public void ProbeWritesNothing()
    {
        MakeZip("plain.zip", ("a.txt", "aaa"));
        MakeLockedZip("ready.zip", "aaa", 0, ("a.txt", "aaa"));
        MakeLockedZip("needs.zip", "zzz", 256, ("a.txt", "aaa"));
        File.WriteAllBytes(Path.Combine(_dir, "junk.zip"), Encoding.UTF8.GetBytes("junk"));

        static (string, long, DateTime)[] Snapshot(string dir) => Directory.GetFiles(dir)
            .Select(f => (Path.GetFileName(f)!, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal).ToArray();
        var before = Snapshot(_dir);

        Zipper.Probe(Path.Combine(_dir, "plain.zip"), new[] { "x" });
        Zipper.Probe(Path.Combine(_dir, "ready.zip"), new[] { "aaa" });
        Zipper.Probe(Path.Combine(_dir, "needs.zip"), new[] { "nope" });
        Zipper.Probe(Path.Combine(_dir, "junk.zip"), new[] { "x" });
        Zipper.Probe(Path.Combine(_dir, "missing.zip"), new[] { "x" });

        Assert.Equal(before, Snapshot(_dir));
    }
}
