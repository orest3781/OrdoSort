using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class UnzipWindow : Window
{
    /// <summary>Share of ZipsGrid's own LIVE ActualWidth that Result may grow
    /// to before ellipsizing — same mechanism and same reasoning as
    /// ZipMergeWindow's identical constant (see its own doc comment): one
    /// capped column here (Zip is the star filler), so this runs higher than
    /// BulkRename/MatchMerge's 0.35-per-of-two-columns share. Result carries
    /// the extract's own error message on Error (UnzipRow's own doc
    /// comment) — an unreadable zip's exception text was growing this
    /// column unbounded before this fix, producing the exact horizontal
    /// scrollbar every other grid's Auto content columns are capped to
    /// prevent.</summary>
    private const double ContentColumnShare = 0.45;

    private readonly UnzipViewModel _vm;

    public UnzipWindow(UnzipViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(ZipsGrid, ContentColumnShare, ResultColumn);
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

    /// <summary>A closed window must not keep extracting zips invisibly: the
    /// work is async and owned by the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
