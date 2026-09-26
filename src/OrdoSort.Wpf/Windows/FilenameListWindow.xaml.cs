using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class FilenameListWindow : Window
{
    private readonly FilenameListViewModel _vm;

    public FilenameListWindow(FilenameListViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // DataGridColumn is not part of the visual or logical tree (it lives
        // in DataGrid.Columns, a plain object collection, not a Panel's
        // Children), so it has no DataContext and no ancestor a
        // RelativeSource could walk up to. A column's own
        // Visibility="{Binding ...}" therefore binds to nothing and fails
        // SILENTLY — the column just stays visible forever, which is worse
        // than a compile error because the XAML looks correct. So instead
        // the window pushes each Show* flag down onto its column by name,
        // the same shape of answer SelectedPaths uses below for
        // DataGrid.SelectedItems (also not bindable): sync once now so the
        // initial state matches Columns before any toggle happens, then
        // again every time one of the Show* properties changes.
        SyncColumnVisibility();
        // Explorer-style columns (table rules v2). The header checklist flips
        // the same view-model flags as the column toggles, so the export
        // always matches what's shown. File name is the anchor: always shown,
        // never dragged.
        ExplorerColumns.Attach(NamesGrid, "FilenameList", anchor: FileNameColumn,
            visibility: new FlagVisibility(this));
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.SelectionRestored += OnSelectionRestored;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SelectionRestored -= OnSelectionRestored;
        };
    }

    /// <summary>Re-applies a selection the view model preserved across a
    /// reproject (audit FL-03). The grid drops its selection whenever Rows is
    /// Reset, so without this the rows come back unselected even though the
    /// view model still knows which ones the user picked. Assigning
    /// SelectedItems re-enters OnSelectionChanged below, which pushes the same
    /// set straight back down — idempotent, and it keeps the two sides in
    /// agreement rather than needing a re-entrancy guard.</summary>
    private void OnSelectionRestored(object? sender, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        var wanted = new HashSet<string>(paths, System.StringComparer.OrdinalIgnoreCase);
        NamesGrid.SelectedItems.Clear();
        foreach (var row in NamesGrid.Items.OfType<OrdoSort.Core.FilenameList.FileRow>())
            if (wanted.Contains(row.FullPath))
                NamesGrid.SelectedItems.Add(row);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilenameListViewModel.ShowNumber)
            or nameof(FilenameListViewModel.ShowSize)
            or nameof(FilenameListViewModel.ShowModified)
            or nameof(FilenameListViewModel.ShowFolder)
            or nameof(FilenameListViewModel.ShowFullPath)
            or nameof(FilenameListViewModel.ShowPages))
        {
            SyncColumnVisibility();
        }
    }

    private void SyncColumnVisibility()
    {
        NumberColumn.Visibility = _vm.ShowNumber ? Visibility.Visible : Visibility.Collapsed;
        SizeColumn.Visibility = _vm.ShowSize ? Visibility.Visible : Visibility.Collapsed;
        ModifiedColumn.Visibility = _vm.ShowModified ? Visibility.Visible : Visibility.Collapsed;
        FolderColumn.Visibility = _vm.ShowFolder ? Visibility.Visible : Visibility.Collapsed;
        FullPathColumn.Visibility = _vm.ShowFullPath ? Visibility.Visible : Visibility.Collapsed;
        PagesColumn.Visibility = _vm.ShowPages ? Visibility.Visible : Visibility.Collapsed;
    }

    // DataGrid.SelectedItems is not bindable, so the window pushes the
    // selection down rather than the view model reaching up for it.
    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _vm.SelectedPaths = NamesGrid.SelectedItems
            .OfType<OrdoSort.Core.FilenameList.FileRow>()
            .Select(r => r.FullPath)
            .ToList();

    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            _vm.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+C routes to the SAME method the Copy button uses (audit FL-04).
        // The grid's ClipboardCopyMode is None, so WPF's own copy — tab-separated
        // cells with no header row — is gone; without this branch the keystroke
        // would simply do nothing, which is a worse tool than one that copies the
        // wrong format. Sharing PerformCopy is what makes the two paths incapable
        // of disagreeing, rather than two implementations that happen to match.
        if (e.Key == System.Windows.Input.Key.C
            && e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            PerformCopy();
            e.Handled = true;
        }
    }

    // CLIPBOARD RULE: System.Windows.Clipboard appears ONLY here, never in
    // the view model — Clipboard is a WPF/COM type the headless MTA tests
    // can't safely touch.
    private void OnCopy(object sender, RoutedEventArgs e) => PerformCopy();

    private void PerformCopy()
    {
        var text = _vm.CopyText;
        if (text.Length == 0) return;   // nothing listed yet — Clipboard.SetText throws on ""
        try
        {
            Clipboard.SetText(text);
            _vm.NoteCopied();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another app is holding the clipboard right now — say so instead
            // of losing the failure silently
            _vm.NoteClipboardBusy();
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

    /// <summary>Maps a column to the view-model flag that shows it; columns
    /// without a flag (File name) are always shown.</summary>
    private sealed class FlagVisibility(FilenameListWindow window) : IColumnVisibility
    {
        public bool IsShown(DataGridColumn column) => Flag(column) is not { } get || get();

        public void SetShown(DataGridColumn column, bool shown)
        {
            var vm = window._vm;
            if (column == window.NumberColumn) vm.ShowNumber = shown;
            else if (column == window.SizeColumn) vm.ShowSize = shown;
            else if (column == window.ModifiedColumn) vm.ShowModified = shown;
            else if (column == window.FolderColumn) vm.ShowFolder = shown;
            else if (column == window.FullPathColumn) vm.ShowFullPath = shown;
            else if (column == window.PagesColumn) vm.ShowPages = shown;
        }

        private Func<bool>? Flag(DataGridColumn column)
        {
            var vm = window._vm;
            if (column == window.NumberColumn) return () => vm.ShowNumber;
            if (column == window.SizeColumn) return () => vm.ShowSize;
            if (column == window.ModifiedColumn) return () => vm.ShowModified;
            if (column == window.FolderColumn) return () => vm.ShowFolder;
            if (column == window.FullPathColumn) return () => vm.ShowFullPath;
            if (column == window.PagesColumn) return () => vm.ShowPages;
            return null;
        }
    }
}
