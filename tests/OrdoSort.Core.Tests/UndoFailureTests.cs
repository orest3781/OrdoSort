using System.Reflection;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Commit.UndoAction guards three ways an undo can fail after the
/// filing already happened: the filed copy is gone, the original name has
/// been reused, or the inbox folder itself vanished. In every case the filed
/// copy must stay exactly where it was — that is UndoAction's documented
/// promise. And because Session.UndoLast() calls UndoAction FIRST and only
/// mutates state (the undo stack, Filed/Skipped, Pos, MarkReverted) once it
/// returns without throwing, a failed undo must leave every one of those
/// exactly as it found them — most importantly, the history row must NOT be
/// marked reverted while the document is still filed. That surviving-state
/// property, not just "it throws", is what these tests pin.
///
/// Final-review M (2026-08-05): RaceAtTheFinalMomentIsReportedAsTheSameActionable
/// CommitError below sets the static, unsynchronized <see cref="Commit.RaceHookForTests"/>
/// seam. PipelineTests.SessionUndoRoundTrip and AuditFailureTests.
/// UndoStillWorksAfterAnUnrecordedCommit also drive Commit.UndoAction (the
/// method the hook lives inside), each from its own different, undeclared
/// xUnit collection — which by default runs concurrently with this one. A
/// grep for <c>UndoLast(</c>/<c>Commit.UndoAction(</c>/<c>RaceHookForTests</c>
/// across tests/ confirms those three classes — PipelineTests,
/// AuditFailureTests, and this one — are the complete set that touches this
/// seam. This is the exact same defect class as OrdoSort.Wpf.App._crashDir
/// (see OrdoSort.Wpf.Tests.CultureInvariantDatesTests's class doc), fixed the
/// same way: put all three classes in one shared collection (<see cref="Name"/>)
/// so xUnit's own "never run two classes in the same collection concurrently"
/// rule serializes them. No lock was added around the seam in Commit.cs itself
/// — a lock would still leave separate collections free to interleave their
/// set/invoke/clear sequences arbitrarily, just without a data race on the
/// field; only serializing the collections closes the window outright, and it
/// costs nothing outside these three classes (unlike disabling
/// parallelization suite-wide). No ICollectionFixture is declared: unlike the
/// WPF STA Application fixture, nothing here needs to be built once and
/// shared — each class already builds and tears down its own isolated temp
/// root per test.</summary>
[Collection(Name)]
public class UndoFailureTests : IDisposable
{
    public const string Name = "Commit undo-race collection";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoundo_" + Guid.NewGuid());
    private readonly string _inbox, _dest, _deferred, _cfgPath;

    public UndoFailureTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        _dest = Path.Combine(_root, "dest");
        _deferred = Path.Combine(_root, "deferred");
        _cfgPath = Path.Combine(_root, "config.json");   // Deferred above is already absolute, so this base is never actually consulted
        foreach (var d in new[] { _inbox, _dest, _deferred }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        Commit.RaceHookForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var a = 0; ; a++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (IOException) when (a < 10) { Thread.Sleep(50); }
        }
    }

    private string MakePdf(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    /// <summary>File names present under <paramref name="dir"/> (recursive),
    /// sorted — used to assert "nothing else moved or vanished" by comparing
    /// a directory's contents before and after a failed undo.</summary>
    private static string[] Listing(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    // ---------------------------------------------------------------------
    // Commit.UndoAction, each of the three branches in isolation
    // ---------------------------------------------------------------------

    [Fact]
    public void FiledCopyGoneRaisesCommitErrorAndTouchesNothing()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        File.Delete(filedPath);   // the filed copy disappears behind the app's back

        var inboxBefore = Listing(_inbox);
        var destBefore = Listing(_dest);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(filedPath), ex.Message);
        Assert.Contains("no longer there", ex.Message);
        Assert.Equal(inboxBefore, Listing(_inbox));
        Assert.Equal(destBefore, Listing(_dest));
    }

    [Fact]
    public void OriginalNameReusedRaisesCommitErrorAndLeavesBothCopiesAlone()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        File.WriteAllBytes(src, new byte[] { 9, 9 });   // a new file lands at the old name

        var inboxBefore = Listing(_inbox);
        var destBefore = Listing(_dest);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(src), ex.Message);
        Assert.Contains("already exists again", ex.Message);
        Assert.True(File.Exists(filedPath));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(src));   // untouched, not overwritten
        Assert.Equal(inboxBefore, Listing(_inbox));
        Assert.Equal(destBefore, Listing(_dest));
    }

    [Fact]
    public void InboxFolderGoneRaisesCommitErrorAndLeavesFiledCopyAlone()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        Directory.Delete(_inbox, true);   // the whole inbox folder is gone

        var destBefore = Listing(_dest);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains("inbox folder is gone", ex.Message);
        Assert.True(File.Exists(filedPath));
        Assert.Equal(destBefore, Listing(_dest));
        Assert.False(Directory.Exists(_inbox));
    }

    // ---------------------------------------------------------------------
    // Session.UndoLast(): a failed undo must leave session state untouched
    // ---------------------------------------------------------------------

    private (Session Session, History History, long RowId) CommitOneForUndo(
        out string filedPath, out string src)
    {
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred };
        var history = new History(Path.Combine(_root, "h_" + Guid.NewGuid() + ".sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        src = MakePdf(_inbox, "20240115--111111.pdf");
        session.Start(new[] { src });
        var outcome = session.CommitCurrent("SMITH", new Route { Label = "R", Path = _dest });
        filedPath = outcome.NewPath!;
        return (session, history, session.RowIds[0]);
    }

    private static void AssertRowNotReverted(History history, long rowId)
    {
        var row = history.Rows().Single(r => Convert.ToInt64(r["id"]) == rowId);
        Assert.Equal(0L, Convert.ToInt64(row["reverted"]));
    }

    [Fact]
    public void FailedUndoFromMissingFiledCopyLeavesSessionStateAlone()
    {
        var (session, history, rowId) = CommitOneForUndo(out var filedPath, out _);
        using var _h = history;
        File.Delete(filedPath);

        Assert.Throws<CommitError>(() => session.UndoLast());

        Assert.True(session.CanUndo);        // the undo entry is still there
        Assert.Equal(1, session.Filed);      // counters unchanged
        Assert.Equal(0, session.Skipped);
        Assert.Equal(1, session.Pos);        // queue position unchanged
        AssertRowNotReverted(history, rowId);   // the log must not lie
    }

    [Fact]
    public void FailedUndoFromReusedOriginalNameLeavesSessionStateAlone()
    {
        var (session, history, rowId) = CommitOneForUndo(out _, out var src);
        using var _h = history;
        File.WriteAllBytes(src, new byte[] { 7 });

        Assert.Throws<CommitError>(() => session.UndoLast());

        Assert.True(session.CanUndo);
        Assert.Equal(1, session.Filed);
        Assert.Equal(0, session.Skipped);
        Assert.Equal(1, session.Pos);
        AssertRowNotReverted(history, rowId);
    }

    [Fact]
    public void FailedUndoFromVanishedInboxLeavesSessionStateAlone()
    {
        var (session, history, rowId) = CommitOneForUndo(out _, out _);
        using var _h = history;
        Directory.Delete(_inbox, true);

        Assert.Throws<CommitError>(() => session.UndoLast());

        Assert.True(session.CanUndo);
        Assert.Equal(1, session.Filed);
        Assert.Equal(0, session.Skipped);
        Assert.Equal(1, session.Pos);
        AssertRowNotReverted(history, rowId);
    }

    // ---------------------------------------------------------------------
    // Step 5: the FileExistsRace leak. MoveNeverOverwrite throws a private
    // nested FileExistsRace when the target appears at the very last
    // instant. CommitFile catches and retries it; UndoAction did not catch
    // it at all, so a race in the tiny window between the ":94" guard and
    // the move would let a private type escape OrdoSort.Core, and the user
    // would see the generic "didn't finish" dialog instead of the same
    // actionable "already exists again" message its sibling guard produces
    // two lines earlier. Commit.RaceHookForTests is a test-only seam (see
    // Commit.cs) that deterministically reproduces the race by recreating
    // the file in that exact window, rather than relying on real thread
    // timing, which this codebase has documented problems trusting.
    // ---------------------------------------------------------------------

    [Fact]
    public void RaceAtTheFinalMomentIsReportedAsTheSameActionableCommitError()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;

        // Recreate `src` exactly between UndoAction's own File.Exists(originalPath)
        // guard and its call to MoveNeverOverwrite — the collision
        // MoveNeverOverwrite's internal guard turns into FileExistsRace.
        Commit.RaceHookForTests = () => File.WriteAllBytes(src, new byte[] { 1 });

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(src), ex.Message);
        Assert.Contains("already exists again", ex.Message);
        Assert.True(File.Exists(filedPath));   // the filed copy never moved
    }
}

/// <summary>Declares the shared collection <see cref="UndoFailureTests.Name"/>
/// names. Deliberately holds no <c>ICollectionFixture</c> — see UndoFailureTests's
/// class doc for why nothing here needs one.</summary>
[CollectionDefinition(UndoFailureTests.Name)]
public class UndoRaceCollection
{
}

/// <summary>Mirrors OrdoSort.Wpf.Tests.CrashDirTestCollectionMembershipTests:
/// this fix is xUnit collection membership, not a code path, so a
/// timing-based test would either always pass or be flaky, never a reliable
/// "fails without the fix" (a scheduler interleaving on the order of
/// microseconds can't be forced to reproduce on demand). This instead pins
/// the mechanism the fix actually relies on: every class that touches the
/// static <see cref="Commit.RaceHookForTests"/> seam —
/// <see cref="UndoFailureTests"/> (sets it), <see cref="PipelineTests"/>
/// (drives <see cref="Commit.UndoAction"/> from SessionUndoRoundTrip), and
/// <see cref="AuditFailureTests"/> (drives it from
/// UndoStillWorksAfterAnUnrecordedCommit) — must declare the SAME
/// <c>[Collection(...)]</c> name, since that, not anything about any one
/// class's own behavior, is what stops xUnit from ever running them
/// concurrently. Checked pairwise (not just "all equal one fixed string") so
/// that dropping the attribute from ANY one of the three — including
/// AuditFailureTests, the one the first fix pass missed — fails this test.
/// Pre-fix, none of the three classes had a [Collection] attribute at all, so
/// every name below was null and this failed; a future edit that drops any
/// one attribute or typos its name fails it again.
///
/// Task-4 fix-round-1 review (2026-08-22, QC-03) widened what this class
/// guards. <see cref="Commit.SurvivingSourceHookForTests"/> (the QC-03 test
/// seam) sits inside the shared private <c>Commit.MoveNeverOverwrite</c>
/// itself, not one call site, so it fires unconditionally for every caller —
/// <c>CommitFile</c>, <c>SkipFile</c>, and <c>UndoAction</c> alike — whether
/// or not that caller's own test ever touches a hook. That means the
/// invariant isn't "every class that sets a hook shares this collection" but
/// "every class that reaches <c>MoveNeverOverwrite</c> at all does" — a
/// stray, already-torn-down closure from a finished <see cref="PipelineTests"/>
/// test can otherwise fire mid-move in a completely unrelated class and throw
/// a wrong exception type into its assertion. A grep across tests/ for
/// <c>CommitFile(</c>/<c>SkipFile(</c>/<c>UndoAction(</c>/<c>CommitCurrent(</c>/
/// <c>SkipCurrent(</c>/<c>UndoLast(</c> confirms five classes reach it:
/// the original three above, plus <see cref="CommitSkipFileTests"/> and
/// <see cref="SessionDeferredResolutionTests"/> (both call
/// <c>Commit.SkipFile</c>/<c>Session.SkipCurrent</c> and, pre-fix, had no
/// <c>[Collection]</c> attribute at all). Same pairwise-anchored-to-
/// UndoFailureTests shape as the original three, for the same reason.</summary>
public class UndoRaceTestCollectionMembershipTests
{
    // Reads the [Collection("...")] name via CustomAttributeData's
    // constructor argument rather than CollectionAttribute.Name — robust
    // against exactly which xunit.core build resolves at compile time, and
    // it's the constructor argument, not a settled property, that xUnit's
    // own discovery reads to group classes into one collection.
    private static string? CollectionNameOf(Type t) =>
        t.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Xunit.CollectionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    [Fact]
    public void UndoFailureTestsSharesPipelineTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var pipelineCollection = CollectionNameOf(typeof(PipelineTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(pipelineCollection, undoCollection);
    }

    [Fact]
    public void AuditFailureTestsSharesUndoFailureTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var auditCollection = CollectionNameOf(typeof(AuditFailureTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(undoCollection, auditCollection);
    }

    [Fact]
    public void AuditFailureTestsSharesPipelineTestsCollection()
    {
        var pipelineCollection = CollectionNameOf(typeof(PipelineTests));
        var auditCollection = CollectionNameOf(typeof(AuditFailureTests));

        Assert.NotNull(pipelineCollection);
        Assert.Equal(pipelineCollection, auditCollection);
    }

    [Fact]
    public void CommitSkipFileTestsSharesUndoFailureTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var commitSkipCollection = CollectionNameOf(typeof(CommitSkipFileTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(undoCollection, commitSkipCollection);
    }

    [Fact]
    public void SessionDeferredResolutionTestsSharesUndoFailureTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var sessionDeferredCollection = CollectionNameOf(typeof(SessionDeferredResolutionTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(undoCollection, sessionDeferredCollection);
    }
}
