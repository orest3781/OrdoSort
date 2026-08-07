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

        var stop = false;
        Exception? caught = null;
        long iterations = 0;

        var flipper = Task.Run(() =>
        {
            while (!stop)
            {
                posSetter.Invoke(session, toOne);
                posSetter.Invoke(session, toZero);
            }
        });
        var reader = Task.Run(() =>
        {
            while (!stop && caught is null)
            {
                try { _ = session.Current; }
                catch (Exception ex) { caught = ex; break; }
                iterations++;
            }
        });

        await Task.Delay(500);
        stop = true;
        await Task.WhenAll(flipper, reader);

        // Check the real defect first: a fast failure (a handful of reads
        // before the exception) is itself strong evidence of the race, not
        // a reason to report "budget too short" instead.
        Assert.Null(caught);
        Assert.True(iterations > 1000, $"only {iterations} reads happened — budget too short to mean anything");
    }
}
