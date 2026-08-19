using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class ZipToolsWindow : Window
{
    private readonly ZipToolsViewModel _vm;

    public ZipToolsWindow(ZipToolsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ItemsGrid, ItemsResultColumn);
        DataGridColumnCap.Track(ZipsGrid, ZipsResultColumn);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", Multiselect = true };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.ZipExtract.AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) _ = _vm.ZipExtract.AddPaths(new[] { dlg.FolderName });
    }

    private void OnAddZips(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Zip archives (*.zip)|*.zip", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _ = _vm.MergePdfs.AddPaths(dlg.FileNames);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.ZipExtract.RemoveSelected(ItemsGrid.SelectedItems);

    private void OnRemoveSelectedMerge(object sender, RoutedEventArgs e) =>
        _vm.MergePdfs.RemoveSelected(ZipsGrid.SelectedItems);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A drop lands on whichever tab is showing — the tab is the
    /// statement of intent, so routing anywhere else would silently put the
    /// files in a list the person is not looking at.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (ReferenceEquals(Tabs.SelectedItem, MergePdfsTab)) _ = _vm.MergePdfs.AddPaths(paths);
        else _ = _vm.ZipExtract.AddPaths(paths);
    }

    /// <summary>A closed window must not keep working invisibly: the work is
    /// async and owned by the view models rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
