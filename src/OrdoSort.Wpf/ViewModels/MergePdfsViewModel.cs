using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Merge PDFs tab: drop archives, and every PDF inside each one
/// is merged (natural-sorted by entry path) into a single &lt;zipname&gt;.pdf
/// beside it. Its own tab and its own list because it is a different job
/// wearing a zip costume — it consumes archives and produces a document —
/// and because separate lists mean extracting an archive on the other tab
/// has no bearing on merging it here.</summary>
public sealed class MergePdfsViewModel : ZipListViewModel
{
    private readonly Func<string, ZipMerge.MergeResult> _merger;

    /// <summary>Extension set in Intake's shape (dot-less, lowercase).</summary>
    private static readonly ISet<string> Zips = new HashSet<string> { "zip" };

    public MergePdfsViewModel(IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null,
        Func<string, ZipMerge.MergeResult>? merger = null)
        : base(scheduler, uiContext)
    {
        _merger = merger ?? ZipMerge.MergeZip;

        MergeCommand = new AsyncRelayCommand(MergeAsync, () => Rows.Count > 0);
    }

    /// <summary>Archives only: this tab has nothing to offer anything else,
    /// so "that isn't a zip" is still the honest answer here.</summary>
    protected override ISet<string>? Extensions => Zips;

    protected override string IntakeNoun => "zip";

    protected override void OnRowsChanged()
    {
        Raise(nameof(MergeButtonText));
        MergeCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand MergeCommand { get; }

    /// <summary>Reflects the TOTAL row count, matching MergeCommand's own
    /// CanExecute: re-clicking after everything has run is harmless.</summary>
    public string MergeButtonText => Rows.Count switch
    {
        0 => "Merge",
        1 => "Merge 1 zip",
        var n => $"Merge {n} zips",
    };

    internal Task MergeAsync() => RunBatchAsync(
        _merger,
        r => r.Status,
        (row, r) => row.Apply(r),
        "Merging",
        new[] { ("ok", "merged"), ("no_pdfs", "had no PDFs"), ("error", "failed") });
}
