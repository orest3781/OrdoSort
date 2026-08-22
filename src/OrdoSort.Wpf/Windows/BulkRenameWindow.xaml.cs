using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class BulkRenameWindow : Window
{
    // ContentColumnShare lived here — a flat fraction of the viewport
    // that this column's cap was set to. DataGridColumnCap now computes
    // the cap as what is actually left over instead, so there is no
    // share to tune: see that class's Track doc comment for the measured
    // reason a fixed fraction truncated content while the filler column
    // beside it held space nobody was using.

    private readonly BulkRenameViewModel _vm;

    public BulkRenameWindow(BulkRenameViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(PreviewGrid, CurrentColumn, NoteColumn);
        _vm.SelectionRestored += OnSelectionRestored;
        Closed += (_, _) => _vm.SelectionRestored -= OnSelectionRestored;
    }

    /// <summary>Re-applies a selection the view model preserved across a
    /// rebuilt preview (audit QC-11). The grid drops its selection whenever
    /// Preview is Reset, so without this the rows come back unselected even
    /// though the view model still knows which ones the user picked.
    /// Assigning SelectedItems re-enters OnSelectionChanged below, which
    /// pushes the same set straight back down — idempotent, and it keeps the
    /// two sides in agreement rather than needing a re-entrancy guard.</summary>
    private void OnSelectionRestored(object? sender, IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return;

        var wanted = new HashSet<string>(sources, System.StringComparer.OrdinalIgnoreCase);
        PreviewGrid.SelectedItems.Clear();
        foreach (var row in PreviewGrid.Items.OfType<RenameRow>())
            if (wanted.Contains(row.Source))
                PreviewGrid.SelectedItems.Add(row);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SelectedSources = PreviewGrid.SelectedItems
            .OfType<RenameRow>()
            .Select(r => r.Source)
            .ToList();

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _ = _vm.AddFilesAsync(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true)
            _ = _vm.AddFilesAsync(Directory.GetFiles(dlg.FolderName));
    }

    private void OnJumpToNextStray(object sender, RoutedEventArgs e)
    {
        var from = PreviewGrid.SelectedItem is RenameRow r
            ? PreviewGrid.Items.IndexOf(r) : -1;
        var next = _vm.IndexOfNextNeedingName(from);
        if (next >= 0 && PreviewGrid.Items[next] is RenameRow target) BeginEdit(target);
    }

    /// <summary>Reads the selection the view model preserved, not the
    /// grid's live one (audit QC-11): the grid's is empty for a beat after
    /// every rebuilt preview, which is any keystroke in one of the operation
    /// fields, and removing an empty selection is a silent no-op.</summary>
    private void OnRemoveSelected(object sender, RoutedEventArgs e) => _vm.RemoveSelected();

    /// <summary>The "New name" column is the escape hatch for the handful of
    /// files an operation can't name, so getting into it must not be a secret.
    /// F2 and Enter both start an edit on the selected row, as they do in every
    /// other grid; double-click still works.</summary>
    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (PreviewGrid.SelectedItem is not RenameRow row) return;
        // while a cell editor is open these keys belong to it: Enter commits
        // and Escape cancels, both of which the grid already does
        if (!_editing && e.Key is System.Windows.Input.Key.F2 or System.Windows.Input.Key.Enter)
        {
            BeginEdit(row);
            e.Handled = true;
        }
    }

    /// <summary>Put the caret in the New name cell, seeded. A stray in review
    /// mode opens as "20240802-" with the caret after it, so the typing left is
    /// the name — the only part a person actually has to supply.</summary>
    private void BeginEdit(RenameRow row)
    {
        var column = PreviewGrid.Columns.FirstOrDefault(c => !c.IsReadOnly);
        if (column is null) return;
        PreviewGrid.CurrentCell = new DataGridCellInfo(row, column);
        PreviewGrid.ScrollIntoView(row, column);
        _editing = true;
        PreviewGrid.BeginEdit();
        // the editor exists only after BeginEdit, so seed on the next beat
        Dispatcher.BeginInvoke(() =>
        {
            if (GetEditor() is not { } box) return;
            if (row.NeedsName && row.EditSeed != box.Text) box.Text = row.EditSeed;
            box.CaretIndex = box.Text.Length;
        });
    }

    private TextBox? GetEditor() =>
        PreviewGrid.CurrentCell.Column?.GetCellContent(PreviewGrid.CurrentCell.Item)
            as TextBox;

    private bool _editing;

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // cleared FIRST: Escape cancels the edit and returns below, and leaving
        // this set would quietly stop F2 and Enter working for the rest of the
        // session
        _editing = false;
        if (e.EditAction != DataGridEditAction.Commit
            || e.Row.Item is not RenameRow row
            || e.EditingElement is not TextBox box) return;
        // route the hand edit through the view model (it strips extensions,
        // clears on empty, and rebuilds the preview)
        var text = box.Text;
        var wasAt = PreviewGrid.Items.IndexOf(row);
        Dispatcher.BeginInvoke(() =>
        {
            _vm.SetOverride(row.Source, text);
            // straight on to the next file still waiting on a name: fixing six
            // strays out of seventy-five should be type-Enter-type-Enter, not a
            // hunt through the rows that are already right
            var next = _vm.IndexOfNextNeedingName(wasAt);
            if (next >= 0 && PreviewGrid.Items[next] is RenameRow target)
                BeginEdit(target);
        });
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddFilesAsync(paths);
    }
}
