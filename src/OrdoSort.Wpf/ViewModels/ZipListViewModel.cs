using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a row's operation ended in. The union of the two enums the
/// zip tools carried one each (UnzipRowStatus, ZipRowStatus): NoPdfs is
/// reachable only from a merge, and stays Pending-shaped for every other
/// operation rather than being modelled per-tab.</summary>
public enum ZipItemRowStatus { Pending, Ok, NoPdfs, Error }

/// <summary>One listed source: a loose file, a whole folder, or an archive.
/// The union of PathRow (Kind/Display), UnzipRow and ZipRow (the status,
/// note and output a batch operation writes back). Kind is a plain string
/// tag rather than an enum for the same reason PathRow's was — nothing
/// switches on it but its own grid column and <see cref="IsZip"/>.</summary>
public sealed class ZipItemRow : ObservableObject
{
    public string Path { get; }
    public string Kind { get; }

    /// <summary>Drives which actions a tab can offer for this row.</summary>
    public bool IsZip => Kind == "zip";

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

    /// <summary>Classifies a path the one way both tabs agree on. Checked in
    /// this order deliberately: a directory named "x.zip" is a folder.</summary>
    public static string KindOf(string path) =>
        Directory.Exists(path) ? "folder"
        : System.IO.Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) ? "zip"
        : "file";

    private ZipItemRowStatus _statusKind = ZipItemRowStatus.Pending;
    public ZipItemRowStatus StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>"" while Pending; the operation's own message on a failure;
    /// a short result line on success.</summary>
    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private string? _output;
    public string? Output { get => _output; private set => Set(ref _output, value); }

    internal void Apply(Zipper.UnzipResult result)
    {
        StatusKind = result.Status == "ok" ? ZipItemRowStatus.Ok : ZipItemRowStatus.Error;
        Output = result.OutputFolder;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.OutputFolder!)}"
            : result.Message;
    }

    internal void Apply(ZipMerge.MergeResult result)
    {
        StatusKind = result.Status switch
        {
            "ok" => ZipItemRowStatus.Ok,
            "no_pdfs" => ZipItemRowStatus.NoPdfs,
            _ => ZipItemRowStatus.Error,   // "error", or anything unrecognized
        };
        Output = result.Output;
        Note = StatusKind == ZipItemRowStatus.Ok
            ? $"→ {System.IO.Path.GetFileName(result.Output!)} ({result.PdfCount} PDF{(result.PdfCount == 1 ? "" : "s")})"
            : result.Message;
    }
}

/// <summary>Everything the two zip-tool tabs share: the list, intake and its
/// dedupe, selection removal, Clear, the add note, the status line, and the
/// sequential cancellable batch runner. Each tab owns its OWN instance, so
/// nothing here is shared state between them — extracting on one tab has no
/// bearing on merging on the other, which is the whole reason the tabs have
/// separate lists.
///
/// Sequential rather than parallel, and cancelled BETWEEN items rather than
/// mid-item: each operation writes a folder or a document, so running
/// several at once buys contention rather than speed, and a half-written
/// output is worse than a late one. Both rules are inherited verbatim from
/// the two batch tools this replaces.</summary>
public abstract class ZipListViewModel : ObservableObject
{
    protected readonly IWorkScheduler Scheduler;
    protected readonly SynchronizationContext? UiContext;

    // Cancelled once, from the window's OnClosed: a closed window must not
    // keep working invisibly.
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ZipItemRow> Rows { get; } = new();

    protected ZipListViewModel(IWorkScheduler? scheduler, SynchronizationContext? uiContext)
    {
        Scheduler = scheduler ?? new TaskWorkScheduler();
        UiContext = uiContext;

        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Status = "";
            AddNote = "";
            OnRowsChanged();
        });

        Rows.CollectionChanged += (_, _) => OnRowsChanged();
    }

    /// <summary>Which extensions this tab accepts, in Intake's shape
    /// (dot-less, lowercase); null means anything that exists, files and
    /// folders alike.</summary>
    protected abstract ISet<string>? Extensions { get; }

    /// <summary>The noun Intake's own note builder uses — "item" where a tab
    /// takes anything, "zip" where it takes archives only.</summary>
    protected abstract string IntakeNoun { get; }

    /// <summary>Raised whenever the list changes so a subclass can refresh
    /// its own button texts and command enablement.</summary>
    protected virtual void OnRowsChanged() { }

    public RelayCommand ClearCommand { get; }

    /// <summary>Feedback for the last AddPaths call ("2 added · 1 ignored…");
    /// blank when it added something with nothing to complain about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Live progress during a batch, then its verdict; or a single
    /// verdict for a one-shot operation. One line per tab.</summary>
    private string _status = "";
    public string Status { get => _status; protected set => Set(ref _status, value); }

    /// <summary>Called by drag-drop and the Add buttons. Existence checks run
    /// off-thread: a big drop from a slow share must not stall the UI thread
    /// one File.Exists at a time.</summary>
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
        foreach (var p in settled.Files) Rows.Add(new ZipItemRow(p, kinds[p]));

        AddNote = (offThread with
        {
            Files = settled.Files,
            AlreadyListed = offThread.AlreadyListed + settled.AlreadyListed,
        }).Note(IntakeNoun);
    }

    /// <summary>Removes exactly the rows the window's grid selection holds.</summary>
    public void RemoveSelected(IList rows)
    {
        foreach (var item in rows.Cast<ZipItemRow>().ToList())
            Rows.Remove(item);
    }

    /// <summary>Runs one operation over every still-Pending ZIP row, one at a
    /// time. Extract and Merge are this method with a different operation —
    /// the duplication the two batch tools used to carry a copy of each.
    ///
    /// Only Pending rows run: a row that already finished is left exactly as
    /// it is, and re-adding the archive (a fresh Pending row) is how a failed
    /// one is retried.
    ///
    /// <paramref name="clauses"/> are matched against each result's own
    /// status string, in order; a status matching none of them counts toward
    /// the LAST clause, which is how "error" and anything unrecognized share
    /// a bucket.</summary>
    protected async Task RunBatchAsync<TResult>(
        Func<string, TResult> operation,
        Func<TResult, string> statusOf,
        Action<ZipItemRow, TResult> apply,
        string progressVerb,
        IReadOnlyList<(string Status, string Label)> clauses)
    {
        var pending = Rows.Where(r => r.IsZip && r.StatusKind == ZipItemRowStatus.Pending).ToList();
        if (pending.Count == 0) return;   // nothing new — re-add to retry

        var token = _cts.Token;
        var counts = new int[clauses.Count];

        for (var i = 0; i < pending.Count; i++)
        {
            // Checked BETWEEN items, never mid-item: a half-written output is
            // worse than a late one.
            if (token.IsCancellationRequested) break;

            var row = pending[i];
            Status = $"{progressVerb} {i + 1} of {pending.Count}…";
            var result = await Scheduler.Run(() => operation(row.Path));

            // Tallied from the result's OWN status rather than from the row
            // after applying it: the apply may be marshalled onto the UI
            // thread and has not necessarily landed yet.
            var status = statusOf(result);
            var slot = -1;
            for (var c = 0; c < clauses.Count; c++)
                if (clauses[c].Status == status) { slot = c; break; }
            counts[slot >= 0 ? slot : clauses.Count - 1]++;

            ApplyOnUi(row, result, apply);
        }

        var parts = new List<string>();
        for (var i = 0; i < clauses.Count; i++)
            if (counts[i] > 0) parts.Add($"{counts[i]} {clauses[i].Label}");
        Status = string.Join(" · ", parts);

        // Rows leaving Pending during the loop above change each row's OWN
        // StatusKind, not the Rows collection, so the CollectionChanged
        // subscription in the constructor never fires for it. Without this
        // call, a button whose count derives from row status (e.g.
        // ExtractButtonText's PendingZips) goes stale the instant the batch
        // finishes: CanExecute correctly disables it, but the label still
        // names the pre-run count. Matches the unmarshalled Status
        // assignment just above — both run wherever this method's own
        // continuation lands.
        OnRowsChanged();
    }

    /// <summary>Marshals onto UiContext when one is set — a raw thread-pool
    /// continuation has no synchronization context of its own to inherit.</summary>
    protected void ApplyOnUi<TResult>(ZipItemRow row, TResult result, Action<ZipItemRow, TResult> apply)
    {
        if (UiContext is null) apply(row, result);
        else UiContext.Post(_ => apply(row, result), null);
    }

    /// <summary>Marshals a context-free action onto UiContext, for the
    /// one-shot operations that write Status rather than a row.</summary>
    protected void RunOnUi(Action action)
    {
        if (UiContext is null) action();
        else UiContext.Post(_ => action(), null);
    }

    /// <summary>Stops any not-yet-started item from starting; one already
    /// under way finishes.</summary>
    public void Cancel() => _cts.Cancel();
}
