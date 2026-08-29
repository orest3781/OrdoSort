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
/// A star column is a participant but is never assigned: it takes what
/// the governed columns leave, which is its share by construction once
/// every governed column sits at its own. (The vertical-scrollbar
/// reservation and the safety margin land in it too, so a star column is
/// a few pixels wider than its share, never narrower.)
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
/// column from, so a column can still widen as longer rows scroll into
/// view — the pre-existing behaviour docs/superpowers/plans/
/// 2026-08-14-column-stability-while-scrolling.md measured and left alone.
///
/// A user's drag still wins: DragStarted relaxes every not-yet-pinned
/// governed column so WPF's live clamp can't block the gesture;
/// DragCompleted reads "Width became absolute" as "this one was dragged"
/// and pins it for the window's lifetime — out of the governed set, in
/// with the fixed claims. Track is idempotent per grid: a second call
/// detaches the first call's handlers before subscribing its own.</summary>
internal static class DataGridColumnCap
{
    /// <summary>Keeps the arithmetic off the exact viewport edge, where a
    /// rounding difference decides whether a scrollbar appears, and
    /// absorbs an untracked Auto column growing between recomputes.</summary>
    private const double SafetyMargin = 20;

    /// <summary>Floor under any cap, matching the floor every window's own
    /// column MinWidths already respect; WPF's own space-fitting resolves
    /// the layout below it.</summary>
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

            var governed = columns.Where(column => !pinned.Contains(column)).ToArray();
            if (governed.Length == 0) return;

            var caps = computeCap is not null
                ? Enumerable.Repeat(computeCap(columnViewportWidth), governed.Length).ToArray()
                : AutofitCaps(grid, columnViewportWidth, governed, widths);

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

    /// <summary>One cap per governed column, in the same order.</summary>
    private static double[] AutofitCaps(
        DataGrid grid, double viewportWidth, DataGridColumn[] governed, ContentWidths widths)
    {
        var governedSet = new HashSet<DataGridColumn>(governed);
        var participants = new List<DataGridColumn>(governed);
        var claimed = 0.0;
        foreach (var column in grid.Columns)
        {
            if (governedSet.Contains(column) || column.Visibility != Visibility.Visible) continue;
            if (column.Width.IsStar) participants.Add(column);
            else claimed += column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
        }

        var available = viewportWidth - claimed - SafetyMargin;
        var rows = RealizedRows(grid);
        var natural = participants.Select(column => widths.Of(grid, column, rows)).ToList();
        var floors = participants.Select(column => Math.Max(MinimumCap, column.MinWidth)).ToList();
        var shares = ColumnShares.Compute(available, natural, floors);
        return shares.Take(governed.Length).ToArray();
    }

    /// <summary>The rows WPF currently has containers for — the same
    /// population it sizes an Auto column from. One pass per recompute,
    /// shared by every participant.</summary>
    private static List<DataGridRow> RealizedRows(DataGrid grid)
    {
        var rows = new List<DataGridRow>();
        var count = grid.Items.Count;
        for (var i = 0; i < count; i++)
            if (grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow row) rows.Add(row);
        return rows;
    }

    /// <summary>Content widths from the realized cells and the header,
    /// cached per string so a layout pass costs a dictionary lookup per
    /// visible cell. Font properties are part of the key: BulkRename bolds
    /// a hand-edited row, and Settings can change the app's type scale
    /// while a window is open.</summary>
    private sealed class ContentWidths
    {
        private readonly Dictionary<(string Text, string Family, double Size, FontWeight Weight, FontStyle Style), double> _measured = new();
        private DataGridColumnHeadersPresenter? _headers;

        public double Of(DataGrid grid, DataGridColumn column, List<DataGridRow> rows)
        {
            var widest = HeaderWidthOf(grid, column);
            foreach (var row in rows)
            {
                var width = column.GetCellContent(row) switch
                {
                    TextBlock text => TextWidthOf(text),
                    FrameworkElement other => other.DesiredSize.Width,
                    _ => 0,
                };
                widest = Math.Max(widest, width);
            }
            return widest;
        }

        /// <summary>The header's own text plus the header's padding, measured
        /// rather than read from its DesiredSize, which is clipped to the
        /// column once the column is capped.</summary>
        private double HeaderWidthOf(DataGrid grid, DataGridColumn column)
        {
            _headers ??= FindDescendant<DataGridColumnHeadersPresenter>(grid);
            if (_headers is null) return 0;
            var header = FindDescendants<DataGridColumnHeader>(_headers).FirstOrDefault(h => h.Column == column);
            if (header is null) return 0;
            var text = FindDescendant<TextBlock>(header);
            return text is null
                ? header.DesiredSize.Width
                : TextWidthOf(text) + header.Padding.Left + header.Padding.Right;
        }

        private double TextWidthOf(TextBlock text)
        {
            var key = (text.Text, text.FontFamily.Source, text.FontSize, text.FontWeight, text.FontStyle);
            if (!_measured.TryGetValue(key, out var width))
            {
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
