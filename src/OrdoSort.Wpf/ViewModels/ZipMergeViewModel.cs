using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a zip's merge attempt ended in, mirrored 1:1 onto
/// ZipMerge.MergeResult.Status ("ok"/"no_pdfs"/"error") plus Pending for
/// "hasn't been merged yet" — a state Core has no need for since it never
/// sits idle mid-answer the way a row in this list does (same shape as
/// UnlockViewModel's ReadinessStatus next to Unlock.ProbeResult.Status).</summary>
public enum ZipRowStatus { Pending, Ok, NoPdfs, Error }

/// <summary>One row: a dropped/added zip plus whatever its merge attempt
/// found out, once it has run. Modeled on PageCountRow (see that class's doc
/// comment for the FileName/Path split reasoning) but with an extra
/// dimension PageCounts never needed — ok vs. no_pdfs vs. error, not just
/// "counted" vs. "couldn't" — because a zip with zero PDFs inside isn't a
/// failure the way an unreadable PDF is.</summary>
public sealed class ZipRow : ObservableObject
{
    public string Path { get; }

    // System.IO.Path is qualified because this type's own Path property
    // would otherwise shadow it — same reasoning as PageCountRow.FileName.
    public string FileName => System.IO.Path.GetFileName(Path);

    public ZipRow(string path) => Path = path;

    private ZipRowStatus _statusKind = ZipRowStatus.Pending;
    public ZipRowStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>"" while Pending; the merge's own error/no-pdfs message on
    /// NoPdfs/Error; a short "→ merged.pdf (N PDFs)" result line on Ok.</summary>
    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private string? _output;
    public string? Output { get => _output; private set => Set(ref _output, value); }

    internal void Apply(ZipMerge.MergeResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipRowStatus.Ok,
            "no_pdfs" => ZipRowStatus.NoPdfs,
            _ => ZipRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.Output;
        Note = StatusKind == ZipRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.Output!)} ({result.PdfCount} PDF{(result.PdfCount == 1 ? "" : "s")})"
            : result.Message;
    }
}

/// <summary>Merge PDFs from zip: drop one or more .zip files, and for each
/// one every PDF inside gets merged (natural-sorted by entry path) into a
/// single &lt;zipname&gt;.pdf saved next to the zip. Per-zip status rows, and
/// one bad zip never stops the batch — ZipMerge.MergeZip itself never throws,
/// and each result is applied to its own row independently as it lands, the
/// same discipline PageCountsViewModel and UnlockViewModel already use for
/// their own batches.</summary>
public sealed class ZipMergeViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<string, ZipMerge.MergeResult> _merger;

    // Cancelled once, from the window's OnClosed — same shape and same
    // reasoning as PageCountsViewModel's own _cts: this tool has no
    // "re-probe everything" case the way UnlockViewModel's saved-password
    // changes do, so there is no need to ever replace this token with a
    // fresh one.
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ZipRow> Rows { get; } = new();

    public ZipMergeViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, Func<string, ZipMerge.MergeResult>? merger = null)
    {
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _uiContext = uiContext;
        _merger = merger ?? ZipMerge.MergeZip;

        MergeCommand = new AsyncRelayCommand(MergeAsync, () => Rows.Count > 0);
        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Summary = "";
            AddNote = "";
            Raise(nameof(MergeButtonText));
            MergeCommand.RaiseCanExecuteChanged();
        });

        Rows.CollectionChanged += (_, _) =>
        {
            Raise(nameof(MergeButtonText));
            MergeCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>Feedback for the last AddFilesAsync call ("2 added · 1
    /// ignored…"); blank when it added something with nothing to complain
    /// about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Live progress while a run is under way ("Merging 2 of 5…"),
    /// then the verdict line: "3 merged · 1 had no PDFs · 1 failed" — each
    /// clause omitted entirely when its count is zero.</summary>
    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>"Merge 3 zips" / "Merge 1 zip" / "Merge" for an empty list —
    /// bound to the Merge button's content, same pattern as BulkRename's own
    /// RenameButtonText. Reflects the TOTAL row count (matching MergeCommand's
    /// own CanExecute, which is also "Rows non-empty", not "some row is still
    /// Pending") rather than just the rows a click would actually act on —
    /// re-clicking Merge after everything already ran is harmless (MergeAsync
    /// finds nothing Pending and returns immediately), so the button staying
    /// enabled and worded off the full list is simpler than tracking a second
    /// count that would only matter in that one edge case.</summary>
    public string MergeButtonText => Rows.Count switch
    {
        0 => "Merge",
        1 => "Merge 1 zip",
        var n => $"Merge {n} zips",
    };

    public AsyncRelayCommand MergeCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>Called by drag-drop and Add zips…. Files only — no folder
    /// expansion, unlike PageCountsViewModel/FilenameListViewModel's
    /// Intake.Expand-based intake — because a zip is a leaf the user drops
    /// directly, not something to search a folder tree for. Existence checks
    /// run off-thread the same way UnlockViewModel.AddFilesAsync's do, so a
    /// big drop from a slow share doesn't stall the UI thread one File.Exists
    /// at a time.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = new HashSet<string>(Rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

        var (keep, ignored) = await _scheduler.Run(() =>
        {
            var keepList = new List<string>();
            var ignoredCount = 0;
            var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
            foreach (var p in candidates)
            {
                if (p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && seen.Add(p) && File.Exists(p))
                    keepList.Add(p);
                else
                    ignoredCount++;
            }
            return (keepList, ignoredCount);
        });

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await — the same second-drop race UnlockViewModel.AddFilesAsync
        // guards against.
        var live = new HashSet<string>(Rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var p in keep)
            if (live.Add(p))
            {
                Rows.Add(new ZipRow(p));
                added++;
            }
        ignored += keep.Count - added;

        AddNote = added == 0 && ignored > 0
            ? $"nothing added — {ignored} item{(ignored == 1 ? " isn't a zip" : "s aren't zips")} (or already listed)"
            : ignored > 0
                ? $"{added} added · {ignored} ignored (not zips, or already listed)"
                : "";
    }

    /// <summary>Removes exactly the rows the window's grid selection holds —
    /// same shape as PageCountsViewModel.RemoveSelected.</summary>
    public void RemoveSelected(IList rows)
    {
        foreach (var item in rows.Cast<ZipRow>().ToList())
            Rows.Remove(item);
    }

    /// <summary>Runs every still-Pending row's merge, one zip at a time —
    /// never in parallel. Two reasons, both about the SAME resource: each
    /// merge buffers every PDF inside its zip in memory simultaneously (see
    /// ZipMerge's own doc comment on why), so running several zips at once
    /// would stack their peak memory on top of each other with no bound the
    /// way PageCountsViewModel's _countGate or UnlockViewModel's
    /// MaxConcurrentUnlocks impose on THEIR lighter-weight work; and a zip
    /// full of large PDFs is already the expensive case ZipMerge exists to
    /// handle reasonably, not one to make worse by contending with three
    /// siblings for the same disk and memory. Only rows still Pending run —
    /// a row that already finished (Ok/NoPdfs/Error) is left exactly as it
    /// is, and re-adding the same zip (which starts a fresh Pending row) is
    /// how a failed one gets retried.</summary>
    internal async Task MergeAsync()
    {
        var pending = Rows.Where(r => r.StatusKind == ZipRowStatus.Pending).ToList();
        if (pending.Count == 0) return;   // nothing new — re-add to retry

        var token = _cts.Token;
        int ok = 0, noPdfs = 0, error = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            // Checked BETWEEN zips, not mid-zip: _merger is synchronous and
            // runs to completion once started, same as
            // PageCountsViewModel.CountOneAsync's own token check — this
            // only stops a zip that hasn't started yet, and everything from
            // here on stays Pending for a later re-run.
            if (token.IsCancellationRequested) break;

            var row = pending[i];
            Summary = $"Merging {i + 1} of {pending.Count}…";
            var result = await _scheduler.Run(() => _merger(row.Path));

            // Tallied from the result's own Status, not from the row AFTER
            // ApplyResult — ApplyResult may marshal onto _uiContext and land
            // slightly later than this line, so reading row.StatusKind here
            // would race it. result.Status is available immediately.
            switch (result.Status)
            {
                case "ok": ok++; break;
                case "no_pdfs": noPdfs++; break;
                default: error++; break;
            }
            ApplyResult(row, result);
        }

        var parts = new List<string>();
        if (ok > 0) parts.Add($"{ok} merged");
        if (noPdfs > 0) parts.Add($"{noPdfs} had no PDFs");
        if (error > 0) parts.Add($"{error} failed");
        Summary = string.Join(" · ", parts);
    }

    /// <summary>Marshals onto _uiContext when one is set, same shape as
    /// PageCountsViewModel.ApplyResult — a raw thread-pool continuation has
    /// no synchronization context of its own to inherit.</summary>
    private void ApplyResult(ZipRow row, ZipMerge.MergeResult result)
    {
        if (_uiContext is null) row.Apply(result);
        else _uiContext.Post(_ => row.Apply(result), null);
    }

    /// <summary>Stops any not-yet-started merge from starting; a merge
    /// already under way finishes (see MergeAsync's own comment) — same
    /// reasoning as PageCountsViewModel.Cancel. Called from the window's
    /// OnClosed: a closed window must not keep merging zips invisibly.</summary>
    public void Cancel() => _cts.Cancel();
}
