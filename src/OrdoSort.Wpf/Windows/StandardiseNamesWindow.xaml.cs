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
