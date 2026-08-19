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

    public ZipToolsViewModel(IDialogService dialogs, SynchronizationContext? uiContext = null)
    {
        ZipExtract = new ZipExtractViewModel(dialogs, uiContext: uiContext);
        MergePdfs = new MergePdfsViewModel(dialogs, uiContext: uiContext);
    }

    public void Cancel()
    {
        ZipExtract.Cancel();
        MergePdfs.Cancel();
    }
}
