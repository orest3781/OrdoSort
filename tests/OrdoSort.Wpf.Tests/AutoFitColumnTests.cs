using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// confirm it directly.
///
/// FIX ROUND 5 (2026-08-08, "the columns are not able to be moved and are
/// hiding text" — the owner's own report on TriageWindow, "Review matches").
/// Two independent defects, measured off-screen before either was touched:
///
/// (1) Every capped column in all FOUR grid windows was unable to be widened
/// by a user at all — WPF's own interactive column-resize is live-clamped to
/// MaxWidth in real time, and every one of these columns had a MaxWidth. The
/// *_UserDraggedXSurvivesBeyondTheCapAndStaysPinnedAfterResize facts below
/// simulate the real drag gesture via the column header's actual resize
/// Thumb (SimulateColumnHeaderDragResize) — not just setting Width, which
/// wouldn't exercise DataGridColumnCap's fix at all — and prove both that
/// the drag survives AND that a later automatic recompute doesn't reclamp
/// it. See DataGridColumnCap.cs's own doc comment for the fix.
///
/// (2) TriageWindow specifically computed its roster-column cap ONCE, from
/// its side panel's DECLARED Width — but that panel is resizable (a
/// GridSplitter) and the cap never revisited a live resize.
/// Triage_RosterColumnCapTracksLivePanelWidthNotDeclared proves the fix;
/// Triage_DefaultRosterColumnCapIsWideEnoughToBeReadable is the assertion
/// whose ABSENCE let the underlying starvation ship in the first place (this
/// suite had scrollbar-avoidance coverage for Triage but nothing that
/// checked the resulting cap was actually usable) — WhyColumnWidth also
/// dropped from 260 to 150 in this round; see TriageWindow.xaml.cs's own
/// doc comment on that constant.
///
/// FIX ROUND 6 (2026-08-09, task-7 review finding): ProductionWindow (Task 7,
/// the Production report grid) shipped with the FLAT-share
/// DataGridColumnCap.Track overload — the same one MatchMerge/BulkRename/
/// History use — but, unlike those three windows' fixed, small,
/// repo-baked-in capped-column count, Production's capped-column count is
/// however many group columns a person ticks beyond the first, with no
/// upper bound. A flat share gives EVERY capped column the same
/// independently-computed MaxWidth, so N capped columns can jointly demand N
/// times that share: 4 ticked group columns alone would have claimed 3 ×
/// 0.30 = 90% of the viewport before Records/the sum columns were even
/// counted — guaranteed overflow, the exact invariant this whole suite
/// exists to catch. Fixed by switching to the Func&lt;double,double&gt;
/// overload, mirroring TriageWindow's own budget-dividing formula (divide
/// what's left of the viewport, after a fixed reservation, by
/// cappedColumnCount) — see ProductionWindow.xaml.cs's own doc comment for
/// the exact shape. The Production_* facts below are new coverage, not a
/// revision of existing ones: this suite had no ProductionWindow coverage at
/// all before this round, so — unlike Triage's fix rounds, each proving a
/// REGRESSION against previously-passing facts — there was no green suite
/// for the bug to slip past; this is the ABSENCE this round closes.</summary>
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

    /// <summary>FIX ROUND 5 (2026-08-08, "columns can't be moved" task): before
    /// this round, EVERY capped column in every one of the four grid windows
    /// was measured unable to be widened past its cap even by setting Width
    /// directly — WPF's own live column-resize drag is clamped to MaxWidth in
    /// real time, so a real mouse drag could never even produce a wider Width
    /// value. Simulates the actual drag gesture (DragStarted/DragCompleted on
    /// the column header's real resize Thumb, via SimulateColumnHeaderDragResize)
    /// rather than just setting Width, since DataGridColumnCap's fix
    /// specifically listens for those events, not for Width changing in
    /// isolation — a test that skipped the Thumb events would prove nothing
    /// about the real fix. Asserts the survived width, THEN forces an ordinary
    /// automatic recompute (a further window resize) and asserts it's STILL not
    /// reclamped — that second assertion is what actually proves "the cap
    /// governs automatic sizing only, and stops once the user has resized,"
    /// not just that one drag briefly succeeded.
    ///
    /// The requested growth (+50px over the old cap) is deliberately modest,
    /// not an arbitrarily huge target: diagnosed directly (temporary probe,
    /// deleted after use) that WPF's own interactive column resize — separate
    /// from and in addition to MaxWidth — also declines to shrink OTHER
    /// columns below their own MinWidth, so an oversized request (+200px, on
    /// Triage's tight side panel) landed short of the literal target (334px of
    /// 369px requested) even with this fix's MaxWidth relaxed to infinity.
    /// That's a separate, legitimate WPF behavior this fix neither causes nor
    /// needs to override — a real user's mouse-driven drag would hit the exact
    /// same floor. +50px stays comfortably inside every one of the four
    /// grids' headroom while still being unreachable pre-fix.</summary>
    [Fact]
    public void MatchMerge_UserDraggedFileColumnSurvivesBeyondTheCapAndStaysPinnedAfterResize() => _fx.Invoke(() =>
    {
        var win = BuildMatchMergeWindow(fileValue: VeryLongValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var column = FindColumnByHeader(win, "File");
            var capBefore = column.MaxWidth;
            var draggedWidth = capBefore + 50;

            SimulateColumnHeaderDragResize(grid, column, draggedWidth);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"MatchMerge File column after a simulated drag to {draggedWidth}px is " +
                $"{column.ActualWidth}px — expected it to survive beyond the old cap {capBefore}px");

            win.Width -= 10;
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"MatchMerge File column after a SUBSEQUENT window resize is {column.ActualWidth}px — " +
                $"expected the user's {draggedWidth}px drag to still win, not be reclamped");
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

    /// <summary>Same proof as MatchMerge's identical fact above, for this
    /// window's own capped Current name column — see its doc comment for the
    /// full reasoning.</summary>
    [Fact]
    public void BulkRename_UserDraggedCurrentNameColumnSurvivesBeyondTheCapAndStaysPinnedAfterResize() => _fx.Invoke(() =>
    {
        var win = BuildBulkRenameWindow(currentValue: VeryLongValue, noteValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var column = FindColumnByHeader(win, "Current name");
            var capBefore = column.MaxWidth;
            var draggedWidth = capBefore + 50;

            SimulateColumnHeaderDragResize(grid, column, draggedWidth);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"BulkRename Current name column after a simulated drag to {draggedWidth}px is " +
                $"{column.ActualWidth}px — expected it to survive beyond the old cap {capBefore}px");

            win.Width -= 10;
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"BulkRename Current name column after a SUBSEQUENT window resize is {column.ActualWidth}px — " +
                $"expected the user's {draggedWidth}px drag to still win, not be reclamped");
        }
        finally { win.Close(); }
    });

    private static BulkRenameWindow BuildBulkRenameWindow(string currentValue, string noteValue, int rowCount = 1)
    {
        var vm = new BulkRenameViewModel();
        for (var i = 0; i < rowCount; i++)
            vm.Preview.Add(new RenameRow($"src{i}.pdf", currentValue, "SOMETHING-SHORT.pdf", noteValue,
                changed: true, manual: false, needsName: false, editSeed: "SOMETHING-SHORT.pdf",
                noteIsProblem: false));
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

    /// <summary>Same proof as MatchMerge's identical fact above, for this
    /// window's own capped Name column — see its doc comment for the full
    /// reasoning.</summary>
    [Fact]
    public void History_UserDraggedNameColumnSurvivesBeyondTheCapAndStaysPinnedAfterResize() => _fx.Invoke(() =>
    {
        var (win, history, dbPath) = BuildHistoryWindow(nameValue: VeryLongValue, routeLabelValue: "");
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var column = FindColumnByHeader(win, "Name");
            var capBefore = column.MaxWidth;
            var draggedWidth = capBefore + 50;

            SimulateColumnHeaderDragResize(grid, column, draggedWidth);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"History Name column after a simulated drag to {draggedWidth}px is " +
                $"{column.ActualWidth}px — expected it to survive beyond the old cap {capBefore}px");

            win.Width -= 10;
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"History Name column after a SUBSEQUENT window resize is {column.ActualWidth}px — " +
                $"expected the user's {draggedWidth}px drag to still win, not be reclamped");
        }
        finally { CleanupHistory(win, history, dbPath); }
    });

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
    /// production constants (WhyColumnWidth/SafetyMargin/FillerMinWidth,
    /// duplicated here — same pattern as MatchMerge/BulkRename's tests
    /// inlining their 0.35 share) and this test's own expectation can't
    /// silently drift apart.
    ///
    /// FIX ROUND 5 (2026-08-08): was hardcoded against the panel's DECLARED
    /// 440px width minus a separate 24px margin constant — matching what
    /// TriageWindow.xaml.cs used to compute once, itself. Now that the
    /// production cap is tracked live off Candidates' own ActualWidth via
    /// DataGridColumnCap.Track (which reserves
    /// SystemParameters.VerticalScrollBarWidth unconditionally, same as the
    /// other three windows' own ExpectedColumnCap below), this helper takes
    /// the window and reproduces that same viewport math instead of a
    /// hardcoded 440. WhyColumnWidth dropped from 260 to 150 in this same
    /// round — see TriageWindow.xaml.cs's own doc comment on that
    /// constant for why.</summary>
    private static double ExpectedTriageColumnCap(TriageWindow win, int headerCount, bool couldShowWhy)
    {
        const double whyColumnWidth = 150;
        const double safetyMargin = 20;
        const double fillerMinWidth = 60;
        var viewportWidth = Math.Max(0, win.Candidates.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        var rosterBudget = Math.Max(fillerMinWidth, viewportWidth - safetyMargin
            - (couldShowWhy ? whyColumnWidth : 0));
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
            // (see ExpectedTriageColumnCap) applies; "a.pdf" needs a
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
            var expectedCap = ExpectedTriageColumnCap(win, headerCount: 2, couldShowWhy: false);
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

    /// <summary>FIX ROUND 5 (2026-08-08, "columns can't be moved and are
    /// hiding text" task) — bug 1's proof. Before this round, Triage computed
    /// its roster-column cap ONCE, from SidePanelColumn's DECLARED Width
    /// (440) — but that column IS resizable (TriageWindow.xaml's
    /// GridSplitter, ResizeBehavior="PreviousAndNext"), and dragging it never
    /// revisited the cap. Measured directly, off-screen, pre-fix: widening
    /// the panel from 440 to 700px left the cap frozen at 76px both before
    /// and after. Sets SidePanelColumn's own Width directly — exactly what
    /// dragging the GridSplitter does (it changes the ColumnDefinition's
    /// Width, same mechanism this file's ShowOffscreenAtWidth/
    /// ShowOffscreenThenResizeTo use to simulate a WINDOW resize elsewhere)
    /// — then asserts the cap grew.</summary>
    [Fact]
    public void Triage_RosterColumnCapTracksLivePanelWidthNotDeclared() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: true, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            var capAtDefault = column.MaxWidth;

            win.SidePanelColumn.Width = new GridLength(700);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();
            var capAfterWidening = column.MaxWidth;

            Assert.True(capAfterWidening > capAtDefault,
                $"Triage Control ID cap at the 440px default panel width was {capAtDefault}px; after " +
                $"widening the panel to 700px it's {capAfterWidening}px — expected it to grow, proving " +
                "the cap tracks the panel's LIVE width, not its declared one");
        }
        finally { win.Close(); }
    });

    /// <summary>The assertion whose ABSENCE let this ship: this suite already
    /// had scrollbar-avoidance coverage for Triage but nothing that checked
    /// the resulting cap was actually WIDE ENOUGH, not just narrow enough to
    /// avoid a scrollbar. 120px is this codebase's own established "readable
    /// content column" floor — the star-column MinWidth MatchMergeWindow's
    /// Becomes and HistoryWindow's Original/Filed-as already use — reused
    /// here as the concrete bar rather than inventing a new one. Pre-fix
    /// (WhyColumnWidth=260, declared-width cap), this configuration measured
    /// 76px — the "hidden text" the owner reported.</summary>
    [Fact]
    public void Triage_DefaultRosterColumnCapIsWideEnoughToBeReadable() => _fx.Invoke(() =>
    {
        // the app's own default roster pick (2 headers) reviewing a
        // SUGGESTED item (the common queue, per MatchMerge.cs) — the exact
        // configuration a person sees most often opening this window.
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: true, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            Assert.True(column.MaxWidth >= 120,
                $"Triage's default roster column cap (2 headers, Why present) is {column.MaxWidth}px, " +
                "expected >= 120px to show meaningful content, not just avoid a scrollbar");
        }
        finally { win.Close(); }
    });

    /// <summary>Triage's own axis for "minimum size, many rows, no
    /// scrollbar" — its side panel doesn't resize with the window the way
    /// MatchMerge/BulkRename/History's grids do; it resizes via the
    /// GridSplitter/SidePanelColumn, down to SidePanelColumn's own declared
    /// MinWidth (380). Uses many CANDIDATES on one item (not many items —
    /// ShowCurrentAsync binds Candidates.ItemsSource to the CURRENT item's
    /// own candidate/suggestion list, so many MatchResults would still only
    /// ever show one row at a time) to force the vertical scrollbar this
    /// class's own doc comment identifies as the thing that must be reserved
    /// for. ManyRowCount matches the other three windows' own bracketed
    /// value.</summary>
    [Fact]
    public void Triage_AtMinPanelWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindowWithManyCandidates(
            new[] { "First name", "Control ID" }, suggested: true, ManyRowCount, VeryLongValue);
        try
        {
            win.SidePanelColumn.Width = new GridLength(win.SidePanelColumn.MinWidth);
            ShowOffscreenAndDriveCurrent(win);
            Assert.Contains(win.Candidates.Columns, c => (string)c.Header == "Why");
            AssertNoHorizontalScrollbar(win,
                $"Triage (at panel MinWidth {win.SidePanelColumn.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    /// <summary>Same proof as MatchMerge's identical fact above, for Triage's
    /// own capped roster column — see its doc comment for the full
    /// reasoning. The automatic-recompute step that must NOT reclamp the
    /// user's width here is a PANEL resize (SidePanelColumn/GridSplitter),
    /// Triage's own live-tracking axis, not a window resize.
    ///
    /// The subsequent resize is a MODEST one (-10px, same magnitude the other
    /// three windows' equivalent facts use), not a drop all the way to
    /// SidePanelColumn's own MinWidth (380). Diagnosed directly (temporary
    /// probe, deleted after use): a drop that drastic triggers a SEPARATE,
    /// unrelated WPF behavior — the DataGrid's own space-fitting shrinks
    /// EVERY non-floor column, not just this fix's capped ones, to try to
    /// avoid a scrollbar before giving up (measured: even the "Why" column,
    /// fixed-width and never touched by DataGridColumnCap at all, shrank from
    /// 135px to 105px on that same drop). That's WPF's own pre-existing
    /// layout behavior, outside this fix's contract ("the cap governs
    /// automatic sizing only" is about OUR MaxWidth-based cap specifically,
    /// not WPF's independent space-reclaiming) — Triage_AtMinPanelWidthNo
    /// HorizontalScrollbar above already covers that axis (no scrollbar at
    /// the panel's true floor) without conflating it with the pinning
    /// contract this fact exists to prove.</summary>
    [Fact]
    public void Triage_UserDraggedRosterColumnSurvivesBeyondTheCapAndStaysPinnedAfterPanelResize() => _fx.Invoke(() =>
    {
        var win = BuildTriageWindow(new[] { "First name", "Control ID" }, suggested: true, VeryLongValue);
        try
        {
            ShowOffscreenAndDriveCurrent(win);
            var column = win.Candidates.Columns.First(c => (string)c.Header == "Control ID");
            var capBefore = column.MaxWidth;
            var draggedWidth = capBefore + 50;

            SimulateColumnHeaderDragResize(win.Candidates, column, draggedWidth);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            // NOT ">= draggedWidth - 1", for the reason the SECOND assertion
            // in this same fact already gives below and has since it was
            // written: Triage's uniquely tight panel margin engages WPF's own
            // independent space-fitting, which shrinks the dragged column by a
            // few pixels to avoid a scrollbar. That reasoning was applied to
            // the assertion after the panel resize but not to this one, so
            // this half kept demanding pixel-exact preservation on the one
            // grid that cannot grant it — measured 211.5px against a requested
            // 219px, and failing roughly three runs in four.
            //
            // Whether WPF reclaims 5px or 8px depends on the other columns'
            // realized content, so any fixed tolerance would just be a
            // narrower race — the same lesson 41ae2f7 recorded when it tore a
            // millisecond budget out of the probe tests. The contract this
            // fact actually exists to prove is "a user-dragged column is no
            // longer clamped by the automatic cap", and capBefore is exactly
            // that line: if pinning regressed, Recalculate() would restore
            // MaxWidth to capBefore and ActualWidth could not exceed it.
            Assert.True(column.ActualWidth > capBefore,
                $"Triage Control ID column after a simulated drag to {draggedWidth}px is " +
                $"{column.ActualWidth}px — expected it to survive beyond the old cap {capBefore}px");

            win.SidePanelColumn.Width = new GridLength(win.SidePanelColumn.Width.Value - 10);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            // NOT ">= draggedWidth - 1" here (unlike MatchMerge/BulkRename/
            // History's identical facts above, whose far larger grids don't
            // trigger this): even a MODEST panel shrink measurably engages
            // WPF's own independent space-fitting on Triage's uniquely tight
            // margin (measured: 219px requested -> 214px rendered here, a
            // legitimate few-pixel WPF adjustment, not a defect — see this
            // fact's own doc comment). The assertion that actually matters is
            // "not reclamped back down to what the OLD, PRE-DRAG automatic
            // cap was" (capBefore, 169px) — that's the specific regression
            // this fix's Recalculate()-skips-pinned-columns logic exists to
            // prevent, and a few pixels of WPF's own legitimate space-fitting
            // is nowhere near it.
            Assert.True(column.ActualWidth > capBefore,
                $"Triage Control ID column after a SUBSEQUENT panel resize is {column.ActualWidth}px — " +
                $"expected it to stay clearly above the pre-drag automatic cap {capBefore}px, not be reclamped to it");
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

    /// <summary>Like BuildTriageWindow, but the CURRENT item carries
    /// <paramref name="candidateCount"/> candidates/suggestions instead of
    /// one — needed to force a vertical scrollbar (Triage_AtMinPanelWidthNo
    /// HorizontalScrollbar): ShowCurrentAsync binds Candidates.ItemsSource to
    /// the CURRENT item's own candidate list, so many MatchResults (as
    /// BuildTriageWindow's single-item batches use) would still only ever
    /// show ONE row at a time — many rows on screen means many candidates on
    /// ONE item, not many items in the batch.</summary>
    private static TriageWindow BuildTriageWindowWithManyCandidates(
        string[] headers, bool suggested, int candidateCount, params string[] columnValues)
    {
        var candidates = new List<MatchMerge.Candidate>();
        for (var i = 0; i < candidateCount; i++)
        {
            var row = new Dictionary<string, string> { [headers[0]] = "Pat" + i };
            for (var j = 1; j < headers.Length; j++) row[headers[j]] = columnValues[j - 1];
            candidates.Add(new MatchMerge.Candidate(i.ToString(), row));
        }

        var item = suggested
            ? new MatchMerge.MatchResult("doc.pdf", "suggested",
                Suggestions: candidates.Select(c => new MatchMerge.Suggestion(c, "token match")).ToList())
            : new MatchMerge.MatchResult("doc.pdf", "ambiguous", Candidates: candidates);

        return new TriageWindow(new List<MatchMerge.MatchResult> { item }, headers)
        {
            Dialogs = new FakeDialogs(),
        };
    }

    // --------------------------------------------------------- Turnaround
    //
    // FIX (final whole-branch review, feature/reports, finding 2): unlike
    // every other grid's unbounded text column in this app, Turnaround's own
    // Category column (TurnaroundWindow.xaml) shipped Width="Auto" with NO
    // DataGridColumnCap.Track call at all. Category is user-mappable to ANY
    // loaded header — the whole point of the mapping row above the grid — so
    // an Auto column with no cap just grows to fit whatever the longest
    // mapped value happens to be, CharacterEllipsis never engaging, a real
    // horizontal scrollbar. File name is this grid's own star filler
    // (Width="*", MinWidth=180); Doc date/Upload date/TAT (days) stay
    // uncapped — same reasoning History's own Undone column gives for
    // staying uncapped (short, bounded values with no realistic way to grow
    // large). This suite had NO Turnaround coverage at all before this round
    // — new coverage, not a revision of an existing fact.

    [Fact]
    public void Turnaround_LongCategoryValueAtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildTurnaroundWindowWithLongCategory(VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win,
                $"Turnaround (at MinWidth {win.MinWidth}, {ManyRowCount} rows, long category)");
        }
        finally { CleanupTurnaround(win, vm, dir); }
    });

    /// <summary>Builds a real TurnaroundWindow with <paramref name="rowCount"/>
    /// PECF-shaped rows, every one carrying <paramref name="categoryValue"/>
    /// as its SourceType (auto-guessed as CategoryColumn — "SourceType"
    /// contains the "sourcetype" needle) — so every row's Category cell is
    /// worst-cased, not just some. Each row's own FileName still carries a
    /// real 8-digit date prefix so TurnaroundTime.ComputeAll gives it a real
    /// TatDays (irrelevant to this fact, but keeps the fixture honest rather
    /// than relying on ComputeAll's "still counts even without a parseable
    /// date" fallback). Same off-thread-probe-then-poll shape every other
    /// builder in this suite (and DataGridSelectionContrastTests' own
    /// BuildTurnaroundWindow) uses for TurnaroundViewModel/ProductionViewModel's
    /// shared DebouncedProbe&lt;SweptTable.Table&gt; load.</summary>
    private static (TurnaroundWindow win, TurnaroundViewModel vm, string dir) BuildTurnaroundWindowWithLongCategory(
        string categoryValue, int rowCount)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ordo_test_tatfit_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SourceType,FileName");
        for (var i = 0; i < rowCount; i++)
            sb.AppendLine($"{categoryValue},20250101-f{i}.pdf");
        File.WriteAllText(Path.Combine(dir, "20250303-1144-PECF Report.csv"), sb.ToString());

        var vm = new TurnaroundViewModel(new Config(), new FakeDialogs(), saveCfg: null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);
        vm.AddPaths(new[] { dir });
        WaitForTurnaroundLoad(() => vm.Documents.Count == rowCount, "the turnaround fixture rows should load");

        return (new TurnaroundWindow(vm), vm, dir);
    }

    /// <summary>Same shape as ProductionViewModelTests.WaitFor/
    /// DataGridSelectionContrastTests.WaitForTurnaroundLoad — the load this
    /// builder drives is genuinely off-thread even with InlineWorkScheduler/
    /// probeDelayMs: 0 (DebouncedProbe's own Timer still fires on a
    /// threadpool thread), so Documents has to be polled for rather than
    /// asserted the instant AddPaths returns.</summary>
    private static void WaitForTurnaroundLoad(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    private static void CleanupTurnaround(Window win, TurnaroundViewModel vm, string dir)
    {
        try { win.Close(); } catch { /* best effort */ }
        vm.Dispose();
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------- Production
    //
    // FIX ROUND 6 (2026-08-09, task-7 review finding): new coverage, not a
    // revision of an existing fact — see this class's own doc comment. The
    // one configuration named in the finding — "4 ticked group columns plus
    // a sum column" — is BuildProductionWindowWithManyGroupColumns' whole
    // reason to exist: the app's own defaults only ever tick TWO group
    // columns (SOURCE-FOLDER + Employee), which a flat share can (and did)
    // get away with by accident; this suite has to actually reach four to
    // mean anything.

    /// <summary>Reproduces ProductionWindow.xaml.cs's own budget-dividing cap
    /// formula (FillerMinWidth/SafetyMargin/NumericColumnWidthEstimate,
    /// duplicated here — same pattern as ExpectedColumnCap/
    /// ExpectedTriageColumnCap above for the other three windows' own
    /// formulas) so this test doubles as a live check that the production
    /// constants and this expectation can't silently drift apart.</summary>
    private static double ExpectedProductionGroupColumnCap(DataGrid grid, int cappedColumnCount, int numericColumnCount)
    {
        const double fillerMinWidth = 120;
        const double safetyMargin = 20;
        const double numericColumnWidthEstimate = 140;
        var viewportWidth = Math.Max(0, grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        var groupBudget = Math.Max(fillerMinWidth,
            viewportWidth - safetyMargin - numericColumnCount * numericColumnWidthEstimate);
        return Math.Max(20, (groupBudget - fillerMinWidth) / cappedColumnCount);
    }

    [Fact]
    public void Production_ShortGroupValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildProductionWindowWithManyGroupColumns(ShortValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "DEPARTMENT");
            // Threshold is looser than MatchMerge's analogous check: an Auto
            // column sizes to max(header, cell content), and "DEPARTMENT" is
            // itself a 10-character header (measured 104px) — same reasoning
            // BulkRename_ShortCurrentValueMeasuresNarrow gives for its own
            // looser threshold. Still well under this fact's own cap (~193px
            // pre this round's NumericColumnWidthEstimate fix, smaller now).
            Assert.True(column.ActualWidth < 130,
                $"Production DEPARTMENT column with short content is {column.ActualWidth}px, expected < 130px");
        }
        finally { CleanupProduction(win, vm, dir); }
    });

    [Fact]
    public void Production_LongGroupValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildProductionWindowWithManyGroupColumns(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var column = FindColumnByHeader(win, "DEPARTMENT");
            // 3 capped columns (DEPARTMENT, REGION, Employee — SOURCE-FOLDER
            // is the first-ticked column, so it's the filler, not capped) and
            // 2 numeric columns (Records, PDF-PAGE-COUNT).
            var expectedCap = ExpectedProductionGroupColumnCap(grid, cappedColumnCount: 3, numericColumnCount: 2);
            Assert.True(column.ActualWidth == expectedCap,
                $"Production DEPARTMENT column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            // Finding 3 (final whole-branch review): ProductionWindow's own
            // columns now bind by INDEX, not by name (ProductionViewModel.
            // Rows' own doc comment) — the tooltip Binding's Path is
            // "[{index}]", not "[DEPARTMENT]", so the expected substring has
            // to be DEPARTMENT's live position in ColumnNames, not its name.
            var departmentIndex = vm.ColumnNames.ToList().IndexOf("DEPARTMENT");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, $"[{departmentIndex}]");
        }
        finally { CleanupProduction(win, vm, dir); }
    });

    [Fact]
    public void Production_FirstTickedGroupColumnIsTheStarFillerColumn() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildProductionWindowWithManyGroupColumns(ShortValue);
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var star = Assert.Single(grid.Columns.Where(c => c.Width.IsStar));
            Assert.Equal("SOURCE-FOLDER", star.Header);
        }
        finally { CleanupProduction(win, vm, dir); }
    });

    /// <summary>The Critical this round's fix addresses, made concrete: FOUR
    /// ticked group columns (SOURCE-FOLDER as the filler, DEPARTMENT/REGION/
    /// Employee capped) plus PDF-PAGE-COUNT as the one ticked sum column —
    /// the exact configuration named in the finding — every CAPPED column
    /// fed VeryLongValue so each is straining for room, the real worst case
    /// for this invariant (same shape as every other window's own
    /// *AllCappedColumnsAtWorstCase fact above). Pre-fix (flat 0.30 share,
    /// 3 capped columns), this alone demanded 90% of the viewport before
    /// Records/PDF-PAGE-COUNT were even counted.</summary>
    [Fact]
    public void Production_FourGroupColumnsPlusSumColumnStillFitWithNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildProductionWindowWithManyGroupColumns(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            // sanity: this really is the 4-group-columns-plus-a-sum-column
            // configuration the finding names — SOURCE-FOLDER/Employee/
            // DEPARTMENT/REGION, Records, PDF-PAGE-COUNT — not a smaller
            // configuration that would leave this fact silently vacuous.
            // (NOT a MaxWidth-based count here: a star/filler column's own
            // MaxWidth reads back as WPF's internal star-sizing sentinel,
            // 10000 — not double.PositiveInfinity — measured directly while
            // building this fact, so "how many columns have MaxWidth <
            // PositiveInfinity" silently over-counts by one and isn't a
            // reliable way to tell "how many did DataGridColumnCap.Track
            // actually cap.")
            var grid = FindDescendant<DataGrid>(win)!;
            Assert.Equal(6, grid.Columns.Count);
            AssertNoHorizontalScrollbar(win, "Production (4 group columns + 1 sum column)");
        }
        finally { CleanupProduction(win, vm, dir); }
    });

    /// <summary>Same "shown small from the start" axis as the other three
    /// windows' own AtMinWidth facts, carrying ManyRowCount DISTINCT groups
    /// (not ManyRowCount identical rows — ProductionReport.Group aggregates
    /// identical keys, so identical input rows would collapse to ONE
    /// displayed row regardless of row count; BuildProductionWindowWithManyGroupColumns
    /// gives each row its own SOURCE-FOLDER value specifically so ManyRowCount
    /// really does produce ManyRowCount displayed groups, forcing the same
    /// vertical-scrollbar-claims-width-too case the other windows' identical
    /// facts prove for their own grids) so the columns' cap actually has to
    /// shrink to account for it, the same reasoning fix round 3 (this class's
    /// own doc comment) established for the other three windows.</summary>
    [Fact]
    public void Production_AtMinWidthWithFourGroupColumnsNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildProductionWindowWithManyGroupColumns(VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win,
                $"Production (at MinWidth {win.MinWidth}, {ManyRowCount} groups, 4 group columns + 1 sum column)");
        }
        finally { CleanupProduction(win, vm, dir); }
    });

    /// <summary>Same proof as MatchMerge/BulkRename/History's identical facts
    /// above, for Production's own capped DEPARTMENT column — see MatchMerge's
    /// doc comment for the full reasoning. Only ONE DataGridColumnCap.Track
    /// registration is ever live during this test (no pick list is re-ticked
    /// after the window is built), so the "PREVIOUS Track call's handlers
    /// stay subscribed too" caveat ProductionWindow.xaml.cs's own
    /// RebuildColumns doc comment describes doesn't apply here — this proves
    /// the SAME single registration's pin-then-resize contract every other
    /// window's equivalent fact proves for its own grid.</summary>
    [Fact]
    public void Production_UserDraggedGroupColumnSurvivesBeyondTheCapAndStaysPinnedAfterResize() => _fx.Invoke(() =>
    {
        var (win, vm, dir) = BuildProductionWindowWithManyGroupColumns(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var grid = FindDescendant<DataGrid>(win)!;
            var column = FindColumnByHeader(win, "DEPARTMENT");
            var capBefore = column.MaxWidth;
            var draggedWidth = capBefore + 50;

            SimulateColumnHeaderDragResize(grid, column, draggedWidth);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            Assert.True(column.ActualWidth >= draggedWidth - 1,
                $"Production DEPARTMENT column after a simulated drag to {draggedWidth}px is " +
                $"{column.ActualWidth}px — expected it to survive beyond the old cap {capBefore}px");

            win.Width -= 10;
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            win.UpdateLayout();

            // NOT ">= draggedWidth - 1" here (unlike MatchMerge/BulkRename/
            // History's identical facts): measured directly, a modest
            // -10px window resize on Production's own grid — three capped
            // columns sharing one divided budget, tighter than MatchMerge/
            // BulkRename's one or two flat-shared columns — engages WPF's
            // own independent space-fitting by a couple of pixels (241px
            // rendered against a 243px target), the same "legitimate few-
            // pixel WPF adjustment, not a defect" Triage's own identical
            // fact already documents for its own tight margin (see
            // Triage_UserDraggedRosterColumnSurvivesBeyondTheCapAndStaysPinned
            // AfterPanelResize's doc comment). The assertion that actually
            // matters is "not reclamped back down to the OLD, PRE-DRAG
            // automatic cap" (capBefore) — that's the specific regression
            // DataGridColumnCap's Recalculate()-skips-pinned-columns logic
            // exists to prevent, and a couple of pixels of WPF's own
            // legitimate space-fitting is nowhere near it.
            Assert.True(column.ActualWidth > capBefore,
                $"Production DEPARTMENT column after a SUBSEQUENT window resize is {column.ActualWidth}px — " +
                $"expected it to stay clearly above the pre-drag automatic cap {capBefore}px, not be reclamped to it");
        }
        finally { CleanupProduction(win, vm, dir); }
    });

    /// <summary>Builds a real ProductionWindow with a fixture carrying enough
    /// extra headers to tick FOUR group columns in total: SOURCE-FOLDER
    /// (ticked first, by the app's own default — see ProductionViewModel.
    /// RebuildPicksAndResults' DefaultColumns call — so it's the FILLER, not
    /// capped) plus DEPARTMENT and REGION (plain sweep columns, ticked here)
    /// and Employee (the derived column, already ticked by default) — three
    /// CAPPED columns total, plus PDF-PAGE-COUNT as the one ticked sum
    /// column (also already a default). <paramref name="groupValue"/> feeds
    /// DEPARTMENT/REGION/FILE-OWNER directly, so the derived Employee (owner
    /// minus its ACME\ prefix) carries it too — every CAPPED column
    /// worst-cased, not just some. Each row's SOURCE-FOLDER is
    /// "{groupValue}-{row index}" — a per-row discriminator so
    /// <paramref name="rowCount"/> distinct rows really do produce
    /// <paramref name="rowCount"/> distinct GROUPS (ProductionReport.Group
    /// aggregates identical keys — see Production_AtMinWidthWithFourGroupColumns
    /// NoHorizontalScrollbar's own doc comment for why that distinction
    /// matters here and not for the other three windows' flat, ungrouped
    /// grids).</summary>
    private static (ProductionWindow win, ProductionViewModel vm, string dir) BuildProductionWindowWithManyGroupColumns(
        string groupValue, int rowCount = 1)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ordo_test_prodfit_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DATE-TIME,FILE-OWNER,FILE-NAME,ACTION,PDF-PAGE-COUNT,SOURCE-FOLDER,DEPARTMENT,REGION");
        for (var i = 0; i < rowCount; i++)
            sb.AppendLine(
                $"4/1/2025 7:55,ACME\\{groupValue},f{i}.pdf,filed,3,{groupValue}-{i},{groupValue},{groupValue}");
        File.WriteAllText(Path.Combine(dir, "20250303-1144-swept.csv"), sb.ToString());

        var vm = new ProductionViewModel(new Config(), new FakeDialogs(), saveCfg: null,
            new InlineWorkScheduler(), uiContext: null, probeDelayMs: 0);
        vm.AddPaths(new[] { dir });
        // Wait for the EXACT expected row count, not just "GroupPicks has its
        // first entry" — ApplyTable/RebuildPicksAndResults/RecomputeResults
        // all run on the probe's threadpool thread (uiContext: null), and
        // GroupPicks gets its FIRST HeaderPick added partway through that
        // same call, well before it (and _groupOrder, and Rows) are actually
        // done settling. Ticking DEPARTMENT/REGION below from THIS (test)
        // thread while that's still in flight is a genuine data race on the
        // shared, unsynchronized _groupOrder List<string> — reproduced
        // directly (temporary diagnostic, since removed): it threw
        // IndexOutOfRange inside ProductionViewModel.RecomputeResults, some
        // rows' Key built against a 2-element _groupOrder and others against
        // a 3-element one from the SAME Group() call, because _groupOrder
        // was mutated mid-iteration. Every row's SOURCE-FOLDER value below is
        // unique ("{groupValue}-{i}"), so the default (SOURCE-FOLDER +
        // Employee) grouping already produces exactly rowCount distinct
        // groups — Rows.Count only reaches rowCount once RecomputeResults'
        // own Rows.Add loop (the last thing RebuildPicksAndResults does) has
        // fully finished, which is what actually proves _groupOrder is done
        // being read and safe for this thread to mutate next. This is a
        // test-construction race, not a production one: the real app always
        // marshals ApplyTable back to the UI thread via DebouncedProbe's
        // SynchronizationContext (never null), so a real CheckBox tick can
        // never land mid-load the way this test's uiContext: null can.
        WaitForProductionLoad(() => vm.Rows.Count == rowCount, "the production fixture rows should fully load");

        // defaults already tick SOURCE-FOLDER + Employee (group) and
        // PDF-PAGE-COUNT (sum) — see ProductionViewModel.RebuildPicksAndResults
        // — so ticking these two more gets to FOUR group columns total,
        // ticked synchronously (InlineWorkScheduler; OnGroupTick has no async
        // step of its own), so vm.Rows already reflects all four the moment
        // this returns.
        vm.GroupPicks.Single(p => p.Name == "DEPARTMENT").IsChosen = true;
        vm.GroupPicks.Single(p => p.Name == "REGION").IsChosen = true;

        return (new ProductionWindow(vm), vm, dir);
    }

    /// <summary>Same shape as ProductionViewModelTests.WaitFor/
    /// DataGridSelectionContrastTests.WaitForProductionLoad — the load this
    /// builder drives is genuinely off-thread even with InlineWorkScheduler/
    /// probeDelayMs: 0 (DebouncedProbe's own Timer still fires on a
    /// threadpool thread), so the pick lists have to be polled for rather
    /// than asserted the instant AddPaths returns.</summary>
    private static void WaitForProductionLoad(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    private static void CleanupProduction(Window win, ProductionViewModel vm, string dir)
    {
        try { win.Close(); } catch { /* best effort */ }
        vm.Dispose();
        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // --------------------- PDF page counts / Merge PDFs from zip / Unzip
    //
    // 2026-08-09 Tools-menu utilities audit finding 2: these three windows'
    // Note/Result columns render ex.Message-derived text (PageCounts.Count's
    // own failure message; ZipMerge/Zipper's own merge/extract failure
    // message) but never called DataGridColumnCap.Track, unlike every other
    // Auto content column in the app — BulkRenameWindow.xaml.cs:24 and
    // MatchMergeWindow.xaml.cs:23 are the reference. A long exception message
    // therefore produced the exact horizontal scrollbar every other grid's
    // capped columns are built to prevent. Only ONE capped column per window
    // here (File/Zip is the star filler, and PageCounts' own Pages column is
    // a short uncapped number — see each window's own XAML comments), so
    // ExpectedColumnCap(win, ContentColumnShare) below reuses the SAME
    // single-share helper MatchMerge/BulkRename's own facts use, just with
    // each window's own (higher, since there's only one competing column)
    // share constant duplicated from its own XAML.CS file — same pattern
    // every other window's facts in this suite already follow.
    //
    // Builders seed rows directly through each row type's own internal
    // Apply (ZipRow/UnzipRow/PageCountRow — InternalsVisibleTo covers this
    // test assembly, the same access ZipMergeViewModelTests/
    // UnzipViewModelTests/PageCountsViewModelTests already use) rather than
    // touching the filesystem through AddFilesAsync — there is no probe to
    // debounce here (unlike FilenameListViewModel), so this is a pure
    // shortcut, not a behavior change from what AddFilesAsync would produce.

    private const double PageCountsNoteShare = 0.45;
    private const double ZipMergeResultShare = 0.45;
    private const double UnzipResultShare = 0.45;

    private static PageCountsWindow BuildPageCountsWindow(string noteValue, int rowCount = 1)
    {
        var vm = new PageCountsViewModel(new FakeDialogs());
        for (var i = 0; i < rowCount; i++)
        {
            var row = new PageCountRow($@"C:\inbox\f{i}.pdf");
            row.Apply(new PageCounts.CountResult(row.Path, null, noteValue));
            vm.Rows.Add(row);
        }
        return new PageCountsWindow(vm);
    }

    private static ZipMergeWindow BuildZipMergeWindow(string resultValue, int rowCount = 1)
    {
        var vm = new ZipMergeViewModel(new FakeDialogs());
        for (var i = 0; i < rowCount; i++)
        {
            var row = new ZipRow($@"C:\inbox\f{i}.zip");
            row.Apply(new ZipMerge.MergeResult(row.Path, "error", Message: resultValue));
            vm.Rows.Add(row);
        }
        return new ZipMergeWindow(vm);
    }

    private static UnzipWindow BuildUnzipWindow(string resultValue, int rowCount = 1)
    {
        var vm = new UnzipViewModel(new FakeDialogs());
        for (var i = 0; i < rowCount; i++)
        {
            var row = new UnzipRow($@"C:\inbox\f{i}.zip");
            row.Apply(new Zipper.UnzipResult(row.Path, "error", null, resultValue));
            vm.Rows.Add(row);
        }
        return new UnzipWindow(vm);
    }

    [Fact]
    public void PageCounts_ShortNoteValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildPageCountsWindow(ShortValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Note");
            Assert.True(column.ActualWidth < 100,
                $"PageCounts Note column with short content is {column.ActualWidth}px, expected < 100px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void PageCounts_LongNoteValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildPageCountsWindow(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Note");
            var expectedCap = ExpectedColumnCap(win, PageCountsNoteShare);
            Assert.True(column.ActualWidth == expectedCap,
                $"PageCounts Note column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Note");
        }
        finally { win.Close(); }
    });

    /// <summary>The literal proof of this task's Finding 2: before
    /// DataGridColumnCap.Track was added, this exact configuration (a long
    /// "couldn't count" message, enough rows to force a vertical scrollbar,
    /// shown at the window's own MinWidth) produced a visible horizontal
    /// scrollbar — the Note column had no cap at all and simply grew to fit
    /// the longest message. Break the fix (remove PageCountsWindow.xaml.cs's
    /// DataGridColumnCap.Track call) and this fails for a value reason — a
    /// visible scrollbar, not a render error.</summary>
    [Fact]
    public void PageCounts_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildPageCountsWindow(VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"PageCounts (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void ZipMerge_ShortResultValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildZipMergeWindow(ShortValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            Assert.True(column.ActualWidth < 100,
                $"ZipMerge Result column with short content is {column.ActualWidth}px, expected < 100px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void ZipMerge_LongResultValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildZipMergeWindow(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            var expectedCap = ExpectedColumnCap(win, ZipMergeResultShare);
            Assert.True(column.ActualWidth == expectedCap,
                $"ZipMerge Result column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Note");
        }
        finally { win.Close(); }
    });

    /// <summary>Same proof as PageCounts_AtMinWidthNoHorizontalScrollbar
    /// above, for ZipMergeWindow's own Result column — a long merge-failure
    /// exception message, ManyRowCount rows so a vertical scrollbar is also
    /// claiming part of the viewport, shown at this window's own MinWidth.</summary>
    [Fact]
    public void ZipMerge_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildZipMergeWindow(VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"ZipMerge (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Unzip_ShortResultValueMeasuresNarrow() => _fx.Invoke(() =>
    {
        var win = BuildUnzipWindow(ShortValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            Assert.True(column.ActualWidth < 100,
                $"Unzip Result column with short content is {column.ActualWidth}px, expected < 100px");
        }
        finally { win.Close(); }
    });

    [Fact]
    public void Unzip_LongResultValueStopsAtTheCapWithEllipsisAndTooltip() => _fx.Invoke(() =>
    {
        var win = BuildUnzipWindow(VeryLongValue);
        try
        {
            ShowOffscreen(win);
            var column = FindColumnByHeader(win, "Result");
            var expectedCap = ExpectedColumnCap(win, UnzipResultShare);
            Assert.True(column.ActualWidth == expectedCap,
                $"Unzip Result column with long content is {column.ActualWidth}px, " +
                $"expected exactly its cap {expectedCap}px");
            AssertTrimmingAndTooltip((DataGridBoundColumn)column, "Note");
        }
        finally { win.Close(); }
    });

    /// <summary>Same proof as PageCounts_AtMinWidthNoHorizontalScrollbar
    /// above, for UnzipWindow's own Result column — a long extract-failure
    /// exception message, ManyRowCount rows, shown at this window's own
    /// MinWidth.</summary>
    [Fact]
    public void Unzip_AtMinWidthNoHorizontalScrollbar() => _fx.Invoke(() =>
    {
        var win = BuildUnzipWindow(VeryLongValue, ManyRowCount);
        try
        {
            ShowOffscreenAtWidth(win, win.MinWidth);
            AssertNoHorizontalScrollbar(win, $"Unzip (at MinWidth {win.MinWidth}, {ManyRowCount} rows)");
        }
        finally { win.Close(); }
    });

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

    /// <summary>FIX ROUND 5 (2026-08-08): simulates a real user's column-header
    /// drag-resize without a real mouse (this session cannot drive one — see
    /// this repo's own standing note on that). Finds <paramref name="column"/>'s
    /// realized <see cref="DataGridColumnHeader"/> and the resize
    /// <see cref="Thumb"/> inside its (unmodified — Theme/Styles.xaml only
    /// carries Setters for DataGridColumnHeader, no ControlTemplate override,
    /// so the default template's PART_RightHeaderGripper/PART_LeftHeaderGripper
    /// Thumbs are present) template, then raises the exact routed events
    /// DataGridColumnCap.Track listens for: DragStarted (which it responds to
    /// by relaxing MaxWidth so the drag isn't blocked), then sets the column's
    /// Width directly to <paramref name="newWidth"/> — this is what WPF's own
    /// internal DragDelta handler computes once MaxWidth no longer clamps it,
    /// converting Width from Auto to a fixed pixel value — then raises
    /// DragCompleted (which Track responds to by reading that Auto-to-pixel
    /// change as "this column was the one dragged" and pinning it). Does NOT
    /// call UpdateLayout() itself — the caller decides when to flush layout,
    /// same as every other resize helper in this file.</summary>
    private static void SimulateColumnHeaderDragResize(DataGrid grid, DataGridColumn column, double newWidth)
    {
        var header = FindAllDescendants<DataGridColumnHeader>(grid).FirstOrDefault(h => h.Column == column)
            ?? throw new InvalidOperationException(
                $"no realized DataGridColumnHeader for column '{column.Header}'");
        var thumb = FindDescendant<Thumb>(header)
            ?? throw new InvalidOperationException(
                $"no resize Thumb found under the header for column '{column.Header}'");

        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        column.Width = new DataGridLength(newWidth);
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
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

    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }
}
