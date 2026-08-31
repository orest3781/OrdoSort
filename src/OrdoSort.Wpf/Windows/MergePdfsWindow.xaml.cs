using System.Windows;
using Microsoft.Win32;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class MergePdfsWindow : Window
{
    private readonly MergePdfsViewModel _vm;

    public MergePdfsWindow(MergePdfsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ItemsGrid, ResultColumn);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = SupportedFilesFilter(), Multiselect = true };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(dlg.FileNames);
    }

    /// <summary>Built from MergeTypes.AllExtensions, not a second hard-coded
    /// list: the old hard-coded "*.pdf;*.zip" filter meant this dialog was
    /// the ONE way to reach this window that could never actually add any
    /// of the types Task 7 already widened intake to accept — only
    /// drag-and-drop could. "All files" stays as a second choice: a pick
    /// outside the supported set is still refused by AddPaths' own intake
    /// filtering with its usual note (AddNote/IntakeNoun), so offering it
    /// costs nothing and saves a trip back to this dialog for someone who
    /// already knows what they meant to add.</summary>
    private static string SupportedFilesFilter()
    {
        var patterns = string.Join(";", MergeTypes.AllExtensions
            .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
            .Select(extension => $"*.{extension}"));
        return $"Supported files ({patterns})|{patterns}|All files (*.*)|*.*";
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveSelected(ItemsGrid.SelectedItems);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e) => AcceptDrop(e.Data);

    /// <summary>The one list a drop can reach. Internal so the window test
    /// can hand it a DataObject and count the row, without a real drag.</summary>
    internal void AcceptDrop(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddPaths(paths);
    }

    /// <summary>A closed window must not keep working invisibly: the work is
    /// async and owned by the view model rather than the window. Dispose,
    /// after Cancel: whatever Office session the window's converter started
    /// or borrowed during this session is torn down or restored now, not
    /// left running for however long it takes the GC to get around to it —
    /// see MergePdfsViewModel.Dispose.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
