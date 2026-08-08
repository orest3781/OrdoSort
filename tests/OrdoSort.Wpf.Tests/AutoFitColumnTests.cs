using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-07 autofit-columns, Task 1 Step 6: MatchMergeWindow,
/// BulkRenameWindow and TriageWindow all moved from "some columns star,
/// unbounded" (or, for Triage, "every roster column plain Auto, unbounded")
/// to "content-sized (Auto) and capped, exactly one star filler with a
/// MinWidth". This suite proves the four things that shape is supposed to
/// guarantee, for each of the three grids that took the general shape
/// (History is covered separately — see DataGridStarColumnTests and
/// HistoryWindowXamlTests — because its own measurement, recorded in
/// HistoryWindow.xaml, kept Original/Filed-as on the OLD star+MinWidth shape
/// on purpose):
///
/// 1. A short value in a capped column measures narrow — it fits the
///    content, rather than stretching to fill a fixed/star share the way the
///    pre-fix File/Becomes/Current name columns did.
/// 2. A long value in a capped column stops exactly at that column's
///    MaxWidth rather than growing without bound, and still carries
///    TextTrimming/ToolTip so the capped-off text isn't simply lost.
/// 3. Every capped column in the grid can be at its LONGEST simultaneously
///    (the real worst case for available width) and the grid still shows no
///    horizontal scrollbar — proven by finding the DataGrid's own internal
///    ScrollViewer and reading ComputedHorizontalScrollBarVisibility, with
///    every capped column fed a value long enough to hit its cap.
///
/// What this suite does NOT attempt to prove headlessly: that the filler
/// column visually resolves to "whatever's left" and paints zero dead grey
/// space on a REAL window. DataGridStarColumnTests' own class doc already
/// established (2026-08-02) that headless Show()+UpdateLayout() never
/// resolves genuine star-column fair share at all — it deterministically
/// clamps every star column to its MinWidth instead, regardless of what's
/// tried in-process. That's the SAME mechanism this suite's own
/// GridHasExactlyOneStarFillerColumn fact below still confirms structurally
/// (Width="*", so WPF's own layout engine gives it 100% of whatever's left
/// the moment a real window resolves it) — combined with #3 above (the grid
/// doesn't overflow even with the filler pinned to its worst-case, smallest
/// floor width), a real window can only do better, never worse: the filler
/// gets AT LEAST its floor and never more demand than fits.
///
/// FIX ROUND 1 (2026-08-07): every Triage fact originally set
/// Candidates.ItemsSource directly, which never runs ShowCurrentAsync and
/// therefore never inserts the "Why" column — a real, fixed-260px column
/// the real app DOES show alongside roster columns for any suggested-status
/// item, a normal queue per MatchMerge.cs, not a rare one. That gap let a
/// real default-state overflow (Why + First name + Control ID totaling
/// 422.5px against a 416px panel) ship past a fully green suite. Every
/// Triage fact below now drives the real ShowCurrentAsync — see
/// BuildTriageWindow/ShowOffscreenAndDriveCurrent — and two facts assert
/// ComputedHorizontalScrollBarVisibility directly with Why present, at both
/// the app's default 2 roster columns and at 3.
///
/// FIX ROUND 2 (2026-08-07): every scrollbar/cap fact before this round only
/// ever measured MatchMerge/BulkRename/History at their DECLARED (default)
/// Width. DataGridColumnCap computed each cap once from that declared Width
/// and never revisited it — so a window resized toward its own MinWidth (an
/// ordinary user action, dragging an edge) shrank the grid without ever
/// shrinking the caps, and the column total overflowed the smaller viewport:
/// measured pre-fix, MatchMerge at 720px totaled 708px against a 676px grid,
/// BulkRename at 700px totaled 694px against 656px, History at 700px totaled
/// 727.4px against 656px — all three with a visible horizontal scrollbar,
/// exactly what decision 1 forbids. DataGridColumnCap now tracks each grid's
/// own LIVE ActualWidth via SizeChanged (see its class doc) instead of a
/// value baked in once; the "Long...StopsAtTheCap" facts below now compare
/// against the grid's ActualWidth-derived cap rather than Window.Width
/// (they're no longer equal — the grid is narrower than the window by its
/// own chrome/margins), and new "AtMinWidth"/"AtMidWidth" facts (window
/// constructed directly at that size, then shown) assert no horizontal
/// scrollbar across the size range, not just at one size, for
/// MatchMerge/BulkRename/History (Triage's panel is a fixed 440px column
/// that never resizes with the window, so it needed no new coverage). A
/// genuine post-Show() live-resize fact was also tried; see the comment
/// where it would have gone (MatchMerge section) for why it was dropped —
/// a separate, pre-existing DataGrid star-column quirk specific to
/// simulating a resize off-screen, not a defect in this fix.
///
/// FIX ROUND 3 (2026-08-07): round 2's fix (and this suite's own
/// AtMinWidth/AtMidWidth facts) capped against the grid's raw outer
/// ActualWidth — which includes whatever a VERTICAL scrollbar claims once
/// there are enough rows to need one. Every fact in this suite until this
/// round used exactly ONE row, so no vertical scrollbar ever appeared and
/// the gap never showed up: reproduced directly, a 60-row HistoryWindow at
/// its own MinWidth (700) showed a visible horizontal scrollbar even
/// post-round-2-fix. DataGridColumnCap now reserves
/// SystemParameters.VerticalScrollBarWidth unconditionally (see its own
/// class doc for why unconditionally, not just when a scrollbar happens to
/// be visible right now). ManyRowCount (60 — reviewer-bracketed clean at
/// 20, failing at 60, one ordinary commit's worth of filing) replaces the
/// single row every AtMinWidth/AtMidWidth fact below carries, for all
/// three grids — MatchMerge/BulkRename weren't independently reproduced
/// failing, but the reviewer's own margin analysis found them one
/// column-share tweak away from the identical failure, so their facts
/// needed the same many-row coverage to actually prove it, not just assume
/// it from a passing single-row fact.
///
/// This round also restored the dropped live-resize fact
/// (MatchMerge_ResizingLiveDownToMinWidthStillHasNoHorizontalScrollbar):
/// round 2's "pre-existing DataGrid star-column quirk, headless-simulation-
/// specific" conclusion was directionally right but the mechanism was
/// guessed at, not found. It's real and specific: WPF's DataGrid defers
/// star-column reconciliation to DispatcherPriority.Background, which
/// neither UpdateLayout() nor this suite's Render-priority pump ever
/// drains. A single added Background-priority pump
/// (ShowOffscreenThenResizeTo) clears it. What that does and doesn't prove
/// about a real user's mouse-driven drag is spelled out on the restored
/// fact's own doc comment — this session still can't drive a real mouse to
/// confirm it directly.</summary>
[Collection(HighlightContrastTests.Name)]
public class AutoFitColumnTests
{
    private readonly HighlightContrastFixture _fx;
    public AutoFitColumnTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Long enough to exceed every capped column's MaxWidth in this
    /// app (the smallest, Triage's roster columns, caps at 154px on the
    /// default panel width) at any reasonable font size, without relying on
    /// a real filesystem name length limit.</summary>
    private const string VeryLongValue =
        "A-Very-Long-Roster-Derived-Name-That-Keeps-Going-Well-Past-Any-Sensible-Column-Width-000000000000000000.pdf";

    private const string ShortValue = "a.pdf";

    /// <summary>Fix round 3: enough rows to force a vertical scrollbar in
    /// every one of these windows — reviewer-bracketed clean at 20, failing
    /// (pre-fix) at 60; used at the low end of that bracket so these facts
    /// stay a genuine reproduction, not an arbitrarily larger number chosen
    /// to be safe. One ordinary commit's worth of filing, not a corner
    /// case.</summary>
    private const int ManyRowCount = 60;

    // --------------------------------------------------------- MatchMerge

    [Fact]
    public void MatchMerge_ShortFileValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "File");
            // Well under the 0.35*840=294px cap: "a.pdf" needs a small
            // fraction of that — proving it fits the content rather than
            // reserving the old star column's arbitrary share of the window.
            Assert.True(column.ActualWidth < 100,
                $"MatchMerge File column with short content is {column.ActualWidth}px, expected < 100px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_LongFileValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "File");
            // Against the GRID's own live viewport, not Window.Width or the
            // grid's raw outer ActualWidth: since fix round 2 the cap tracks
            // the grid live (DataGridColumnCap.Track), and since fix round 3
            // it also reserves room for a vertical scrollbar — see
            // ExpectedColumnCap.
            var expectedCap = ExpectedColumnCap(win, 0.35);
            Assert.True(column.ActualWidth == expectedCap,
                $"MatchMerge File column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "File");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_AllCappedColumnsAtWorstCaseStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: VeryLongValue);
        try
        {
            ShowOffscreen(win);
            AssertNoHorizontalScrollbar(win, "MatchMerge");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_HasExactlyOneStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var star = Assert.Single(grid.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("Becomes", star.Header);
        }
        finally { win.Close(); }
    });

    /// <summary>Fix round 2 — the Critical: pre-fix, this exact configuration
    /// (both capped columns fed enough content to reach their old, static
    /// cap) measured 708px of columns against a 676px grid at this window's
    /// own declared MinWidth (720). Every capped column is fed VeryLongValue
    /// so it's straining for room, the real worst case for this invariant.
    ///
    /// Fix round 3: carries ManyRowCount rows, not one — a single-row grid
    /// never grows a vertical scrollbar, so it can't exercise the
    /// round-3 fix (capping against the space columns can ACTUALLY occupy,
    /// which shrinks the moment a vertical scrollbar claims some of the
    /// grid's outer ActualWidth). See History's identical note below for
    /// the measurement that found this gap.</summary>
    [Fact]
    public void MatchMerge_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"MatchMerge (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MatchMerge_AtMidWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: VeryLongValue, ManyRowCount);
        try
        {
            var midWidth = (win.MinWidth + win.Width) / 2;
            ShowOffscreenAtWidth(win, midWidth);
            AssertNoHorizontalScrollbar(win, $"MatchMerge (at mid width {midWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    /// <summary>Fix round 3: restores the genuine "shown at default, then
    /// resized smaller afterward" fact fix round 2 dropped — the actual
    /// gesture a person dragging a window's edge produces, not a proxy for
    /// it. It WAS diagnosable, just not with the tools this suite reached
    /// for at the time: WPF's DataGrid defers star-column reconciliation to
    /// DispatcherPriority.Background, and neither UpdateLayout() nor a
    /// Render-priority pump (this suite's usual ShowOffscreen shape) ever
    /// drains that queue — so the star filler (Becomes) kept whatever width
    /// it resolved on the FIRST layout instead of recomputing for the
    /// smaller one, independent of anything this fix touches (it only ever
    /// caps the AUTO columns). A single additional
    /// Dispatcher.Invoke(_, DispatcherPriority.Background) pump after the
    /// resize clears it. That fixes the SYMPTOM this suite could observe
    /// (Becomes' stale width); it doesn't by itself prove a real user's
    /// mouse-driven drag recovers the same way — this session can't drive a
    /// real mouse to check. What IS established: the mechanism (a
    /// Background-priority dispatcher operation), and that a real
    /// application's message loop drains Background as soon as it goes
    /// idle, same as it drains every other priority — so a real drag almost
    /// certainly recovers on its own, without any test-only pump, the
    /// moment the user's drag pauses. That's an inference from the
    /// mechanism, not an on-screen observation, and it's reported as
    /// exactly that.</summary>
    [Fact]
    public void MatchMerge_ResizingLiveDownToMinWidthStillHasNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenThenResizeTo(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"MatchMerge (live-resized to MinWidth {win.MinWidth})");
        }
        finally { win.Close(); }
    });

    private static MatchMergeWindow BuildMatchMergeWindow(string fileValue, string noteValue, int rowCount = 1)
    {
        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
        for (var i = 0; i < rowCount; i++)
            vm.Rows.Add(new MatchRow($"src{i}.pdf", fileValue, "SOMETHING-SHORT.pdf", noteValue, "merge"));
        return new MatchMergeWindow(vm);
    }

    // -------------------------------------------------------- BulkRename

    [Fact]
    public void BulkRename_ShortCurrentValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Current name");
            // Threshold is looser than MatchMerge's analogous check: an Auto
            // column sizes to max(header, cell content), and "Current name"
            // is itself a longer header than "File" — still well under the
            // 0.35*820=287px cap either way.
            Assert.True(column.ActualWidth < 150,
                $"BulkRename Current name column with short content is {column.ActualWidth}px, expected < 150px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_LongCurrentValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Current name");
            // Against the GRID's own live viewport — see MatchMerge's
            // identical comment above.
            var expectedCap = ExpectedColumnCap(win, 0.35);
            Assert.True(column.ActualWidth == expectedCap,
                $"BulkRename Current name column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Current");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_AllCappedColumnsAtWorstCaseStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: VeryLongValue);
        try
        {
            ShowOffscreen(win);
            AssertNoHorizontalScrollbar(win, "BulkRename");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_HasExactlyOneStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: ShortValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var star = Assert.Single(grid.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("New name  ·  click or press F2 to edit", star.Header);
        }
        finally { win.Close(); }
    });

    /// <summary>Fix round 2: pre-fix, this measured 694px of columns against
    /// a 656px grid at this window's own declared MinWidth (700).
    ///
    /// Fix round 3: carries ManyRowCount rows — see MatchMerge's identical
    /// note above. The reviewer's own margin analysis flagged this window as
    /// "one column-share tweak from the same failure" as History's; this
    /// many-row version is what actually proves it doesn't fail today, not
    /// just that it fails less obviously than History did.</summary>
    [Fact]
    public void BulkRename_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"BulkRename (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void BulkRename_AtMidWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: VeryLongValue, ManyRowCount);
        try
        {
            var midWidth = (win.MinWidth + win.Width) / 2;
            ShowOffscreenAtWidth(win, midWidth);
            AssertNoHorizontalScrollbar(win, $"BulkRename (at mid width {midWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    private static BulkRenameWindow BuildBulkRenameWindow(string currentValue, string noteValue, int rowCount = 1)
    {
        var vm = new BulkRenameViewModel();
        for (var i = 0; i < rowCount; i++)
            vm.Preview.Add(new RenameRow($"src{i}.pdf", currentValue, "SOMETHING-SHORT.pdf", noteValue,
                changed: true, manual: false, needsName: false, editSeed: "SOMETHING-SHORT.pdf"));
        return new BulkRenameWindow(vm);
    }

    // ---------------------------------------------------------- History
    //
    // Fix round 2: this window's capped columns (When/Name/Destination) had
    // no scrollbar coverage in this suite at all before — History's own
    // scrollbar risk was found by the reviewer measuring the real windows
    // directly, not by a gap in an existing fact. Original/Filed-as (the
    // deliberately star-shaped path columns — see HistoryWindow.xaml's own
    // measurement) are untouched by this round and stay covered by
    // DataGridStarColumnTests/HistoryWindowXamlTests, not here.

    /// <summary>Fix round 2: pre-fix, this measured 727.4px of columns
    /// against a 656px grid at this window's own declared MinWidth (700) —
    /// the largest of the three overflows the reviewer found. Name and
    /// Destination (Route) are fed VeryLongValue; When can't be — it's a
    /// formatted timestamp History.LogCommit generates internally, not
    /// something a caller can inject an arbitrary length into — but it's
    /// naturally short (16 chars) regardless, matching the class-doc's
    /// reasoning for why it's capped at all.
    ///
    /// Fix round 3 — the Critical: fix round 2's version of this fact
    /// carried exactly ONE row, which can never grow a vertical scrollbar —
    /// so it never exercised the actual defect. Reproduced directly (before
    /// this round's DataGridColumnCap fix): a 60-row History at this same
    /// MinWidth (60 rows — one ordinary commit's worth of filing, not a
    /// corner case; bracketed clean at 20, failing at 60) showed
    /// ComputedHorizontalScrollBarVisibility=Visible, because the vertical
    /// scrollbar those 60 rows need claims SystemParameters.
    /// VerticalScrollBarWidth of the SAME outer ActualWidth the cap was
    /// computed against, and the columns didn't shrink to compensate.
    /// ManyRowCount (60) is used here, not 1, specifically so this fact
    /// keeps proving that fix rather than silently regressing to only
    /// covering fix round 2's (real, but narrower) defect.</summary>
    [Fact]
    public void History_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var (win, history, dbPath) = BuildHistoryWindow(
            nameValue: VeryLongValue, routeLabelValue: VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"History (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    [Fact]
    public void History_AtMidWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var (win, history, dbPath) = BuildHistoryWindow(
            nameValue: VeryLongValue, routeLabelValue: VeryLongValue, ManyRowCount);
        try
        {
            var midWidth = (win.MinWidth + win.Width) / 2;
            ShowOffscreenAtWidth(win, midWidth);
            AssertNoHorizontalScrollbar(win, $"History (at mid width {midWidth}, {ManyRowCount} rows)");
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

    private static (HistoryWindow win, History history, string dbPath) BuildHistoryWindow(
        string nameValue, string routeLabelValue, int rowCount = 1)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
        var history = new History(dbPath);
        for (var i = 0; i < rowCount; i++)
            history.LogCommit(
                originalPath: $@"C:\inbox\a{i}.pdf", originalName: $"a{i}.pdf",
                newName: $"b{i}.pdf", nameEntered: nameValue,
                namingMode: "replace", suffixApplied: "",
                routeLabel: routeLabelValue, routePath: @"C:\dest",
                tagged: false, collisionSuffix: "");
        // InlineWorkScheduler: HistoryViewModel's constructor kicks off an
        // async LoadAsync(all: false) (default page size 500, comfortably
        // above ManyRowCount) — inline makes it finish synchronously, so
        // Rows/RowsView are fully populated the moment this returns.
        var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
        var win = new HistoryWindow(vm);
        return (win, history, dbPath);
    }

    private static void CleanupHistory(Window win, History history, string dbPath)
    {
        try { win.Close(); } catch { /* best effort */ }
        history.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------ Triage
    //
    // Fix round 1 finding: the first version of these tests set
    // Candidates.ItemsSource directly, bypassing ShowCurrentAsync entirely —
    // which meant the "Why" column (ShowCurrentAsync inserts it, fixed
    // 260px, whenever the current item's Status is "suggested" — a normal,
    // common queue per MatchMerge.cs, not a rare one) was NEVER present in
    // any of these tests, even though the real app can and does show it
    // alongside roster columns in the SAME 440px side panel. That's how a
    // real default-state overflow (Why=251.5 + First name=90 + Control
    // ID=81 = 422.5px against a 416px panel, scrollbar VISIBLE) shipped past
    // a green suite. Every Triage test below now drives the real
    // ShowCurrentAsync — see BuildTriageWindow/ShowOffscreenAndDriveCurrent.

    /// <summary>Reproduces TriageWindow's own internal budget formula (see
    /// its constructor) so these tests double as a live check that the
    /// production constants (WhyColumnWidth/PanelHorizontalMargins/
    /// SafetyMargin/FillerMinWidth, duplicated here — same pattern as
    /// MatchMerge/BulkRename's tests inlining their 0.35 share) and this
    /// test's own expectation can't silently drift apart.</summary>
    private static double ExpectedTriageColumnCap(int headerCount, bool couldShowWhy)
    {
        const double whyColumnWidth = 260;
        const double panelHorizontalMargins = 24;
        const double safetyMargin = 20;
        const double fillerMinWidth = 60;
        var rosterBudget = Math.Max(fillerMinWidth, 440 - panelHorizontalMargins
            - safetyMargin - (couldShowWhy ? whyColumnWidth : 0));
        var cappedColumnCount = Math.Max(1, headerCount - 1);
        return Math.Max(20, (rosterBudget - fillerMinWidth) / cappedColumnCount);
    }

    [Fact]
    public void Triage_ShortRosterValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: false, ShortValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            // No "Why" in this batch (ambiguous only) — the wide, no-Why cap
            // (336px, see ExpectedTriageColumnCap) applies; "a.pdf" needs a
            // small fraction of it regardless.
            Assert.True(column.ActualWidth < 110,
                $"Triage Control ID column with short content is {column.ActualWidth}px, expected < 110px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_LongRosterValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: false, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            var expectedCap = ExpectedTriageColumnCap(headerCount: 2, couldShowWhy: false);
            Assert.True(column.ActualWidth == expectedCap,
                $"Triage Control ID column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Control ID");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Triage_FirstRosterColumnIsTheStarFillerColumn() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: false, ShortValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var star = Assert.Single(win.Candidates.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("First name", star.Header);
        }
        finally { win.Close(); }
    });

    /// <summary>The Critical from fix round 1, made concrete: the app's own
    /// default roster picks ("nothing ticked = the name and id columns" —
    /// two headers) reviewing a SUGGESTED item — the common queue, not an
    /// edge case — with every capped column and Why itself all fed enough
    /// content to reach their respective caps. This is the exact
    /// configuration the reviewer measured overflowing (422.5px against
    /// 416px) against the pre-fix budget.</summary>
    [Fact]
    public void Triage_DefaultTwoRosterColumnsWithWhyPresentStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: true, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            // Why is actually present — otherwise this test would silently
            // stop meaning anything, the same way the pre-fix version did.
            Assert.Contains(win.Candidates.Columns, c => (string)c.Header == "Why");
            AssertNoHorizontalScrollbar(win, "Triage (2 roster columns, Why present)");
        }
        finally { win.Close(); }
    });

    /// <summary>The "as roster columns are added" half of the fix: a third
    /// header (someone ticked an extra optional roster column in "Show in
    /// Review matches") sharing the same panel with Why, both capped roster
    /// columns fed enough content to reach their now-smaller caps.</summary>
    [Fact]
    public void Triage_ThreeRosterColumnsWithWhyPresentStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID", "Extra field" }, suggested: true,
            VeryLongValue, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            Assert.Contains(win.Candidates.Columns, c => (string)c.Header == "Why");
            AssertNoHorizontalScrollbar(win, "Triage (3 roster columns, Why present)");
        }
        finally { win.Close(); }
    });

    /// <summary>Builds a real TriageWindow with one real MatchMerge.MatchResult
    /// (status "suggested" or "ambiguous", matching whether the caller wants
    /// Why present) and a real candidate row — headers[0] always gets a
    /// short, fixed value ("Pat", the filler column), and each subsequent
    /// header gets the corresponding entry from <paramref name="columnValues"/>.
    /// Dialogs is swapped to FakeDialogs defensively; the window is always
    /// Close()d in the caller's finally.</summary>
    private static TriageWindow BuildTriageWindow(string[] headers, bool suggested, params string[] columnValues)
    {
        var row = new Dictionary<string, string> { [headers[0]] = "Pat" };
        for (var i = 1; i < headers.Length; i++) row[headers[i]] = columnValues[i - 1];
        var candidate = new MatchMerge.Candidate("1", row);

        var item = suggested
            ? new MatchMerge.MatchResult("doc.pdf", "suggested",
                Suggestions: new List<MatchMerge.Suggestion> { new(candidate, "token match") })
            : new MatchMerge.MatchResult("doc.pdf", "ambiguous",
                Candidates: new List<MatchMerge.Candidate> { candidate });

        return new TriageWindow(new List<MatchMerge.MatchResult> { item }, headers)
        {
            Dialogs = new FakeDialogs(),
        };
    }

    /// <summary>Show()s off-screen (firing the real Loaded handler, and
    /// therefore starting a real, genuinely-async WebView2 init in the
    /// background — proven safe across repeated runs during fix round 1),
    /// then drives the SAME ShowCurrentAsync the app's own Loaded flow
    /// eventually calls, directly and deterministically, rather than waiting
    /// on that real init to resolve. ShowCurrentAsync awaits
    /// _pdf.ShowAsync(...), which no-ops synchronously (returns
    /// Task.CompletedTask) as long as the WebView2 was never actually
    /// initialized — true here, since that's the genuinely-async part still
    /// pending in the background — so GetAwaiter().GetResult() is
    /// synchronous-safe, not a deadlock risk: same reasoning
    /// TriageWindowDisposalTests' class doc already established for the
    /// identical pattern.</summary>
    private static void ShowOffscreenAndDriveCurrent(TriageWindow win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
#pragma warning disable xUnit1031
        win.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        win.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Render);
        win.UpdateLayout();
    }

    // ---------------------------------------------------------- plumbing

    private static void ShowOffscreen(Window win)
    {
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = 0;
        win.ShowActivated = false;
        win.Show();
        win.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Render);
        win.UpdateLayout();
    }

    /// <summary>Fix round 2: sets Width BEFORE Show() — the window is
    /// already at <paramref name="width"/> for its very first layout pass.
    /// Covers "opened already this small" (a remembered small window size, a
    /// small display) — see ShowOffscreenThenResizeTo for the complementary
    /// live-resize case.</summary>
    private static void ShowOffscreenAtWidth(Window win, double width)
    {
        win.Width = width;
        ShowOffscreen(win);
    }

    /// <summary>Fix round 2, restored in fix round 3 with the missing pump:
    /// shows at the window's DEFAULT declared Width first, THEN resizes
    /// down — the scenario a person actually produces by dragging an edge.
    /// The extra Dispatcher.Invoke(_, DispatcherPriority.Background) pump
    /// (beyond ShowOffscreen's own Render-priority one) is load-bearing, not
    /// decorative: WPF's DataGrid defers star-column width reconciliation to
    /// Background priority, and fix round 2 found — by diagnosing actual
    /// column widths before/after a resize, not by assumption — that without
    /// draining that specific priority, the star filler column keeps
    /// whatever width it resolved on the FIRST layout instead of
    /// recomputing for the new, smaller one. See
    /// MatchMerge_ResizingLiveDownToMinWidthStillHasNoHorizontalScrollbar's
    /// own doc comment for what this does and doesn't establish about a
    /// real user's mouse-driven drag.</summary>
    private static void ShowOffscreenThenResizeTo(Window win, double newWidth)
    {
        ShowOffscreen(win);
        win.Width = newWidth;
        win.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);
        win.UpdateLayout();
    }

    private static DataGridColumn FindColumnByHeader(Window win, string header)
    {
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException("no DataGrid descendant found");
        return grid.Columns.FirstOrDefault(c => (string)c.Header == header)
            ?? throw new InvalidOperationException($"no '{header}' column found");
    }

    /// <summary>Reproduces DataGridColumnCap.Track's own cap formula (fix
    /// round 3: viewport-aware, not the grid's raw outer ActualWidth) so
    /// these tests double as a live check that the production formula and
    /// this test's expectation can't silently drift apart — same pattern as
    /// ExpectedTriageColumnCap below for Triage's own budget.</summary>
    private static double ExpectedColumnCap(Window win, double share)
    {
        var grid = FindDescendant<DataGrid>(win)!;
        var viewportWidth = Math.Max(0, grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        return viewportWidth * share;
    }

    private static void AssertTrimmingAndTooltip(DataGridBoundColumn column, string bindingPathContains)
    {
        Assert.NotNull(column.ElementStyle);
        var trimSetter = column.ElementStyle!.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == TextBlock.TextTrimmingProperty);
        Assert.NotNull(trimSetter);
        Assert.Equal(TextTrimming.CharacterEllipsis, trimSetter!.Value);

        var tooltipSetter = column.ElementStyle!.Setters.OfType<Setter>()
            .FirstOrDefault(s => s.Property == FrameworkElement.ToolTipProperty);
        Assert.NotNull(tooltipSetter);
        var binding = Assert.IsType<Binding>(tooltipSetter!.Value);
        Assert.Contains(bindingPathContains, binding.Path.Path);
    }

    /// <summary>Finds the DataGrid's own internal ScrollViewer (its
    /// PART_..., the real control that decides whether a scrollbar renders)
    /// and asserts it never needs to show one horizontally — the real,
    /// meaningful proof that every capped column at its worst case (longest
    /// possible content, fed by each test's own setup) plus the filler
    /// column pinned to its headless worst case (its MinWidth floor — see
    /// this class's own doc comment for why that's the CONSERVATIVE case,
    /// not an optimistic one) still fits inside the grid's available
    /// width.</summary>
    private static void AssertNoHorizontalScrollbar(Window win, string windowName)
    {
        var grid = FindDescendant<DataGrid>(win)
            ?? throw new InvalidOperationException($"{windowName}: no DataGrid descendant found");
        var scrollViewer = FindDescendant<ScrollViewer>(grid)
            ?? throw new InvalidOperationException($"{windowName}: no ScrollViewer descendant found in the grid");
        Assert.NotEqual(Visibility.Visible, scrollViewer.ComputedHorizontalScrollBarVisibility);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
