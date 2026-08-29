using System.Windows;
using Microsoft.Win32;
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
        var dlg = new OpenFileDialog
        {
            Filter = "PDFs and zip archives (*.pdf;*.zip)|*.pdf;*.zip|PDF files (*.pdf)|*.pdf|Zip archives (*.zip)|*.zip",
            Multiselect = true,
        };
        // fire and forget: the add work is off-thread, so the dialog closes
        // immediately instead of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddPaths(dlg.FileNames);
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
    /// async and owned by the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
