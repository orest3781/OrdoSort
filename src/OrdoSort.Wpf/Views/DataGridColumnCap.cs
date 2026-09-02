using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrdoSort.Wpf.Views;

/// <summary>Autofit for a DataGrid's text columns: every column gets the
/// width most of its content needs when the total fits the grid, and when
/// it doesn't, the shortfall is shared in proportion to content width — no
/// column below its MinWidth, and none below its own header. A column takes
/// the 80th percentile of its content rather than its widest cell, so one
/// very long value cannot drag it wide at every other column's expense.
/// Content that still doesn't fit is trimmed with an ellipsis and carries
/// its full text as a tooltip (GridCellText's shared Setters, plus
/// TrimmedTextTooltip) — so nothing is lost, it is one hover away. No
/// horizontal scrollbar, nothing for the user to readjust.
///
/// Cells used to WRAP instead, which is what this paragraph described until
/// the table-rules change of 2026-09-02: the owner asked for the opposite
/// once one outlier filename had widened a column too far, and uniform row
/// heights turned out to be half of what "janky" meant.
///
/// Until 2026-08-29 this class capped the message column at whatever was
/// left after the name column's floor, and the name column got the
/// leftovers — six fix rounds of it, all in git history. The owner's
/// report that ended it: "the columns don't autofit, hiding text, and I
/// have to readjust every time." Both halves were that rule: the message
/// column cut names down to their floor and trimmed itself with an
/// ellipsis, and every window reopened with the same squeeze.
///
/// SEVEN windows depend on this class through <see cref="Track(DataGrid,
/// DataGridColumn[])"/>: History, MatchMerge, BulkRename, PageCounts,
/// ZipTools, MergePdfs and StandardiseNames (the last joined after this
/// count was first written — flagged here rather than left to go stale a
/// second time). Triage supplies its own budget through the
/// <see cref="Func{Double,Double}"/> overload and is untouched by the
/// autofit rule. FilenameListWindow calls neither.
///
/// HOW. Track keeps every governed column's MaxWidth at its share,
/// recomputed after every layout pass (SizeChanged fires before the other
/// columns have laid out, so LayoutUpdated is the one that reads settled
/// numbers; assignments happen only when a cap actually moves, which is
/// what lets the LayoutUpdated cycle end). The share comes from
/// <see cref="ColumnShares.Compute"/> over the PARTICIPANTS — the governed
/// columns plus every visible star column in the grid — against what the
/// viewport has left after the fixed claims: absolute widths, and every
/// other column at its live width (an untracked Auto column has bounded
/// content — a date, a count, a tag — so what it measures is what it
/// needs; a column the user has dragged is absolute, and theirs).
///
/// A star column is a participant, and — fix round 1 (2026-08-29 review) —
/// its own star WEIGHT is set to carry the share <see cref="ColumnShares"/>
/// computed for it: <c>Width = new DataGridLength(share, Star)</c>, not the
/// flat factor of 1 every star column starts with. HistoryWindow has TWO
/// (Original, Filed as) in the SAME grid, and leaving both at weight 1 would
/// have WPF's own leftover distribution split them 1:1 regardless of
/// content — the doc paragraph below on MEASURING was already promising a
/// proportional split; a second star column is where a flat weight would
/// have silently broken that promise. The star column's MaxWidth is still
/// never touched — capping it would fight WPF's own star reconciliation —
/// only its Width factor changes, and only while it's still genuinely a
/// star: a column the user has since dragged reads Width.IsStar == false
/// (WPF itself converts it to an absolute pixel value the moment the drag
/// starts, the same conversion a governed Auto column undergoes), so it is
/// no longer classified as a participant at all — it is folded into the
/// fixed claims instead, at its own dragged width, and this class never
/// writes to it again.
///
/// (The star column's computed SHARE and its rendered WIDTH are not the
/// same number, and by design: <c>available</c> is the viewport net of
/// SafetyMargin and the vertical-scrollbar reservation, both sized for the
/// worst case, but neither is actually SPENT unless something claims it —
/// no scrollbar, no untracked column growing into the margin. What isn't
/// spent doesn't vanish; it lands back in the star column, because WPF's own
/// star reconciliation gives it whatever the OTHER columns' resolved widths
/// leave, not the share this class computed for accounting purposes. On a
/// single-star-participant grid that is every unspent pixel of both
/// reservations at once — measured ~37px on this machine, SafetyMargin(20) +
/// SystemParameters.VerticalScrollBarWidth(~17) — so the star column renders
/// that much WIDER than its own computed share, every time neither
/// reservation is actually needed.)
///
/// MEASURED, NOT READ BACK. A column's DESIRED width (table-rules, Rule 3)
/// is the <see cref="AutofitPercentile"/> of its realized cells' own
/// measured widths — FormattedText in each cell's own font, cached per
/// string — NOT the widest one any more, and NOT folded together with the
/// header any more either: the header floor is enforced once, separately,
/// in <c>floors</c> (<see cref="AutofitCaps"/>), the same number this
/// figure used to also seed itself with redundantly. WPF's own
/// Width.DesiredValue would be cheaper than measuring at all, but it only
/// ever grows (measured 2026-08-29: 802px after the long row was removed),
/// so a class that read it could never shrink a column back. Measuring is
/// exact (801.53px against WPF's 802px for the same string; the default
/// DataGridCell template applies no padding), and <see cref="MeasureSlack"/>
/// covers the rounding so a one-line cell is never wrapped by its own cap.
/// Capping at the measured width in the fit case — rather than relaxing to
/// infinity — is the whole shrink-back mechanism: an Auto column displays
/// at min(desired, MaxWidth).
///
/// Only realized rows are measured, the same population WPF sizes an Auto
/// column from — and this cuts BOTH ways, unlike plain WPF Auto (which only
/// ever grows; see MEASURED, NOT READ BACK above). A column can still widen
/// or narrow as the realized set changes while scrolling — this class
/// re-measures that set from scratch every pass rather than remembering a
/// high-water mark, the pre-existing behaviour docs/superpowers/plans/
/// 2026-08-14-column-stability-while-scrolling.md measured and left alone —
/// but Rule 3 changes how much any ONE row can move it: under the OLD rule
/// (widest cell wins) a single outlier scrolling into view immediately
/// widened the column to match it, and scrolling it back out immediately
/// narrowed it again. Under the percentile, a lone outlier among several
/// realized rows typically moves the percentile little or not at all — by
/// design; that damping IS Rule 3 — so "scroll a long row into view, watch
/// the column jump to match it" is no longer generally true the way it was
/// before this change. It is still true with the realized set at or below
/// four rows, where the percentile picks the row's own maximum — not
/// because the function branches on that range specially (only zero rows
/// and exactly one are actual explicit branches; see
/// <see cref="ContentWidths.PercentileOf"/> for both), but because
/// nearest-rank's own formula, ceil(0.8 × count), lands on the top rank for
/// any count from one through four and only starts landing below it at
/// five — so a small, typical grid still visibly reacts to a single long
/// value scrolling in — only a LARGER realized set damps it the way Rule 3
/// intends.
///
/// Measured directly before this class existed and plain WPF Auto was still
/// the mechanism (2026-08-07 autofit-columns round, recorded in
/// HistoryWindow.xaml): "a history seeded with 3000 rows (one, at
/// index 1500, holding a ~185-character path) and Original's column
/// temporarily switched to Width="Auto" in a throwaway test — same
/// off-screen Show()+UpdateLayout() shape as every other headless test here.
/// Two back-to-back layout passes over the same top-of-list realized rows
/// held steady at 173px; scrolling the long-path row into view (forcing the
/// virtualizer to realize it) jumped the SAME column to 410px; scrolling
/// back to the top afterward did NOT shrink it back down — it stayed at
/// 410px." That last sentence is exactly the half plain WPF Auto cannot do
/// and this class's own re-measurement now can.
///
/// The consequence: row height is cap-dependent (a narrower cap wraps a cell
/// onto more lines, which grows its row — see AWrappedCellGrowsItsRowRatherThanClippingTheText),
/// and row height decides how many rows fit on screen and therefore which
/// ones the virtualizer realizes. Cap -> row height -> realized set ->
/// measured width -> cap is a feedback path, not a one-shot calculation: a
/// cap change can shift which rows are realized on the NEXT pass, which
/// this class then measures again. It settles rather than oscillates because
/// the epsilon guard in Recalculate is what lets a converged state
/// terminate — skipping the assignment once two consecutive passes compute
/// the same cap ends the LayoutUpdated cycle, but does not itself cause the
/// convergence: a two-value oscillation (A -> B -> A) never reads as
/// "unchanged" pass to pass, so the guard alone could not stop one. What
/// actually converges is <see cref="ColumnShares.Compute"/>'s own
/// arithmetic, because its inputs — viewport width, non-participant claims,
/// the FormattedText-measured content strings, the floors — do not
/// themselves feed back from the assignment this class makes. The honest
/// caveat: the cap -> row height -> realized set -> measured width -> cap
/// path described immediately above is the one route by which an input CAN
/// move in response to a cap, which is why that paragraph matters.
///
/// If the realized set is momentarily EMPTY while the grid still has items
/// (mid-scroll; the instant after a bulk Clear before new rows realize),
/// this class leaves every cap exactly where it was rather than collapsing
/// every column to its header width and snapping back next pass — see
/// <see cref="AutofitCaps"/>. When the grid genuinely has no items, header-
/// only sizing is correct and still happens.
///
/// A user's drag still wins: DragStarted relaxes every not-yet-pinned
/// governed column so WPF's live clamp can't block the gesture;
/// DragCompleted reads "Width became absolute" as "this one was dragged"
/// and pins it for the window's lifetime — out of the governed set, in
/// with the fixed claims. Track is idempotent per grid: a second call
/// detaches the first call's handlers before subscribing its own.
///
/// SCOPE OF THE GUARANTEE. "No horizontal scrollbar" holds for AUTOMATIC
/// sizing, which is all this class governs — it does not survive a column
/// the user has dragged. A dragged column's width is the user's, not
/// reclaimable, counted in full among the fixed claims; drag one wide
/// enough afterward and the grid can genuinely run out of room, and WPF's
/// own space-fitting is what resolves it from there (typically a few
/// pixels shaved off the dragged column itself, not a new scrollbar) —
/// exactly the boundary AutoFitColumnTests.
/// MatchMerge_UserDraggedFileColumnSurvivesBeyondTheCapAndStaysPinnedAfterResize
/// landed on: a cap that legitimately grew larger under the proportional
/// rule left a dragged column with less headroom than the old, smaller
/// remainder cap did, and the last couple of pixels of a subsequent resize
/// were WPF's to reclaim, not this class's to prevent.
///
/// WHAT A PASS COSTS. LayoutUpdated fires after EVERY layout pass the
/// dispatcher runs, ANYWHERE in the app, not just on this grid — so
/// whatever runs here taxes every hover, tooltip and animation tick for as
/// long as a tracked window stays open. Per pass: the realized rows come
/// from the grid's DataGridRowsPresenter's own Children (found once, cached,
/// same pattern as the header lookup below) rather than probing
/// ContainerFromIndex once per item in <c>grid.Items</c> — O(realized), not
/// O(items), which matters once HistoryWindow's "Show all" is holding
/// thousands of rows with a screenful realized. Each participant's
/// DataGridColumnHeader is found by one tree walk and cached per column,
/// not re-walked every pass. The FormattedText measurement itself is cached
/// per distinct string+font+DPI, bounded (<see cref="ContentWidths"/>) so a
/// long-lived window cannot grow the cache without bound.
///
/// What stayed, said plainly rather than left implied: this is O(realized
/// rows × participants), not O(realized rows) — <see cref="ContentWidths.Of"/>
/// walks the full realized set again for EVERY participant column; there is
/// no single shared pass across columns. Each cell, hit or miss, pays a
/// <see cref="VisualTreeHelper.GetDpi"/> call and a dictionary hash over a
/// nine-field tuple (text, font family, size, weight, style, stretch, flow
/// direction, formatting mode, DPI) to even ask the cache — cheap per cell,
/// not free at thousands of cells. And the header-to-column lookup being
/// cached (above) is not the whole header cost: <see cref="ContentWidths.HeaderWidthOf"/>'s
/// own <c>FindDescendant&lt;TextBlock&gt;</c> walk from that cached header
/// down to its own text is NOT cached — it re-walks once per participant per
/// pass regardless, the one tree walk in this class that a cache hit does
/// not avoid.</summary>
internal static class DataGridColumnCap
{
    /// <summary>Keeps the arithmetic off the exact viewport edge, where a
    /// rounding difference decides whether a scrollbar appears, and
    /// absorbs an untracked Auto column growing between recomputes.</summary>
    private const double SafetyMargin = 20;

    /// <summary>Floor under any cap — WPF's own default DataGridColumn.
    /// MinWidth, the value a column silently carries when it declares none
    /// of its own. That describes every GOVERNED column in this app (none
    /// declares a MinWidth); only the star FILLER columns declare a real
    /// one (120/180px), which is why this floor alone was never enough to
    /// protect a governed column's own header — see the header floor added
    /// below. WPF's own space-fitting resolves the layout below either
    /// floor.</summary>
    private const double MinimumCap = 20;

    /// <summary>Added to every measured width so a rounding difference
    /// between FormattedText and the layout engine can never wrap a cell
    /// that fits on one line.</summary>
    private const double MeasureSlack = 1;

    /// <summary>Table-rules, Rule 3: a column's desired width is this
    /// percentile of its realized cells' measured widths, not the maximum —
    /// the fix for the governing defect the owner reported ("i simply want
    /// to prevent 1 really long filename to make the column too wide"), so
    /// one outlier no longer sets the whole column's width. 0.80 is the
    /// controller's own ruling in requirements.md: the owner asked for
    /// roughly the longest 10-20% to be the ones that overflow, and the 80th
    /// percentile is the middle of that range. THIS IS A TUNING KNOB, not a
    /// derived constant — the one number in this whole feature the owner is
    /// expected to want adjusted after seeing it against real data, which is
    /// exactly why it lives here, alone, named, rather than inlined into the
    /// arithmetic that uses it (<see cref="ContentWidths.PercentileOf"/>).</summary>
    private const double AutofitPercentile = 0.80;

    /// <summary>Autofit-then-wrap for <paramref name="columns"/> — the
    /// grid's Auto text columns that may wrap. Star columns are found from
    /// the grid itself and need not be passed.</summary>
    public static void Track(DataGrid grid, params DataGridColumn[] columns) =>
        TrackCore(grid, computeCap: null, columns);

    /// <summary>Same live tracking, but every governed column's cap is
    /// <paramref name="computeCap"/> applied to the grid's live column
    /// viewport width (net of the vertical-scrollbar reservation) — for
    /// TriageWindow, whose budget depends on how many roster columns exist
    /// and whether its fixed "Why" column might appear.</summary>
    public static void Track(DataGrid grid, Func<double, double> computeCap, params DataGridColumn[] columns) =>
        TrackCore(grid, computeCap, columns);

    private static void TrackCore(DataGrid grid, Func<double, double>? computeCap, DataGridColumn[] columns)
    {
        var pinned = new HashSet<DataGridColumn>();
        var widths = new ContentWidths();
        // Fix round 1 (correctness review), table-rules Rule 5: captured
        // ONCE, before this class ever writes to a governed column's own
        // MinWidth (see the Rule 5 write-back below), so AutofitCaps' own
        // floors computation can keep using each column's TRUE declared
        // floor even after a later pass raises MinWidth to force a blank
        // column's adopted width to actually render. Reading column.MinWidth
        // LIVE there instead would ratchet: a raised MinWidth becomes the
        // very next pass's own floor input, permanently locking in the
        // highest value this class has ever written — never able to shrink
        // back down even once a borrowed-from neighbour narrows or the
        // column's own blankness ends. Keyed by reference (DataGridColumn
        // overrides neither Equals nor GetHashCode), the same identity
        // every other lookup in this file already relies on.
        var declaredMinWidth = columns.ToDictionary(c => c, c => c.MinWidth);

        void Recalculate()
        {
            // 0 before the grid's first layout pass — nothing to size
            // against yet; SizeChanged fires the moment a real width exists.
            if (grid.ActualWidth <= 0) return;
            var columnViewportWidth = Math.Max(0, grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);

            var governed = columns
                .Where(column => !pinned.Contains(column) && column.Visibility == Visibility.Visible)
                .ToArray();
            if (governed.Length == 0) return;

            if (computeCap is not null)
            {
                // TriageWindow's own budget: no Rule 5 concept here at all
                // (that overload is untouched by the autofit rule — see the
                // class doc), so MinWidth is never written, only MaxWidth.
                var cap = computeCap(columnViewportWidth);
                for (var i = 0; i < governed.Length; i++)
                    if (Math.Abs(governed[i].MaxWidth - cap) > 0.5) governed[i].MaxWidth = cap;
                return;
            }

            var result = AutofitCaps(grid, columnViewportWidth, governed, widths, declaredMinWidth);
            // null: the realized set is momentarily empty while the grid
            // still has items (mid-scroll, or the instant after a Clear
            // before new rows realize) — keep every cap exactly where it
            // was rather than collapse to header widths and snap back.
            if (result is null) return;
            var (caps, blankSubstituted) = result.Value;

            // Assign only what moved: an assignment invalidates layout, which
            // raises LayoutUpdated, which recomputes — so unconditional
            // assignment would never let the cycle end.
            for (var i = 0; i < governed.Length; i++)
            {
                if (Math.Abs(governed[i].MaxWidth - caps[i]) > 0.5) governed[i].MaxWidth = caps[i];

                // Fix round 1, table-rules Rule 5: MaxWidth alone is a
                // ceiling, and an Auto column displays at min(desired,
                // MaxWidth) — a blank column's own DESIRED width is its bare
                // header, already well under any cap this class would ever
                // compute, so raising the ceiling alone changes nothing on
                // screen (the CRITICAL this fix round found: Rule 5 was
                // inert). Forcing MinWidth up to the SAME adopted cap is what
                // actually moves the rendered width — MinWidth is a hard
                // floor WPF's own column-width resolution cannot undercut
                // the way it can ignore a MaxWidth ceiling nothing is
                // pressing against. Written EVERY pass, for every governed
                // column, one way or the other: a column that stops being
                // blank-substituted (borrowed content arrived, or a
                // still-blank column's own substitution lapsed) resets to
                // ITS OWN declared MinWidth in the same breath, so nothing
                // here can outlive the condition that set it.
                var wantedMinWidth = blankSubstituted.Contains(i) ? caps[i] : declaredMinWidth[governed[i]];
                if (Math.Abs(governed[i].MinWidth - wantedMinWidth) > 0.5) governed[i].MinWidth = wantedMinWidth;
            }
        }

        (grid.GetValue(DetachProperty) as Action)?.Invoke();

        void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Recalculate();
        grid.SizeChanged += OnSizeChanged;

        var recomputing = false;
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (recomputing) return;
            recomputing = true;
            try { Recalculate(); }
            finally { recomputing = false; }
        }
        grid.LayoutUpdated += OnLayoutUpdated;

        Recalculate();

        // handledEventsToo: nothing in this app's column templates marks
        // these handled today; a future template that did would silently
        // break the drag fix otherwise.
        var dragStarted = new DragStartedEventHandler((_, _) =>
        {
            foreach (var column in columns)
                if (!pinned.Contains(column)) column.MaxWidth = double.PositiveInfinity;
        });
        grid.AddHandler(Thumb.DragStartedEvent, dragStarted, true);

        var dragCompleted = new DragCompletedEventHandler((_, _) =>
        {
            foreach (var column in columns)
                if (!pinned.Contains(column) && column.Width.IsAbsolute) pinned.Add(column);
            Recalculate();
        });
        grid.AddHandler(Thumb.DragCompletedEvent, dragCompleted, true);

        grid.SetValue(DetachProperty, new Action(() =>
        {
            grid.SizeChanged -= OnSizeChanged;
            grid.LayoutUpdated -= OnLayoutUpdated;
            grid.RemoveHandler(Thumb.DragStartedEvent, dragStarted);
            grid.RemoveHandler(Thumb.DragCompletedEvent, dragCompleted);
        }));
    }

    /// <summary>Holds the Action that detaches the current Track call's
    /// handlers, so the next Track call on the same grid can run it first.
    /// An attached property rather than a static table because it lives
    /// and dies with the grid.</summary>
    private static readonly DependencyProperty DetachProperty =
        DependencyProperty.RegisterAttached(
            "Detach", typeof(Action), typeof(DataGridColumnCap), new PropertyMetadata(null));

    /// <summary>One cap per governed column, in the same order, PLUS the
    /// subset of governed indices Rule 5 blank-substituted this pass (see
    /// <see cref="ApplyEmptyColumnNeighbourRule"/>) — the caller needs to
    /// know which ones to also force via MinWidth, not just cap via
    /// MaxWidth. Null if the realized-rows guard (see the class doc) says to
    /// skip this pass entirely. As a side effect, also sets each STAR
    /// participant's own Width factor to the share <see cref="ColumnShares"/>
    /// computed for it, so a grid with more than one star column
    /// (HistoryWindow) splits its leftover in proportion to content rather
    /// than evenly.</summary>
    private static (double[] Caps, HashSet<int> BlankSubstituted)? AutofitCaps(
        DataGrid grid, double viewportWidth, DataGridColumn[] governed, ContentWidths widths,
        Dictionary<DataGridColumn, double> declaredMinWidth)
    {
        var rows = widths.RealizedRows(grid);
        if (rows.Count == 0 && grid.Items.Count > 0) return null;

        var governedSet = new HashSet<DataGridColumn>(governed);
        var participants = new List<DataGridColumn>(governed);
        var claimed = 0.0;
        foreach (var column in grid.Columns)
        {
            if (governedSet.Contains(column) || column.Visibility != Visibility.Visible) continue;
            // A star column the user has dragged reads IsStar == false from
            // here on (WPF itself converted Width to an absolute pixel
            // value the moment the drag started) — it falls straight into
            // the claimed branch below, at its own dragged width, and never
            // becomes a participant again.
            if (column.Width.IsStar) participants.Add(column);
            else claimed += column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        }

        var available = viewportWidth - claimed - SafetyMargin;
        // Header width computed ONCE per participant per pass, not twice:
        // HeaderWidthOf's own header-TO-column lookup is cached, but the
        // FindDescendant<TextBlock> walk from a cached header down to its
        // text is not (see that method's own doc comment) — calling it here
        // AND inside Of() would silently double that walk every pass. Of()
        // no longer needs it at all (table-rules, Rule 3) — this is now the
        // header's only consumer.
        var headerWidths = participants.Select(column => widths.HeaderWidthOf(grid, column)).ToList();
        var natural = participants.Select(column => widths.Of(column, rows)).ToList();
        // Table-rules, Rule 5: a column whose realized cells are ALL blank
        // matches its visual neighbour's own desired width instead of
        // collapsing toward its bare header — see this method's own doc
        // comment for the full rule and why it runs here, on "natural",
        // rather than after the split below.
        var blankSubstituted = ApplyEmptyColumnNeighbourRule(grid, participants, natural, rows);
        // Floor #3, added 2026-08-29 review: a column's own header never
        // wraps or trims (DataGridColumnHeader hard-clips), so a share
        // computed below the header's width would clip it silently. Without
        // this, every unheld participant in the non-fit branch gets
        // wanted*(available/Sigma-wanted) < wanted — including a column
        // whose only "wanted" width IS its header (empty or short cells) —
        // and MinimumCap/MinWidth alone (20px on every governed column in
        // this app; none declares a MinWidth) did nothing to stop it.
        // declaredMinWidth, not column.MinWidth: see that dictionary's own
        // doc comment (TrackCore) for the ratchet a live read would cause,
        // now that Rule 5 (below) writes a governed column's MinWidth back.
        // A star participant is never a key in it (only ever built from the
        // GOVERNED columns Track() was called with), so the live read is
        // still exactly right for one — this class never writes a star
        // column's MinWidth, only its Width factor, so there is nothing for
        // it to ratchet against.
        var floors = participants
            .Select((column, i) => Math.Max(
                Math.Max(MinimumCap, declaredMinWidth.TryGetValue(column, out var d) ? d : column.MinWidth),
                headerWidths[i]))
            .ToList();
        var shares = ColumnShares.Compute(available, natural, floors);

        // Star participants: carry the share in the star WEIGHT itself
        // rather than assigning anything — a star column's rendered width
        // is WPF's to resolve, not ours, so writing its Width factor (not
        // its MaxWidth) is what lets WPF's OWN leftover distribution
        // reproduce this proportional split among two or more of them.
        // Same epsilon guard as the MaxWidth assignment below and for the
        // same reason: an unconditional Width write would invalidate
        // layout every pass and the LayoutUpdated cycle would never settle.
        //
        // Comparing column.Width.Value — a star WEIGHT, a unitless ratio —
        // against "share" — a PIXEL count — only makes sense because this is
        // the one place that weight is ever written: it is always literally
        // last pass's pixel share, never renormalized to sum to 1 or to any
        // other scale, so pass-over-pass it stays directly comparable to
        // this pass's pixel share without conversion. Renormalizing the
        // weights later (summing them to 1, say, the way star weights are
        // often described) would make this comparison permanently true —
        // a fraction near 1 is never within 0.5 of a pixel count — so the
        // guard would never again see "unchanged" and would write Width
        // every single pass. That is not a wrong pixel, which a screenshot
        // would catch; it is an unconditional invalidation on every
        // LayoutUpdated, forever, which is an app-wide CPU spin with nothing
        // on screen to blame it on.
        for (var i = governed.Length; i < participants.Count; i++)
        {
            var column = participants[i];
            var share = shares[i];
            if (Math.Abs(column.Width.Value - share) > 0.5)
                column.Width = new DataGridLength(share, DataGridLengthUnitType.Star);
        }

        return (shares.Take(governed.Length).ToArray(), blankSubstituted);
    }

    /// <summary>Table-rules, Rule 5: a column whose realized cells are ALL
    /// blank (<see cref="IsBlank"/>) collapses to its own header width
    /// today — a bare Math.Max(0, floor) once "natural" is 0 — which leaves
    /// the header row looking uneven next to a full column beside it.
    /// Substitutes such a column's entry in <paramref name="natural"/>, IN
    /// PLACE, with the DESIRED width of the column immediately to its LEFT
    /// in the grid's own on-screen order (<see
    /// cref="DataGridColumn.DisplayIndex"/>, so a user's own column drag is
    /// respected) — or its RIGHT if it is the first visible column in the
    /// grid. One hop, exactly what the rule says, not a walk past a
    /// neighbour that is ALSO blank: every neighbour width this method reads
    /// comes from a SNAPSHOT of <paramref name="natural"/> taken before any
    /// substitution runs, so two adjacent blank columns never chain through
    /// each other — the second one simply reads the first's own
    /// pre-substitution (near-zero) figure and falls back to ITS OWN header
    /// floor downstream, exactly as if this rule had never run for it. This
    /// never lowers a column below its own header either way: <c>floors</c>,
    /// computed by the caller AFTER this returns, still applies
    /// Math.Max(natural, floor) per column regardless of where "natural"
    /// came from — Rule 3's floor guarantee is unconditional on Rule 5
    /// having touched a column or not.
    ///
    /// The neighbour can be a fellow PARTICIPANT (governed or star — its own
    /// entry in <paramref name="natural"/>) or an ordinary claimed column
    /// this class does not govern at all (PageCountsWindow's own Pages sits
    /// between its star filler and its governed Note, exactly this second
    /// case) — read off that column's own live rendered width, the same
    /// number AutofitCaps' own "claimed" accounting above already uses for
    /// a non-participant.
    ///
    /// A genuinely empty GRID (zero rows) is left alone entirely — every
    /// cell is vacuously "blank" with no rows to call blank, and a header-
    /// only sizing is the correct answer for that case, not a neighbour
    /// match; this rule only ever engages once there is at least one REAL
    /// row and it happens to hold nothing for this column. A column with
    /// SOME blank cells and some filled ones is not empty either — see
    /// <see cref="IsBlank"/> — and is left to Rule 3's percentile exactly as
    /// before.
    ///
    /// Returns the PARTICIPANT indices this call actually substituted —
    /// AutofitCaps' own caller (TrackCore) needs to know which governed
    /// columns to also force via MinWidth, since a cap alone (MaxWidth) is a
    /// ceiling a blank column's own small desired width never presses
    /// against (fix round 1, the CRITICAL this rule shipped inert with).
    /// Two ADJACENT blank columns still cannot chain through each other even
    /// though both end up in this set: the second reads the first's own
    /// PRE-substitution (near-zero) figure from <c>original</c>, which then
    /// floors at the second column's own header downstream — substituted in
    /// name, but not in any way that moves its final width away from where
    /// it would have landed anyway.</summary>
    private static HashSet<int> ApplyEmptyColumnNeighbourRule(
        DataGrid grid, List<DataGridColumn> participants, List<double> natural, List<DataGridRow> rows)
    {
        var substituted = new HashSet<int>();
        if (rows.Count == 0) return substituted;
        var original = natural.ToList();
        var visualOrder = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        double? NeighbourWidth(DataGridColumn neighbour)
        {
            var participantIndex = participants.IndexOf(neighbour);
            return participantIndex >= 0
                ? original[participantIndex]
                : neighbour.Width.IsAbsolute ? neighbour.Width.Value : neighbour.ActualWidth;
        }

        for (var i = 0; i < participants.Count; i++)
        {
            if (!IsBlank(participants[i], rows)) continue;

            var position = visualOrder.IndexOf(participants[i]);
            if (position < 0) continue;   // not laid out yet this pass — the header floor alone is correct

            var neighbour = position > 0 ? visualOrder[position - 1]
                : position + 1 < visualOrder.Count ? visualOrder[position + 1]
                : null;
            if (neighbour is not null && NeighbourWidth(neighbour) is { } width)
            {
                natural[i] = width;
                substituted.Add(i);
            }
        }
        return substituted;
    }

    /// <summary>Rule 5's own definition of "empty": every rendered value in
    /// <paramref name="column"/>, across every realized row, is null or
    /// whitespace — not merely the empty string, so a column of single-space
    /// placeholders reads as empty too. A row whose cell is not a TextBlock
    /// at all (never happens for a governed column in this app today; see
    /// <see cref="ContentWidths.Of"/>'s own matching comment) counts as NOT
    /// blank rather than guessed at — the same conservative default that
    /// method already applies for content it cannot classify.</summary>
    private static bool IsBlank(DataGridColumn column, List<DataGridRow> rows) =>
        rows.All(row => column.GetCellContent(row) is TextBlock text && string.IsNullOrWhiteSpace(text.Text));

    /// <summary>Content widths from the realized cells and the header,
    /// cached per string so a layout pass costs a dictionary lookup per
    /// visible cell. Font properties are part of the key: BulkRename bolds
    /// a hand-edited row, and Settings can change the app's type scale
    /// while a window is open.</summary>
    private sealed class ContentWidths
    {
        /// <summary>Bounds the measurement cache so a long-lived window (a
        /// BulkRename over thousands of filenames, a History left open all
        /// day) cannot grow it without bound. Cleared wholesale rather than
        /// evicted entry-by-entry when hit: a cold rebuild of a few hundred
        /// visible cells' worth of distinct strings is cheap (see the class
        /// doc's per-pass cost paragraph), and that simplicity beats an LRU
        /// here.</summary>
        private const int MaxMeasuredEntries = 4096;

        private readonly Dictionary<(string Text, string Family, double Size, FontWeight Weight, FontStyle Style,
            FontStretch Stretch, FlowDirection FlowDirection, TextFormattingMode FormattingMode, double PixelsPerDip),
            double> _measured = new();
        private DataGridColumnHeadersPresenter? _headers;
        private DataGridRowsPresenter? _rowsPresenter;
        private readonly Dictionary<DataGridColumn, DataGridColumnHeader> _headerByColumn = new();

        /// <summary>The rows WPF currently has containers for — the same
        /// population it sizes an Auto column from — read directly off the
        /// grid's DataGridRowsPresenter (found once, cached, same pattern as
        /// the header lookup below): O(realized rows), not O(grid.Items.Count)
        /// with a ContainerFromIndex probe per item, which matters once a
        /// grid like HistoryWindow's "Show all" holds thousands of rows with
        /// only a screenful realized — the same population this repo already
        /// found too expensive to iterate a SECOND time per row
        /// (docs/superpowers/plans/2026-08-14-column-stability-while-scrolling.md
        /// measured ~2.8ms/row for a full-realize pass and reverted it; this
        /// is cheaper because it never forces anything to realize, it only
        /// reads what is already there).</summary>
        public List<DataGridRow> RealizedRows(DataGrid grid)
        {
            _rowsPresenter ??= FindDescendant<DataGridRowsPresenter>(grid);
            var rows = new List<DataGridRow>();
            if (_rowsPresenter is null) return rows;
            foreach (var child in _rowsPresenter.Children)
                if (child is DataGridRow row) rows.Add(row);
            return rows;
        }

        /// <summary>Table-rules, Rule 3: this column's DESIRED width is the
        /// <see cref="AutofitPercentile"/> of <paramref name="rows"/>' own
        /// measured cell widths — no longer folded together with the
        /// header's own width the way this method used to seed "widest"
        /// with it (AutofitCaps' own <c>floors</c> is where the header floor
        /// lives now, the one place it needs to). See
        /// <see cref="PercentileOf"/> for the percentile itself, including
        /// its two degenerate cases (no rows, exactly one row).</summary>
        public double Of(DataGridColumn column, List<DataGridRow> rows)
        {
            var widths = new List<double>(rows.Count);
            foreach (var row in rows)
            {
                var width = column.GetCellContent(row) switch
                {
                    TextBlock text => TextWidthOf(text),
                    // No governed column in this app is anything but a
                    // DataGridTextColumn today, so this branch is never
                    // exercised — kept so a future non-text tracked column
                    // measures SOMETHING rather than throwing. DesiredSize
                    // is clipped to the column's OWN current cap, so a real
                    // tracked column relying on this would reproduce the
                    // same never-shrinks ratchet the class doc warns about
                    // for WPF's own Width.DesiredValue.
                    FrameworkElement other => other.DesiredSize.Width,
                    _ => 0,
                };
                widths.Add(width);
            }
            return PercentileOf(widths);
        }

        /// <summary>The 80th percentile (table-rules, Rule 3's own named
        /// constant, <see cref="AutofitPercentile"/>) of a column's realized
        /// cell widths, nearest-rank on the SORTED widths: rank =
        /// ceil(percentile × count), one-based, clamped into [1, count] —
        /// i.e. the value that has at least 80% of the sample at or below
        /// it, the smallest such value when several tie. Two degenerate
        /// cases, handled explicitly rather than left to fall out of the
        /// general formula by accident: zero widths (nothing realized —
        /// AutofitCaps' own header floor is the only thing that should size
        /// this column, so 0 here lets it) returns 0, and exactly one width
        /// returns that width itself (unambiguous — there is nothing to take
        /// a percentile OF). The general formula would in fact compute the
        /// same two answers on its own (ceil(0.8×1) = 1, the only rank there
        /// is), so these branches change no behaviour — they exist so
        /// neither case has to rely on an empty or singleton list surviving
        /// the general sort-and-index path unharmed, and so a reader can see
        /// both boundaries stated rather than infer them from the formula.</summary>
        private static double PercentileOf(List<double> widths)
        {
            if (widths.Count == 0) return 0;
            if (widths.Count == 1) return widths[0];
            var sorted = widths.OrderBy(w => w).ToList();
            var rank = Math.Clamp((int)Math.Ceiling(AutofitPercentile * sorted.Count), 1, sorted.Count);
            return sorted[rank - 1];
        }

        /// <summary>The header's own text plus the header's padding, measured
        /// rather than read from its DesiredSize, which is clipped to the
        /// column once the column is capped. The DataGridColumnHeader itself
        /// is cached per column after the first lookup — headers are stable
        /// for the grid's life, so the tree walk that finds one costs once
        /// per column, not once per column per layout pass; a lookup that
        /// somehow returns a header no longer wired to this column re-walks
        /// rather than trusting a stale hit.
        ///
        /// Public, not private: AutofitCaps calls this directly (2026-08-29
        /// review, header floor) for its own <c>floors</c> computation — the
        /// ONLY place this number feeds any more (table-rules, Rule 3: <see
        /// cref="Of"/> no longer folds the header into a column's "natural"
        /// width at all, so there is no second caller left to double-walk
        /// for). Kept public rather than folded back to private regardless,
        /// since AutofitCaps is still the one place that needs it and a
        /// second caller returning would only need to double-walk the ONE
        /// thing this method does NOT cache, the FindDescendant&lt;TextBlock&gt;
        /// below: the header lookup above is cached in
        /// <c>_headerByColumn</c>, but the header's own child TextBlock is
        /// re-found on every call regardless of that cache hitting.</summary>
        public double HeaderWidthOf(DataGrid grid, DataGridColumn column)
        {
            if (!_headerByColumn.TryGetValue(column, out var header) || header.Column != column)
            {
                _headers ??= FindDescendant<DataGridColumnHeadersPresenter>(grid);
                if (_headers is null) return 0;
                var found = FindDescendants<DataGridColumnHeader>(_headers).FirstOrDefault(h => h.Column == column);
                if (found is null) return 0;
                header = found;
                _headerByColumn[column] = header;
            }
            var text = FindDescendant<TextBlock>(header);
            return text is null
                ? header.DesiredSize.Width
                : TextWidthOf(text) + header.Padding.Left + header.Padding.Right;
        }

        private double TextWidthOf(TextBlock text)
        {
            // DPI and formatting mode are part of the key: dragging a
            // window between monitors of different scale factors otherwise
            // keeps serving a width measured for the OLD monitor for the
            // rest of the window's life — this app has already shipped one
            // multi-monitor DPI bug. FlowDirection and FontStretch are part
            // of it for the same reason as FontWeight/FontStyle below: they
            // change what FormattedText measures for the same string.
            var key = (text.Text, text.FontFamily.Source, text.FontSize, text.FontWeight, text.FontStyle,
                text.FontStretch, text.FlowDirection, TextOptions.GetTextFormattingMode(text),
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            if (!_measured.TryGetValue(key, out var width))
            {
                if (_measured.Count >= MaxMeasuredEntries) _measured.Clear();
                width = text.Text.Length == 0 ? 0 : Math.Ceiling(new FormattedText(
                    text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                    text.FontSize, Brushes.Black,
                    // no number substitution — the same default a TextBlock
                    // formats with; the parameter is unannotated, ! keeps the
                    // nullable analysis quiet either way
                    null!, TextOptions.GetTextFormattingMode(text),
                    VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace) + MeasureSlack;
                _measured[key] = width;
            }
            return width + text.Margin.Left + text.Margin.Right + text.Padding.Left + text.Padding.Right;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static List<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindDescendants<T>(child));
        }
        return results;
    }
}
