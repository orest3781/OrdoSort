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

    /// <summary>One label per MergeTypes group, for this filter dropdown
    /// specifically — distinct from MergePdfsViewModel.GroupLabels (that one
    /// is checkbox text: "PDF", "PowerPoint"; a file-picker filter entry
    /// reads better as a plural noun phrase: "PDF files", "PowerPoint
    /// presentations"). Small and separate rather than shared, the same call
    /// GroupLabels itself already makes against titlecasing MergeTypes' own
    /// group constants.</summary>
    private static readonly IReadOnlyDictionary<string, string> FilterLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MergeTypes.Pdf] = "PDF files",
            [MergeTypes.Zip] = "Zip archives",
            [MergeTypes.Word] = "Word documents",
            [MergeTypes.Excel] = "Excel spreadsheets",
            [MergeTypes.PowerPoint] = "PowerPoint presentations",
            [MergeTypes.Images] = "Images",
            [MergeTypes.Text] = "Text files",
        };

    /// <summary>Built from MergeTypes.AllExtensions/AllGroups, not a second
    /// hard-coded list: the old hard-coded "*.pdf;*.zip" filter meant this
    /// dialog was the ONE way to reach this window that could never
    /// actually add any of the types Task 7 already widened intake to
    /// accept — only drag-and-drop could.
    ///
    /// Review Minor 3: the FIRST fix here (Task 8) dropped the old filter's
    /// own per-type narrowing ("PDF files only", "Zip archives only") down
    /// to just "Supported files" and "All files" — deriving from MergeTypes
    /// was right, but losing the narrowing was not required to get there.
    /// One entry per MergeTypes.AllGroups restores it, still entirely
    /// derived rather than a second hard-coded list. "All files" stays as
    /// the last choice: a pick outside the supported set is still refused
    /// by AddPaths' own intake filtering with its usual note (AddNote/
    /// IntakeNoun), so offering it costs nothing and saves a trip back to
    /// this dialog for someone who already knows what they meant to add.
    ///
    /// internal, not private: lets a test assert on the exact filter string
    /// without driving the real (unautomatable) Win32 file dialog.</summary>
    internal static string SupportedFilesFilter()
    {
        static string PatternsOf(IEnumerable<string> extensions) =>
            string.Join(";", extensions.Select(extension => $"*.{extension}"));

        var allPatterns = PatternsOf(MergeTypes.AllExtensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
        var entries = new List<string> { $"Supported files ({allPatterns})|{allPatterns}" };
        foreach (var group in MergeTypes.AllGroups)
        {
            var groupPatterns = PatternsOf(MergeTypes.ExtensionsOf(group));
            var label = FilterLabels.TryGetValue(group, out var text) ? text : group;
            entries.Add($"{label} ({groupPatterns})|{groupPatterns}");
        }
        entries.Add("All files (*.*)|*.*");
        return string.Join("|", entries);
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
    /// see MergePdfsViewModel.Dispose.
    ///
    /// Recorded, not fixed: Cancel() stops units BETWEEN, not within, so a
    /// unit already mid-conversion when the window closes keeps running on
    /// its own background thread after Dispose has already torn the
    /// converter down underneath it — its ToPdf call then throws
    /// ObjectDisposedException, which PdfMerge's own guard around every
    /// converter call (AsPdfBytes) catches and turns into an ordinary error
    /// result, applied to rows nobody is looking at anymore. Harmless (no
    /// crash, no leak — the unit still finishes and settles normally) but
    /// worth naming.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.Cancel();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
