using System.Reflection;
using System.Windows;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>A scheduler that runs everything on a REAL thread-pool thread
/// (via <see cref="TaskWorkScheduler"/>, not inline) — genuine async
/// suspension is the point — except the one call the test arms it for
/// (<see cref="ArmHold"/>): that call's delegate is only invoked once
/// <see cref="Release"/> is called, so the calling code sees a Task that
/// stays incomplete, and therefore <c>ShellViewModel._busy</c>, under the
/// test's control. This is the "scheduler you can block" seam this task's
/// brief calls out — the same shape as <c>ManualWorkScheduler</c> in
/// DebouncedProbeTests, adapted to hold up the ONE call the test cares
/// about (CommitCurrent) while letting every other scheduled call
/// (the initial scan, RefreshCompleterAsync, …) run normally.</summary>
file sealed class GateWorkScheduler : IWorkScheduler
{
    private readonly TaskWorkScheduler _inner = new();
    private readonly TaskCompletionSource _gate = new();
    private volatile bool _armed;

    /// <summary>The NEXT <see cref="Run{T}"/> call blocks on the gate
    /// instead of running immediately.</summary>
    public void ArmHold() => _armed = true;

    /// <summary>Let the held call's delegate actually run. Safe to call more
    /// than once (idempotent) so test cleanup can always call it without
    /// tracking whether the happy path already did.</summary>
    public void Release() => _gate.TrySetResult();

    public Task<T> Run<T>(Func<T> work)
    {
        if (_armed)
        {
            _armed = false;
            return _inner.Run(() => { _gate.Task.Wait(); return work(); });
        }
        return _inner.Run(work);
    }

    public Task Run(Action work) => _inner.Run(work);
}

/// <summary>Task 4 (2026-08-04 audit remediation, finding 1.1): File > Exit
/// and OS shutdown set <c>_reallyExit</c>, and MainWindow's Closing handler
/// used to return immediately on it — taking no busy check at all, while the
/// branch three lines below is commented "respects the mid-commit busy
/// guard". Closed then disposed the shell, and with it the History
/// connection, while CommitCurrent was still moving the file and writing its
/// row on a thread-pool thread. The document moved and the audit row
/// vanished, breaking the app's stated invariant that every move is in the
/// log.
///
/// PROOF STANDARD: this drives the REAL <c>MainWindow.Closing</c>/<c>Closed</c>
/// events, not a re-creation of their logic. That is reachable headlessly —
/// confirmed by the existing <c>TriageWindowDisposalTests</c> /
/// <c>CopyAndTerminologyTests</c> precedent in this fixture: a WPF
/// <c>Window</c> that is constructed but never <c>Show()</c>n still raises
/// both events from a plain <c>Close()</c> call (Loaded never fires, so
/// nothing here ever starts a real WebView2/Edge process either). The one
/// gap a real, unshown MainWindow leaves is that its constructor always
/// builds its own <see cref="ShellViewModel"/> with the production
/// <see cref="TaskWorkScheduler"/> — there is no constructor seam to hand it
/// a test scheduler. This test closes that gap with reflection: it swaps
/// the private <c>_scheduler</c> field for a <see cref="GateWorkScheduler"/>
/// AFTER construction, the same "reach into the real production object"
/// technique <c>CopyAndTerminologyTests.TheShellsUnexpectedErrorChannel…</c>
/// already uses (there, to read a private event's subscriber; here, to
/// replace a private field) — so the rest of the object is exactly what
/// ships, and only the one seam needed to hold a commit open is touched.
///
/// Sequence: start a session on a real MainWindow, arm the gate, fire the
/// route command (it blocks inside <c>_scheduler.Run</c>, before
/// <c>Session.CommitCurrent</c> — and therefore the file move and the
/// history write — ever starts, so <c>Shell.IsBusy</c> is true). Drive the
/// exit path via <c>MainWindow.OnExit</c> (private; invoked via reflection —
/// File > Exit and <c>SessionEnding</c> both just set <c>_reallyExit</c> then
/// call <c>Close()</c>, so OnExit is the exact shared tail of both real
/// triggers, not a re-implementation of either). Assert the close did NOT
/// complete while busy — with the fix reverted this fails immediately,
/// because the unguarded branch lets Close() finish synchronously right
/// there. Then release the gate, let the deferred close land, and assert the
/// history row for the document survived — a fresh <see cref="History"/>
/// connection, since the shell's own connection is disposed by that point.</summary>
[Collection(HighlightContrastTests.Name)]
public class ShutdownDuringCommitTests
{
    private readonly HighlightContrastFixture _fx;
    public ShutdownDuringCommitTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Polls by hopping onto the fixture's dispatcher thread for
    /// every check — the shell and its fields are mutated exclusively on
    /// that thread (see the _busy thread-affinity note in the report), so
    /// evaluating <paramref name="condition"/> from the xunit worker thread
    /// directly would be an unsynchronized cross-thread read.</summary>
    private void WaitFor(Func<bool> condition, string because, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var hit = false;
            _fx.Invoke(() => hit = condition());
            if (hit) return;
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(10);
        }
    }

    private static void SetScheduler(ShellViewModel shell, IWorkScheduler scheduler) =>
        typeof(ShellViewModel).GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(shell, scheduler);

    /// <summary>File > Exit's handler: private, so reflection is the only
    /// way in — but it is the exact shared tail SessionEnding also runs
    /// (see the class doc), so invoking it IS driving the real exit path,
    /// not standing in for it.</summary>
    private static void InvokeOnExit(MainWindow window) =>
        typeof(MainWindow).GetMethod("OnExit", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, new object?[] { window, new RoutedEventArgs() });

    [Fact]
    public void ExitingMidCommitWaitsForItAndKeepsTheAuditRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ordo_exit_" + Guid.NewGuid());
        var inbox = Path.Combine(dir, "inbox");
        var deferred = Path.Combine(dir, "deferred");
        var routeDir = Path.Combine(dir, "routed");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(deferred);
        Directory.CreateDirectory(routeDir);
        var cfgPath = Path.Combine(dir, "config.json");
        var historyDb = Path.Combine(dir, "history.sqlite");
        var cfg = new Config
        {
            Inbox = inbox,
            Deferred = deferred,
            HistoryDb = historyDb,
            Sort = "filename_asc",
            Routes = { new Route { Label = "Filed", Path = routeDir, Color = "#2e7d32" } },
        };
        const string sourceName = "20240115--111111.pdf";
        File.WriteAllText(Path.Combine(inbox, sourceName), "pdf");

        var gate = new GateWorkScheduler();
        MainWindow window = null!;
        ShellViewModel shell = null!;
        var closed = false;

        try
        {
            _fx.Invoke(() =>
            {
                window = new MainWindow(cfg, cfgPath)
                {
                    Left = -20000, Top = 0, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                shell = window.Shell;
                SetScheduler(shell, gate);
                window.Closed += (_, _) => closed = true;
                // Never Show()n: Loaded (which would call _pdf.InitAsync and
                // start a real Edge environment) never fires — same trick
                // TriageWindowDisposalTests uses — so StartProcessing can be
                // driven directly without Initialize() first.
                shell.StartProcessing();
            });

            WaitFor(() => shell.Screen == Screen.Processing, "the session should have started");

            _fx.Invoke(() =>
            {
                shell.TypedName = "SMITH JOHN";
                gate.ArmHold();
                shell.RouteCommand.Execute(0);
                // Everything up to the gated _scheduler.Run call (releasing
                // the never-initialized viewer) completes synchronously, so
                // by the time Execute() returns here, the commit is already
                // parked on the gate and _busy is already true — no poll
                // needed for this half.
                Assert.True(shell.IsBusy,
                    "the commit should already be in flight, blocked on the gate scheduler");
            });

            _fx.Invoke(() =>
            {
                InvokeOnExit(window);
                // THE ASSERTION: with the Step 4 fix reverted, the Closing
                // handler's _reallyExit branch returns without ever
                // consulting Shell.IsBusy, so Close() finishes synchronously
                // right here and `closed` is already true — this line is
                // where the reverted build fails, for exactly the reason
                // the audit describes: the exit path took no busy check.
                Assert.False(closed,
                    "the window closed while a commit was still in flight — File > Exit's " +
                    "Closing handler did not consult Shell.IsBusy before letting Close() land");
            });

            gate.Release();

            WaitFor(() => closed,
                "the deferred close (FinishClosingWhenIdle) should complete once the commit lands",
                timeoutMs: 15000);

            Assert.True(File.Exists(Path.Combine(routeDir, "20240115-SMITH JOHN-111111.pdf")),
                "the document should have moved once the held commit was released");

            // The shell's own History connection is disposed by now (Closed
            // ran Shell.Dispose()) — a fresh connection is the only way to
            // read back what actually landed.
            using var history = new History(historyDb);
            Assert.Equal(1, history.Count());
            var row = Assert.Single(history.Rows());
            Assert.Equal(sourceName, (string)row["original_name"]);
        }
        finally
        {
            gate.Release();   // idempotent — never leaves the gated thread stuck
            if (!closed)
                _fx.Invoke(() => window.Close());
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var i = 0; i < 10; i++)
            {
                try { Directory.Delete(dir, true); break; } catch { Thread.Sleep(50); }
            }
        }
    }
}
