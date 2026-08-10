using System.Windows;
using Microsoft.Win32;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class PageCountsWindow : Window
{
    /// <summary>Share of CountsGrid's own LIVE ActualWidth that Note may grow
    /// to before ellipsizing — tracked continuously, same mechanism
    /// BulkRenameWindow/MatchMergeWindow use for their own Note columns; see
    /// DataGridColumnCap's class doc. Only one capped column here (File is
    /// the star filler, Pages is a short uncapped number — see this window's
    /// XAML comments), so this can run higher than BulkRename/MatchMerge's
    /// 0.35-per-column share without risking the same combined-overflow this
    /// class's own "HONESTY CHECK" paragraph warns about: Note is the ONLY
    /// column competing for the viewport share this constant governs.
    /// Note carries PageCounts.Count's own failure message (PageCountsRow's
    /// doc comment: "the count/read that failed's own error"), which was
    /// growing this column unbounded before this fix — a long exception
    /// message produced the exact horizontal scrollbar every other grid's
    /// Auto content columns are capped to prevent.</summary>
    private const double ContentColumnShare = 0.45;

    private readonly PageCountsViewModel _vm;

    public PageCountsWindow(PageCountsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(CountsGrid, ContentColumnShare, NoteColumn);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Multiselect = true };
        // fire and forget: the expand-and-count work is off-thread and the
        // grid updates as it lands, so the dialog closes immediately instead
        // of hanging on a slow share
        if (dlg.ShowDialog(this) == true) _ = _vm.AddFilesAsync(dlg.FileNames);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) _ = _vm.AddFilesAsync(new[] { dlg.FolderName });
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) =>
        _vm.RemoveSelected(CountsGrid.SelectedItems);

    // CLIPBOARD RULE: System.Windows.Clipboard appears ONLY here, never in
    // the view model — Clipboard is a WPF/COM type the headless MTA tests
    // can't safely touch.
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = _vm.OutputText;
        if (text.Length == 0) return;   // nothing listed yet — Clipboard.SetText throws on ""
        try
        {
            Clipboard.SetText(text);
            _vm.NoteCopied();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another app is holding the clipboard right now — say so instead
            // of losing the failure silently
            _vm.NoteClipboardBusy();
        }
    }

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

    /// <summary>A closed window must not keep counting PDFs invisibly: the
    /// work is async and owned by the view model rather than the window.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        base.OnClosed(e);
    }
}
