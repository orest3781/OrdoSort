using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>M1 (2026-08-03 final-review), a second QC follow-up to Task 5's
/// disposal fix — <see cref="TriageWindowInitRaceTests"/> covers the INIT
/// race (Escape during WebView2 cold start); this covers the DECISION race
/// the same disposal fix newly exposed: accept a match with Enter, then
/// Escape ("Stop reviewing", IsCancel="True") while
/// <c>WebViewPdfViewer.ReleaseAsync</c>'s up-to-2s await is still pending.
/// Pre-fix, the resumed continuation ran <c>MatchMerge.MergeOne</c> (correct —
/// no data loss, see <see cref="TriageWindow.UseSelectedAsync"/>'s class doc)
/// and then called <see cref="TriageWindow.ShowCurrentAsync"/> unguarded,
/// which would touch the by-then-disposed Viewer.
///
/// Same proof standard as TriageWindowInitRaceTests, for the same underlying
/// reason (a real WebView2/Edge startup is slow and untimeable): this drives
/// <see cref="TriageWindow.UseSelectedAsync"/> directly with a
/// <c>Func&lt;Task&gt;</c> standing in for <c>ReleaseAsync</c>, giving full,
/// deterministic control over exactly when "release" resolves relative to
/// Close() — the actual race — without a real WebView2/Edge process. Because
/// the window is never Show()n, <c>WebViewPdfViewer</c>'s internal <c>_ready</c>
/// flag is never true, so <c>ShowAsync</c> itself can't be caught mid-navigate
/// here to reproduce the literal ObjectDisposedException; instead, exactly
/// like TriageWindowInitRaceTests, <c>Progress.Text</c> is the observable
/// proxy for "did the post-Close() continuation reach into
/// ShowCurrentAsync's body" — which is the same body that, against a real,
/// initialized Viewer, is where the exception actually throws.</summary>
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

    [Fact]
    public void ClosingDuringReleaseSkipsShowCurrentButStillMerges() => _fx.Invoke(() =>
    {
        var dir = Directory.CreateTempSubdirectory("ordo_triage_decision_race_");
        try
        {
            var path = Path.Combine(dir.FullName, "SMITH_JOHN.pdf");
            File.WriteAllText(path, "x");

            // Two items: after item 0 is decided, _index advances to 1 and
            // Current is STILL non-null (item 1) — so, unguarded,
            // ShowCurrentAsync's body genuinely re-renders (Progress.Text,
            // Candidates.ItemsSource, …) rather than hitting its own
            // separate "Current is null -> Close()" early exit, which would
            // prove nothing about the guard this test targets.
            var items = new List<MatchMerge.MatchResult>
            {
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
            var win = new TriageWindow(items, new[] { "A" });

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
            var progressBeforeDecision = win.Progress.Text;
            Assert.Equal("1 / 2", progressBeforeDecision);

            var tcs = new TaskCompletionSource();

            // Mirrors OnUseSelected(Enter) calling _pdf.ReleaseAsync() — held
            // open by this test instead of a real, untimeable WebView2 release.
            var flow = win.UseSelectedAsync(() => tcs.Task);

            // Mirrors Escape -> "Stop reviewing" (IsCancel="True") while that
            // release is still pending.
            win.Close();
            Assert.True(win.IsClosed);

            // "release" only resolves NOW, after Close() already ran and
            // Viewer is already disposed.
            tcs.SetResult();
            PumpUntilComplete(flow);

            Assert.True(flow.IsCompletedSuccessfully);   // no exception reached the caller
            // The rename still happened — MergeOne runs regardless of
            // IsClosed, so there is no data loss on the way out.
            var outcome = Assert.Single(win.Outcomes);
            Assert.NotNull(outcome.Final);
            Assert.True(File.Exists(outcome.Final));
            // ShowCurrentAsync's IsClosed guard stopped the continuation
            // before it re-rendered for item 1 — Progress.Text is exactly
            // what it was before the decision, not "2 / 2".
            Assert.Equal(progressBeforeDecision, win.Progress.Text);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, true); } catch { }
        }
    });
}
