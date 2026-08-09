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

    /// <summary>Share of ResultsGrid's own live ActualWidth each capped group
    /// column (every group column after the first — see RebuildColumns) may
    /// grow to before ellipsizing. The same flat-share DataGridColumnCap.Track
    /// overload BulkRenameWindow/MatchMergeWindow/HistoryWindow already use
    /// for their own content columns, not TriageWindow's per-column-count
    /// formula: Production has no fixed-width "Why"-style column competing
    /// for the same budget, so the simpler overload is the honest fit here,
    /// not an under-engineered shortcut.</summary>
    private const double GroupColumnShare = 0.30;

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

        if (cappedGroupColumns.Count > 0)
            DataGridColumnCap.Track(ResultsGrid, GroupColumnShare, cappedGroupColumns.ToArray());
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
