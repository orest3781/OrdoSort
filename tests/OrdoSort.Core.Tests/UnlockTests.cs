using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

public class UnlockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "unlocktest_" + Guid.NewGuid());
    public UnlockTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeEncrypted(string name, string userPw = "secret")
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPw;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPw;
        doc.Save(path);
        return path;
    }

    private string MakePlain(string name)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    private static bool NeedsPassword(string path)
    {
        try { using var _ = PdfReader.Open(path, PdfDocumentOpenMode.Import); return false; }
        catch { return true; }
    }

    [Fact]
    public void CorrectPasswordMakesAnOpenCopy()
    {
        var src = MakeEncrypted("locked.pdf");
        var r = Unlock.UnlockPdf(src, "secret", suffix: "_unlocked");
        Assert.True(r.Ok);
        Assert.Equal(Path.Combine(_dir, "locked_unlocked.pdf"), r.NewPath);
        Assert.False(NeedsPassword(r.NewPath!));   // copy opens freely
        Assert.True(NeedsPassword(src));           // original intact
    }

    [Fact]
    public void WrongPasswordWritesNothing()
    {
        var src = MakeEncrypted("locked.pdf");
        var r = Unlock.UnlockPdf(src, "nope", suffix: "_unlocked");
        Assert.Equal("wrong_password", r.Status);
        Assert.Null(r.NewPath);
        Assert.False(File.Exists(Path.Combine(_dir, "locked_unlocked.pdf")));
    }

    [Fact]
    public void UnencryptedIsReportedNotCopied()
    {
        var src = MakePlain("open.pdf");
        var r = Unlock.UnlockPdf(src, "", suffix: "_unlocked");
        Assert.Equal("not_encrypted", r.Status);
        Assert.False(File.Exists(Path.Combine(_dir, "open_unlocked.pdf")));
    }

    [Fact]
    public void MissingFileIsReadableError()
    {
        var r = Unlock.UnlockPdf(@"Z:\nope\gone.pdf", "x");
        Assert.Equal("error", r.Status);
        Assert.Contains("not found", r.Message);
    }

    private string ArchiveDir => Path.Combine(_dir,
        "locked_archive_" + DateTime.Now.ToString("yyyyMMdd"));

    [Fact]
    public void EmptySuffixArchivesTheLockedOriginalAndLeavesTheUnlockedOneInPlace()
    {
        var src = MakeEncrypted("20240115--1234567890.pdf");
        var r = Unlock.UnlockPdf(src, "secret", suffix: "");

        Assert.True(r.Ok);
        Assert.True(r.InPlace);
        Assert.Equal(src, r.NewPath);       // same folder, same name, no suffix
        Assert.False(NeedsPassword(src));   // and it opens freely now

        // the locked one is kept, not destroyed
        var archived = Path.Combine(ArchiveDir, "20240115--1234567890.pdf");
        Assert.Equal(archived, r.ArchivedTo);
        Assert.True(File.Exists(archived));
        Assert.True(NeedsPassword(archived));   // still locked, in the archive

        // one file beside it and no temp debris
        Assert.Equal(new[] { "20240115--1234567890.pdf" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x));
    }

    [Fact]
    public void AReadOnlyOriginalIsArchivedRatherThanRefused()
    {
        // This is the case that used to fail: overwriting had to DELETE the
        // original, and deleting a read-only file is refused. Moving it only
        // rewrites a directory entry, so it succeeds — and the archived copy
        // keeps the read-only flag, because someone set it deliberately.
        var src = MakeEncrypted("readonly.pdf");
        File.SetAttributes(src, FileAttributes.ReadOnly);
        try
        {
            var r = Unlock.UnlockPdf(src, "secret", suffix: "");

            Assert.True(r.Ok);
            Assert.False(NeedsPassword(src));
            var archived = Path.Combine(ArchiveDir, "readonly.pdf");
            Assert.True(File.Exists(archived));
            Assert.True(File.GetAttributes(archived).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(_dir, "*.pdf", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    [Fact]
    public void EmptySuffixWrongPasswordLeavesOriginalIntact()
    {
        var src = MakeEncrypted("x.pdf");
        var r = Unlock.UnlockPdf(src, "nope", suffix: "");
        Assert.Equal("wrong_password", r.Status);
        Assert.True(NeedsPassword(src));            // still locked
        Assert.Single(Directory.GetFiles(_dir));    // nothing new
        Assert.False(Directory.Exists(ArchiveDir)); // and no empty archive folder
    }

    [Fact]
    public void ArchivingTwiceInADayNeverOverwritesTheFirstOne()
    {
        var first = MakeEncrypted("dup.pdf");
        Assert.True(Unlock.UnlockPdf(first, "secret", suffix: "").Ok);
        var firstArchived = Path.Combine(ArchiveDir, "dup.pdf");
        var firstBytes = new FileInfo(firstArchived).Length;

        // a second locked file arrives with the same name and is unlocked too
        File.Delete(first);
        var second = MakeEncrypted("dup.pdf", userPw: "secret");
        var r = Unlock.UnlockPdf(second, "secret", suffix: "");

        Assert.True(r.Ok);
        Assert.Equal(Path.Combine(ArchiveDir, "dup (2).pdf"), r.ArchivedTo);
        Assert.True(File.Exists(firstArchived));
        Assert.Equal(firstBytes, new FileInfo(firstArchived).Length);   // untouched
    }

    [Fact]
    public void ALargeFileStreamsFromDiskAndBehavesIdentically()
    {
        // Over the threshold the bytes are never buffered — File.ReadAllBytes
        // has a hard 2GB wall and four buffered files multiply peak memory —
        // but the OUTCOME must be indistinguishable from the buffered path:
        // unlocked in place, original archived, nothing left over.
        var was = Unlock.LargeFileThresholdBytes;
        Unlock.LargeFileThresholdBytes = 1;   // every file takes the streaming path
        try
        {
            var src = MakeEncrypted("big.pdf");
            var r = Unlock.UnlockPdf(src, "secret", suffix: "");

            Assert.True(r.Ok);
            Assert.True(r.InPlace);
            Assert.Equal(src, r.NewPath);
            Assert.False(NeedsPassword(src));
            Assert.True(File.Exists(Path.Combine(ArchiveDir, "big.pdf")));
            Assert.True(NeedsPassword(Path.Combine(ArchiveDir, "big.pdf")));
            // no temp debris beside the file
            Assert.Equal(new[] { "big.pdf" },
                Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x));
        }
        finally { Unlock.LargeFileThresholdBytes = was; }
    }

    [Fact]
    public void AStreamedWrongPasswordLeavesNoDebrisAnywhere()
    {
        // the streaming path works through a local temp file — a failure must
        // clean it up, not leak one per failed attempt
        var was = Unlock.LargeFileThresholdBytes;
        Unlock.LargeFileThresholdBytes = 1;
        try
        {
            var src = MakeEncrypted("x.pdf");
            var r = Unlock.UnlockPdf(src, "nope", suffix: "");

            Assert.Equal("wrong_password", r.Status);
            Assert.True(NeedsPassword(src));
            Assert.Single(Directory.GetFiles(_dir));
            Assert.False(Directory.Exists(ArchiveDir));
        }
        finally { Unlock.LargeFileThresholdBytes = was; }
    }

    [Fact]
    public void AStreamedNotEncryptedFileIsReportedNotMoved()
    {
        var was = Unlock.LargeFileThresholdBytes;
        Unlock.LargeFileThresholdBytes = 1;
        try
        {
            var src = MakePlain("plain.pdf");
            var r = Unlock.UnlockPdf(src, "anything", suffix: "");
            Assert.Equal("not_encrypted", r.Status);
            Assert.True(File.Exists(src));
            Assert.False(Directory.Exists(ArchiveDir));
        }
        finally { Unlock.LargeFileThresholdBytes = was; }
    }

    [Fact]
    public void AFileHeldOpenElsewhereFailsBeforeAnythingMoves()
    {
        // Nothing can move a file another process holds without FILE_SHARE_DELETE.
        // What matters is that it fails cleanly: the original still locked, no
        // archive folder, no half-finished state.
        var src = MakeEncrypted("held.pdf");
        using (File.Open(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = Unlock.UnlockPdf(src, "secret", suffix: "");
            Assert.Equal("error", r.Status);
            Assert.Contains("another program", r.Message);
        }
        Assert.True(NeedsPassword(src));
        Assert.Equal(new[] { "held.pdf" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x));
    }


    [Fact]
    public void CollisionCountsAndNeverOverwrites()
    {
        var src = MakeEncrypted("locked.pdf");
        File.WriteAllText(Path.Combine(_dir, "locked_unlocked.pdf"), "existing");
        var r = Unlock.UnlockPdf(src, "secret", suffix: "_unlocked");
        Assert.Equal("locked_unlocked (2).pdf", Path.GetFileName(r.NewPath!));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_dir, "locked_unlocked.pdf")));
    }
}
