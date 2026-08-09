using PdfSharp.Pdf;

namespace OrdoSort.Core.Tests;

/// <summary>Task 3 (PDF page counts tool). Fixture builders mirror
/// UnlockTests' MakePlain/MakeEncrypted, same temp-dir-per-class idiom.</summary>
public class PageCountsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pagecountstest_" + Guid.NewGuid());
    public PageCountsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakePlain(string name, int pages)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.Save(path);
        return path;
    }

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

    [Fact]
    public void APlainThreePagePdfCountsCleanly()
    {
        var path = MakePlain("three.pdf", 3);
        var r = PageCounts.Count(path);
        Assert.Equal(3, r.Pages);
        Assert.Equal("", r.Error);
    }

    [Fact]
    public void AnEncryptedPdfReportsAPasswordNoteRatherThanACount()
    {
        var path = MakeEncrypted("locked.pdf");
        var r = PageCounts.Count(path);
        Assert.Null(r.Pages);
        Assert.Contains("password", r.Error);
    }

    [Fact]
    public void GarbageBytesWithAPdfNameFailCleanlyWithoutThrowing()
    {
        var path = Path.Combine(_dir, "garbage.pdf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var r = PageCounts.Count(path);   // must not throw
        Assert.Null(r.Pages);
        Assert.NotEqual("", r.Error);
    }

    [Fact]
    public void AMissingFileReportsFileNotFound()
    {
        var r = PageCounts.Count(Path.Combine(_dir, "nope.pdf"));
        Assert.Null(r.Pages);
        Assert.Equal("file not found", r.Error);
    }

    [Fact]
    public void APathUnderAMissingFolderAlsoReportsFileNotFound()
    {
        var r = PageCounts.Count(Path.Combine(_dir, "no-such-folder", "nope.pdf"));
        Assert.Null(r.Pages);
        Assert.Equal("file not found", r.Error);
    }

    [Fact]
    public void AFileHeldOpenWithNoSharingReportsInUseRatherThanACount()
    {
        // FileShare.None, not FileShare.Read: File.OpenRead's own share mode
        // is Read, so a Read+Read hold (the way Unlock's own "held" fixture
        // proves ITS sharing violation, on the later File.Move step) would
        // let this read through cleanly and prove nothing about IsInUse.
        // None is what actually reproduces a sharing violation on the read
        // itself, the only I/O PageCounts.Count ever does.
        var path = MakePlain("held.pdf", 1);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = PageCounts.Count(path);   // must not throw
            Assert.Null(r.Pages);
            Assert.Contains("another program", r.Error);
        }
    }
}
