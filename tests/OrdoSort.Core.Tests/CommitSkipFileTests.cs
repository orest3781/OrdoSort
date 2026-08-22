namespace OrdoSort.Core.Tests;

/// <summary>2026-08 audit finding: Commit.SkipFile called MoveNeverOverwrite
/// with no catch for the private FileExistsRace type that CommitFile (:67)
/// and UndoAction (:123) both guard against. So a collision race in the tiny
/// window between Naming.BuildTarget picking a name and MoveNeverOverwrite's
/// own File.Exists guard firing a few microseconds later let FileExistsRace
/// escape OrdoSort.Core entirely, past ShellViewModel.OnSkipAsync's
/// CommitError/AuditError handlers, as an unhandled crash — even though no
/// document was lost, since the guard fires before the move ever touches
/// src. This class pins that SkipFile now produces the same actionable
/// CommitError its siblings do, using Commit.SkipRaceHookForTests (mirrors
/// UndoFailureTests' use of Commit.RaceHookForTests for the identical kind
/// of race in UndoAction) to reproduce the race deterministically instead of
/// relying on real thread timing.
///
/// Task-4 fix-round-1 review (2026-08-22, QC-03): this class calls
/// Commit.SkipFile, which — like every other caller of the shared private
/// Commit.MoveNeverOverwrite — now runs through the unconditional
/// Commit.SurvivingSourceHookForTests?.Invoke() that method fires after
/// every successful move. That seam is armed only by PipelineTests, but
/// this class had no [Collection] attribute at all, so xUnit could run it
/// concurrently with PipelineTests and let a stray, already-torn-down
/// closure fire mid-test here — a flaky, misleading failure with a wrong
/// exception type in a class that has nothing to do with that fix. Joining
/// UndoFailureTests.Name serializes this against every other class that
/// reaches MoveNeverOverwrite; see UndoRaceTestCollectionMembershipTests in
/// UndoFailureTests.cs for the meta-test that now guards the full set.</summary>
[Collection(UndoFailureTests.Name)]
public class CommitSkipFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoskip_" + Guid.NewGuid());
    private readonly string _inbox, _deferred;

    public CommitSkipFileTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        _deferred = Path.Combine(_root, "deferred");
        foreach (var d in new[] { _inbox, _deferred }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        Commit.SkipRaceHookForTests = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakePdf(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    [Fact]
    public void ARaceAtTheFinalMomentIsReportedAsTheSameActionableCommitErrorItsSiblingsProduce()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");

        // Recreate the name Naming.BuildTarget is about to pick, exactly
        // between its probe and MoveNeverOverwrite's own guard checking it —
        // the collision MoveNeverOverwrite's internal guard turns into the
        // private FileExistsRace this fix must no longer let escape.
        Commit.SkipRaceHookForTests = () =>
            File.WriteAllBytes(Path.Combine(_deferred, "20240115--111111.pdf"), new byte[] { 9 });

        var ex = Assert.Throws<CommitError>(() => Commit.SkipFile(src, _deferred));

        // Actionable, not just "some exception": the same message shape
        // MoveNeverOverwrite's guard produces for its other two callers.
        Assert.Contains("20240115--111111.pdf", ex.Message);
        Assert.Contains("appeared at the destination", ex.Message);
    }

    /// <summary>The observable outcome that actually matters: the source
    /// document is exactly where it started, not half-moved or lost, when
    /// the race guard fires. "The guard fires before the move" is the
    /// finding's own claim about why no document is lost — this proves it
    /// rather than trusting the comment.</summary>
    [Fact]
    public void ARaceAtTheFinalMomentLeavesTheSourceDocumentWhereItStarted()
    {
        var src = MakePdf(_inbox, "20240115--222222.pdf");
        var originalBytes = File.ReadAllBytes(src);
        var claimedPath = Path.Combine(_deferred, "20240115--222222.pdf");
        Commit.SkipRaceHookForTests = () => File.WriteAllBytes(claimedPath, new byte[] { 9 });

        Assert.Throws<CommitError>(() => Commit.SkipFile(src, _deferred));

        Assert.True(File.Exists(src));
        Assert.Equal(originalBytes, File.ReadAllBytes(src));
        // The thing that raced in ahead of it is untouched too — SkipFile
        // never overwrote it.
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(claimedPath));
    }

    /// <summary>Sanity check that the fix didn't change the happy path:
    /// no race, no hook set, the document lands in the deferred folder under
    /// its own name exactly as before.</summary>
    [Fact]
    public void WithNoRaceTheDocumentIsMovedToTheDeferredFolderUnderItsOwnName()
    {
        var src = MakePdf(_inbox, "20240115--333333.pdf");

        var outcome = Commit.SkipFile(src, _deferred);

        Assert.False(outcome.Vanished);
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(outcome.NewPath!));
        Assert.Equal(Path.Combine(_deferred, "20240115--333333.pdf"), outcome.NewPath);
    }
}
