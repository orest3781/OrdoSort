using System.ComponentModel;
using System.Windows;
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
    /// needs. Unlike MergePdfsWindow.OnClosed's cancel-on-close (built for
    /// work measured in minutes), a batch here is a handful of File.Moves
    /// inside one directory — sub-second — so blocking the close for that
    /// long is the honest trade, and it honours the same rule
    /// MergePdfsWindow's own comment states — a closed window must not keep
    /// moving files — by making sure this window is never closed while
    /// files still are.</summary>
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
