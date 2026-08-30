using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrdoSort.Wpf.Views;

/// <summary>Autofit for a DataGrid's text columns: every column gets its
/// content width when the total fits the grid, and when it doesn't, the
/// shortfall is shared in proportion to content width — no column below
/// its MinWidth — with the text in the columns that gave way wrapping onto
/// extra lines (each column's own ElementStyle carries the TextWrapping).
/// No horizontal scrollbar, nothing hidden behind an ellipsis, nothing
/// for the user to readjust.
///
/// Until 2026-08-29 this class capped the message column at whatever was
/// left after the name column's floor, and the name column got the
/// leftovers — six fix rounds of it, all in git history. The owner's
/// report that ended it: "the columns don't autofit, hiding text, and I
/// have to readjust every time." Both halves were that rule: the message
/// column cut names down to their floor and trimmed itself with an
/// ellipsis, and every window reopened with the same squeeze.
///
/// SIX windows depend on this class through <see cref="Track(DataGrid,
/// DataGridColumn[])"/>: History, MatchMerge, BulkRename, PageCounts,
/// ZipTools and MergePdfs. Triage supplies its own budget through the
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
/// MEASURED, NOT READ BACK. A column's content width is the widest realized
/// cell's text — FormattedText in that cell's own font, cached per string —
/// or its header, whichever is wider. WPF's own Width.DesiredValue would be
/// cheaper, but it only ever grows (measured 2026-08-29: 802px after the
/// long row was removed), so a class that read it could never shrink a
/// column back. Measuring is exact (801.53px against WPF's 802px for the
/// same string; the default DataGridCell template applies no padding), and
/// <see cref="MeasureSlack"/> covers the rounding so a one-line cell is
/// never wrapped by its own cap. Capping at the measured width in the fit
/// case — rather than relaxing to infinity — is the whole shrink-back
/// mechanism: an Auto column displays at min(desired, MaxWidth).
///
/// Only realized rows are measured, the same population WPF sizes an Auto
/// column from — and this cuts BOTH ways, unlike plain WPF Auto (which only
/// ever grows; see MEASURED, NOT READ BACK above). A column widens as a
/// longer row scrolls into view — the pre-existing behaviour docs/
/// superpowers/plans/2026-08-14-column-stability-while-scrolling.md measured
/// and left alone — and, because this class re-measures the realized set
/// from scratch every pass rather than remembering a high-water mark, it
/// also NARROWS again once that row scrolls back out of view: scroll a long
/// row out, the column shrinks; scroll it back in, the column widens again.
/// Measured directly before this class existed and plain WPF Auto was still
/// the mechanism (2026-08-14, docs/superpowers/plans/2026-08-14-column-
/// stability-while-scrolling.md): "a history seeded with 3000 rows (one, at
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
/// this class then measures again. It settles rather than oscillates only
/// because assignment is skipped once a cap stops moving (see Recalculate's
/// own epsilon guard) — the same mechanism that lets the LayoutUpdated cycle
/// end at all.
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

            var caps = computeCap is not null
                ? Enumerable.Repeat(computeCap(columnViewportWidth), governed.Length).ToArray()
                : AutofitCaps(grid, columnViewportWidth, governed, widths);
            // null: the realized set is momentarily empty while the grid
            // still has items (mid-scroll, or the instant after a Clear
            // before new rows realize) — keep every cap exactly where it
            // was rather than collapse to header widths and snap back.
            if (caps is null) return;

            // Assign only what moved: an assignment invalidates layout, which
            // raises LayoutUpdated, which recomputes — so unconditional
            // assignment would never let the cycle end.
            for (var i = 0; i < governed.Length; i++)
                if (Math.Abs(governed[i].MaxWidth - caps[i]) > 0.5) governed[i].MaxWidth = caps[i];
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

    /// <summary>One cap per governed column, in the same order — or null if
    /// the realized-rows guard (see the class doc) says to skip this pass
    /// entirely. As a side effect, also sets each STAR participant's own
    /// Width factor to the share <see cref="ColumnShares"/> computed for it,
    /// so a grid with more than one star column (HistoryWindow) splits its
    /// leftover in proportion to content rather than evenly.</summary>
    private static double[]? AutofitCaps(
        DataGrid grid, double viewportWidth, DataGridColumn[] governed, ContentWidths widths)
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
        // text is not (see that method's own doc comment) — calling it a
        // second time here, after Of() already calls it once to seed
        // "natural", would silently double that walk every pass.
        var headerWidths = participants.Select(column => widths.HeaderWidthOf(grid, column)).ToList();
        var natural = participants.Select((column, i) => widths.Of(column, rows, headerWidths[i])).ToList();
        // Floor #3, added 2026-08-29 review: a column's own header never
        // wraps or trims (DataGridColumnHeader hard-clips), so a share
        // computed below the header's width would clip it silently. Without
        // this, every unheld participant in the non-fit branch gets
        // wanted*(available/Sigma-wanted) < wanted — including a column
        // whose only "wanted" width IS its header (empty or short cells) —
        // and MinimumCap/MinWidth alone (20px on every governed column in
        // this app; none declares a MinWidth) did nothing to stop it.
        var floors = participants
            .Select((column, i) => Math.Max(Math.Max(MinimumCap, column.MinWidth), headerWidths[i]))
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

        return shares.Take(governed.Length).ToArray();
    }

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

        /// <summary><paramref name="headerWidth"/> is passed in rather than
        /// measured here — the caller (AutofitCaps) needs the same number
        /// for the floor computation and must not measure it twice; see
        /// <see cref="HeaderWidthOf"/>'s own doc comment for the walk that
        /// would otherwise double.</summary>
        public double Of(DataGridColumn column, List<DataGridRow> rows, double headerWidth)
        {
            var widest = headerWidth;
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
                widest = Math.Max(widest, width);
            }
            return widest;
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
        /// review, header floor) to get the same number <see cref="Of"/>
        /// needs for "natural" and the floor computation needs independently
        /// — calling it twice per participant per pass rather than once
        /// would double the ONE walk this method does NOT cache, the
        /// FindDescendant&lt;TextBlock&gt; below: the header lookup above is
        /// cached in <c>_headerByColumn</c>, but the header's own child
        /// TextBlock is re-found on every call regardless of that cache
        /// hitting.</summary>
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
