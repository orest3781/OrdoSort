using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Views;

/// <summary>Who decides whether a column is shown. The default flips the
/// column's own Visibility; the File list supplies one that flips its
/// view-model flags, so its export follows what the table shows.</summary>
internal interface IColumnVisibility
{
    bool IsShown(DataGridColumn column);
    void SetShown(DataGridColumn column, bool shown);

    /// <summary>False for a column the owner keeps on show (Review matches'
    /// name and id columns); its header-menu entry is disabled.</summary>
    bool CanChange(DataGridColumn column) => true;

    /// <summary>False for a column whose showing the owner keeps itself
    /// (Review matches' spreadsheet columns, in the shared config): the
    /// per-PC layout then restores its width and order only.</summary>
    bool RestoresFromLayout(DataGridColumn column) => true;
}

/// <summary>Makes one DataGrid behave like File Explorer's Details view
/// (table rules v2, docs/superpowers/specs/2026-09-25-explorer-columns-design.md):
/// fixed widths only the user changes, fit to content on demand, a header
/// menu, drag-to-reorder with the first column pinned, a remembered layout,
/// type-to-jump and click-empty-space-to-clear. One instance per grid,
/// attached by the window that owns it.</summary>
internal sealed partial class ExplorerColumns
{
    /// <summary>No column can be dragged narrower than this.</summary>
    public const double MinColumnWidth = 40;

    private static readonly DependencyProperty InstanceProperty = DependencyProperty.RegisterAttached(
        "Instance", typeof(ExplorerColumns), typeof(ExplorerColumns));

    private sealed class OwnVisibility : IColumnVisibility
    {
        public bool IsShown(DataGridColumn column) => column.Visibility == Visibility.Visible;
        public void SetShown(DataGridColumn column, bool shown) =>
            column.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly DataGrid _grid;
    private readonly string _key;
    // Null when this grid doesn't remember its layout (see RememberByDefault).
    private readonly TableLayoutStore? _store;
    private readonly IColumnVisibility _visibility;
    private readonly Func<DateTime> _clock;
    private readonly Action<Exception> _reportSaveError;
    private readonly DataGridColumn? _explicitAnchor;
    private readonly Action? _chooseColumns;
    private bool _applied;

    private ExplorerColumns(DataGrid grid, string key, DataGridColumn? anchor, IColumnVisibility? visibility,
        TableLayoutStore? store, Func<DateTime>? clock, Action<Exception>? reportSaveError, Action? chooseColumns)
    {
        _chooseColumns = chooseColumns;
        _grid = grid;
        _key = key;
        _explicitAnchor = anchor;
        _visibility = visibility ?? new OwnVisibility();
        _reportSaveError = reportSaveError ?? (ex => App.LogCrash(ex));
        _store = store ?? (RememberByDefault ? new TableLayoutStore(TableLayoutStore.DefaultPath, _reportSaveError) : null);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Attaches the behaviour to <paramref name="grid"/>.</summary>
    /// <param name="key">The window's name in the saved-layout file.</param>
    /// <param name="anchor">The column that can never be hidden or dragged;
    /// defaults to whichever column is first. It is also kept first when it
    /// starts first.</param>
    /// <param name="visibility">Who shows and hides columns; defaults to the
    /// column's own Visibility.</param>
    /// <param name="store">Where the layout is remembered; defaults to
    /// <see cref="TableLayoutStore.DefaultPath"/>.</param>
    /// <param name="clock">Time source for type-ahead; tests pass their own.</param>
    /// <param name="reportSaveError">Where a failed save is reported;
    /// defaults to crash.log.</param>
    /// <param name="chooseColumns">Opens the table's full column chooser. When
    /// given, the header menu lists only the columns on show and ends with
    /// "More columns…", as in Explorer — for tables with too many columns
    /// for one menu (Review matches' spreadsheet columns).</param>
    public static ExplorerColumns Attach(DataGrid grid, string key, DataGridColumn? anchor = null,
        IColumnVisibility? visibility = null, TableLayoutStore? store = null, Func<DateTime>? clock = null,
        Action<Exception>? reportSaveError = null, Action? chooseColumns = null)
    {
        var explorer = new ExplorerColumns(grid, key, anchor, visibility, store, clock, reportSaveError, chooseColumns);
        grid.SetValue(InstanceProperty, explorer);
        grid.MinColumnWidth = MinColumnWidth;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.Loaded += (_, _) => explorer.ApplySaved();
        grid.Unloaded += (_, _) => explorer.Save();
        grid.Columns.CollectionChanged += explorer.OnColumnsChanged;
        grid.ColumnReordering += explorer.OnColumnReordering;
        grid.ColumnReordered += (_, _) => explorer.KeepAnchorFirst();
        explorer.WireInput();
        return explorer;
    }

    /// <summary>Whether a grid attached without its own store remembers its
    /// layout. Always true in the app; the test assembly turns it off, since
    /// every window a test builds would otherwise save its layout on close
    /// and hand it to the next test that opens the same window.</summary>
    internal static bool RememberByDefault { get; set; } = true;

    /// <summary>The behaviour attached to <paramref name="grid"/>, if any.</summary>
    public static ExplorerColumns? For(DataGrid grid) => (ExplorerColumns?)grid.GetValue(InstanceProperty);

    /// <summary>The column that is never hidden or dragged.</summary>
    public DataGridColumn? Anchor => _explicitAnchor ?? FirstDataColumn();

    private DataGridColumn? FirstDataColumn() =>
        _grid.Columns.Where(c => !IsControlColumn(c)).OrderBy(c => c.DisplayIndex).FirstOrDefault();

    // The anchor is "kept first" only if it was first when the grid opened;
    // an explicit anchor elsewhere (the File list's File name) just stays put.
    private DataGridColumn? _pinnedFirst;

    private static string? HeaderOf(DataGridColumn column) => column.Header as string;

    /// <summary>A control column — the grid's frozen columns, such as Bulk
    /// rename's tick boxes — is part of the table's furniture, not its data:
    /// it stays in front in declared order and is never dragged, fitted,
    /// hidden or saved.</summary>
    private bool IsControlColumn(DataGridColumn column) =>
        _grid.Columns.IndexOf(column) < _grid.FrozenColumnCount;

    /// <summary>Applies the saved layout once, when the grid first loads:
    /// widths, visibility, order, then sort. Columns the save doesn't
    /// mention keep their XAML width; saved columns the grid lacks are
    /// ignored.</summary>
    public void ApplySaved()
    {
        if (_applied) return;
        _applied = true;
        _pinnedFirst = FirstDataColumn();
        if (_explicitAnchor is not null && _explicitAnchor != _pinnedFirst) _pinnedFirst = null;

        var saved = _store?.Load(_key);
        if (saved is null) return;
        var byHeader = saved.Columns.GroupBy(c => c.Header).ToDictionary(g => g.Key, g => g.Last());

        foreach (var column in _grid.Columns)
            if (HeaderOf(column) is { } header && byHeader.TryGetValue(header, out var layout))
            {
                column.Width = new DataGridLength(Math.Max(MinColumnWidth, layout.Width));
                if (column != Anchor && _visibility.RestoresFromLayout(column)) _visibility.SetShown(column, layout.Visible);
            }

        // Order: saved positions first (stable for ties), unknown columns after.
        var ordered = _grid.Columns
            .Select((column, declared) => (column, declared,
                rank: IsControlColumn(column) ? int.MinValue
                    : HeaderOf(column) is { } h && byHeader.TryGetValue(h, out var l) ? l.DisplayIndex : int.MaxValue))
            .OrderBy(t => t.rank).ThenBy(t => t.declared)
            .Select(t => t.column)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        KeepAnchorFirst();

        if (saved.SortHeader is { } sortHeader && saved.SortDirection is { } direction
            && _grid.CanUserSortColumns
            && _grid.Columns.FirstOrDefault(c => HeaderOf(c) == sortHeader) is { SortMemberPath: { Length: > 0 } path } sortColumn)
        {
            foreach (var column in _grid.Columns) column.SortDirection = null;
            _grid.Items.SortDescriptions.Clear();
            _grid.Items.SortDescriptions.Add(new SortDescription(path, direction));
            sortColumn.SortDirection = direction;
        }
    }

    /// <summary>A column added after the grid opened (Review matches' Why
    /// column, per file) takes its saved width, visibility and place, as if
    /// it had been there when the layout was applied. Wherever it lands, the
    /// pinned first column stays first.</summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_applied || e.NewItems is null) return;
        var saved = _store?.Load(_key);
        foreach (DataGridColumn column in e.NewItems)
        {
            if (IsControlColumn(column)) continue;
            if (HeaderOf(column) is { } header && saved?.Columns.LastOrDefault(c => c.Header == header) is { } layout)
            {
                column.Width = new DataGridLength(Math.Max(MinColumnWidth, layout.Width));
                if (_visibility.RestoresFromLayout(column)) _visibility.SetShown(column, layout.Visible);
                column.DisplayIndex = Math.Clamp(layout.DisplayIndex, 0, _grid.Columns.Count - 1);
            }
        }
        KeepAnchorFirst();
    }

    /// <summary>Shows <paramref name="items"/> in place of the current rows,
    /// keeping the current sort. A new ItemsSource clears WPF's sort, so a
    /// window that swaps its rows (Review matches, per file) would otherwise
    /// lose the saved or clicked sort every time.</summary>
    public void ReplaceItems(System.Collections.IEnumerable items)
    {
        var sorts = _grid.Items.SortDescriptions.ToList();
        var directions = _grid.Columns.Where(c => c.SortDirection is not null)
            .Select(c => (Column: c, Direction: c.SortDirection))
            .ToList();
        _grid.ItemsSource = items;
        foreach (var sort in sorts) _grid.Items.SortDescriptions.Add(sort);
        foreach (var (column, direction) in directions) column.SortDirection = direction;
    }

    private void OnColumnReordering(object? sender, DataGridColumnReorderingEventArgs e)
    {
        if (e.Column == Anchor || IsControlColumn(e.Column)) e.Cancel = true;
    }

    /// <summary>After any reorder, puts the control columns back in front
    /// and the pinned first column straight after them.</summary>
    public void KeepAnchorFirst()
    {
        var controls = _grid.Columns.Where(IsControlColumn).ToList();
        for (var i = 0; i < controls.Count; i++)
            if (controls[i].DisplayIndex != i) controls[i].DisplayIndex = i;
        var pinned = _pinnedFirst ?? (_applied ? null : FirstDataColumn());
        if (pinned is not null && !IsControlColumn(pinned) && pinned.DisplayIndex != controls.Count)
            pinned.DisplayIndex = controls.Count;
    }

    /// <summary>Writes the current layout to the store. Called when the grid
    /// unloads (its window closed). A failed save is reported, never thrown:
    /// losing a column width must not break closing a window.</summary>
    public void Save()
    {
        if (_store is null) return;
        var columns = _grid.Columns
            .Where(c => HeaderOf(c) is not null)
            .Select(c => new ColumnLayout(HeaderOf(c)!, c.ActualWidth > 0 ? c.ActualWidth : c.Width.DisplayValue,
                _visibility.IsShown(c), c.DisplayIndex))
            .ToList();
        var sorted = _grid.Columns.FirstOrDefault(c => c.SortDirection is not null && HeaderOf(c) is not null);
        try
        {
            _store.Save(_key, new TableLayout(columns, sorted is null ? null : HeaderOf(sorted), sorted?.SortDirection));
        }
        catch (Exception ex)
        {
            _reportSaveError(ex);
        }
    }

    // Input wiring (fit, menu, type-ahead, empty space) lives in
    // ExplorerColumns.Input.cs (Tasks 3 and 6).
    partial void WireInputCore();

    private void WireInput() => WireInputCore();
}
