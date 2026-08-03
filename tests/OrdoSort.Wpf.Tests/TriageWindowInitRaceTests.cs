using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>QC follow-up to Task 5's disposal fix (2026-08-02/03):
/// TriageWindow.Closed now disposes Viewer (see TriageWindowDisposalTests),
/// but nothing checked whether the window had already closed by the time the
/// Loaded continuation (InitAsync → warn-on-failure → ShowCurrentAsync)
/// resumed. WebView2 cold start is not instant, and "Stop reviewing" is
/// IsCancel="True", so Escape can close the window while that continuation
/// is still mid-flight. Pre-fix (before disposal existed) this was harmless;
/// disposal is what made it reachable: on success the continuation would
/// navigate an already-disposed Viewer, on failure it would show a
/// MessageBox owned an already-closed window (WPF throws for both).
///
/// TriageWindow.InitAndShowAsync (extracted from the constructor's Loaded
/// lambda specifically for this) takes the init call as a
/// <c>Func&lt;Task&lt;bool&gt;&gt;</c> rather than calling
/// <c>_pdf.InitAsync</c> directly — the real method wraps a genuine, slow,
/// untimeable WebView2/Edge startup that this project's other tests already
/// avoid calling for exactly that reason (see TriageWindowDisposalTests'
/// class doc, and this suite's own near-miss discovered while verifying that
/// one: blocking on a REAL, not-yet-disposed WebView2's
/// EnsureCoreWebView2Async from the same dispatcher thread deadlocks). A
/// TaskCompletionSource stands in for it here, giving full, deterministic
/// control over exactly when "init" resolves relative to Close() — the
/// actual race the fix needs to survive — without ever touching a real
/// WebView2 or Edge process.</summary>
[Collection(HighlightContrastTests.Name)]
public class TriageWindowInitRaceTests
{
    private readonly HighlightContrastFixture _fx;
    public TriageWindowInitRaceTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Pumps this thread's Dispatcher (a nested message loop, same
    /// mechanism ShowDialog uses) until <paramref name="task"/> completes.
    /// Needed because <c>InitAndShowAsync</c>'s <c>await</c> captures this
    /// STA thread's DispatcherSynchronizationContext, so the continuation
    /// that runs after <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}.SetResult"/>
    /// is POSTED to the dispatcher queue, not run inline — a plain blocking
    /// wait (`.GetAwaiter().GetResult()`) on this same thread would never let
    /// that posted continuation run at all.</summary>
    private static void PumpUntilComplete(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void ClosingWhileInitIsPendingSkipsShowAndTouchesNothingDisposed() => _fx.Invoke(() =>
    {
        // A real item, not an empty list: with an empty list, ShowCurrentAsync's
        // OWN early exit (`Current is null => Close(); return;`) sets nothing
        // observable either way, so an empty-list version of this test can't
        // actually tell "the guard skipped ShowCurrentAsync" apart from
        // "ShowCurrentAsync ran and immediately no-op'd" — confirmed by first
        // running this exact test against the guard-removed code with an
        // empty list, which passed regardless (a false negative). One real
        // item makes ShowCurrentAsync's Progress.Text write an observable
        // proxy for "did the post-Close() continuation reach this far".
        var item = new MatchMerge.MatchResult("doc.pdf", "ambiguous", "SMITH", "JOHN",
            Candidates: new List<MatchMerge.Candidate>
            {
                new("1", new Dictionary<string, string> { ["A"] = "x" }),
            });
        var win = new TriageWindow(new List<MatchMerge.MatchResult> { item }, new[] { "A" });
        var dialogs = new FakeDialogs();
        win.Dialogs = dialogs;
        var tcs = new TaskCompletionSource<bool>();

        // Starts InitAndShowAsync; it suspends at `await initAsync()` since
        // tcs.Task is not yet complete — mirrors InitAsync still being
        // mid-flight when the window closes.
        var flow = win.InitAndShowAsync(() => tcs.Task);

        win.Close();
        Assert.True(win.IsClosed);

        // "init" resolves successfully only NOW, after Close() already ran
        // and Viewer is already disposed.
        tcs.SetResult(true);
        PumpUntilComplete(flow);

        Assert.True(flow.IsCompletedSuccessfully);   // no ObjectDisposedException reached the caller
        Assert.Empty(dialogs.Warnings);              // success path: no warning expected anyway
        Assert.Equal("", win.Progress.Text);         // ShowCurrentAsync never ran (would have set "1 / 1")
    });

    [Fact]
    public void ClosingWhileInitIsPendingSkipsTheFailureWarningToo() => _fx.Invoke(() =>
    {
        var win = new TriageWindow(new List<MatchMerge.MatchResult>(), new[] { "A" });
        var dialogs = new FakeDialogs();
        win.Dialogs = dialogs;
        var tcs = new TaskCompletionSource<bool>();

        var flow = win.InitAndShowAsync(() => tcs.Task);

        win.Close();
        Assert.True(win.IsClosed);

        // "init" resolves as a FAILURE only now — pre-fix this would call
        // Dialogs.Warn with Owner = this (a closed Window), which WPF's real
        // DialogService/MessageBox.Show throws on.
        tcs.SetResult(false);
        PumpUntilComplete(flow);

        Assert.True(flow.IsCompletedSuccessfully);
        Assert.Empty(dialogs.Warnings);               // the warning was skipped, not shown-and-crashed
        Assert.Equal("", win.Progress.Text);          // ShowCurrentAsync never ran either
    });

    [Fact]
    public void InitAndShowAsyncStillWarnsAndShowsWhenTheWindowStaysOpen() => _fx.Invoke(() =>
    {
        // Contrast case: proves the guard doesn't over-fire and swallow the
        // normal (window-stays-open) path — a real item is shown and a real
        // failure still warns when nothing closed the window.
        var item = new MatchMerge.MatchResult("doc.pdf", "ambiguous", "SMITH", "JOHN",
            Candidates: new List<MatchMerge.Candidate>
            {
                new("1", new Dictionary<string, string> { ["A"] = "x" }),
            });
        var win = new TriageWindow(new List<MatchMerge.MatchResult> { item }, new[] { "A" });
        var dialogs = new FakeDialogs();
        win.Dialogs = dialogs;
        var tcs = new TaskCompletionSource<bool>();

        var flow = win.InitAndShowAsync(() => tcs.Task);
        Assert.False(win.IsClosed);

        tcs.SetResult(false);   // init "fails", window never closed
        PumpUntilComplete(flow);

        Assert.True(flow.IsCompletedSuccessfully);
        Assert.Single(dialogs.Warnings);              // the warning DOES fire when the window is still open
        Assert.Equal("1 / 1", win.Progress.Text);     // and ShowCurrentAsync DOES still run
    });
}
