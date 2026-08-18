using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The decision race: accept a match with Enter, then Escape ("Stop
/// reviewing", IsCancel="True") while <c>WebViewPdfViewer.ReleaseAsync</c>'s
/// up-to-2s await is still pending. <see cref="TriageWindowInitRaceTests"/>
/// covers the sibling INIT race (Escape during WebView2 cold start).
///
/// This started (M1, 2026-08-03) as a disposal question — the resumed
/// continuation called <see cref="TriageWindow.ShowCurrentAsync"/> unguarded
/// and would touch the by-then-disposed Viewer — and that guard is still in
/// place and still pinned, by
/// <see cref="ShowCurrentAsyncStillNoOpsOnceTheWindowIsGone"/> below.
///
/// The 2026-08-17 review round found the LOUDER half of the same race, one
/// level up: MatchMergeWindow reads <c>win.Outcomes</c> the instant ShowDialog
/// returns, so a window that finished closing mid-decision handed the parent a
/// list the rename had not been added to yet. The file was renamed a moment
/// later regardless — so the disk was right, the grid was wrong, and "Undo
/// last merge" could not see the merge at all. The fix lives in TriageWindow,
/// not the parent: closing is DEFERRED while a decision is in flight, so
/// ShowDialog cannot return until the outcome has landed.
///
/// Same proof standard as TriageWindowInitRaceTests, for the same underlying
/// reason (a real WebView2/Edge startup is slow and untimeable): this drives
/// <see cref="TriageWindow.UseSelectedAsync"/> directly with a
/// <c>Func&lt;Task&gt;</c> standing in for <c>ReleaseAsync</c>, giving full,
/// deterministic control over exactly when "release" resolves relative to
/// Close() — the actual race — without a real WebView2/Edge process.</summary>
[Collection(HighlightContrastTests.Name)]
public class TriageWindowDecisionRaceTests
{
    private readonly HighlightContrastFixture _fx;
    public TriageWindowDecisionRaceTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Same pumping technique as TriageWindowInitRaceTests — see its
    /// own doc for why a plain blocking wait can't observe a continuation
    /// whose resumption was posted to this thread's own Dispatcher queue.</summary>
    private static void PumpUntilComplete(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Drains everything already queued at a higher priority — the
    /// deferred Close() is posted back to this dispatcher when the decision
    /// completes, so it has run by the time this returns.</summary>
    private static void PumpQueued() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static List<MatchMerge.MatchResult> TwoItems(string path) => new()
    {
        // Two items: after item 0 is decided, _index advances to 1 and Current
        // is STILL non-null, so the decision's own ShowCurrentAsync genuinely
        // re-renders rather than hitting its "Current is null -> Close()"
        // early exit — which would muddle what this file is measuring.
        new(path, "ambiguous", "SMITH", "JOHN",
            Candidates: new List<MatchMerge.Candidate>
            {
                new("1", new Dictionary<string, string> { ["A"] = "x" }),
            }),
        new("other.pdf", "ambiguous", "DOE", "JANE",
            Candidates: new List<MatchMerge.Candidate>
            {
                new("2", new Dictionary<string, string> { ["A"] = "y" }),
            }),
    };

    [Fact]
    public void ClosingDuringADecisionWaitsForTheRenameToLandInOutcomes() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_decision_race_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");
            var win = new TriageWindow(TwoItems(path), new[] { "A" });

            // Populate Candidates/Progress the way Loaded normally would,
            // without ever calling Show() (no real WebView2 environment —
            // WebViewPdfViewer._ready stays false, so ShowAsync inside this
            // is a genuine no-op, and the whole call completes synchronously
            // — GetResult() here is not a deadlock risk, same reasoning as
            // TriageWindowDisposalTests' identical pattern).
#pragma warning disable xUnit1031
            win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            win.Candidates.SelectedIndex = 0;
            Assert.Equal("1 / 2", win.Progress.Text);

            var tcs = new TaskCompletionSource();

            // Mirrors OnUseSelected(Enter) calling _pdf.ReleaseAsync() — held
            // open by this test instead of a real, untimeable WebView2 release.
            var flow = win.UseSelectedAsync(() => tcs.Task);
            Assert.False(flow.IsCompleted, "the decision resolved before the release did");

            // Mirrors Escape -> "Stop reviewing" (IsCancel="True") while that
            // release is still pending. This is the exact moment ShowDialog
            // used to return, with Outcomes still empty.
            win.Close();
            Assert.False(win.IsClosed,
                "the window finished closing with a decision still in flight — ShowDialog would " +
                "return here and MatchMergeWindow would read Outcomes before the rename landed");
            Assert.Empty(win.Outcomes);

            // A second request (another Escape, the Stop button, the title
            // bar) must neither throw nor queue a second wait.
            win.Close();
            Assert.False(win.IsClosed);

            // "release" only resolves NOW.
            tcs.SetResult();
            PumpUntilComplete(flow);

            Assert.True(flow.IsCompletedSuccessfully);   // no exception reached the caller
            var outcome = Assert.Single(win.Outcomes);
            Assert.NotNull(outcome.Final);
            Assert.True(File.Exists(outcome.Final));
            Assert.Equal("2 / 2", win.Progress.Text);    // the decision ran to the end

            // ...and only once it had, the deferred close goes through on its
            // own — the person pressed Escape and the window still leaves.
            PumpQueued();
            Assert.True(win.IsClosed,
                "the deferred close never retried — the window would stay open after Escape");
        }
        finally
        {
            try { Directory.Delete(dir.FullName, true); } catch { }
        }
    });

    /// <summary>The original (2026-08-03) half of this race, kept because the
    /// close deferral above does NOT replace it: an application shutdown
    /// closes windows with cancellation IGNORED (Window.ShouldCloseWindow), so
    /// a continuation resuming into a disposed Viewer is still reachable.
    /// Progress.Text is the observable proxy for "did the continuation reach
    /// into ShowCurrentAsync's body" — the same body that, against a real,
    /// initialized Viewer, is where the ObjectDisposedException throws.</summary>
    [Fact]
    public void ShowCurrentAsyncStillNoOpsOnceTheWindowIsGone() => _fx.Invoke(() =>
    {
        var win = new TriageWindow(TwoItems("doc.pdf"), new[] { "A" });
#pragma warning disable xUnit1031
        win.ShowCurrentAsync().GetAwaiter().GetResult();
        Assert.Equal("1 / 2", win.Progress.Text);

        win.Close();                 // nothing in flight, so this closes at once
        Assert.True(win.IsClosed);

        // Exactly what a post-decision continuation does when it resumes after
        // the window is gone, with the advance it would have made.
        win.Index = 1;
        win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        Assert.Equal("1 / 2", win.Progress.Text);   // never re-rendered as "2 / 2"
    });
}
