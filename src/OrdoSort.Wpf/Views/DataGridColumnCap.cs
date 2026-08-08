using System.Windows.Controls;

namespace OrdoSort.Wpf.Views;

/// <summary>Keeps a set of DataGridColumns' <c>MaxWidth</c> at a share of the
/// grid's own LIVE <c>ActualWidth</c> — not a value computed once from a
/// declared <c>Window.Width</c> and then left alone. That was this class's
/// first shape (2026-08-07 autofit-columns Task 1) and it undercounted a
/// real, ordinary user action: dragging a window's edge toward its own
/// declared <c>MinWidth</c> shrinks the grid without ever changing
/// <c>Window.Width</c> (a design-time value, not a live one), so a cap baked
/// in at construction stayed exactly as generous as it was at the window's
/// starting size — measured (fix round 2) letting MatchMerge/BulkRename/
/// History's column total overflow their own MinWidth by 32-71px, each with
/// a horizontal scrollbar the owner's decision explicitly forbids.
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
/// both are just "the grid's RenderSize changed"), and re-measuring off the
/// grid's own resolved <c>ActualWidth</c> is inherently more accurate than
/// approximating it from <c>Window.Width</c> minus guessed-at chrome/margins
/// ever was.</summary>
internal static class DataGridColumnCap
{
    /// <summary>Registers live tracking: recomputes every column's
    /// <c>MaxWidth</c> as <c>share</c> of <paramref name="grid"/>'s current
    /// <c>ActualWidth</c> immediately, and again on every subsequent
    /// <c>SizeChanged</c> — covering both a window shown small from the
    /// start and one dragged smaller afterward.</summary>
    public static void Track(DataGrid grid, double share, params DataGridColumn[] columns)
    {
        void Recalculate()
        {
            // 0 before the grid's first layout pass — nothing to size
            // against yet; the SizeChanged this method also subscribes to
            // fires the moment that first real width is known.
            if (grid.ActualWidth <= 0) return;
            var cap = grid.ActualWidth * share;
            foreach (var column in columns) column.MaxWidth = cap;
        }

        grid.SizeChanged += (_, _) => Recalculate();
        Recalculate();
    }
}
