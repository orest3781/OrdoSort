using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OrdoSort.Wpf.Views;

/// <summary>Keeps a set of DataGridColumns' <c>MaxWidth</c> at a share of the
/// space the columns can actually occupy — not the grid's raw outer
/// <c>ActualWidth</c>, and not a value computed once from a declared
/// <c>Window.Width</c> and then left alone.
///
/// The "declared Width, computed once" shape was this class's first design
/// (2026-08-07 autofit-columns Task 1) and it undercounted a real, ordinary
/// user action: dragging a window's edge toward its own declared
/// <c>MinWidth</c> shrinks the grid without ever changing <c>Window.Width</c>
/// (a design-time value, not a live one), so a cap baked in at construction
/// stayed exactly as generous as it was at the window's starting size —
/// measured (fix round 2) letting MatchMerge/BulkRename/History's column
/// total overflow their own MinWidth by 32-71px.
///
/// Tracking the grid's raw <c>ActualWidth</c> live (fix round 2's fix) still
/// wasn't the whole story: a DataGrid's OUTER <c>ActualWidth</c> includes
/// whatever a vertical scrollbar claims once there are enough rows to need
/// one — the scrollbar and the columns compete for the SAME outer width, so
/// capping columns against the full outer width overshoots the moment a
/// vertical scrollbar appears. Measured (fix round 3): HistoryWindow at its
/// own MinWidth (700) with 60 rows (one ordinary commit's worth of filing,
/// not a corner case) showed a VISIBLE horizontal scrollbar post-round-2-fix,
/// because the vertical one had already claimed
/// <see cref="SystemParameters.VerticalScrollBarWidth"/> of the space the
/// columns were capped against. MatchMerge/BulkRename didn't reproduce it
/// only because their combined capped-column share (0.70) left more margin
/// than History's (0.54 plus fixed floors) — one column-share tweak away
/// from the identical failure, not actually safe.
///
/// The cap is now computed against <c>ActualWidth -
/// SystemParameters.VerticalScrollBarWidth</c> UNCONDITIONALLY, whether or
/// not a vertical scrollbar happens to be visible right now: a grid that's
/// scrollbar-free today can gain rows later (MatchMerge/BulkRename via "Add
/// files…"; History as more gets filed) without this class re-running, and
/// the space columns can actually occupy genuinely IS <c>ActualWidth -
/// scrollbar</c> the moment one appears — reserving it up front is the more
/// correct basis to cap against, independent of whether it's currently the
/// thing keeping any particular grid under its viewport.
///
/// HONESTY CHECK (fix round 4, prompted by review — this repo has hit
/// comment/code mismatch four times this week, each one hiding a real
/// defect): this reservation is currently DEFENSIVE, not load-bearing.
/// Verified directly — reverting ONLY this subtraction (leaving every
/// window's share constant exactly as fix round 3 left them) still passes
/// the FULL suite, including the many-row scrollbar facts. What actually
/// supplies History's margin today is <c>HistoryWindow</c>'s own
/// <c>ContentColumnShare</c>, cut from 0.18 to 0.15 in that same round;
/// reverting THIS subtraction TOGETHER WITH that share change is what
/// reproduces the original overflow (see HistoryWindow.xaml.cs's own
/// comment on that constant). So: kept because it's the structurally
/// correct thing to cap against and the cost is small — not because a test
/// currently depends on it alone. Don't read its presence as proof it's
/// preventing anything by itself right now, and don't assume a future
/// column-share change stays safe just because this line exists — if one
/// ever needs this reservation to actually hold the line, that dependency
/// should be re-verified the same way this finding was: revert it alone
/// and confirm a test breaks, not assumed from this comment.
///
/// A <see cref="DataGridColumn"/> is not part of the visual or logical tree —
/// it hangs off <c>DataGrid.Columns</c>, not the grid's own <c>Content</c> —
/// so XAML cannot bind its <c>MaxWidth</c> to an ancestor's <c>ActualWidth</c>
/// with a RelativeSource or ElementName the way an ordinary FrameworkElement
/// could; there is no NameScope path to reach it that way. A
/// <c>SizeChanged</c> handler on the grid itself is the practical
/// alternative: it fires for every genuine layout-driven width change,
/// including the very first one (an initial <c>Show()</c> at a Width already
/// at/near MinWidth lays out exactly like a subsequent resize down to it —
/// both are just "the grid's RenderSize changed").
///
/// FIX ROUND 5 (2026-08-08, "columns can't be moved and are hiding text"
/// task) — two additions, both driven by measurement (see the task's own
/// report for the full off-screen numbers):
///
/// (1) The <see cref="Func{Double,Double}"/> overload below. Every caller
/// used to supply a flat share of the viewport; TriageWindow instead computed
/// its own cap ONCE, in its constructor, from <c>SidePanelColumn</c>'s
/// DECLARED Width (440) — the side panel is actually resizable (a
/// GridSplitter), so that cap never revisited the panel's live width.
/// Measured directly: widening the panel from 440px to 700px left the
/// roster-column cap sitting at 76px both before and after. TriageWindow now
/// calls this overload with its own per-column-count, Why-aware formula
/// instead of computing a cap once itself — see TriageWindow.xaml.cs.
///
/// (2) User-dragged columns are no longer re-clamped. Measured, off-screen,
/// before this round: every capped column in all FOUR grid windows was
/// unable to be widened even by directly setting <c>Width</c> past the
/// current <c>MaxWidth</c> — e.g. MatchMerge's File column, cap 272.65px:
/// set <c>Width</c> to cap+300, <c>ActualWidth</c> afterward was still
/// 272.65px, unchanged. That's WPF's own column-resize mechanism: an
/// interactive drag is live-clamped to the column's CURRENT MaxWidth in real
/// time, so a real mouse drag can never even produce a Width value beyond
/// it — watching <c>Width</c> for a change and reacting afterward cannot fix
/// this; the drag needs an ALREADY-relaxed MaxWidth to exceed the cap in the
/// first place. The owner's decision: automatic layout must still never
/// produce a horizontal scrollbar, but a column the user drags by hand is
/// free to exceed the cap, and their width wins permanently afterward — the
/// cap governs automatic sizing only. See the <c>Thumb</c> drag handling at
/// the bottom of <see cref="Track(DataGrid, Func{double, double},
/// DataGridColumn[])"/> below.</summary>
internal static class DataGridColumnCap
{
    /// <summary>Registers live tracking: recomputes every column's
    /// <c>MaxWidth</c> as <paramref name="share"/> of <paramref name="grid"/>'s
    /// current ActualWidth minus a reserved vertical-scrollbar allowance,
    /// immediately and again on every subsequent <c>SizeChanged</c> —
    /// covering a window shown small from the start, one dragged smaller
    /// afterward, and a grid that gains enough rows to need a vertical
    /// scrollbar it didn't have when this last ran. A thin wrapper over the
    /// <see cref="Func{Double,Double}"/> overload below for the common case
    /// (a flat share of the viewport) — MatchMergeWindow, BulkRenameWindow
    /// and HistoryWindow all use this one; TriageWindow's own per-column-count
    /// formula needs the other.</summary>
    public static void Track(DataGrid grid, double share, params DataGridColumn[] columns) =>
        Track(grid, viewportWidth => viewportWidth * share, columns);

    /// <summary>Same live tracking as the <c>share</c> overload, but the cap
    /// is computed by an arbitrary <paramref name="computeCap"/> formula
    /// applied to the grid's live column viewport width (already net of the
    /// reserved vertical-scrollbar allowance) — for TriageWindow, whose cap
    /// depends on how many roster columns exist and whether the fixed-width
    /// "Why" column might appear in this batch, not just a flat share.
    ///
    /// Also owns the "let the user win" half of fix round 5 (see this class's
    /// own doc comment above): every capped column's resize grip is a
    /// <see cref="Thumb"/> inside its <see cref="DataGridColumnHeader"/>'s
    /// template, and <c>DragStarted</c>/<c>DragCompleted</c> bubble up
    /// through the visual tree to <paramref name="grid"/> itself.
    /// <c>DragStarted</c> relaxes every not-yet-pinned tracked column's
    /// MaxWidth to <see cref="double.PositiveInfinity"/> — deliberately ALL
    /// of them, not just whichever one turns out to be the one being
    /// dragged: WPF does not expose a reliable, version-stable way from here
    /// to resolve a specific gripper Thumb back to "this is the LEFT or
    /// RIGHT gripper of column N, and column N's OWN gripper resizes column N
    /// but the other one may resize its neighbour instead" without depending
    /// on DataGridColumnHeader/DataGridColumn internals this class has no
    /// business coupling to. Relaxing every tracked column is harmless — at
    /// worst a sibling Auto column briefly re-measures to its natural
    /// (uncapped) width for the duration of the drag gesture if its own
    /// content happens to exceed its current capped width, then
    /// <c>DragCompleted</c> restores it. <c>DragCompleted</c> is the point
    /// that actually identifies which column was dragged: WPF's own
    /// column-resize logic is what converts a column's <c>Width</c> from
    /// <see cref="DataGridLength.IsAuto"/> (every one of these columns starts
    /// life that way) to <see cref="DataGridLength.IsAbsolute"/> — a fixed
    /// pixel value — so IsAbsolute after a drag IS the signal "this specific
    /// column was the one the user just resized." That column is pinned
    /// (added to <c>pinned</c>, its MaxWidth left at PositiveInfinity
    /// permanently, for this window instance's lifetime — Recalculate below
    /// skips it forever after); every other, still-Auto column gets its
    /// MaxWidth restored to the live computed cap via the same Recalculate
    /// the SizeChanged handler already uses.</summary>
    public static void Track(DataGrid grid, Func<double, double> computeCap, params DataGridColumn[] columns)
    {
        var pinned = new HashSet<DataGridColumn>();

        void Recalculate()
        {
            // 0 before the grid's first layout pass — nothing to size
            // against yet; the SizeChanged this method also subscribes to
            // fires the moment that first real width is known.
            if (grid.ActualWidth <= 0) return;
            // Defensive, not (currently) load-bearing — see this class's own
            // "HONESTY CHECK" doc paragraph above before touching this line
            // or assuming it's what keeps any particular grid's columns
            // under its viewport.
            var columnViewportWidth = Math.Max(0,
                grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);
            var cap = computeCap(columnViewportWidth);
            foreach (var column in columns)
                if (!pinned.Contains(column)) column.MaxWidth = cap;
        }

        grid.SizeChanged += (_, _) => Recalculate();
        Recalculate();

        // handledEventsToo: true — defensively; nothing in this app's own
        // column templates currently marks these handled, but a future
        // template change silently breaking this fix (by marking the event
        // handled before it bubbles here) is exactly the kind of thing this
        // repo has been bitten by before.
        grid.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            foreach (var column in columns)
                if (!pinned.Contains(column)) column.MaxWidth = double.PositiveInfinity;
        }), true);

        grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            foreach (var column in columns)
                if (!pinned.Contains(column) && column.Width.IsAbsolute) pinned.Add(column);
            // restores the cap for every column that DIDN'T just get pinned
            // (including any merely relaxed-and-not-actually-dragged by the
            // DragStarted handler above) — Recalculate already skips pinned
            // columns, so this is the single source of truth for "what's the
            // cap right now," not a second copy of computeCap's call site.
            Recalculate();
        }), true);
    }
}
