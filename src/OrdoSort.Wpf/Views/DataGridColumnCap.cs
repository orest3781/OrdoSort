using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OrdoSort.Wpf.Views;

/// <summary>Keeps a set of DataGridColumns' <c>MaxWidth</c> at what the grid
/// can actually spare — not the grid's raw outer <c>ActualWidth</c>, and not
/// a value computed once from a declared <c>Window.Width</c> and then left
/// alone.
///
/// It was "a SHARE of that space" until fix round 6, at the bottom of this
/// comment — start there for the rule as it stands rather than how it got
/// here.
///
/// SEVEN windows depend on this class (nine before the reports feature —
/// Turnaround and Production — was removed): History, MatchMerge,
/// BulkRename, ZipMerge, Unzip and PageCounts take the remainder rule;
/// Triage supplies its own <see cref="Func{Double,Double}"/> budget. Only
/// ZipWindow and FilenameListWindow never call it, correctly — neither has a
/// capped column to govern.
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
    /// <summary>Caps every column in <paramref name="columns"/> at what is
    /// actually LEFT — the grid's live viewport minus the width every other
    /// column is entitled to — shared equally among them.
    ///
    /// This replaced a flat share of the viewport (0.15/0.25/0.35/0.45,
    /// depending on the window), and the difference is the whole point. A
    /// fixed fraction is a ceiling that binds whether or not anything else
    /// needs the room, so a capped column truncated its content while the
    /// star column beside it sat on space nobody was using. Measured, every
    /// window at its own MinWidth, before this change:
    ///
    ///   MatchMerge   File   231px, cap 231 — pinned AT the cap; Becomes 213
    ///   ZipMerge     Result 234px, cap 234 — pinned AT the cap; Zip     300
    ///   Unzip        Result 198px, cap 198 — pinned AT the cap; Zip     256
    ///   PageCounts   Note   234px, cap 234 — pinned AT the cap; File    246
    ///
    /// In the three tools windows that meant the ERROR MESSAGE was the thing
    /// being cut off so the filename could be wide — backwards, since the
    /// message is what the window is for. No dead space was involved: the
    /// columns filled the viewport exactly. The space was going to the wrong
    /// column, which is a different complaint and needs a different fix.
    ///
    /// What every other column is "entitled to":
    ///   absolute width  -> that width (it is not negotiable)
    ///   pinned          -> its current width (the user chose it — fix round 5)
    ///   star / filler   -> its MinWidth, because a star column's whole job is
    ///                      to give way; this is why every filler in the app
    ///                      now declares one rather than inheriting WPF's 20px
    ///                      default, which would have let a filename collapse
    ///   untracked Auto  -> its current width; these are the deliberately
    ///                      uncapped short columns (a date, a count, a page
    ///                      total), so what they measure now is what they need
    ///
    /// The residual risk, stated plainly rather than left for someone to find:
    /// an untracked Auto column's width is read LIVE, and this only recomputes
    /// on SizeChanged. If such a column grew after the last recompute — new
    /// rows realizing longer content — the cap would be that much too
    /// generous. <see cref="SafetyMargin"/> absorbs it, and the exposure is
    /// small by construction because a column is left untracked precisely
    /// when its content is bounded. It is not zero, and a future column that
    /// is untracked but NOT bounded would make it real.</summary>
    public static void Track(DataGrid grid, params DataGridColumn[] columns) =>
        TrackCore(grid, computeCap: null, columns);

    /// <summary>Absorbs the small drift the doc comment above describes, and
    /// keeps the arithmetic off the exact viewport edge where a rounding
    /// difference decides whether a scrollbar appears. 20px matches the
    /// margin TriageWindow and ProductionWindow already chose for their own
    /// budgets.</summary>
    private const double SafetyMargin = 20;

    /// <summary>Floor for the computed cap. Two ways to reach it: a window so
    /// narrow that the other columns' floors already consume the viewport
    /// (which every window's own MinWidth makes unlikely — measured margins
    /// run 335-535px), or a column the user has dragged wide, whose claim is
    /// fixed and unshrinkable. In both cases WPF's own space-fitting is what
    /// actually resolves the layout, and in the second a horizontal scrollbar
    /// can appear — see RemainderCap's own note on the scope of the
    /// guarantee. Matches ProductionWindow's existing Math.Max(20, …).</summary>
    private const double MinimumCap = 20;

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
    public static void Track(DataGrid grid, Func<double, double> computeCap, params DataGridColumn[] columns) =>
        TrackCore(grid, computeCap, columns);

    private static void TrackCore(
        DataGrid grid, Func<double, double>? computeCap, DataGridColumn[] columns)
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

            // A pinned column is no longer governed by any cap, so it is not
            // in the set the cap is divided among — it is one of the columns
            // the remainder is measured AGAINST instead.
            var governed = columns.Where(c => !pinned.Contains(c)).ToArray();
            if (governed.Length == 0) return;

            var cap = computeCap is not null
                ? computeCap(columnViewportWidth)
                : RemainderCap(grid, columnViewportWidth, governed, pinned);

            // Only assign when it actually moves. This is what makes the
            // LayoutUpdated subscription below terminate: an assignment
            // invalidates layout, which raises LayoutUpdated, which recomputes
            // — so an unconditional assignment would keep the cycle alive
            // forever. Once the numbers settle, nothing is assigned and the
            // cycle stops on its own.
            foreach (var column in governed)
                if (Math.Abs(column.MaxWidth - cap) > 0.5) column.MaxWidth = cap;
        }

        // Detach whatever a PREVIOUS Track call on this same grid left behind.
        //
        // ProductionWindow.RebuildColumns() calls Track afresh on every
        // pick-list tick and every reload, against the same long-lived
        // ResultsGrid — its own comment accepted the resulting leftover
        // handlers as "harmless, bounded, cosmetic", which was fair when they
        // only ever wrote MaxWidth onto orphaned DataGridColumn objects.
        // It stopped being fair when LayoutUpdated joined them: that event
        // fires after EVERY layout pass the dispatcher runs, anywhere in the
        // app, not just on this grid — so N accumulated closures tax every
        // hover, tooltip and animation tick for as long as the window is
        // open, and N grows for as long as someone keeps ticking boxes.
        //
        // Rather than ask each caller to remember, Track is now idempotent per
        // grid: at most one live subscription set exists no matter how many
        // times it is called.
        (grid.GetValue(DetachProperty) as Action)?.Invoke();

        void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Recalculate();
        grid.SizeChanged += OnSizeChanged;

        // SizeChanged alone is not enough for the remainder formula, and this
        // was measured rather than reasoned: the formula reads the OTHER
        // columns' ActualWidth to decide what is left over, and at
        // SizeChanged time those columns have not finished laying out. In
        // PageCounts the Pages column measured 35px there and rendered at
        // 54px, so the cap came out 19px too generous — absorbed by
        // SafetyMargin with one row, but not with sixty rows of long content,
        // where it produced exactly the horizontal scrollbar this class
        // exists to prevent. Three grids' no-scrollbar facts caught it.
        //
        // LayoutUpdated fires after layout has settled, so the widths read
        // there are the real ones. It converges in a pass or two: the first
        // recompute uses stale numbers, the next uses settled ones, and the
        // one after that finds nothing to change and assigns nothing —
        // which, with the epsilon guard in Recalculate, is what ends it.
        //
        // The re-entrancy guard is belt and braces: assigning MaxWidth inside
        // a LayoutUpdated handler can raise the event again synchronously,
        // and one recompute at a time is easier to reason about than relying
        // on the epsilon alone.
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

        // handledEventsToo: true — defensively; nothing in this app's own
        // column templates currently marks these handled, but a future
        // template change silently breaking this fix (by marking the event
        // handled before it bubbles here) is exactly the kind of thing this
        // repo has been bitten by before.
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
            // restores the cap for every column that DIDN'T just get pinned
            // (including any merely relaxed-and-not-actually-dragged by the
            // DragStarted handler above) — Recalculate already skips pinned
            // columns, so this is the single source of truth for "what's the
            // cap right now," not a second copy of computeCap's call site.
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
    /// handlers, so the next Track call on the same grid can run it first. An
    /// attached property rather than a static table because it lives and dies
    /// with the grid — no separate lifetime to get wrong.</summary>
    private static readonly DependencyProperty DetachProperty =
        DependencyProperty.RegisterAttached(
            "Detach", typeof(Action), typeof(DataGridColumnCap), new PropertyMetadata(null));

    /// <summary>The viewport minus what every OTHER column is entitled to,
    /// split equally among the governed ones — see <see cref="Track(DataGrid,
    /// DataGridColumn[])"/>'s doc comment for what "entitled to" means per
    /// column kind and why.
    ///
    /// Equally, not by demand: distributing by demand needs each column's
    /// natural (uncapped) width, which is only knowable by laying the grid
    /// out uncapped first and measuring — a second layout pass on every
    /// resize, and a visible flash of a wider column while it happens. An
    /// even split is the conservative version: it never over-allocates, and a
    /// column that wants less than its share simply takes less (Auto sizes to
    /// content; the cap is only a ceiling).
    ///
    /// SCOPE OF THE GUARANTEE, corrected after review flagged this comment as
    /// overclaiming — it used to say the no-scrollbar guarantee "holds by
    /// arithmetic rather than by measurement", full stop, which is not true.
    /// It holds for AUTOMATIC sizing, which is the only thing this class
    /// governs.
    ///
    /// It does not survive a user-pinned column. A column the user has
    /// dragged is deliberately exempt from the cap (fix round 5: "their width
    /// wins permanently afterward"), so it becomes a fixed claim with no
    /// upper bound, counted in <c>claimed</c> but never shrinkable. Drag one
    /// wide enough and then shrink the window and <c>available</c> goes
    /// negative; <see cref="MinimumCap"/> then floors every remaining column
    /// unconditionally, and floor-plus-pinned can exceed the viewport. WPF's
    /// own space-fitting is what resolves the layout at that point, and a
    /// horizontal scrollbar can appear.
    ///
    /// That is the accepted consequence of letting a hand-drag win, not a
    /// defect to fix here — but it is the boundary of the claim, and saying
    /// so is the difference between a guarantee and a slogan.</summary>
    private static double RemainderCap(
        DataGrid grid, double viewportWidth, DataGridColumn[] governed, HashSet<DataGridColumn> pinned)
    {
        var governedSet = new HashSet<DataGridColumn>(governed);
        var claimed = 0.0;
        foreach (var column in grid.Columns)
        {
            if (governedSet.Contains(column)) continue;
            claimed += EntitlementOf(column, pinned);
        }

        var available = viewportWidth - claimed - SafetyMargin;
        return Math.Max(MinimumCap, available / governed.Length);
    }

    private static double EntitlementOf(DataGridColumn column, HashSet<DataGridColumn> pinned) =>
        column.Width.IsAbsolute ? column.Width.Value
        // The user dragged this one; its width is theirs, not ours to reclaim.
        : pinned.Contains(column) ? column.ActualWidth
        // A star column's job is to absorb slack, so it is the one that gives
        // way when a governed column needs room — down to its declared floor.
        : column.Width.IsStar ? column.MinWidth
        // An untracked Auto column: deliberately uncapped because its content
        // is bounded, so what it currently measures is what it needs.
        : column.ActualWidth;
}
