using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Drives the Result column's status colour — the same vocabulary
/// ZipToolsWindow/MergePdfsWindow's own Result column already uses
/// (StatusRed for a real failure, StatusAmber for "needs attention", nothing
/// special for the ordinary case). Renamed is the ordinary case: plain text,
/// like a successful row in every sibling tool's grid. Unchanged means
/// TidyStem's target already matched the source — a re-dropped, already-
/// standardised file — informational rather than a problem, so SubtleText,
/// matching BulkRename's own "(no change)" treatment. Skipped is
/// Naming.RejectIllegal turning a name away inside PlanTidy; see that
/// method's own doc comment for why a legally-named source can only reach
/// this through a bad date, which the date prompt already refuses before
/// this ever runs — defensive, not a state the owner's own files should
/// ever actually show. Failed is Execute's own per-file catch (locked, in
/// use, access denied) — a genuine failure, StatusRed.</summary>
public enum StandardiseRowStatus { Renamed, Unchanged, Skipped, Failed }

/// <summary>One line of the Standardise names tool's result log: what a
/// dropped file was called, and what happened to it. Unlike
/// BulkRenameViewModel's RenameRow, which reprojects a live PREVIEW from the
/// current file list on every keystroke, this tool has nothing to preview —
/// a row is written once, when its batch finishes, and stands as a record of
/// what this window actually did, which is also why it carries no settable
/// property: nothing ever edits a row in place; a new batch only ever adds
/// new ones (or Undo removes the ones its batch produced).</summary>
public sealed class StandardiseNameRow
{
    public StandardiseNameRow(string current, string result, StandardiseRowStatus status)
    {
        Current = current;
        Result = result;
        Status = status;
    }

    public string Current { get; }
    public string Result { get; }
    public StandardiseRowStatus Status { get; }
}

/// <summary>The Standardise names window: drop messy filenames, answer one
/// date prompt per add, and every file in that add is renamed immediately —
/// there is no separate preview/click-Rename step the way Bulk rename has
/// one, because the add itself IS the action. Reuses BulkRename.PlanTidy/
/// Execute/Revert for the actual file-system work (see PlanTidy's own doc
/// comment for why it, not Plan, is the entry point here) and IWorkScheduler
/// for the same reason every sibling tool needs it: a File.Move (or a
/// File.Exists during intake) is a network round trip on the shares this app
/// targets, and must never run on the UI thread.
///
/// One session, one remembered date: <see cref="_lastDate"/> starts at
/// today and is overwritten with whatever the person actually accepted, for
/// the rest of THIS window's lifetime only — nothing here is written to
/// Config, so a fresh app run defaults to today again rather than
/// remembering yesterday's date across a restart, which is the one time
/// "today" would actually be wrong to assume.</summary>
public sealed class StandardiseNamesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;

    /// <summary>What Undo would put back — only the rows the LAST add
    /// actually renamed (Execute's own successes; a skipped, unchanged or
    /// failed row was never moved, so Revert has nothing to do for it).
    /// Overwritten whole by the next add: one batch undo, the same limit
    /// BulkRename's own tool accepts.</summary>
    private List<RenameOutcome> _lastOutcomes = new();

    /// <summary>The grid rows <see cref="_lastOutcomes"/> corresponds to,
    /// 1:1 and in the same order — what Undo removes from <see cref="Results"/>
    /// once it has actually put the files back, so the grid never keeps
    /// showing a "Result" name a file no longer has.</summary>
    private List<StandardiseNameRow> _lastRenamedRows = new();

    private string _lastDate = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public ObservableCollection<StandardiseNameRow> Results { get; } = new();

    public StandardiseNamesViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null)
    {
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        UndoCommand = new AsyncRelayCommand(UndoLastBatchAsync, () => _lastOutcomes.Count > 0 && !IsBusy);
        // AsyncRelayCommand routes a faulted run here and then swallows it
        // with no subscriber — the same vanishing-failure shape
        // FireAndForgetGuardTests exists for, and the reason
        // BulkRenameViewModel wires both of its own commands this way.
        // UndoLastBatchAsync's own try/finally has no catch (Revert is
        // already per-outcome fail-soft), so anything reaching here is
        // unexpected — a scheduler failure, not a file-move problem Revert
        // already reports through Status on its own.
        UndoCommand.OnError += ex => Status = $"Undo stopped unexpectedly: {ex.Message}";
    }

    private bool _isBusy;

    /// <summary>True while an add's rename batch, or an undo, is running —
    /// gates Add files… and Undo alike, the same reason BulkRenameViewModel's
    /// own IsBusy does: either one touching the file list while the other is
    /// mid-flight is exactly the kind of overlap QC-05 named in three other
    /// tools.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            UndoCommand.RaiseCanExecuteChanged();
            Raise(nameof(IsIdle));
        }
    }

    /// <summary>The inverse of <see cref="IsBusy"/>, for Add files… — a
    /// Click handler, not a Command, so it has no CanExecute of its own to
    /// disable it.</summary>
    public bool IsIdle => !IsBusy;

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Feedback for the last add ("2 added · 1 ignored…") — set
    /// even when the date prompt that follows is then cancelled, since it
    /// describes what was DROPPED, not what was renamed; the grid and the
    /// disk are what "cancelling adds nothing and renames nothing" is
    /// actually about (see AddFilesAsync).</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Called by StandardiseNamesWindow.OnClosing when it refuses
    /// a close because <see cref="IsBusy"/> is true — see that method's own
    /// doc comment for why the window blocks the close outright rather than
    /// threading a cancellation token through Execute/Revert. Puts the
    /// refusal in the same Status line everything else here reports
    /// through, so it reads as explained rather than the window simply
    /// ignoring the click.</summary>
    internal void ExplainCloseWasRefused() =>
        Status = "Still renaming — please wait for this batch to finish before closing.";

    /// <summary>Drop files (or Add files…) → prompt for the date → rename
    /// immediately. Cancelling the prompt returns before anything is added
    /// to <see cref="Results"/> or touched on disk — a true no-op, not
    /// merely an empty status message.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Intake.Added intake;
            try
            {
                // Intake's existence check is a File.Exists per dropped
                // path — a network round trip on the shares this tool
                // targets, same reasoning as BulkRenameViewModel.AddFilesAsync.
                // "existing" is always empty: this tool keeps no running
                // list across adds (each add is its own self-contained
                // batch), so the only dedupe Intake.Add can do — and the
                // only one needed — is within THIS drop.
                intake = await _scheduler.Run(
                    () => Intake.Add(Array.Empty<string>(), paths, exists: File.Exists));
            }
            catch (Exception ex)
            {
                // The window calls this as `_ = AddFilesAsync(…)`, and a
                // discarded Task discards its failure with it — the same
                // vanishing-failure defect FireAndForgetGuardTests exists
                // for in BulkRenameViewModel. Caught here so it reports
                // instead, through the line that already carries intake
                // feedback.
                AddNote = $"Couldn't read what was dropped: {ex.Message}";
                return;
            }

            AddNote = intake.Note("file");
            if (intake.Files.Count == 0) return;   // nothing left to ask a date for

            var date = _dialogs.AskDate(_lastDate, intake.Files.Count);
            if (date is null) return;   // cancelled: nothing added, nothing renamed
            _lastDate = date;

            var plans = PlanTidy(intake.Files, date);
            // The dashed collision suffix is what makes this tool's own
            // "wrinkle" safe: Execute's DEFAULT counter (" (2)") would hand
            // back a space and parentheses — the two characters TidyStem
            // exists to strip — straight into a name this tool just tidied.
            var outcomes = await _scheduler.Run(() => Execute(plans, CollisionSuffixStyle.Dashed));

            ApplyBatchResult(plans, outcomes);
        }
        catch (Exception ex)
        {
            // Anything reaching here is unexpected: intake's own failure is
            // already caught above with its own message, and Execute is
            // already per-file fail-soft for IO/access errors. Same net as
            // BulkRenameViewModel's RenameCommand.OnError, needed here for
            // the same reason — this method is fire-and-forget from the
            // window, so nothing else would catch it.
            Status = $"Adding files stopped unexpectedly: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Turns one batch's plans and outcomes into grid rows, the
    /// running undo state, and the status line. Split out from AddFilesAsync
    /// (single responsibility) so that method reads as the four-step story
    /// the brief describes: intake, prompt, plan, execute — this is what
    /// happens with the result.</summary>
    private void ApplyBatchResult(List<PlannedRename> plans, List<RenameOutcome> outcomes)
    {
        var newRows = new List<StandardiseNameRow>();
        var renamedOutcomes = new List<RenameOutcome>();
        int renamed = 0, failed = 0, skipped = 0, unchanged = 0;
        var outcomeIndex = 0;

        foreach (var plan in plans)
        {
            var currentName = Path.GetFileName(plan.Source);
            if (!plan.Changed)
            {
                // PlanTidy leaves Note empty for a genuine no-op (the target
                // already matched the source) and non-empty only when
                // RejectIllegal turned the date-tainted name away — the same
                // distinction Plan()'s own Note carries for the Bulk rename
                // tool.
                if (plan.Note.Length > 0)
                {
                    skipped++;
                    newRows.Add(new StandardiseNameRow(currentName, plan.Note, StandardiseRowStatus.Skipped));
                }
                else
                {
                    unchanged++;
                    newRows.Add(new StandardiseNameRow(
                        currentName, "already standardised", StandardiseRowStatus.Unchanged));
                }
                continue;
            }

            // Execute() emits exactly one outcome per Changed plan, in the
            // same order — its own loop skips a !Changed plan with
            // `continue` before ever appending to its result list (see
            // BulkRename.Execute) — so this index always lands on the
            // outcome for THIS plan without needing to match by path.
            var outcome = outcomes[outcomeIndex++];
            if (outcome.Final is { } final)
            {
                renamed++;
                renamedOutcomes.Add(outcome);
                newRows.Add(new StandardiseNameRow(
                    currentName, Path.GetFileName(final), StandardiseRowStatus.Renamed));
            }
            else
            {
                failed++;
                newRows.Add(new StandardiseNameRow(currentName, outcome.Error, StandardiseRowStatus.Failed));
            }
        }

        foreach (var row in newRows) Results.Add(row);
        _lastOutcomes = renamedOutcomes;
        _lastRenamedRows = newRows.Where(r => r.Status == StandardiseRowStatus.Renamed).ToList();

        Status = BuildStatus(renamed, failed, skipped, unchanged);
        UndoCommand.RaiseCanExecuteChanged();
    }

    private static string BuildStatus(int renamed, int failed, int skipped, int unchanged)
    {
        if (failed == 0 && skipped == 0 && unchanged == 0)
            return $"Renamed {renamed} file{(renamed == 1 ? "" : "s")}.";
        if (renamed == 0 && failed == 0 && skipped == 0)
            return unchanged == 1 ? "Already standardised." : $"All {unchanged} files were already standardised.";

        var parts = new List<string> { $"Renamed {renamed}" };
        if (unchanged > 0) parts.Add($"{unchanged} already standardised");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (failed > 0) parts.Add($"{failed} failed");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Put the last batch's renamed files back, newest first —
    /// BulkRename.Revert's own contract — off the UI thread for the same
    /// reason Execute is. Runs the whole batch through Revert in one
    /// off-thread hop (unlike BulkRenameViewModel's per-file loop, which
    /// needs that granularity for a resumable, cancellable undo): this
    /// tool's batches are the size of one drop and carry no Cancel button.
    ///
    /// Revert's own return value is a list of PROBLEM MESSAGES, not a
    /// per-outcome verdict, so on a partial failure this works out on disk
    /// which outcomes actually moved: one whose Final no longer exists was
    /// restored (File.Move succeeded, whatever happened to any OTHER
    /// outcome in the same batch); one still sitting at Final was not,
    /// whichever of Revert's own reasons is why (the "exists again" guard,
    /// a caught IOException, or anything else). Restored rows come out of
    /// the grid and out of the undo record; UNrestored ones stay in both —
    /// so a locked file that failed to revert keeps its row, keeps
    /// UndoCommand armed, and a second Undo can retry exactly it, rather
    /// than the whole batch's undo record vanishing the instant any single
    /// file in it can't be moved back.</summary>
    internal async Task UndoLastBatchAsync()
    {
        if (_lastOutcomes.Count == 0) return;
        IsBusy = true;
        try
        {
            var batch = _lastOutcomes;
            var rows = _lastRenamedRows;

            var (problems, restored) = await _scheduler.Run(() =>
            {
                var problems = Revert(batch);
                var restored = batch.Select(o => !File.Exists(o.Final)).ToList();
                return (problems, restored);
            });

            var outstandingOutcomes = new List<RenameOutcome>();
            var outstandingRows = new List<StandardiseNameRow>();
            for (var i = 0; i < batch.Count; i++)
            {
                if (restored[i]) Results.Remove(rows[i]);
                else
                {
                    outstandingOutcomes.Add(batch[i]);
                    outstandingRows.Add(rows[i]);
                }
            }
            _lastOutcomes = outstandingOutcomes;
            _lastRenamedRows = outstandingRows;

            Status = problems.Count == 0 ? "Original names restored." : string.Join("; ", problems);
        }
        finally
        {
            IsBusy = false;
            UndoCommand.RaiseCanExecuteChanged();
        }
    }
}
