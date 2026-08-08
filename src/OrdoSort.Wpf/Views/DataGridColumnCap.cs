using System.Windows;
using System.Windows.Controls;

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
/// both are just "the grid's RenderSize changed").</summary>
internal static class DataGridColumnCap
{
    /// <summary>Registers live tracking: recomputes every column's
    /// <c>MaxWidth</c> as <c>share</c> of <paramref name="grid"/>'s current
    /// ActualWidth minus a reserved vertical-scrollbar allowance,
    /// immediately and again on every subsequent <c>SizeChanged</c> —
    /// covering a window shown small from the start, one dragged smaller
    /// afterward, and a grid that gains enough rows to need a vertical
    /// scrollbar it didn't have when this last ran.</summary>
    public static void Track(DataGrid grid, double share, params DataGridColumn[] columns)
    {
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
            var cap = columnViewportWidth * share;
            foreach (var column in columns) column.MaxWidth = cap;
        }

        grid.SizeChanged += (_, _) => Recalculate();
        Recalculate();
    }
}
