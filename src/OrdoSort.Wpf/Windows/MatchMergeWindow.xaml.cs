using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class MatchMergeWindow : Window
{
    private readonly MatchMergeViewModel _vm;

    public MatchMergeWindow(MatchMergeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.AutoLoadRosterAsync();
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Multiselect = true };
        if (dlg.ShowDialog(this) == true) _vm.AddFiles(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true)
            _vm.AddFiles(Directory.GetFiles(dlg.FolderName, "*.pdf"));
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveFiles(MatchGrid.SelectedItems.OfType<MatchRow>()
            .Select(r => r.Source).ToList());

    private void OnReview(object sender, RoutedEventArgs e)
    {
        var items = _vm.ReviewItems;
        if (items.Count == 0) return;
        // a fresh WebView2 per run — never reused, so nothing leaks across files
        var win = new TriageWindow(items, _vm.ChosenColumns) { Owner = this };
        win.ShowDialog();
        _vm.Absorb(win.Outcomes);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm.AddFiles(paths);
    }
}
