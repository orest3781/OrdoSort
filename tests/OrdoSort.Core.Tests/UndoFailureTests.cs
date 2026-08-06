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
/// seam. PipelineTests.SessionUndoRoundTrip also drives Commit.UndoAction (the
/// method the hook lives inside), from a different, undeclared xUnit collection
/// — which by default runs concurrently with this one. This is the exact same
/// defect class as OrdoSort.Wpf.App._crashDir (see
/// OrdoSort.Wpf.Tests.CultureInvariantDatesTests's class doc), fixed the same
/// way: put both classes in one shared collection (<see cref="Name"/>) so
/// xUnit's own "never run two classes in the same collection concurrently"
/// rule serializes them. No lock was added around the seam in Commit.cs itself
/// — a lock would still leave two collections free to interleave their
/// set/invoke/clear sequences arbitrarily, just without a data race on the
/// field; only serializing the two collections closes the window outright, and
/// it costs nothing outside these two classes (unlike disabling parallelization
/// suite-wide). No ICollectionFixture is declared: unlike the WPF STA
/// Application fixture, nothing here needs to be built once and shared — each
/// class already builds and tears down its own isolated temp root per
/// test.</summary>
[Collection(Name)]
public class UndoFailureTests : IDisposable
{
    public const string Name = "Commit undo-race collection";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoundo_" + Guid.NewGuid());
    private readonly string _inbox, _dest, _deferred;

    public UndoFailureTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        _dest = Path.Combine(_root, "dest");
        _deferred = Path.Combine(_root, "deferred");
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
        var session = new Session(cfg, history);
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
/// the mechanism the fix actually relies on: both classes that touch the
/// static <see cref="Commit.RaceHookForTests"/> seam —
/// <see cref="UndoFailureTests"/> (sets it) and <see cref="PipelineTests"/>
/// (drives <see cref="Commit.UndoAction"/> from SessionUndoRoundTrip) — must
/// declare the SAME <c>[Collection(...)]</c> name, since that, not anything
/// about either class's own behavior, is what stops xUnit from ever running
/// them concurrently. Pre-fix, neither class had a [Collection] attribute at
/// all, so both names below were null and this failed; a future edit that
/// drops either attribute or typos the name fails it again.</summary>
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
}
