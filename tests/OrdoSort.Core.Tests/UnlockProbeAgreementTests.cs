using PdfSharp.Pdf;

namespace OrdoSort.Core.Tests;

/// <summary>Step 5 of the readiness-probe plan (2026-08-08) -- the test that
/// matters. A probe that says "ready" and is then contradicted by the real
/// unlock is worse than no probe at all, because the user acts on it. Every
/// assertion here checks Unlock.ProbeReadiness's verdict against what
/// Unlock.UnlockPdf ACTUALLY does when called for real with the same source
/// and the same candidates -- never against this test's own expectation of
/// what "should" happen. UnlockPdf mutates the filesystem on success (moves
/// the original aside, archives it), so the real call always runs against a
/// disposable COPY of the fixture, never the file the probe itself examined
/// -- ProbeReadiness's own promise to write nothing is a separate class,
/// UnlockProbeWritesNothingTests.</summary>
public class UnlockProbeAgreementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "unlockprobeagree_" + Guid.NewGuid());
    public UnlockProbeAgreementTests() => Directory.CreateDirectory(_dir);
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

    private string MakeDamaged(string name)
    {
        var path = Path.Combine(_dir, name);
        var garbage = new byte[512];
        new Random(1234).NextBytes(garbage);
        File.WriteAllBytes(path, garbage);
        return path;
    }

    private int _copyCounter;

    /// <summary>A fresh, disposable copy of a fixture in its own subfolder, so
    /// a real UnlockPdf call can mutate it (move it aside, archive it) without
    /// touching the file ProbeReadiness examined or colliding with any other
    /// copy made in the same test.</summary>
    private string FreshCopy(string src)
    {
        var sub = Path.Combine(_dir, "verify" + _copyCounter++);
        Directory.CreateDirectory(sub);
        var dst = Path.Combine(sub, Path.GetFileName(src));
        File.Copy(src, dst);
        return dst;
    }

    [Fact]
    public void NotEncryptedAgreesWithRealUnlock()
    {
        var src = MakePlain("plain.pdf");
        var probe = Unlock.ProbeReadiness(src, new[] { "irrelevant" });
        Assert.Equal("not_encrypted", probe.Status);

        var real = Unlock.UnlockPdf(FreshCopy(src), "irrelevant", suffix: "_x");
        Assert.Equal("not_encrypted", real.Status);
    }

    [Fact]
    public void ReadyAtFirstCandidateAgreesWithRealUnlockSucceeding()
    {
        var src = MakeEncrypted("locked.pdf", "secret1");
        var candidates = new[] { "secret1", "secret2" };

        var probe = Unlock.ProbeReadiness(src, candidates);
        Assert.Equal("ready", probe.Status);
        Assert.Equal(0, probe.MatchedIndex);

        var real = Unlock.UnlockPdf(FreshCopy(src), candidates[probe.MatchedIndex!.Value], suffix: "_x");
        Assert.True(real.Ok);
    }

    [Fact]
    public void ReadyAtALaterCandidateAgreesWithRealUnlockSucceeding()
    {
        var src = MakeEncrypted("locked.pdf", "secret3");
        var candidates = new[] { "secret1", "secret2", "secret3" };

        var probe = Unlock.ProbeReadiness(src, candidates);
        Assert.Equal("ready", probe.Status);
        Assert.Equal(2, probe.MatchedIndex);

        // the candidate the probe actually picked really does unlock it for real
        var real = Unlock.UnlockPdf(FreshCopy(src), candidates[probe.MatchedIndex!.Value], suffix: "_x");
        Assert.True(real.Ok);

        // and the probe's silence about candidates 0 and 1 is not an
        // endorsement -- confirm independently that they are genuinely wrong,
        // not merely untried-and-actually-fine
        Assert.Equal("wrong_password", Unlock.UnlockPdf(FreshCopy(src), candidates[0], suffix: "_x").Status);
        Assert.Equal("wrong_password", Unlock.UnlockPdf(FreshCopy(src), candidates[1], suffix: "_x").Status);
    }

    [Fact]
    public void NeedsPasswordAgreesWithRealUnlockRejectingEveryCandidate()
    {
        var src = MakeEncrypted("locked.pdf", "the-actual-password");
        var candidates = new[] { "nope1", "nope2", "nope3" };

        var probe = Unlock.ProbeReadiness(src, candidates);
        Assert.Equal("needs_password", probe.Status);

        // wrong_password never mutates the source (UnlockTests.
        // EmptySuffixWrongPasswordLeavesOriginalIntact already pins that), so
        // the same original can be reused for each of these real calls
        // instead of needing a fresh copy per candidate.
        foreach (var candidate in candidates)
            Assert.Equal("wrong_password", Unlock.UnlockPdf(src, candidate, suffix: "_x").Status);
    }

    [Fact]
    public void EmptyCandidateListAgreesThatTheFileGenuinelyNeedsAPassword()
    {
        var src = MakeEncrypted("locked.pdf", "the-actual-password");
        var probe = Unlock.ProbeReadiness(src, Array.Empty<string>());
        Assert.Equal("needs_password", probe.Status);

        // nothing for "real Unlock" to try from an empty list, but the file
        // must still really be protected by a password none of them supply
        Assert.Equal("wrong_password",
            Unlock.UnlockPdf(src, "not-the-real-one", suffix: "_x").Status);
    }

    [Fact]
    public void DamagedFileAgreesWithRealUnlockErroringNotAskingForAPassword()
    {
        var src = MakeDamaged("damaged.pdf");
        var probe = Unlock.ProbeReadiness(src, new[] { "whatever" });
        Assert.Equal("unreadable", probe.Status);

        var real = Unlock.UnlockPdf(FreshCopy(src), "whatever", suffix: "_x");
        Assert.Equal("error", real.Status);
        // and specifically not mistaken for "locked by another program" --
        // that would be risk 3 leaking into the real call too
        Assert.DoesNotContain("another program", real.Message);
    }

    /// <summary>FileShare.Read alone would not block File.ReadAllBytes (two
    /// Read+Read handles are compatible on Windows) -- FileShare.None is what
    /// makes the read itself fail, for both the probe and the real call,
    /// which is exactly why this is the fixture that lets the two agree at
    /// the SAME operation (the shared read) rather than the probe reporting
    /// in_use for a lock it never actually experienced. Measured 2026-08-08.</summary>
    [Fact]
    public void InUseFileAgreesWithRealUnlockErroringAsInUse()
    {
        var src = MakeEncrypted("held.pdf", "secret");
        using (File.Open(src, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var probe = Unlock.ProbeReadiness(src, new[] { "secret" });
            Assert.Equal("in_use", probe.Status);

            var real = Unlock.UnlockPdf(src, "secret", suffix: "_x");
            Assert.Equal("error", real.Status);
            Assert.Contains("another program", real.Message);
        }
    }
}
