using System.Reflection;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Session.Current — the queue-walk accessor read from the UI thread
/// while the commit thread advances Pos underneath it.</summary>
public class SessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sesstest_" + Guid.NewGuid());

    public SessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) when (attempt < 10)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Reproduces audit finding 2.6b: <c>Current</c> read
    /// <c>Pos</c> TWICE — <c>Pos &lt; Queue.Count ? Queue[Pos] : null</c>.
    /// CommitCurrent/SkipCurrent/LogVanished all do <c>Pos++</c> AFTER the
    /// document has already moved and the audit row already written, on
    /// whatever thread is running the commit (a thread-pool thread via
    /// ShellViewModel's IWorkScheduler); Current is read from the UI thread
    /// (progress line, preview, autocomplete). If Pos++ lands between
    /// Current's two reads of Pos, the first read sees Pos &lt; Queue.Count
    /// and the second sees Pos == Queue.Count — List&lt;T&gt;'s indexer then
    /// throws ArgumentOutOfRangeException (the brief calls this
    /// IndexOutOfRangeException informally; List&lt;T&gt; itself throws
    /// ArgumentOutOfRangeException, which this test also treats as the bug).
    ///
    /// WHAT THIS DOES NOT PROVE: it does not drive the race through the real
    /// CommitCurrent path. A real commit does file I/O (move, write, fsync)
    /// that takes milliseconds, while the gap between Current's two field
    /// reads is a handful of CPU instructions — nanoseconds. Hitting that
    /// window by firing real commits from a background thread while
    /// spinning on Current was tried and did not fail in bounded time; the
    /// window is real but the odds of a slow, I/O-bound writer landing in it
    /// are vanishingly small, which is itself evidence the interleaving is
    /// rare in practice, not that it's safe.
    ///
    /// WHAT THIS DOES PROVE: it isolates the exact same field-level access
    /// Current is exposed to. A background thread flips Pos back and forth
    /// across the Queue-length boundary via reflection on Session's own
    /// private Pos setter — the identical setter Pos++ compiles down to —
    /// as fast as it can, with no I/O in the way, while a foreground thread
    /// calls Current in a tight loop. That reproduces the interleaving
    /// Current's implementation permits, using the real getter and the real
    /// setter, without needing to win a race against disk I/O to do it. On
    /// the unfixed code this reliably throws within a fraction of a second
    /// (600,457 exceptions over 26.5M reads in 5 seconds in one local run,
    /// a 2.3% hit rate); the 500ms budget below is comfortably enough to
    /// catch at least one.</summary>
    [Fact]
    public async Task CurrentDoesNotThrowWhenPosIsMutatedBetweenItsTwoReads()
    {
        using var h = new History(Path.Combine(_dir, "h.sqlite"));
        var session = new Session(new Config(), h);
        session.Start(new[] { "only.pdf" });

        var posSetter = typeof(Session).GetProperty(nameof(Session.Pos))!
            .GetSetMethod(nonPublic: true)!;
        var toOne = new object[] { 1 };
        var toZero = new object[] { 0 };

        // Round-2 review, Minor 3: the stop signal used to be a captured
        // plain `bool`, with nothing stopping the JIT from hoisting the
        // flipper/reader loops' reads of it and spinning forever past the
        // 500ms budget — a suite-hang mode with no xUnit timeout to catch
        // it. CancellationTokenSource gives the same handshake proper
        // cross-thread visibility (IsCancellationRequested/Cancel are
        // documented thread-safe) at no cost to what's under test.
        using var cts = new CancellationTokenSource();
        Exception? caught = null;
        long iterations = 0;

        var flipper = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                posSetter.Invoke(session, toOne);
                posSetter.Invoke(session, toZero);
            }
        });
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested && caught is null)
            {
                try { _ = session.Current; }
                catch (Exception ex) { caught = ex; break; }
                iterations++;
            }
        });

        await Task.Delay(500);
        cts.Cancel();
        await Task.WhenAll(flipper, reader);

        // Check the real defect first: a fast failure (a handful of reads
        // before the exception) is itself strong evidence of the race, not
        // a reason to report "budget too short" instead.
        Assert.Null(caught);
        Assert.True(iterations > 1000, $"only {iterations} reads happened — budget too short to mean anything");
    }

    /// <summary>Round-1 review finding: <c>Current</c> reads <c>Queue</c>
    /// TWICE too — once for <c>.Count</c>, once for the indexer — the
    /// identical hazard class as Pos, in the identical expression.
    /// <c>Queue</c> is reassigned wholesale only by <c>Start()</c>; today
    /// every UI path that could call <c>Start()</c> while a commit is live
    /// is gated by ShellViewModel._busy, so there is no reachable crash
    /// path in the shipped app. That is an unenforced ViewModel-level
    /// convention, not a guarantee <c>Session</c> makes about itself.
    ///
    /// This uses the same technique as the Pos test above, and IS
    /// deterministic in the same sense: reflection on Session's own private
    /// Queue setter — the identical setter Start() calls — flips Queue
    /// between a 2-element list (where the fixed Pos=1 below is in range)
    /// and a 1-element list (where it is not), while Pos itself is held
    /// fixed via the same private-setter technique so only the Queue swap
    /// is under test. A foreground thread calls Current in a tight loop for
    /// a 500ms budget. This does not drive the race through a real
    /// Start()-during-a-commit interleaving (no such path exists today); it
    /// isolates the same field-level access Current's getter performs,
    /// exactly as the Pos test does for Pos.</summary>
    [Fact]
    public async Task CurrentDoesNotThrowWhenQueueIsMutatedBetweenItsTwoReads()
    {
        using var h = new History(Path.Combine(_dir, "h2.sqlite"));
        var session = new Session(new Config(), h);
        session.Start(new[] { "a.pdf", "b.pdf" });

        var posSetter = typeof(Session).GetProperty(nameof(Session.Pos))!
            .GetSetMethod(nonPublic: true)!;
        posSetter.Invoke(session, new object[] { 1 });   // fixed for the whole test

        var queueSetter = typeof(Session).GetProperty(nameof(Session.Queue))!
            .GetSetMethod(nonPublic: true)!;
        var bigEnough = new object[] { new List<string> { "a.pdf", "b.pdf" } };   // Pos=1 in range
        var tooSmall = new object[] { new List<string> { "a.pdf" } };             // Pos=1 out of range

        // Round-2 review, Minor 3 — same fix as the Pos test above: a
        // CancellationTokenSource instead of a captured plain `bool` so the
        // stop signal can't be hoisted out of the flipper/reader loops.
        using var cts = new CancellationTokenSource();
        Exception? caught = null;
        long iterations = 0;

        var flipper = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                queueSetter.Invoke(session, bigEnough);
                queueSetter.Invoke(session, tooSmall);
            }
        });
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested && caught is null)
            {
                try { _ = session.Current; }
                catch (Exception ex) { caught = ex; break; }
                iterations++;
            }
        });

        await Task.Delay(500);
        cts.Cancel();
        await Task.WhenAll(flipper, reader);

        Assert.Null(caught);
        Assert.True(iterations > 1000, $"only {iterations} reads happened — budget too short to mean anything");
    }
}
