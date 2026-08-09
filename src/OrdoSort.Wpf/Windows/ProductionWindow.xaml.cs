using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

/// <summary>Production report: point it at a folder of sweep CSVs, tick
/// group/sum columns, read the grouped counts and sums. The one grid in this
/// feature with dynamic columns — TriageWindow.xaml.cs is the template for
/// building DataGrid columns from a runtime list in code-behind, including
/// DataGridColumnCap.Track and the selected-row contrast lessons in its own
/// comments (a DataGridColumn isn't part of the visual/logical tree, so an
/// implicit XAML style never reaches it).</summary>
public partial class ProductionWindow : Window
{
    /// <summary>Floor for the first (filler) group column — the same 120px
    /// floor History/MatchMerge/BulkRename use for their own star column.
    /// This window is a full-size report tool, not TriageWindow's narrow
    /// 380-440px side panel, so there's no reason to shrink it the way
    /// TriageWindow's own FillerMinWidth (60) does.</summary>
    private const double FillerMinWidth = 120;

    /// <summary>Headroom subtracted from the group-column budget for the
    /// DataGrid's own border/padding — same role as TriageWindow's own
    /// SafetyMargin (a possible vertical scrollbar is reserved separately, by
    /// DataGridColumnCap.Track itself, before this budget ever sees its
    /// viewport width — see that class's own doc comment).</summary>
    private const double SafetyMargin = 20;

    /// <summary>Estimated width reserved PER numeric column (Records plus
    /// every ticked sum column — see RebuildColumns' own isNumericColumn
    /// comment) when computing how much budget is left for the capped group
    /// columns. Those columns are Auto and deliberately left UNCAPPED (short
    /// formatted numbers, "0.##" InvariantCulture —
    /// ProductionViewModel.RecomputeResults), but their rendered width still
    /// comes out of the same viewport a group column's cap is computed
    /// against, and — unlike TriageWindow's WhyColumnWidth, a single
    /// fixed-width column — there's no exact figure to subtract: both HOW
    /// MANY numeric columns exist (Records + however many sum columns are
    /// ticked) and their content are user/data controlled, and an Auto
    /// column's width is driven by its HEADER TEXT too, not just its short
    /// numeric cell values — a sum column's header is a real, arbitrary CSV
    /// column name (this app's own default, "PDF-PAGE-COUNT", is 14
    /// characters). 90px was the first guess here and measured directly
    /// (AutoFitColumnTests.Production_AtMinWidthWithFourGroupColumnsNo
    /// HorizontalScrollbar) short: "PDF-PAGE-COUNT" alone rendered at 133px,
    /// leaving too little reserved and a real, reproduced horizontal
    /// scrollbar at this window's own MinWidth with many rows. 140px is
    /// comfortably above that measurement — chosen generously so a further
    /// miss here fails safe (a slightly smaller group-column cap) rather
    /// than unsafe (an overflow), matching the ~150px magnitude
    /// TriageWindow's own WhyColumnWidth already reserves for
    /// comparably-sized text.</summary>
    private const double NumericColumnWidthEstimate = 140;

    /// <summary>Same FindResource lookup as TriageWindow.xaml.cs's own
    /// GridCellTextStyle — resolved from code since this grid's columns are
    /// built programmatically, not declared in XAML.</summary>
    private static Style GridCellTextStyle => (Style)Application.Current.FindResource("GridCellText");

    private readonly ProductionViewModel _vm;

    public ProductionWindow(ProductionViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RebuildColumns();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProductionViewModel.ResultsVersion)) RebuildColumns();
        };
    }

    /// <summary>Rebuilds ResultsGrid.Columns from the view model's current
    /// ColumnNames whenever ResultsVersion changes — a fresh load, a tick on
    /// either pick list, or a DatetimeColumn change can each add, drop, or
    /// reorder columns, so unlike every other grid in the app (TriageWindow's
    /// own roster columns included — only its single "Why" column is ever
    /// inserted/removed after construction) this grid has no stable column
    /// identity to keep tracking across calls at all: every column here is
    /// discarded and rebuilt from scratch each time.
    ///
    /// That means DataGridColumnCap.Track (below) is called fresh each
    /// rebuild too, rather than once in the constructor the way every other
    /// window's Track call works. Each call subscribes its own SizeChanged/
    /// drag handlers to ResultsGrid; the PREVIOUS call's handlers stay
    /// subscribed as well, but the DataGridColumn objects they close over are
    /// no longer in Columns, so they only ever set MaxWidth on orphaned
    /// objects nobody renders — harmless, and bounded by how many times a
    /// person ticks a box or loads a folder in one window session, not
    /// unbounded growth. Accepted rather than engineered around: avoiding it
    /// would mean either changing DataGridColumnCap's fixed-array contract
    /// (shared by four other windows) or hand-rolling a parallel live-tracking
    /// mechanism just for this one grid — more risk than the cosmetic
    /// leftover handlers it would avoid. Skipping Track for the capped
    /// columns instead is not an option: WPF live-clamps a column drag to its
    /// CURRENT MaxWidth, so without Track's DragStarted/DragCompleted pair a
    /// person could never widen a capped column by hand at all — the exact
    /// bug TriageWindow's own "FIX ROUND 5" fixed for the other four grids
    /// (DataGridColumnCap.cs's own doc comment).</summary>
    private void RebuildColumns()
    {
        ResultsGrid.Columns.Clear();
        var names = _vm.ColumnNames;

        var recordsIndex = names.Count;
        for (var i = 0; i < names.Count; i++)
            if (names[i] == "Records") { recordsIndex = i; break; }

        // Records + every ticked sum column — see NumericColumnWidthEstimate's
        // own doc comment for why this count (not a fixed figure) drives the
        // budget reservation below.
        var numericColumnCount = Math.Max(0, names.Count - recordsIndex);

        var cappedGroupColumns = new List<DataGridColumn>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            // the first group column (if any) is the filler — the most
            // variable, most-read value (a folder or employee name) — same
            // reasoning TriageWindow's own leading-roster-column comment
            // gives for its choice.
            var isFirstGroupColumn = i == 0 && recordsIndex > 0;
            // Records and every sum column hold short formatted numbers
            // ("0.##" InvariantCulture — ProductionViewModel.RecomputeResults)
            // that need no cap at all, the same reasoning HistoryWindow.xaml
            // gives for leaving its own short "Undone" column uncapped.
            var isNumericColumn = i >= recordsIndex;

            var style = new Style(typeof(TextBlock), GridCellTextStyle);
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding($"[{name}]")));
            if (isNumericColumn)
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));

            var column = new DataGridTextColumn
            {
                Header = name,
                Binding = new Binding($"[{name}]"),
                Width = isFirstGroupColumn
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : DataGridLength.Auto,
                ElementStyle = style,
            };
            if (isFirstGroupColumn) column.MinWidth = FillerMinWidth;
            else if (i < recordsIndex) cappedGroupColumns.Add(column);   // a group column, but not the filler

            ResultsGrid.Columns.Add(column);
        }

        // Budget-DIVIDING, not a flat per-column share: unlike MatchMerge/
        // BulkRename/HistoryWindow's own flat-share Track calls — each tuned
        // for a small, REPO-FIXED capped-column count baked into their own
        // share constant — the number of capped group columns here is
        // however many the user has ticked beyond the first, with no upper
        // bound. A flat share (the first version of this fix) gives EVERY
        // capped column the same independent MaxWidth, so N capped columns
        // can jointly claim N times that share — 4 ticked group columns at a
        // 0.30 share alone would demand 120% of the viewport before Records/
        // the sum columns are even counted, guaranteed overflow, the exact
        // invariant DataGridColumnCap exists to prevent. TriageWindow solved
        // the identical "variable roster-column count" problem with the
        // Func&lt;double,double&gt; overload dividing ITS OWN budget by
        // cappedColumnCount (TriageWindow.xaml.cs's own ComputeRosterColumnCap)
        // — this mirrors that shape exactly, substituting
        // NumericColumnWidthEstimate*numericColumnCount for Triage's single
        // fixed WhyColumnWidth reservation.
        if (cappedGroupColumns.Count > 0)
        {
            var cappedColumnCount = cappedGroupColumns.Count;

            double ComputeGroupColumnCap(double viewportWidth)
            {
                var groupBudget = Math.Max(FillerMinWidth,
                    viewportWidth - SafetyMargin - numericColumnCount * NumericColumnWidthEstimate);
                return Math.Max(20, (groupBudget - FillerMinWidth) / cappedColumnCount);
            }

            DataGridColumnCap.Track(ResultsGrid, ComputeGroupColumnCap, cappedGroupColumns.ToArray());
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm.AddPaths(paths);
    }
}
