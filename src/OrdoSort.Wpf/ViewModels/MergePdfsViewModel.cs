using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>The Merge PDFs window: drop PDFs and zips, and one document
/// comes out per source — every PDF inside a zip into &lt;zipname&gt;.pdf
/// beside it, and every loose PDF in the list into one file beside the first
/// of them. Its own window and its own list because it is a different job
/// wearing a zip costume — it consumes archives and documents and produces
/// a document — and because a separate list means extracting an archive in
/// the other window has no bearing on merging it here.
///
/// Units (see the base class): each runnable zip row is a unit of one; the
/// runnable loose PDFs are one unit of many, run last. Fail-whole applies
/// per unit: one PDF nobody can open merges nothing from its unit, and the
/// rows it held back say so (<see cref="ApplyToUnit"/>).</summary>
public sealed class MergePdfsViewModel : ZipListViewModel
{
    private readonly Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> _zipMerger;
    private readonly Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult> _fileMerger;
    private readonly Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult> _zipProbe;
    private readonly Func<string, IReadOnlyList<string>, Unlock.ProbeResult> _pdfProbe;

    /// <summary>Extension set in Intake's shape (dot-less, lowercase).</summary>
    private static readonly ISet<string> PdfsAndZips = new HashSet<string> { "pdf", "zip" };

    public MergePdfsViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null,
        Func<string, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? zipMerger = null,
        Func<IReadOnlyList<string>, string?, IReadOnlyList<string>, Func<PasswordRequest, string?>?, PdfMerge.MergeResult>? fileMerger = null,
        Func<string, IReadOnlyList<string>, Zipper.ZipProbeResult>? zipProbe = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? pdfProbe = null)
        : base(dialogs, savedPasswords, scheduler, uiContext)
    {
        // A bare method group here would need the C# compiler to pick
        // PdfMerge.MergeZip/MergeFiles's overload by eliding their two new
        // trailing optional parameters (converter, includeTypes) — which
        // works for a plain assignment but not as an operand of ??, so the
        // lambda spells out the exact three/four-argument shape this field
        // actually needs. Behavior is unchanged: converter and includeTypes
        // still default to null (every type, no conversion) until this
        // view model itself is wired to pass them through.
        _zipMerger = zipMerger ?? ((path, mergeCandidates, ask) => PdfMerge.MergeZip(path, mergeCandidates, ask));
        _fileMerger = fileMerger ?? ((paths, outputPath, mergeCandidates, ask) => PdfMerge.MergeFiles(paths, outputPath, mergeCandidates, ask));
        _zipProbe = zipProbe ?? Zipper.Probe;
        _pdfProbe = pdfProbe ?? Unlock.ProbeReadiness;

        MergeCommand = new AsyncRelayCommand(() => MergeAsync(null), () => RunnableRows > 0);
        MergeToCommand = new AsyncRelayCommand(MergeToAsync, () => RunnableLoosePdfs > 0);
    }

    /// <summary>PDFs and archives; anything else is refused by intake with
    /// its usual note — "that isn't a PDF or zip" is the honest answer on a
    /// window that can only merge.</summary>
    protected override ISet<string>? Extensions => PdfsAndZips;

    protected override string IntakeNoun => "PDF or zip";

    private int RunnableRows => Rows.Count(r => r.IsRunnable);
    private int RunnableLoosePdfs => Rows.Count(r => r.IsPdf && r.IsRunnable);

    /// <summary>Zips at archive level, loose PDFs through Unlock's own
    /// probe. PDFs INSIDE a zip are not probed here — that would read every
    /// archive fully twice over a share — and are asked for during the run.</summary>
    protected override (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords) =>
        row.IsZip ? FromZipProbe(_zipProbe(row.Path, savedPasswords))
        : row.IsPdf ? FromPdfProbe(_pdfProbe(row.Path, savedPasswords))
        : null;

    protected override void OnRowsChanged()
    {
        Raise(nameof(MergeButtonText));
        MergeCommand.RaiseCanExecuteChanged();
        MergeToCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand MergeCommand { get; }
    public AsyncRelayCommand MergeToCommand { get; }

    /// <summary>Counts every runnable row — a zip or a loose PDF alike —
    /// matching MergeCommand's own CanExecute.</summary>
    public string MergeButtonText => RunnableRows switch
    {
        0 => "Merge",
        1 => "Merge 1 item",
        var n => $"Merge {n} items",
    };

    /// <summary>Merge to…: a Save-As for the loose-PDF output only — zips
    /// already have a natural name and place. Suggests PdfMerge.DefaultName's
    /// own pick; a cancelled dialog is a silent no-op.</summary>
    internal async Task MergeToAsync()
    {
        var loose = Rows.Where(r => r.IsPdf && r.IsRunnable).Select(r => r.Path).ToList();
        if (loose.Count == 0) return;
        var path = Dialogs.AskSaveFile("PDF (*.pdf)|*.pdf", PdfMerge.DefaultName(loose));
        if (path is null) return;
        await MergeAsync(path);
    }

    /// <summary>Zips first, one unit each, then the loose group as one unit
    /// — runnable rows only. <paramref name="outputPath"/> reaches the loose
    /// group alone. The candidates and the prompt are the base class's; a
    /// merger asks only for what none of the candidates opens.</summary>
    internal Task MergeAsync(string? outputPath)
    {
        var units = new List<Unit<PdfMerge.MergeResult>>();
        foreach (var row in Rows.Where(r => r.IsZip && r.IsRunnable))
        {
            var zipRow = row;
            units.Add(new Unit<PdfMerge.MergeResult>(new[] { zipRow },
                candidates => _zipMerger(zipRow.Path, candidates, AskPassword)));
        }
        var loose = Rows.Where(r => r.IsPdf && r.IsRunnable).ToList();
        if (loose.Count > 0)
        {
            var paths = loose.Select(r => r.Path).ToList();
            units.Add(new Unit<PdfMerge.MergeResult>(loose,
                candidates => _fileMerger(paths, outputPath, candidates, AskPassword)));
        }
        return RunBatchAsync(units, r => r.Status, ApplyToUnit, "Merging",
            new[]
            {
                new TallyClause("ok", "merged"),
                new TallyClause("no_pdfs", "had no PDFs"),
                new TallyClause("needs_password", "needs a password", "need a password"),
                new TallyClause("error", "failed"),
            });
    }

    /// <summary>One result, every row of the unit. A unit of one, or any
    /// success, is the row's own Apply. A failed group is fail-whole: the
    /// culprit (MergeResult.Item, a full path) takes the result — a runnable
    /// NeedsPassword, or an Error — and every other row stays Pending with a
    /// note naming what held it back, so the next run picks them up again
    /// once the culprit is opened or removed. A group failure with no
    /// culprit (the save itself failed) leaves every row Pending with the
    /// message.</summary>
    private static void ApplyToUnit(IReadOnlyList<ZipItemRow> rows, PdfMerge.MergeResult result)
    {
        if (rows.Count == 1 || result.Status == "ok")
        {
            foreach (var row in rows) row.Apply(result);
            return;
        }
        if (result.Item is null)
        {
            foreach (var row in rows) row.Mark(ZipItemRowStatus.Pending, result.Message);
            return;
        }
        var culpritName = System.IO.Path.GetFileName(result.Item);
        var reason = result.Status == "needs_password" ? "needs a password" : "couldn't be read";
        foreach (var row in rows)
        {
            if (string.Equals(row.Path, result.Item, StringComparison.OrdinalIgnoreCase)) row.Apply(result);
            else row.Mark(ZipItemRowStatus.Pending, $"not merged — {culpritName} {reason}");
        }
    }
}
