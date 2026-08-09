using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class ZipMergeWindow : Window
{
    private readonly ZipMergeViewModel _vm;

    public ZipMergeWindow(ZipMergeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Zip archives (*.zip)|*.zip", Multiselect = true };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddFilesAsync(dlg.FileNames);
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveSelected(ZipsGrid.SelectedItems);

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

    /// <summary>A closed window must not keep merging zips invisibly: the
    /// work is async and owned by the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
