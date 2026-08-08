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
/// files…"; History as more gets filed) without this class re-running, so
/// reserving the scrollbar's width up front is what keeps the invariant
/// (never a horizontal scrollbar) true regardless of row count, not just at
/// whatever row count happened to be on screen when a column last resized.
/// The cost is a small, constant amount of unused width when no vertical
/// scrollbar is actually showing — negligible against hundreds of pixels of
/// budget, and a explicit trade for robustness across row counts this class
/// has no visibility into.
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
            var columnViewportWidth = Math.Max(0,
                grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);
            var cap = columnViewportWidth * share;
            foreach (var column in columns) column.MaxWidth = cap;
        }

        grid.SizeChanged += (_, _) => Recalculate();
        Recalculate();
    }
}
