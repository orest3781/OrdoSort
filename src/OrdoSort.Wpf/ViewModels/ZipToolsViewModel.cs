using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The window's DataContext: one view model per tab, each owning
/// its own list. Deliberately holds no state of its own — the tabs do not
/// coordinate, which is what keeps one tab's results from affecting the
/// other's.</summary>
public sealed class ZipToolsViewModel
{
    public ZipExtractViewModel ZipExtract { get; }
    public MergePdfsViewModel MergePdfs { get; }

    /// <summary><paramref name="scheduler"/> is the same off-thread seam both
    /// tabs already take individually, forwarded because the window only ever
    /// receives this shell: the end-to-end scenarios drive the real
    /// ZipToolsWindow, and their whole shape depends on substituting an
    /// inline scheduler so intake and batches complete before the next
    /// assertion reads them.</summary>
    public ZipToolsViewModel(IDialogService dialogs, SynchronizationContext? uiContext = null,
        IWorkScheduler? scheduler = null)
    {
        ZipExtract = new ZipExtractViewModel(dialogs, scheduler, uiContext);
        MergePdfs = new MergePdfsViewModel(dialogs, scheduler, uiContext);
    }

    public void Cancel()
    {
        ZipExtract.Cancel();
        MergePdfs.Cancel();
    }
}
