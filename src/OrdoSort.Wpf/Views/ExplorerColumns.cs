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
    private bool _applied;

    private ExplorerColumns(DataGrid grid, string key, DataGridColumn? anchor, IColumnVisibility? visibility,
        TableLayoutStore? store, Func<DateTime>? clock, Action<Exception>? reportSaveError)
    {
        _grid = grid;
        _key = key;
        _explicitAnchor = anchor;
        _visibility = visibility ?? new OwnVisibility();
        _store = store ?? (RememberByDefault ? new TableLayoutStore(TableLayoutStore.DefaultPath) : null);
        _clock = clock ?? (() => DateTime.UtcNow);
        _reportSaveError = reportSaveError ?? (ex => App.LogCrash(ex));
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
    public static ExplorerColumns Attach(DataGrid grid, string key, DataGridColumn? anchor = null,
        IColumnVisibility? visibility = null, TableLayoutStore? store = null, Func<DateTime>? clock = null,
        Action<Exception>? reportSaveError = null)
    {
        var explorer = new ExplorerColumns(grid, key, anchor, visibility, store, clock, reportSaveError);
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
    public DataGridColumn? Anchor =>
        _explicitAnchor ?? _grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault();

    // The anchor is "kept first" only if it was first when the grid opened;
    // an explicit anchor elsewhere (the File list's File name) just stays put.
    private DataGridColumn? _pinnedFirst;

    private static string? HeaderOf(DataGridColumn column) => column.Header as string;

    /// <summary>Applies the saved layout once, when the grid first loads:
    /// widths, visibility, order, then sort. Columns the save doesn't
    /// mention keep their XAML width; saved columns the grid lacks are
    /// ignored.</summary>
    public void ApplySaved()
    {
        if (_applied) return;
        _applied = true;
        _pinnedFirst = _grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault();
        if (_explicitAnchor is not null && _explicitAnchor != _pinnedFirst) _pinnedFirst = null;

        var saved = _store?.Load(_key);
        if (saved is null) return;
        var byHeader = saved.Columns.GroupBy(c => c.Header).ToDictionary(g => g.Key, g => g.Last());

        foreach (var column in _grid.Columns)
            if (HeaderOf(column) is { } header && byHeader.TryGetValue(header, out var layout))
            {
                column.Width = new DataGridLength(Math.Max(MinColumnWidth, layout.Width));
                if (column != Anchor) _visibility.SetShown(column, layout.Visible);
            }

        // Order: saved positions first (stable for ties), unknown columns after.
        var ordered = _grid.Columns
            .Select((column, declared) => (column, declared,
                rank: HeaderOf(column) is { } h && byHeader.TryGetValue(h, out var l) ? l.DisplayIndex : int.MaxValue))
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

    /// <summary>A column added after the grid opened (Triage builds its
    /// columns per roster) takes its saved width, if it has one.</summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_applied || e.NewItems is null) return;
        var saved = _store?.Load(_key);
        if (saved is null) return;
        foreach (DataGridColumn column in e.NewItems)
            if (HeaderOf(column) is { } header && saved.Columns.LastOrDefault(c => c.Header == header) is { } layout)
                column.Width = new DataGridLength(Math.Max(MinColumnWidth, layout.Width));
    }

    private void OnColumnReordering(object? sender, DataGridColumnReorderingEventArgs e)
    {
        if (e.Column == Anchor) e.Cancel = true;
    }

    /// <summary>After any reorder, puts the pinned first column back first.</summary>
    public void KeepAnchorFirst()
    {
        var pinned = _pinnedFirst ?? (_applied ? null : _grid.Columns.OrderBy(c => c.DisplayIndex).FirstOrDefault());
        if (pinned is not null && pinned.DisplayIndex != 0) pinned.DisplayIndex = 0;
    }

    /// <summary>Writes the current layout to the store. Called when the grid
    /// unloads (its window closed). A failed write is reported, never thrown:
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _reportSaveError(ex);
        }
    }

    // Input wiring (fit, menu, type-ahead, empty space) lives in
    // ExplorerColumns.Input.cs (Tasks 3 and 6).
    partial void WireInputCore();

    private void WireInput() => WireInputCore();
}
