using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs background work inline until <see cref="Holding"/> is set;
/// then every move (and anything else) is parked until released, so a test
/// can look at the screen while documents are still on their way.</summary>
internal sealed class HeldMoves : IWorkScheduler
{
    private readonly ControlledWorkScheduler _held = new();
    public bool Holding { get; set; }
    public int Parked => _held.Queued;

    public Task<T> Run<T>(Func<T> work) => Holding ? _held.Run(work) : Task.FromResult(work());

    public Task Run(Action work)
    {
        if (Holding) return _held.Run(work);
        work();
        return Task.CompletedTask;
    }

    /// <summary>Stop holding and finish everything parked, oldest first.</summary>
    public void LetEverythingLand()
    {
        Holding = false;
        _held.ReleaseAll();
    }

    /// <summary>Finish only the oldest parked item, still holding the rest.</summary>
    public void LandOldest() => _held.ReleaseNext();

    /// <summary>Finish only the newest parked item, still holding the rest.</summary>
    public void LandNewest() => _held.ReleaseNewest();
}

/// <summary>The filing loop (spec 2026-09-29-filing-loop-design.md): a key
/// press shows the next page before the document it left has moved, the
/// moves land in order behind it, and a failure, Undo or Stop still finds
/// every document where it should be.</summary>
public class FilingPipelineTests
{
    private const string First = "20240115--111111.pdf";
    private const string Second = "20240116--222222.pdf";
    private const string Third = "20240117--333333.pdf";
    private const string Fourth = "20240118--444444.pdf";
    private const string Fifth = "20240119--555555.pdf";

    private static (ShellFixture Fx, HeldMoves Moves, FakeStage Stage) Started(params string[] files)
    {
        var moves = new HeldMoves();
        var stage = new FakeStage();
        var fx = new ShellFixture(scheduler: moves, stageFactory: () => stage);
        foreach (var f in files) fx.AddInboxFile(f);
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        return (fx, moves, stage);
    }

    private static bool InInbox(ShellFixture fx, string name) => File.Exists(Path.Combine(fx.Inbox, name));

    [Fact]
    public async Task TheNextPageIsShownBeforeTheDocumentLeftHasMoved()
    {
        var (fx, moves, _) = Started(First, Second);
        using var _fx = fx;
        fx.Shell.TypedName = "SMITH JOHN";
        moves.Holding = true;

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(Second, fx.Shell.CurrentFilename);
        Assert.Equal(FakeStage.CopyOf(Path.Combine(fx.Inbox, Second)), fx.Viewer.Shown[^1]);
        Assert.Equal("", fx.Shell.TypedName);
        Assert.True(InInbox(fx, First), "the move is still on its way");

        moves.LetEverythingLand();

        Assert.False(InInbox(fx, First));
        Assert.Contains("SMITH JOHN", Path.GetFileName(Assert.Single(Directory.GetFiles(fx.RouteDir))));
        Assert.Equal(1, fx.Shell.Session.Filed);
    }

    [Fact]
    public async Task MovesLandInTheOrderTheKeysWerePressed()
    {
        var (fx, moves, _) = Started(First, Second, Third);
        using var _fx = fx;
        moves.Holding = true;

        fx.Shell.TypedName = "ONE";
        await fx.Shell.OnRouteAsync(0);
        fx.Shell.TypedName = "TWO";
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Third, fx.Shell.CurrentFilename);

        moves.LetEverythingLand();

        var filed = Directory.GetFiles(fx.RouteDir).Select(Path.GetFileName).ToList();
        Assert.Contains(filed, n => n!.Contains("ONE") && n.Contains("111111"));
        Assert.Contains(filed, n => n!.Contains("TWO") && n.Contains("222222"));
        // newest first: the history log is in the order the keys were pressed
        Assert.Equal(new object[] { "TWO", "ONE" }, fx.Shell.History.Rows(10).Select(r => r["name_entered"]));
    }

    /// <summary>At most three documents can be left on screen before their
    /// moves land; on a dead share the fourth press waits, rather than the
    /// screen running far ahead of what is actually filed.</summary>
    [Fact]
    public async Task TheFourthPressWaitsForTheOldestMove()
    {
        var (fx, moves, _) = Started(First, Second, Third, Fourth, Fifth);
        using var _fx = fx;
        moves.Holding = true;

        for (var i = 0; i < ShellViewModel.MaxInFlight; i++) await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Fourth, fx.Shell.CurrentFilename);

        var fourth = fx.Shell.OnRouteAsync(0);
        Assert.False(fourth.IsCompleted);
        Assert.Equal(Fourth, fx.Shell.CurrentFilename);

        moves.LetEverythingLand();
        await fourth;

        Assert.Equal(Fifth, fx.Shell.CurrentFilename);
        Assert.Equal(4, Directory.GetFiles(fx.RouteDir).Length);
    }

    /// <summary>A move that fails brings its document back on screen with the
    /// name typed for it. The press made behind it, for a document the
    /// session has not reached, is dropped rather than filed out of order,
    /// and its name comes back too when that document shows again.</summary>
    [Fact]
    public async Task AFailedMoveBringsItsDocumentBackWithItsName()
    {
        var (fx, moves, _) = Started(First, Second, Third);
        using var _fx = fx;
        moves.Holding = true;
        fx.Shell.TypedName = "ONE";
        await fx.Shell.OnRouteAsync(0);
        fx.Shell.TypedName = "TWO";
        await fx.Shell.OnRouteAsync(0);
        fx.Shell.TypedName = "THR";   // part-typed on the document now on screen
        Directory.Delete(fx.RouteDir);   // the destination drops off the network

        moves.LetEverythingLand();

        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Equal("OrdoSort — couldn't file it", warning.Title);
        Assert.Equal(First, fx.Shell.CurrentFilename);
        Assert.Equal("ONE", fx.Shell.TypedName);
        Assert.True(InInbox(fx, First) && InInbox(fx, Second) && InInbox(fx, Third));
        Assert.Equal(0, fx.Shell.Session.Filed);

        Directory.CreateDirectory(fx.RouteDir);   // back
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Second, fx.Shell.CurrentFilename);
        Assert.Equal("TWO", fx.Shell.TypedName);
        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Third, fx.Shell.CurrentFilename);
        Assert.Equal("THR", fx.Shell.TypedName);
    }

    /// <summary>A press that was waiting for room when a move failed was
    /// made for a document no longer on screen: it files nothing.</summary>
    [Fact]
    public async Task APressWaitingWhenAMoveFailsFilesNothing()
    {
        var (fx, moves, _) = Started(First, Second, Third, Fourth, Fifth);
        using var _fx = fx;
        moves.Holding = true;
        for (var i = 0; i < ShellViewModel.MaxInFlight; i++) await fx.Shell.OnRouteAsync(0);
        var waiting = fx.Shell.OnRouteAsync(0);
        Directory.Delete(fx.RouteDir);

        moves.LetEverythingLand();
        await waiting;

        Assert.Equal(First, fx.Shell.CurrentFilename);
        Assert.Equal(0, fx.Shell.Session.Filed);
        Assert.Equal(5, Directory.GetFiles(fx.Inbox).Length);
        Assert.Single(fx.Dialogs.Warnings);
    }

    /// <summary>A name the move would refuse is refused on the document it
    /// belongs to: that press waits for its move, as every press used to.</summary>
    [Fact]
    public async Task ANameTheMoveWouldRefuseIsFiledInPlace()
    {
        var (fx, moves, _) = Started(First, Second);
        using var _fx = fx;
        fx.Shell.TypedName = "A:B";
        moves.Holding = true;

        var press = fx.Shell.OnRouteAsync(0);
        Assert.Equal(First, fx.Shell.CurrentFilename);   // still on it while it is tried
        moves.LetEverythingLand();
        await press;

        Assert.Single(fx.Dialogs.Warnings);
        Assert.Equal(First, fx.Shell.CurrentFilename);
        Assert.Equal("A:B", fx.Shell.TypedName);
    }

    [Fact]
    public async Task UndoWaitsForTheMoveItUndoes()
    {
        var (fx, moves, _) = Started(First, Second);
        using var _fx = fx;
        moves.Holding = true;
        await fx.Shell.OnRouteAsync(0);
        Assert.True(fx.Shell.CanUndo, "a press still landing can be undone");

        var undo = fx.Shell.OnUndoAsync();
        Assert.False(undo.IsCompleted);
        moves.LetEverythingLand();
        await undo;

        Assert.True(InInbox(fx, First));
        Assert.Empty(Directory.GetFiles(fx.RouteDir));
        Assert.Equal(First, fx.Shell.CurrentFilename);
        Assert.Equal(0, fx.Shell.Session.Filed);
    }

    /// <summary>Undo after a move that failed does not reach back and undo
    /// the filing before it: the failure has already put the document back.</summary>
    [Fact]
    public async Task UndoAfterAFailedMoveUndoesNothingElse()
    {
        var (fx, moves, _) = Started(First, Second, Third);
        using var _fx = fx;
        await fx.Shell.OnRouteAsync(0);           // lands
        moves.Holding = true;
        await fx.Shell.OnRouteAsync(0);           // will fail
        Directory.Delete(fx.RouteDir, recursive: true);

        var undo = fx.Shell.OnUndoAsync();
        moves.LetEverythingLand();
        await undo;

        Assert.Equal(1, fx.Shell.Session.Filed);   // the first filing stands
        Assert.Equal(Second, fx.Shell.CurrentFilename);
    }

    [Fact]
    public async Task StopLetsEveryPressLandFirst()
    {
        var (fx, moves, _) = Started(First, Second, Third);
        using var _fx = fx;
        moves.Holding = true;
        await fx.Shell.OnRouteAsync(0);
        await fx.Shell.OnRouteAsync(0);

        fx.Shell.StopSession();
        Assert.Equal(Screen.Processing, fx.Shell.Screen);
        Assert.True(fx.Shell.IsBusy);
        await fx.Shell.OnRouteAsync(0);           // no new presses once Stop is asked for
        Assert.Equal(Third, fx.Shell.CurrentFilename);

        moves.LetEverythingLand();

        Assert.Equal(Screen.Ready, fx.Shell.Screen);
        Assert.Equal(2, Directory.GetFiles(fx.RouteDir).Length);
        Assert.True(InInbox(fx, Third));
        Assert.False(fx.Shell.IsBusy);
    }

    [Fact]
    public async Task TheLastPressShowsDoneOnceItHasLanded()
    {
        var (fx, moves, _) = Started(First);
        using var _fx = fx;
        moves.Holding = true;

        await fx.Shell.OnRouteAsync(0);
        Assert.Equal(Screen.Processing, fx.Shell.Screen);
        Assert.Equal("", fx.Shell.CurrentFilename);

        moves.LetEverythingLand();

        Assert.Equal(Screen.Done, fx.Shell.Screen);
        Assert.Equal(1, fx.Shell.Session.Filed);
    }

    [Fact]
    public async Task TheJustUsedNameIsSuggestedBeforeTheHistoryCatchesUp()
    {
        var (fx, moves, _) = Started(First, Second);
        using var _fx = fx;
        moves.Holding = true;
        fx.Shell.TypedName = "ZEBRA ONE";
        await fx.Shell.OnRouteAsync(0);

        fx.Shell.TypedName = "ZEB";

        Assert.Contains("ZEBRA ONE", fx.Shell.Suggestions);
        moves.LetEverythingLand();
    }

    /// <summary>A file arriving while the last move lands joins the session,
    /// but nothing can be filed until it is on screen with its own name box:
    /// a press on the empty pane files nothing.</summary>
    [Fact]
    public async Task AnArrivalWhileTheLastMoveLandsIsShownBeforeItCanBeFiled()
    {
        var (fx, moves, _) = Started(First);
        using var _fx = fx;
        moves.Holding = true;
        await fx.Shell.OnRouteAsync(0);                 // the pane is empty, the move parked
        fx.AddInboxFile(Second);
        fx.Shell.OnFolderActivity();
        moves.LandNewest();                             // the rescan lands before the move
        Assert.Equal(2, fx.Shell.Session.Total);

        await fx.Shell.OnRouteAsync(0);                 // nothing is on screen to file
        moves.LetEverythingLand();

        Assert.Equal(Screen.Processing, fx.Shell.Screen);
        Assert.Equal(Second, fx.Shell.CurrentFilename);
        Assert.True(InInbox(fx, Second));
        Assert.Single(Directory.GetFiles(fx.RouteDir));
    }

    /// <summary>Closing with presses still queued: the one moving lands and
    /// is logged, the ones not yet started stay in the inbox, and crash.log
    /// says how many (never which).</summary>
    [Fact]
    public async Task ClosingDropsOnlyThePressesWhoseMoveHasNotStarted()
    {
        var (fx, moves, _) = Started(First, Second, Third);
        using var _fx = fx;
        var logged = new List<Exception>();
        fx.Shell.UnexpectedError += logged.Add;
        moves.Holding = true;
        await fx.Shell.OnRouteAsync(0);   // moving (parked in the scheduler)
        await fx.Shell.OnRouteAsync(0);   // queued behind it

        fx.Shell.Dispose();
        moves.LetEverythingLand();

        Assert.False(InInbox(fx, First));
        Assert.True(InInbox(fx, Second));
        var note = Assert.Single(logged.OfType<OperationCanceledException>());
        Assert.Contains("1 pressed document not yet filed", note.Message);
        Assert.DoesNotContain("222222", note.Message);
    }

    // ------------------------------------------------------------ read-ahead

    /// <summary>Shown from its local copy, the document left is not the file
    /// Edge has open, so its move does not wait for Edge to let go.</summary>
    [Fact]
    public async Task ADocumentShownFromItsLocalCopyMovesWithoutWaitingForTheViewer()
    {
        var (fx, _, _) = Started(First, Second);
        using var _fx = fx;
        Assert.Equal(FakeStage.CopyOf(Path.Combine(fx.Inbox, First)), fx.Viewer.Shown[^1]);

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(0, fx.Viewer.Releases);
        Assert.Single(Directory.GetFiles(fx.RouteDir));
    }

    [Fact]
    public async Task WithNoLocalCopyTheInboxFileIsShownAndLetGoBeforeItMoves()
    {
        var moves = new HeldMoves();
        var stage = new FakeStage();
        using var fx = new ShellFixture(scheduler: moves, stageFactory: () => stage);
        var first = fx.AddInboxFile(First);
        fx.AddInboxFile(Second);
        stage.NoCopy.Add(first);
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        Assert.Equal(first, fx.Viewer.Shown[^1]);

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(1, fx.Viewer.Releases);
        Assert.Single(Directory.GetFiles(fx.RouteDir));
    }

    [Fact]
    public async Task TheReadAheadIsToldTheDocumentOnScreenAndTheNextTwo()
    {
        var (fx, _, stage) = Started(First, Second, Third, Fourth, Fifth);
        using var _fx = fx;
        string In(string name) => Path.Combine(fx.Inbox, name);
        Assert.Equal(new[] { In(First), In(Second), In(Third) }, stage.Wants[^1]);

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(In(Second), stage.Wants[^1][0]);
        Assert.Contains(In(Third), stage.Wants[^1]);
        Assert.Contains(In(Fourth), stage.Wants[^1]);
        // the one left is on its way out: kept by LetGo for its move, not copied again
        Assert.DoesNotContain(In(First), stage.Wants[^1]);
        Assert.Contains(In(First), stage.LetGo);
        Assert.DoesNotContain(In(Fifth), stage.Wants[^1]);
    }

    [Fact]
    public async Task TheReadAheadLetsGoOfADocumentBeforeItMoves()
    {
        var (fx, _, stage) = Started(First, Second);
        using var _fx = fx;
        var first = Path.Combine(fx.Inbox, First);

        await fx.Shell.OnRouteAsync(0);

        Assert.Contains(first, stage.LetGo);
        Assert.Contains(first, stage.Discarded);   // its copy goes once it has moved
    }

    /// <summary>What is filed must be what the user looked at: a document
    /// replaced in the inbox after its copy was shown is not filed under the
    /// name typed for the old one.</summary>
    [Fact]
    public async Task ADocumentChangedSinceItWasShownIsNotFiled()
    {
        var (fx, _, stage) = Started(First, Second);
        using var _fx = fx;
        fx.Shell.TypedName = "SMITH JOHN";
        stage.Changed.Add(Path.Combine(fx.Inbox, First));

        await fx.Shell.OnRouteAsync(0);

        Assert.Empty(Directory.GetFiles(fx.RouteDir));
        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Equal(ShellViewModel.ChangedSinceShownMessage, warning.Message);
        Assert.Equal(First, fx.Shell.CurrentFilename);
        Assert.Equal("SMITH JOHN", fx.Shell.TypedName);
    }

    [Fact]
    public async Task TheReadAheadEndsWithTheSession()
    {
        var (fx, _, stage) = Started(First);
        using var _fx = fx;

        await fx.Shell.OnRouteAsync(0);

        Assert.Equal(Screen.Done, fx.Shell.Screen);
        Assert.True(stage.Disposed);
    }

    [Fact]
    public void StoppingEndsTheReadAhead()
    {
        var (fx, _, stage) = Started(First, Second);
        using var _fx = fx;

        fx.Shell.StopSession();

        Assert.True(stage.Disposed);
    }

    /// <summary>The read-ahead is a speed-up: one that throws leaves the
    /// document shown straight from the inbox, and filing carries on.</summary>
    [Fact]
    public async Task AReadAheadThatFailsFallsBackToTheInboxFile()
    {
        using var fx = new ShellFixture(stageFactory: () => throw new IOException("disk full"));
        var first = fx.AddInboxFile(First);
        fx.AddInboxFile(Second);
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Assert.Equal(first, fx.Viewer.Shown[^1]);
        var logged = 0;
        fx.Shell.UnexpectedError += _ => logged++;
        await fx.Shell.OnRouteAsync(0);
        Assert.Single(Directory.GetFiles(fx.RouteDir));
        Assert.Empty(fx.Dialogs.Warnings);
        Assert.Equal(0, logged);   // said once, at the first document, not once per document
    }

    // ------------------------------------------------------------ timing

    [Fact]
    public async Task TimingWritesOneLinePerFilingAndNeverNamesADocument()
    {
        var log = FilingTimingLog.PathInUse;
        var before = File.Exists(log) ? File.ReadAllLines(log).Length : 0;
        using var fx = new ShellFixture(tweak: c => c.Timing = true);
        fx.AddInboxFile(First);
        fx.AddInboxFile(Second);
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        fx.Shell.TypedName = "SECRET NAME";

        await fx.Shell.OnRouteAsync(0);

        var lines = File.ReadAllLines(log).Skip(before).ToList();
        var line = Assert.Single(lines, l => l.Contains("\tfiled\t"));
        Assert.Contains("next page", line);
        Assert.Contains("move", line);
        Assert.DoesNotContain("111111", line);
        Assert.DoesNotContain("SECRET", line);
    }
}
