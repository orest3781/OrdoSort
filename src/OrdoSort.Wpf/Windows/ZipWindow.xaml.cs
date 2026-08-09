using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class ZipWindow : Window
{
    private readonly ZipViewModel _vm;

    public ZipWindow(ZipViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files (*.*)|*.*", Multiselect = true };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        // the folder itself is added as ONE row, not expanded into its
        // files — Zipper.CreateZip walks a folder row's contents itself
        // when the archive is built
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(new[] { dlg.FolderName });
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveSelected(ItemsGrid.SelectedItems);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = _vm.AddPaths(paths);
    }

    // Deliberately no OnClosed/Cancel here, unlike ZipMergeWindow/UnzipWindow/
    // PageCountsWindow: those tools run a per-row BATCH (one zip after
    // another) with something genuinely left to stop between items, so their
    // view models expose Cancel() for the window to call on close. ZipViewModel
    // has no such loop — CreateAsync is a single one-shot zip-the-whole-list
    // operation with nothing "still running invisibly" to cancel once it has
    // started, and ZipViewModel exposes no Cancel() to call. A Status update
    // landing after the window is already closed is harmless (nothing is
    // listening), same as any other command whose window closed mid-flight.
}
