using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a row's operation ended in. NoPdfs is reachable only from a
/// merge. NeedsPassword is reachable from any operation that met a lock
/// nobody could open — and, unlike the other three finished states, it is
/// RUNNABLE again: the next run asks again, so a skipped prompt never needs
/// a remove-and-re-add (see <see cref="ZipItemRow.IsRunnable"/>).</summary>
public enum ZipItemRowStatus { Pending, Ok, NoPdfs, Error, NeedsPassword }

/// <summary>One listed source: a loose file, a PDF, a whole folder, or an
/// archive. Kind is a plain string tag rather than an enum for the same
/// reason PathRow's was — nothing switches on it but its own grid column,
/// <see cref="IsZip"/> and <see cref="IsPdf"/>.</summary>
public sealed class ZipItemRow : ObservableObject
{
    public string Path { get; }
    public string Kind { get; }

    /// <summary>Drives which actions a window can offer for this row.</summary>
    public bool IsZip => Kind == "zip";
    public bool IsPdf => Kind == "pdf";

    /// <summary>The file name for a file or archive row; the folder's OWN
    /// name for a folder row — DirectoryInfo.Name handles a trailing
    /// separator correctly where a bare Path.GetFileName returns "".</summary>
    public string Display => Kind == "folder"
        ? new DirectoryInfo(Path).Name
        : System.IO.Path.GetFileName(Path);

    public ZipItemRow(string path, string kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Classifies a path the one way both windows agree on. Checked
    /// in this order deliberately: a directory named "x.zip" is a folder.</summary>
    public static string KindOf(string path)
    {
        if (Directory.Exists(path)) return "folder";
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return "zip";
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        return "file";
    }

    private ZipItemRowStatus _statusKind = ZipItemRowStatus.Pending;
    public ZipItemRowStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>A row the next run will pick up. Pending has never run;
    /// NeedsPassword ran and was skipped at the prompt — and a password is
    /// something that can be known now that wasn't then.</summary>
    public bool IsRunnable => StatusKind is ZipItemRowStatus.Pending or ZipItemRowStatus.NeedsPassword;

    /// <summary>"" while Pending with nothing to say; a probe's readiness
    /// note while still Pending; the operation's own message on a failure; a
    /// short result line on success.</summary>
    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private string? _output;
    public string? Output { get => _output; private set => Set(ref _output, value); }

    internal void Apply(Zipper.UnzipResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipItemRowStatus.Ok,
            "needs_password" => ZipItemRowStatus.NeedsPassword,
            _ => ZipItemRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.OutputFolder;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.OutputFolder!)}"
            : result.Message;
    }

    internal void Apply(PdfMerge.MergeResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipItemRowStatus.Ok,
            "no_pdfs" => ZipItemRowStatus.NoPdfs,
            "needs_password" => ZipItemRowStatus.NeedsPassword,
            _ => ZipItemRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.Output;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.Output!)} ({result.PdfCount} PDF{(result.PdfCount == 1 ? "" : "s")})"
            : result.Message;
    }

    /// <summary>A verdict that is not an operation's result: a probe's
    /// readiness note on a row still Pending, or "not merged — x needs a
    /// password" on the rows a culprit held back. Status and note only;
    /// Output is left exactly as it is.</summary>
    internal void Mark(ZipItemRowStatus status, string note)
    {
        StatusKind = status;
        Note = note;
    }
}

/// <summary>Everything the two zip-tool windows share: the list, intake and
/// its dedupe, selection removal, Clear, the add note, the status line, the
/// passwords, the probe on add, and the sequential cancellable batch runner.
/// Each window owns its OWN instance, so nothing here is shared state
/// between them.
///
/// The runner works in UNITS: one Core call and the rows it answers for. A
/// zip row is a unit of one; the loose PDFs in the Merge window are one
/// unit of many, because they become one document. Sequential rather than
/// parallel, and cancelled BETWEEN units rather than mid-unit: each
/// operation writes a folder or a document, so running several at once buys
/// contention rather than speed, and a half-written output is worse than a
/// late one.
///
/// Passwords: Core tries the candidates this class hands it — what was
/// typed in this window, most recent first, then the Unlock tool's saved
/// list — and asks through <see cref="AskPassword"/> for anything none of
/// them opens. The prompt crosses to the UI thread with
/// SynchronizationContext.Send: the worker WAITS on the person, which is
/// what "the operation pauses" means. Nothing typed here is ever saved.
///
/// The probe on add: each new row is checked off-thread (four at a time,
/// Unlock's own figure — a probe is a real read, often over a share) against
/// the SAVED passwords only, so "a saved password opens this" is exactly
/// true, and its verdict lands in the Result column while the row is still
/// pending. A probe token replaced on Clear and cancelled on close keeps a
/// verdict from landing on a row nobody can see.</summary>
public abstract class ZipListViewModel : ObservableObject
{
    protected readonly IWorkScheduler Scheduler;
    protected readonly SynchronizationContext? UiContext;
    protected readonly IDialogService Dialogs;

    private readonly IReadOnlyList<string> _savedPasswords;

    /// <summary>What was typed at the prompt in this window, most recent
    /// first, kept for the window's lifetime — a second run never re-asks
    /// for a password the first one learned. Touched only on the UI thread,
    /// inside <see cref="AskPassword"/>'s Send callback, and read only on the
    /// UI thread, in <see cref="Candidates"/> just before each unit is
    /// scheduled — so the worker never sees the live list.</summary>
    private readonly List<string> _typedPasswords = new();

    /// <summary>How many probes run at once — the same figure and the same
    /// reasoning as UnlockViewModel.MaxConcurrentUnlocks: a probe is a real
    /// read, often over a slow share, and four overlaps most of that waiting
    /// without turning the share itself into the bottleneck.</summary>
    internal const int MaxConcurrentProbes = 4;
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentProbes);

    // Replaced (not merely cancelled) on Clear and cancelled for good on
    // close — the same shape UnlockViewModel._probeCts has, for the same
    // reason: a probe must never write to a row nobody can see anymore, and
    // the NEXT add still needs a token that isn't born cancelled.
    private CancellationTokenSource _probeCts = new();

    // Cancelled for good from the window's OnClosed — a closed window must
    // not keep working invisibly — and cancelled-then-REPLACED by every
    // Clear (QC-05): a list just wiped by the user must not go on being
    // written to by whatever batch was running, but the NEXT batch still
    // needs a token that isn't born cancelled. No longer readonly for
    // exactly that swap; see ClearCommand below.
    private CancellationTokenSource _cts = new();

    public ObservableCollection<ZipItemRow> Rows { get; } = new();

    protected ZipListViewModel(IDialogService dialogs, IReadOnlyList<string> savedPasswords,
        IWorkScheduler? scheduler, SynchronizationContext? uiContext)
    {
        Dialogs = dialogs;
        _savedPasswords = savedPasswords;
        Scheduler = scheduler ?? new TaskWorkScheduler();
        UiContext = uiContext;

        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Status = "";
            AddNote = "";
            OnRowsChanged();
            // A batch running when Clear is pressed must stop instead of
            // going on to apply results to rows nobody can see anymore
            // (QC-05) — cancel the RUN token, then hand out a FRESH one so
            // the next Extract/Merge isn't born cancelled, the same swap
            // Unlock's own ClearCommand already does for its probe token.
            // RunBatchAsync's tail checks for exactly this replacement so it
            // doesn't overwrite the "" just set above with a stale partial
            // count. The probe token gets the identical swap, for the
            // identical reason.
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            oldCts.Cancel();
            oldCts.Dispose();

            var oldProbeCts = _probeCts;
            _probeCts = new CancellationTokenSource();
            oldProbeCts.Cancel();
            oldProbeCts.Dispose();
        });

        Rows.CollectionChanged += (_, _) => OnRowsChanged();
    }

    /// <summary>Which extensions this window accepts, in Intake's shape
    /// (dot-less, lowercase); null means anything that exists, files and
    /// folders alike.</summary>
    protected abstract ISet<string>? Extensions { get; }

    /// <summary>The noun Intake's own note builder uses — "item" where a
    /// window takes anything, "PDF or zip" where it takes those only.</summary>
    protected abstract string IntakeNoun { get; }

    /// <summary>The readiness check for one newly added row, run off the UI
    /// thread against the SAVED passwords: what the row should show while
    /// it is still pending, or null to leave it alone (a loose file in the
    /// Zip window needs nothing). Each window decides which rows it probes
    /// and with what; <see cref="FromZipProbe"/> and <see cref="FromPdfProbe"/>
    /// are the two mappings they share.</summary>
    protected abstract (ZipItemRowStatus Status, string Note)? Probe(ZipItemRow row, IReadOnlyList<string> savedPasswords);

    /// <summary>Raised whenever the list changes so a subclass can refresh
    /// its own button texts and command enablement.</summary>
    protected virtual void OnRowsChanged() { }

    public RelayCommand ClearCommand { get; }

    private bool _isBusy;

    /// <summary>True while RunBatchAsync (Extract or Merge, whichever
    /// subclass called it) is running. Gates Remove selected — see IsIdle —
    /// the third place this exact defect (QC-05) turned up: a row removed
    /// mid-batch would still be worked on by a loop that had already
    /// snapshotted it, then leave nothing visible to explain the result.
    /// Deliberately does NOT gate Clear: unlike Remove selected, Clear has
    /// to stay reachable during a run, since pressing it is what actually
    /// stops one (see ClearCommand above).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
    }

    /// <summary>The inverse of IsBusy — Remove selected is a Click handler
    /// with no CanExecute of its own to disable it, same shape as
    /// BulkRenameViewModel.IsIdle.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Feedback for the last AddPaths call ("2 added · 1 ignored…");
    /// blank when it added something with nothing to complain about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Live progress during a batch, then its verdict; or a single
    /// verdict for a one-shot operation. One line per window.</summary>
    private string _status = "";
    public string Status { get => _status; protected set => Set(ref _status, value); }

    /// <summary>Called by drag-drop and the Add buttons. Existence checks run
    /// off-thread: a big drop from a slow share must not stall the UI thread
    /// one File.Exists at a time. Awaits the probe of whatever it added, so a
    /// caller that awaits this sees the verdicts too; production callers
    /// fire and forget it either way.</summary>
    public async Task AddPaths(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = Rows.Select(r => r.Path).ToList();
        var extensions = Extensions;

        var (offThread, kinds) = await Scheduler.Run(() =>
        {
            var taken = extensions is null
                ? Intake.Add(already, candidates, exists: p => File.Exists(p) || Directory.Exists(p))
                : Intake.Add(already, candidates, extensions, File.Exists);
            var kind = taken.Files.ToDictionary(
                p => p, ZipItemRow.KindOf, StringComparer.OrdinalIgnoreCase);
            return (taken, kind);
        });

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await — otherwise a second drop landing mid-await duplicates rows.
        var settled = Intake.Add(Rows.Select(r => r.Path), offThread.Files);
        var added = new List<ZipItemRow>();
        foreach (var p in settled.Files)
        {
            var row = new ZipItemRow(p, kinds[p]);
            Rows.Add(row);
            added.Add(row);
        }

        AddNote = (offThread with
        {
            Files = settled.Files,
            AlreadyListed = offThread.AlreadyListed + settled.AlreadyListed,
        }).Note(IntakeNoun);

        await ProbeRowsAsync(added, _probeCts.Token);
    }

    /// <summary>Removes exactly the rows the window's grid selection holds.
    /// The button is disabled mid-batch (IsIdle), but the guard lives here
    /// too: this is public, and dropping a row while RunBatchAsync's own
    /// snapshot still holds it would let the loop go on to apply a result
    /// to a row nobody can see anymore (QC-05).</summary>
    public void RemoveSelected(IList rows)
    {
        if (IsBusy) return;
        foreach (var item in rows.Cast<ZipItemRow>().ToList())
            Rows.Remove(item);
    }

    // ------------------------------------------------------------ passwords

    /// <summary>The order Core tries: typed in this window (most recent
    /// first), then saved. A fresh list every call, taken on the UI thread
    /// just before a unit is scheduled — the worker gets a snapshot, never
    /// the live list.</summary>
    protected IReadOnlyList<string> Candidates() => _typedPasswords.Concat(_savedPasswords).ToList();

    /// <summary>Core's <c>ask</c> callback, invoked on the worker thread from
    /// inside a running operation. Crosses to the UI thread with Send —
    /// synchronous, so the worker waits on the person and the operation
    /// genuinely pauses — shows the prompt, remembers a non-empty answer at
    /// the front of the typed list, and hands it back. Runs inline when
    /// there is no UiContext (every unit test, the E2E harness's inline
    /// scheduler). ShowDialog disables the owner, so Clear and Remove cannot
    /// fire while the prompt is up.</summary>
    protected string? AskPassword(PasswordRequest request)
    {
        string? answer = null;
        void Prompt()
        {
            answer = Dialogs.AskPassword(request);
            if (string.IsNullOrEmpty(answer)) return;
            _typedPasswords.Remove(answer);
            _typedPasswords.Insert(0, answer);
        }
        if (UiContext is null) Prompt();
        else UiContext.Send(_ => Prompt(), null);
        return answer;
    }

    // ---------------------------------------------------------------- probe

    /// <summary>The verdict a zip probe writes into a row still pending.
    /// The spec's table: not encrypted stays quiet; ready says which kind of
    /// password; needs_password is the runnable NeedsPassword state; an
    /// unreadable archive is an Error with the probe's own message.</summary>
    protected static (ZipItemRowStatus Status, string Note) FromZipProbe(Zipper.ZipProbeResult result) =>
        result.Status switch
        {
            "not_encrypted" => (ZipItemRowStatus.Pending, ""),
            "ready" => (ZipItemRowStatus.Pending, "a saved password opens this"),
            "needs_password" => (ZipItemRowStatus.NeedsPassword, "needs a password"),
            _ => (ZipItemRowStatus.Error, result.Message),
        };

    /// <summary>The same for a loose PDF, from Unlock's own probe. In use is
    /// a passing condition, not a verdict: the row stays pending with a note
    /// and the run reports whatever is true by then.</summary>
    protected static (ZipItemRowStatus Status, string Note) FromPdfProbe(Unlock.ProbeResult result) =>
        result.Status switch
        {
            "not_encrypted" => (ZipItemRowStatus.Pending, ""),
            "ready" => (ZipItemRowStatus.Pending, "a saved password opens this"),
            "needs_password" => (ZipItemRowStatus.NeedsPassword, "needs a password"),
            "in_use" => (ZipItemRowStatus.Pending, "open in another program"),
            _ => (ZipItemRowStatus.Error, result.Message),
        };

    private async Task ProbeRowsAsync(IReadOnlyList<ZipItemRow> rows, CancellationToken token)
    {
        if (rows.Count == 0) return;
        var saved = _savedPasswords;
        await Task.WhenAll(rows.Select(async row =>
        {
            await _probeGate.WaitAsync();
            try
            {
                if (token.IsCancellationRequested) return;
                var verdict = await Scheduler.Run(() => Probe(row, saved));
                if (verdict is null || token.IsCancellationRequested) return;
                var (status, note) = verdict.Value;
                RunOnUi(() =>
                {
                    // Only a row still waiting for its first word: a run that
                    // finished meanwhile has said something truer, and a row
                    // Clear removed is nobody's to write to.
                    if (!Rows.Contains(row) || row.StatusKind != ZipItemRowStatus.Pending) return;
                    row.Mark(status, note);
                    OnRowsChanged();
                });
            }
            finally
            {
                _probeGate.Release();
            }
        }));
    }

    // ---------------------------------------------------------------- batch

    /// <summary>One Core call and the rows it answers for. <see cref="Operation"/>
    /// receives the candidate passwords snapshotted on the UI thread just
    /// before it is scheduled.</summary>
    protected sealed record Unit<TResult>(IReadOnlyList<ZipItemRow> Rows, Func<IReadOnlyList<string>, TResult> Operation);

    /// <summary>One bucket of the verdict line. <see cref="Plural"/> when
    /// the label changes with the count ("1 needs a password" / "2 need a
    /// password"); null when it does not ("1 extracted" / "2 extracted").</summary>
    protected sealed record TallyClause(string Status, string Label, string? Plural = null);

    /// <summary>Runs one operation per unit, one unit at a time. Extract
    /// and Merge are this method with different units — the duplication the
    /// two batch tools used to carry a copy of each.
    ///
    /// The subclass selects the units, so only runnable rows (Pending or
    /// NeedsPassword) ever arrive here: a row that finished is left exactly
    /// as it is, and re-adding the source (a fresh Pending row) is how a
    /// failed one is retried.
    ///
    /// <paramref name="clauses"/> are matched against each result's own
    /// status string, in order; a status matching none of them counts toward
    /// the LAST clause, which is how "error" and anything unrecognized share
    /// a bucket.</summary>
    protected async Task RunBatchAsync<TResult>(
        IReadOnlyList<Unit<TResult>> units,
        Func<TResult, string> statusOf,
        Action<IReadOnlyList<ZipItemRow>, TResult> apply,
        string progressVerb,
        IReadOnlyList<TallyClause> clauses)
    {
        if (units.Count == 0) return;   // nothing runnable — re-add to retry

        var token = _cts.Token;
        var counts = new int[clauses.Count];

        IsBusy = true;
        try
        {
            for (var i = 0; i < units.Count; i++)
            {
                // Checked BETWEEN units, never mid-unit: a half-written output
                // is worse than a late one.
                if (token.IsCancellationRequested) break;

                var unit = units[i];
                Status = $"{progressVerb} {i + 1} of {units.Count}…";
                var candidates = Candidates();
                var result = await Scheduler.Run(() => unit.Operation(candidates));

                // Tallied from the result's OWN status rather than from the
                // rows after applying it: the apply may be marshalled onto the
                // UI thread and has not necessarily landed yet.
                var status = statusOf(result);
                var slot = -1;
                for (var c = 0; c < clauses.Count; c++)
                    if (clauses[c].Status == status) { slot = c; break; }
                counts[slot >= 0 ? slot : clauses.Count - 1]++;

                ApplyOnUi(unit.Rows, result, apply);
            }
        }
        finally
        {
            IsBusy = false;
        }

        // Clear replaces _cts with a FRESH source rather than merely
        // cancelling this one in place (see ClearCommand) — so if Clear ran
        // while this loop was still going, _cts.Token is no longer even the
        // SAME token this run captured above. That is what tells "cancelled
        // because Clear ran" — which already wrote its own "" and must not
        // have it overwritten with a partial count for rows nobody can see
        // anymore (QC-05) — apart from "cancelled because the window
        // closed" (Cancel() cancels this SAME token in place, no
        // replacement; OnClosed is its only caller, and nobody is around to
        // see the difference either way).
        if (token == _cts.Token)
        {
            var parts = new List<string>();
            for (var i = 0; i < clauses.Count; i++)
            {
                if (counts[i] == 0) continue;
                var label = counts[i] == 1 || clauses[i].Plural is null ? clauses[i].Label : clauses[i].Plural;
                parts.Add($"{counts[i]} {label}");
            }
            Status = string.Join(" · ", parts);
        }

        // Rows leaving Pending during the loop above change each row's OWN
        // StatusKind, not the Rows collection, so the CollectionChanged
        // subscription in the constructor never fires for it. Without this
        // call, a button whose count derives from row status (e.g.
        // ExtractButtonText's RunnableZips) goes stale the instant the batch
        // finishes: CanExecute correctly disables it, but the label still
        // names the pre-run count.
        //
        // Marshalled, NOT called directly: ApplyOnUi above POSTS the last
        // unit's apply rather than running it, so a direct call here reads
        // those rows while they are still Pending and announces a count that
        // is one too high — the same defect one step later. Posting queues
        // this behind every apply the loop issued, so it reads settled rows.
        RunOnUi(OnRowsChanged);
    }

    /// <summary>Marshals onto UiContext when one is set — a raw thread-pool
    /// continuation has no synchronization context of its own to inherit.</summary>
    protected void ApplyOnUi<TResult>(IReadOnlyList<ZipItemRow> rows, TResult result,
        Action<IReadOnlyList<ZipItemRow>, TResult> apply)
    {
        if (UiContext is null) apply(rows, result);
        else UiContext.Post(_ => apply(rows, result), null);
    }

    /// <summary>Marshals a context-free action onto UiContext, for the
    /// one-shot operations that write Status rather than a row.</summary>
    protected void RunOnUi(Action action)
    {
        if (UiContext is null) action();
        else UiContext.Post(_ => action(), null);
    }

    /// <summary>Stops any not-yet-started unit from starting (one already
    /// under way finishes) and any not-yet-started probe from landing.</summary>
    public void Cancel()
    {
        _cts.Cancel();
        _probeCts.Cancel();
    }
}
