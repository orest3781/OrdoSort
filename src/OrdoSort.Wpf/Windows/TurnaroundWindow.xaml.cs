using System.Windows;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class TurnaroundWindow : Window
{
    /// <summary>Share of the space DocumentsGrid's columns can actually
    /// occupy (DataGridColumnCap.Track's own live, vertical-scrollbar-aware
    /// viewport — see its class doc) Category may grow to before
    /// ellipsizing. Only ONE capped column here (unlike MatchMerge/
    /// BulkRename's two, or History's three) — File name is this grid's own
    /// star filler (Width="*", MinWidth=180) and Doc date/Upload date/TAT
    /// (days) stay uncapped (short, bounded values — a formatted date, a
    /// small integer — the same reasoning History's own Undone column gives
    /// for staying uncapped). MatchMerge/BulkRename's own 0.35 share for
    /// their single primary capped text column was tried FIRST here and
    /// measured, directly, a real horizontal scrollbar at this window's own
    /// MinWidth (740) with 60 rows and a long Category value
    /// (AutoFitColumnTests.Turnaround_LongCategoryValueAtMinWidthNo
    /// HorizontalScrollbar) — Turnaround's own three extra uncapped columns
    /// (Doc date/Upload date/TAT) leave less room than MatchMerge/
    /// BulkRename's own two-more-capped-columns-and-nothing-else shape.
    /// Binary-searched clean at 0.34, failing at 0.35 — 0.25 leaves
    /// comfortable margin below that boundary rather than sitting right on
    /// it.</summary>
    private const double ContentColumnShare = 0.25;

    private readonly TurnaroundViewModel _vm;

    public TurnaroundWindow(TurnaroundViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        DataGridColumnCap.Track(DocumentsGrid, ContentColumnShare, CategoryColumn);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm.AddPaths(paths);
    }
}
