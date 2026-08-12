using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a zip's extract attempt ended in, mirrored 1:1 onto
/// Zipper.UnzipResult.Status ("ok"/"error") plus Pending for "hasn't run
/// yet" — same reasoning as ZipRowStatus next to ZipMerge.MergeResult.Status.</summary>
public enum UnzipRowStatus { Pending, Ok, Error }

/// <summary>One row: a dropped/added zip plus whatever its extract attempt
/// found out, once it has run. Modeled directly on ZipRow (see that class's
/// doc comment) but with only two outcomes instead of three — a zip either
/// extracts or it doesn't, there's no "no_pdfs"-shaped middle ground here.</summary>
public sealed class UnzipRow : ObservableObject
{
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    public UnzipRow(string path) => Path = path;

    private UnzipRowStatus _statusKind = UnzipRowStatus.Pending;
    public UnzipRowStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>"" while Pending; the extract's own error message on Error;
    /// "→ &lt;folder name&gt;" on Ok.</summary>
    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private string? _outputFolder;
    public string? OutputFolder { get => _outputFolder; private set => Set(ref _outputFolder, value); }

    internal void Apply(Zipper.UnzipResult result)
    {
        StatusKind = result.Status == "ok" ? UnzipRowStatus.Ok : UnzipRowStatus.Error;
        OutputFolder = result.OutputFolder;
        Note = StatusKind == UnzipRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.OutputFolder!)}"
            : result.Message;
    }
}

/// <summary>Unzip: drop one or more .zip files, and for each one its
/// contents get extracted into its own sibling folder. Per-zip status rows,
/// and one bad zip never stops the batch — Zipper.Extract itself never
/// throws, and each result is applied to its own row independently as it
/// lands. This class is ZipMergeViewModel with the merge step swapped for an
/// extract step — same batch-rows shape, same sequential-not-parallel
/// reasoning (each extract writes a folder full of files; running several at
/// once buys nothing but contention), same cancel-between-zips discipline.</summary>
public sealed class UnzipViewModel : ObservableObject
{
    private readonly IWorkScheduler _scheduler;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<string, Zipper.UnzipResult> _extractor;

    // Cancelled once, from the window's OnClosed — same shape and same
    // reasoning as ZipMergeViewModel's own _cts.
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Extension set in Intake's shape (dot-less, lowercase) rather
    /// than the EndsWith(".zip") this used to inline — same rule, one place.</summary>
    private static readonly ISet<string> Zips = new HashSet<string> { "zip" };

    public ObservableCollection<UnzipRow> Rows { get; } = new();

    public UnzipViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, Func<string, Zipper.UnzipResult>? extractor = null)
    {
        // dialogs is accepted for ctor-shape consistency with sibling batch
        // tools (PageCountsViewModel, ZipViewModel) and because a future
        // "extract to…" dialog is plausible — but nothing needs it yet, so
        // there is no field to keep it in.
        _ = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _uiContext = uiContext;
        _extractor = extractor ?? Zipper.Extract;

        ExtractCommand = new AsyncRelayCommand(ExtractAsync, () => Rows.Count > 0);
        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Summary = "";
            AddNote = "";
            Raise(nameof(ExtractButtonText));
            ExtractCommand.RaiseCanExecuteChanged();
        });

        Rows.CollectionChanged += (_, _) =>
        {
            Raise(nameof(ExtractButtonText));
            ExtractCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>Feedback for the last AddFilesAsync call ("2 added · 1
    /// ignored…"); blank when it added something with nothing to complain
    /// about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Live progress while a run is under way ("Extracting 2 of
    /// 5…"), then the verdict line: "3 extracted · 1 failed" — each clause
    /// omitted entirely when its count is zero.</summary>
    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>"Extract 3 zips" / "Extract 1 zip" / "Extract" for an empty
    /// list — bound to the Extract button's content, same pattern as
    /// ZipMergeViewModel.MergeButtonText. Reflects the TOTAL row count
    /// (matching ExtractCommand's own CanExecute) rather than just the rows
    /// a click would actually act on, for the same reason ZipMergeViewModel
    /// does: re-clicking after everything already ran is harmless.</summary>
    public string ExtractButtonText => Rows.Count switch
    {
        0 => "Extract",
        1 => "Extract 1 zip",
        var n => $"Extract {n} zips",
    };

    public AsyncRelayCommand ExtractCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>Called by drag-drop and Add zips…. Files only, .zip
    /// extension required — same shape as ZipMergeViewModel.AddFilesAsync,
    /// existence checks run off-thread for the same reason.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = Rows.Select(r => r.Path).ToList();

        var offThread = await _scheduler.Run(() => Intake.Add(already, candidates, Zips, File.Exists));

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await — the same second-drop race ZipMergeViewModel.AddFilesAsync
        // guards against.
        var settled = Intake.Add(Rows.Select(r => r.Path), offThread.Files);
        foreach (var p in settled.Files) Rows.Add(new UnzipRow(p));

        AddNote = (offThread with
        {
            Files = settled.Files,
            AlreadyListed = offThread.AlreadyListed + settled.AlreadyListed,
        }).Note("zip");
    }

    /// <summary>Removes exactly the rows the window's grid selection holds —
    /// same shape as ZipMergeViewModel.RemoveSelected.</summary>
    public void RemoveSelected(IList rows)
    {
        foreach (var item in rows.Cast<UnzipRow>().ToList())
            Rows.Remove(item);
    }

    /// <summary>Runs every still-Pending row's extract, one zip at a time —
    /// same sequential discipline and same cancel-between-zips checkpoint as
    /// ZipMergeViewModel.MergeAsync (see that method's own doc comment).
    /// Only rows still Pending run — a row that already finished (Ok/Error)
    /// is left exactly as it is, and re-adding the same zip (which starts a
    /// fresh Pending row) is how a failed one gets retried.</summary>
    internal async Task ExtractAsync()
    {
        var pending = Rows.Where(r => r.StatusKind == UnzipRowStatus.Pending).ToList();
        if (pending.Count == 0) return;   // nothing new — re-add to retry

        var token = _cts.Token;
        int ok = 0, error = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            // Checked BETWEEN zips, not mid-zip — same reasoning as
            // ZipMergeViewModel.MergeAsync's own token check.
            if (token.IsCancellationRequested) break;

            var row = pending[i];
            Summary = $"Extracting {i + 1} of {pending.Count}…";
            var result = await _scheduler.Run(() => _extractor(row.Path));

            // Tallied from the result's own Status, not from the row AFTER
            // ApplyResult, for the same race-avoidance reason
            // ZipMergeViewModel.MergeAsync's own comment explains.
            switch (result.Status)
            {
                case "ok": ok++; break;
                default: error++; break;
            }
            ApplyResult(row, result);
        }

        var parts = new List<string>();
        if (ok > 0) parts.Add($"{ok} extracted");
        if (error > 0) parts.Add($"{error} failed");
        Summary = string.Join(" · ", parts);
    }

    /// <summary>Marshals onto _uiContext when one is set, same shape as
    /// ZipMergeViewModel.ApplyResult — a raw thread-pool continuation has no
    /// synchronization context of its own to inherit.</summary>
    private void ApplyResult(UnzipRow row, Zipper.UnzipResult result)
    {
        if (_uiContext is null) row.Apply(result);
        else _uiContext.Post(_ => row.Apply(result), null);
    }

    /// <summary>Stops any not-yet-started extract from starting; one already
    /// under way finishes (see ExtractAsync's own comment) — same reasoning
    /// as ZipMergeViewModel.Cancel. Called from the window's OnClosed: a
    /// closed window must not keep extracting zips invisibly.</summary>
    public void Cancel() => _cts.Cancel();
}
