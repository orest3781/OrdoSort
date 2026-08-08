using PdfSharp.Pdf;

namespace OrdoSort.Core.Tests;

/// <summary>Step 4 of the readiness-probe plan (2026-08-08): Unlock.ProbeReadiness
/// against real encrypted fixtures, not mocks -- not-encrypted,
/// encrypted-and-first-candidate-matches, encrypted-and-later-candidate-matches,
/// encrypted-and-none-match, damaged, empty candidate list, plus the missing-file
/// and in-use edges the real UnlockPdf also has to handle. Each test picks
/// candidate lists shaped so that a stub returning a fixed status, or a loop
/// that always reports index 0, would be caught by a SIBLING test here rather
/// than slipping through -- see LaterCandidateMatchReturnsThatIndexNotZero and
/// MatchedIndexIsNullWheneverNotReady in particular.</summary>
public class UnlockProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "unlockprobe_" + Guid.NewGuid());
    public UnlockProbeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeEncrypted(string name, string userPw)
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

    private string MakeEncryptedMultiPage(string name, string userPw, int pageCount)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        doc.SecuritySettings.UserPassword = userPw;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPw;
        doc.Save(path);
        return path;
    }

    /// <summary>Random bytes under a .pdf name: PdfSharp can't even find the
    /// "%PDF" magic prefix, so it throws a plain InvalidOperationException --
    /// never PdfReaderException -- both with and without a password (measured
    /// 2026-08-08, see Unlock.ProbeReadiness's doc comment). That is exactly
    /// what makes this fixture prove something a wrong password can't: it
    /// exercises the "anything else -> unreadable" branch, not the
    /// "PdfReaderException -> try next candidate" one.</summary>
    private string MakeDamaged(string name)
    {
        var path = Path.Combine(_dir, name);
        var garbage = new byte[512];
        new Random(1234).NextBytes(garbage);
        File.WriteAllBytes(path, garbage);
        return path;
    }

    [Fact]
    public void NotEncryptedFileReportsNotEncrypted()
    {
        var src = MakePlain("plain.pdf");
        var r = Unlock.ProbeReadiness(src, new[] { "some-saved-password" });
        Assert.Equal("not_encrypted", r.Status);
        Assert.Null(r.MatchedIndex);
        Assert.True(r.ReadyToUnlock);
    }

    [Fact]
    public void FirstCandidateMatchReportsReadyAtIndexZero()
    {
        var src = MakeEncrypted("locked.pdf", "right");
        var r = Unlock.ProbeReadiness(src, new[] { "right", "also-wrong" });
        Assert.Equal("ready", r.Status);
        Assert.Equal(0, r.MatchedIndex);
        Assert.True(r.ReadyToUnlock);
    }

    /// <summary>Distinguishes a real loop from a stub that always reports
    /// index 0 on any success: the winning password is deliberately placed
    /// THIRD, so only an implementation that actually skips the two wrong
    /// ones in order and stops at the right one produces index 2.</summary>
    [Fact]
    public void LaterCandidateMatchReturnsThatIndexNotZero()
    {
        var src = MakeEncrypted("locked.pdf", "third-one");
        var r = Unlock.ProbeReadiness(src, new[] { "first-wrong", "second-wrong", "third-one" });
        Assert.Equal("ready", r.Status);
        Assert.Equal(2, r.MatchedIndex);
        Assert.True(r.ReadyToUnlock);
    }

    [Fact]
    public void NoCandidateMatchReportsNeedsPassword()
    {
        var src = MakeEncrypted("locked.pdf", "the-real-one");
        var r = Unlock.ProbeReadiness(src, new[] { "nope1", "nope2", "nope3" });
        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.MatchedIndex);
        Assert.False(r.ReadyToUnlock);
    }

    [Fact]
    public void EmptyCandidateListOnAnEncryptedFileReportsNeedsPassword()
    {
        var src = MakeEncrypted("locked.pdf", "the-real-one");
        var r = Unlock.ProbeReadiness(src, Array.Empty<string>());
        Assert.Equal("needs_password", r.Status);
        Assert.Null(r.MatchedIndex);
        Assert.False(r.ReadyToUnlock);
    }

    [Fact]
    public void DamagedFileReportsUnreadableNotWrongPassword()
    {
        var src = MakeDamaged("damaged.pdf");
        // even a candidate list that happens to be non-empty must not be
        // mistaken for "tried and failed" -- risk 3, collapsing damage into
        // a password problem.
        var r = Unlock.ProbeReadiness(src, new[] { "whatever" });
        Assert.Equal("unreadable", r.Status);
        Assert.Null(r.MatchedIndex);
        Assert.False(r.ReadyToUnlock);
    }

    [Fact]
    public void MissingFileReportsUnreadable()
    {
        var r = Unlock.ProbeReadiness(Path.Combine(_dir, "nope.pdf"), new[] { "x" });
        Assert.Equal("unreadable", r.Status);
        Assert.Contains("not found", r.Message);
    }

    /// <summary>FileShare.Read (as UnlockTests.AFileHeldOpenElsewhereFailsBeforeAnythingMoves
    /// uses) is NOT enough here: two Read+Read-share handles are compatible on
    /// Windows, so File.ReadAllBytes still succeeds and that existing test's
    /// failure actually comes from the later archive-MOVE step, which a
    /// read-only probe never reaches. FileShare.None (measured 2026-08-08,
    /// same pattern as ConfigHardeningTests) blocks the read itself, which is
    /// the only kind of "in use" a probe that never moves anything can ever
    /// observe.</summary>
    [Fact]
    public void FileHeldOpenElsewhereReportsInUse()
    {
        var src = MakeEncrypted("held.pdf", "secret");
        using (File.Open(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Unlock.ProbeReadiness(src, new[] { "secret" });
            Assert.Equal("in_use", r.Status);
            Assert.Null(r.MatchedIndex);
            Assert.False(r.ReadyToUnlock);
        }
    }

    /// <summary>2026-08-08 fix round: ProbeReadiness now touches every page
    /// of the winning candidate (mirroring VerifyReadable) before declaring
    /// ready. All of this file's other "ready" fixtures are single-page, so
    /// this is the only test that runs that loop across more than one page.
    /// Honest scope: this guards against a bug in the loop itself (wrong
    /// bounds, an exception on healthy content) -- it does NOT prove the
    /// loop catches damaged content, because no fixture in this investigation
    /// could make a real one diverge (see Unlock.ProbeReadiness's doc
    /// comment); deleting the loop entirely would not make this test fail.</summary>
    [Fact]
    public void ReadyStillWorksAcrossManyHealthyPages()
    {
        var src = MakeEncryptedMultiPage("multi.pdf", "the-password", pageCount: 25);
        var r = Unlock.ProbeReadiness(src, new[] { "wrong-one", "the-password" });
        Assert.Equal("ready", r.Status);
        Assert.Equal(1, r.MatchedIndex);
        Assert.True(r.ReadyToUnlock);
    }

    /// <summary>MatchedIndex must be null for every status except "ready" --
    /// a loop that leaks the last-tried index on total failure would corrupt
    /// a caller that only checks MatchedIndex without also checking Status.</summary>
    [Fact]
    public void MatchedIndexIsNullWheneverNotReady()
    {
        Assert.Null(Unlock.ProbeReadiness(MakePlain("a.pdf"), new[] { "x" }).MatchedIndex);
        Assert.Null(Unlock.ProbeReadiness(MakeEncrypted("b.pdf", "yes"), new[] { "no1", "no2" }).MatchedIndex);
        Assert.Null(Unlock.ProbeReadiness(MakeDamaged("c.pdf"), new[] { "x" }).MatchedIndex);
    }
}
