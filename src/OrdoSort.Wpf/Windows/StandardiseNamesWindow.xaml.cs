using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class StandardiseNamesWindow : Window
{
    private readonly StandardiseNamesViewModel _vm;

    public StandardiseNamesWindow(StandardiseNamesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ResultsGrid, ResultColumn);
    }

    /// <summary>Refuses the close outright while a batch is running, rather
    /// than plumbing cancellation through BulkRename.Execute/Revert — Core
    /// methods Bulk rename and MatchMerge also call, so threading a token
    /// through them would be a disproportionate change for what this tool
    /// needs. Unlike MergePdfsWindow.OnClosed's cancel-on-close (its own doc
    /// comment: "a closed window must not keep working invisibly" — built
    /// for Office conversion work that can run for minutes), a batch here is
    /// a handful of File.Moves inside one directory — sub-second — so
    /// blocking the close for that long is the honest trade. It reaches the
    /// same "a closed window must not keep moving files" rule
    /// UnlockViewModel.CancelUnlock's own doc comment states (echoed in
    /// BulkRenameViewModel's _batchCts field comment too) by a different
    /// route than either: not by cancelling the work, but by making sure it
    /// never starts running detached from a closed window in the first
    /// place.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            _vm.ExplainCloseWasRefused();
        }
        base.OnClosing(e);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", Multiselect = true };
        // fire and forget: the intake and rename work is off-thread, so the
        // dialog closes immediately instead of hanging on a slow share —
        // same idiom as every sibling tool's own OnAddFiles.
        if (dlg.ShowDialog(this) == true) _ = _vm.AddFilesAsync(dlg.FileNames);
    }

    /// <summary>Pushes the grid's live selection down into the view model —
    /// DataGrid.SelectedItems is not bindable, the same reason
    /// BulkRenameWindow.OnSelectionChanged exists. Rows here, not paths (see
    /// StandardiseNamesViewModel.SelectedRows's own doc comment): unlike
    /// BulkRenameWindow, there is no SelectionRestored counterpart to wire up
    /// here, because nothing in this window ever rebuilds Results out from
    /// under the grid's own selection.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SelectedRows = ResultsGrid.SelectedItems.OfType<StandardiseNameRow>().ToList();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e) => AcceptDrop(e.Data);

    /// <summary>The one list a drop can reach. Internal so the window test
    /// can hand it a DataObject and count the row, without a real drag.</summary>
    internal void AcceptDrop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddFilesAsync(paths);
    }
}
