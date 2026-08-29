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
    private readonly Func<string, PdfMerge.MergeResult> _merger;

    /// <summary>Extension set in Intake's shape (dot-less, lowercase).</summary>
    private static readonly ISet<string> Zips = new HashSet<string> { "zip" };

    public MergePdfsViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null,
        Func<string, PdfMerge.MergeResult>? merger = null)
        : base(dialogs, Array.Empty<string>(), scheduler, uiContext)
    {
        // No passwords yet — Task 7 threads the window's candidates and its
        // prompt through here; until then a locked PDF reports needs_password.
        _merger = merger ?? (path => PdfMerge.MergeZip(path, Array.Empty<string>(), null));

        MergeCommand = new AsyncRelayCommand(MergeAsync, () => Rows.Count > 0);
    }

    /// <summary>Archives only: this tab has nothing to offer anything else,
    /// so "that isn't a zip" is still the honest answer here.</summary>
    protected override ISet<string>? Extensions => Zips;

    protected override string IntakeNoun => "zip";

    // Task 7 gives this window its real probes; until then a row is left
    // exactly as it was added.
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords) => null;

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
        Rows.Where(r => r.IsZip && r.IsRunnable)
            .Select(row => new Unit<PdfMerge.MergeResult>(new[] { row }, _ => _merger(row.Path)))
            .ToList(),
        r => r.Status,
        (rows, r) => rows[0].Apply(r),
        "Merging",
        new[]
        {
            new TallyClause("ok", "merged"),
            new TallyClause("no_pdfs", "had no PDFs"),
            new TallyClause("error", "failed"),
        });
}
