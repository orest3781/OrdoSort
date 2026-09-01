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
/// use, access denied) — a genuine failure, StatusRed.
///
/// A row's add batch is not the only thing that can set these any more.
/// Remove last segment's own ApplyPeelResult sets Renamed on a row it
/// successfully peels (a peel IS a rename) and Failed on one whose peel
/// Execute call itself failed — the same two outcomes an add batch already
/// reports, through the same vocabulary. A row BulkRename.PlanPeel holds at
/// the four-segment floor or refuses for a collision is left exactly as it
/// was — Unchanged and Skipped are never something a peel itself
/// produces.</summary>
public enum StandardiseRowStatus { Renamed, Unchanged, Skipped, Failed }

/// <summary>One line of the Standardise names tool's result log: what a
/// dropped file was called, and what has happened to it since. THIS CLASS
/// USED TO BE WRITE-ONCE — its own doc comment used to say so, and until
/// Remove last segment existed that was true: a row was written when its
/// batch finished and never touched again, because there was nothing here to
/// add TO. Peeling overturned that: the same row can now be renamed again,
/// possibly several times over the life of the window, so it has to carry
/// where its file actually IS as well as what to show for it — and both of
/// those can change after construction, unlike <see cref="Current"/>, which
/// still never does:
///
/// - <see cref="Current"/> is frozen at construction: the name the file was
///   ORIGINALLY dropped under. It is the record of where the row came from,
///   and a peel does not touch it — it is how a person finds "the file that
///   used to be called X" after its Result has moved on.
/// - <see cref="CurrentPath"/> is the file's actual location on disk RIGHT
///   NOW: set at construction to wherever the add batch left it (Execute's
///   own target when the row was renamed, the untouched source otherwise),
///   and advanced by every peel that actually renames the row's file. This
///   is what StandardiseNamesViewModel.PeelSelectedAsync hands to
///   BulkRename.PlanPeel for this row's next click, so the button always
///   acts on where the file really is instead of re-deriving it from
///   display text. Never bound in XAML, so it is a plain mutable property,
///   not an observable one.
/// - <see cref="Result"/> is what the Result column shows, and moves in
///   step with CurrentPath: the newest name on a successful peel, or
///   Execute's own error text if a peel's move itself fails.
/// - <see cref="Status"/> can likewise change: ApplyPeelResult sets it to
///   Renamed or Failed alongside Result, for the same reason — see
///   StandardiseRowStatus's own doc comment.
///
/// Undoing a peel (StandardiseNamesViewModel's own UndoLastPeelAsync) puts
/// all three back to what they were immediately before that peel; a held or
/// refused row is never touched in the first place, so there is nothing to
/// put back for it.</summary>
public sealed class StandardiseNameRow : ObservableObject
{
    public StandardiseNameRow(string current, string result, string currentPath, StandardiseRowStatus status)
    {
        Current = current;
        _result = result;
        CurrentPath = currentPath;
        _status = status;
    }

    public string Current { get; }

    private string _result;
    public string Result { get => _result; internal set => Set(ref _result, value); }

    /// <summary>Where this row's file actually is on disk right now — see
    /// this class's own doc comment for why it exists and how it differs
    /// from <see cref="Current"/>.</summary>
    public string CurrentPath { get; internal set; }

    private StandardiseRowStatus _status;
    public StandardiseRowStatus Status { get => _status; internal set => Set(ref _status, value); }
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
/// "today" would actually be wrong to assume.
///
/// Remove last segment (<see cref="PeelCommand"/>) is the second action this
/// class drives, through BulkRename.PlanPeel instead of PlanTidy but the
/// same Execute/Revert plumbing. It shares the ONE undo slot Add already has
/// rather than getting its own — <see cref="_lastBatchKind"/> records which
/// of the two the slot currently holds, so UndoCommand can reverse whichever
/// one actually ran last.</summary>
public sealed class StandardiseNamesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;

    /// <summary>Which of the two operations that share ONE undo slot
    /// produced what <see cref="_lastOutcomes"/> currently holds — Add or
    /// Peel — so UndoLastBatchAsync knows whether reversing it means
    /// removing rows (Add) or putting a row's Result/CurrentPath/Status back
    /// (Peel). Starts at Add, the only kind that could possibly be armed
    /// before anything has run — <see cref="_lastOutcomes"/> being empty at
    /// that point is what actually keeps UndoCommand disabled, not this.</summary>
    private enum LastBatchKind { Add, Peel }
    private LastBatchKind _lastBatchKind = LastBatchKind.Add;

    /// <summary>What Undo would put back — only the rows the LAST operation
    /// (an add or a peel; see <see cref="_lastBatchKind"/>) actually renamed
    /// on disk (Execute's own successes; a held, refused, skipped, unchanged
    /// or failed row was never moved, so Revert has nothing to do for it).
    /// Overwritten whole by the next add OR peel: one batch undo, the same
    /// limit BulkRename's own tool accepts.</summary>
    private List<RenameOutcome> _lastOutcomes = new();

    /// <summary>The grid rows <see cref="_lastOutcomes"/> corresponds to,
    /// 1:1 and in the same order, when <see cref="_lastBatchKind"/> is Add —
    /// what Undo removes from <see cref="Results"/> once it has actually put
    /// the files back, so the grid never keeps showing a "Result" name a
    /// file no longer has. Left empty while a peel is what is armed; see
    /// <see cref="_lastPeelEntries"/> for that case's own parallel list.</summary>
    private List<StandardiseNameRow> _lastRenamedRows = new();

    /// <summary>One row's Result/CurrentPath/Status immediately before a
    /// peel that actually renamed it — captured so UndoLastPeelAsync can put
    /// them back exactly, rather than trying to reverse-derive them from the
    /// row's post-peel state (which the four-segment floor makes lossy: a
    /// peeled row's PRIOR name could itself have had any number of segments,
    /// not always one more than what is left).</summary>
    private sealed record PeelUndoEntry(
        StandardiseNameRow Row, string PreviousResult, string PreviousCurrentPath, StandardiseRowStatus PreviousStatus);

    /// <summary>The peel-batch counterpart to <see cref="_lastRenamedRows"/>,
    /// 1:1 and in the same order as <see cref="_lastOutcomes"/> when <see
    /// cref="_lastBatchKind"/> is Peel. Left empty while an add is what is
    /// armed.</summary>
    private List<PeelUndoEntry> _lastPeelEntries = new();

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
        PeelCommand = new AsyncRelayCommand(PeelSelectedAsync, () => _selectedRows.Count > 0 && !IsBusy);
        // Same reasoning as UndoCommand's own OnError immediately above:
        // PeelSelectedAsync's own try/finally has no catch (Execute is
        // already per-file fail-soft, same as everywhere else in this
        // file), so anything reaching here is a scheduler failure, not a
        // rename problem — those are already reported through Status via
        // ApplyPeelResult.
        PeelCommand.OnError += ex => Status = $"Removing the last segment stopped unexpectedly: {ex.Message}";
    }

    private bool _isBusy;

    /// <summary>True while an add's rename batch, a peel, or an undo is
    /// running — gates Add files…, Remove last segment and Undo alike here,
    /// so no two of them can touch the same state the others are mid-flight
    /// on: exactly the kind of overlap QC-05 named in three other tools, and
    /// the same underlying reason BulkRenameViewModel's own IsBusy gates its
    /// own file-list-touching actions (Undo, Clear, Remove selected — that
    /// class has no equivalent guard on Add, since Bulk rename's own
    /// AddFilesAsync only ever appends to a list Refresh then re-renders,
    /// nothing this class's own self-contained-batch design needs to protect
    /// the same way).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            UndoCommand.RaiseCanExecuteChanged();
            PeelCommand.RaiseCanExecuteChanged();
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

    /// <summary>Pushed in by the window on the grid's SelectionChanged —
    /// DataGrid's SelectedItems is not bindable, the same reason
    /// BulkRenameViewModel.SelectedSources exists. Rows here, not paths: a
    /// Standardise names row is never rebuilt out from under its selection
    /// the way a Bulk rename preview row is (this window's own rows survive
    /// until their batch is undone), so there is no reprojection to survive
    /// and the row itself is a stable, sufficient key.</summary>
    private IReadOnlyList<StandardiseNameRow> _selectedRows = Array.Empty<StandardiseNameRow>();
    public IReadOnlyList<StandardiseNameRow> SelectedRows
    {
        get => _selectedRows;
        set
        {
            // A WPF selection handler can hand over an empty-or-null
            // sequence on a momentarily empty selection; every reader here
            // assumes a real, if empty, list — same guard as SelectedSources.
            _selectedRows = value ?? Array.Empty<StandardiseNameRow>();
            PeelCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Remove last segment: strips ONE trailing dash-separated
    /// segment from every selected row's file and renames immediately, the
    /// same "the click IS the action" shape Add files… already has. Enabled
    /// only when the grid selection is non-empty and the view model is idle
    /// — gated the same two ways UndoCommand already is, above.</summary>
    public AsyncRelayCommand PeelCommand { get; }

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
                // for (that file's own examples are ShellViewModel's
                // Initialize/StartProcessing/ApplySettings, not this one —
                // the SAME class of defect, caught the same way
                // BulkRenameViewModel.AddFilesAsync already catches it for
                // itself). Caught here so it reports instead, through the
                // line that already carries intake feedback.
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
                    newRows.Add(new StandardiseNameRow(
                        currentName, plan.Note, plan.Source, StandardiseRowStatus.Skipped));
                }
                else
                {
                    unchanged++;
                    newRows.Add(new StandardiseNameRow(
                        currentName, "already standardised", plan.Source, StandardiseRowStatus.Unchanged));
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
                    currentName, Path.GetFileName(final), final, StandardiseRowStatus.Renamed));
            }
            else
            {
                failed++;
                newRows.Add(new StandardiseNameRow(
                    currentName, outcome.Error, plan.Source, StandardiseRowStatus.Failed));
            }
        }

        foreach (var row in newRows) Results.Add(row);
        _lastOutcomes = renamedOutcomes;
        _lastRenamedRows = newRows.Where(r => r.Status == StandardiseRowStatus.Renamed).ToList();
        _lastPeelEntries = new();          // this add batch is now what Undo would reverse
        _lastBatchKind = LastBatchKind.Add;

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

    /// <summary>Remove last segment, driven by whatever <see
    /// cref="SelectedRows"/> currently holds — the window pushes that down
    /// on every SelectionChanged, so this always acts on what is actually
    /// highlighted rather than needing its own parameter. Same plan-then-
    /// execute shape as AddFilesAsync minus the date prompt: peeling has
    /// nothing to ask a person, so there is no cancel-before-touching-
    /// anything step here the way a cancelled date prompt gives AddFilesAsync
    /// one.</summary>
    internal async Task PeelSelectedAsync()
    {
        if (IsBusy) return;
        var rows = _selectedRows;
        if (rows.Count == 0) return;
        IsBusy = true;
        try
        {
            var paths = rows.Select(r => r.CurrentPath).ToList();
            var plans = await _scheduler.Run(() => PlanPeel(paths));
            // Dashed for the same reason AddFilesAsync passes it to its own
            // Execute call: the default " (2)" would hand back a space and
            // parentheses on the vanishingly rare race PlanPeel's own
            // plan-time refusal can't see (see PlanPeel's own doc comment) —
            // and this tool's names must never carry either.
            var outcomes = await _scheduler.Run(() => Execute(plans, CollisionSuffixStyle.Dashed));

            ApplyPeelResult(rows, plans, outcomes);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Turns one peel click's plans and outcomes into row updates,
    /// the running undo state, and the status line — ApplyBatchResult's own
    /// counterpart for Remove last segment. A held row (PlanPeel's four-
    /// segment floor) or a refused one (its collision rule) is left
    /// completely untouched, per the brief: nothing happened to it, so
    /// nothing about its row changes. A row Execute itself failed to move
    /// (locked, in use) is reported the same way an add batch's own failure
    /// is — Result becomes the error, Status becomes Failed — but, like that
    /// case, is not added to the undo record: the file never moved, so
    /// Revert would have nothing to do for it.</summary>
    private void ApplyPeelResult(
        IReadOnlyList<StandardiseNameRow> rows, List<PlannedRename> plans, List<RenameOutcome> outcomes)
    {
        var peeledOutcomes = new List<RenameOutcome>();
        var peeledEntries = new List<PeelUndoEntry>();
        int peeled = 0, failed = 0, atFloor = 0, collided = 0;
        var outcomeIndex = 0;

        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            var row = rows[i];

            if (!plan.Changed)
            {
                // PlanPeel's own doc comment: the four-segment floor and a
                // refused collision are the ONLY two reasons it leaves a
                // file untouched, so anything that isn't the floor's own
                // Note is the collision refusal by elimination — no second
                // constant to import from Core just to tell them apart.
                if (plan.Note == PeelAtFloorNote) atFloor++;
                else collided++;
                continue;
            }

            // Same correlation rule as ApplyBatchResult's own outcomeIndex,
            // and true for the same reason: Execute emits exactly one
            // outcome per Changed plan, in the same order.
            var outcome = outcomes[outcomeIndex++];
            if (outcome.Final is { } final)
            {
                peeled++;
                peeledEntries.Add(new PeelUndoEntry(row, row.Result, row.CurrentPath, row.Status));
                peeledOutcomes.Add(outcome);
                row.Result = Path.GetFileName(final);
                row.CurrentPath = final;
                row.Status = StandardiseRowStatus.Renamed;
            }
            else
            {
                failed++;
                row.Result = outcome.Error;
                row.Status = StandardiseRowStatus.Failed;
            }
        }

        _lastOutcomes = peeledOutcomes;
        _lastRenamedRows = new();          // this peel is now what Undo would reverse
        _lastPeelEntries = peeledEntries;
        _lastBatchKind = LastBatchKind.Peel;

        Status = BuildPeelStatus(peeled, failed, atFloor, collided);
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Mirrors BuildStatus's own two-early-out-then-join shape
    /// (peeled/failed/atFloor/collided standing in for that method's
    /// renamed/failed/skipped/unchanged) so the two tools' status lines read
    /// as one family rather than two different vocabularies for the same
    /// kind of summary.</summary>
    private static string BuildPeelStatus(int peeled, int failed, int atFloor, int collided)
    {
        if (failed == 0 && atFloor == 0 && collided == 0)
            return $"Removed the last segment from {peeled} file{(peeled == 1 ? "" : "s")}.";
        if (peeled == 0 && failed == 0 && collided == 0)
            return atFloor == 1
                ? "Already at four segments."
                : $"All {atFloor} files were already at four segments.";

        var parts = new List<string> { $"Removed the last segment from {peeled}" };
        if (atFloor > 0) parts.Add($"{atFloor} already at four segments");
        if (collided > 0) parts.Add($"{collided} name{(collided == 1 ? "" : "s")} already taken");
        if (failed > 0) parts.Add($"{failed} failed");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Put the last batch back. Undo last batch reverses whichever
    /// of the two operations that share this one slot actually ran last, an
    /// add or a peel (<see cref="_lastBatchKind"/>). What "reverse" means
    /// differs by kind, so the two only share the disk-facing half (<see
    /// cref="RevertAndClassify"/>) and diverge on what happens to the grid
    /// afterward — see <see cref="UndoLastAddAsync"/> and <see
    /// cref="UndoLastPeelAsync"/> for each.</summary>
    internal async Task UndoLastBatchAsync()
    {
        if (_lastOutcomes.Count == 0) return;
        IsBusy = true;
        try
        {
            if (_lastBatchKind == LastBatchKind.Peel) await UndoLastPeelAsync();
            else await UndoLastAddAsync();
        }
        finally
        {
            IsBusy = false;
            UndoCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>BulkRename.Revert, off the UI thread, plus the classification
    /// both undo paths below need to act on its result: Revert's own return
    /// value is a list of PROBLEM MESSAGES, not a per-outcome verdict, so a
    /// caller has to work out on disk which outcomes actually moved. Fix
    /// round 2, item 2 corrected the first cut of this check: "restored" is
    /// NOT simply "Final no longer exists" — a renamed file that was deleted
    /// or moved away by something ELSE between the rename and the Undo also
    /// leaves Final gone, but that is not a successful revert (Revert's own
    /// File.Move(Final, Source) throws FileNotFoundException in that case,
    /// which its existing IOException catch already turns into a real
    /// problem message). An outcome counts as restored only when the file is
    /// actually sitting AT Source again: both Final gone AND Source present.
    /// One still sitting at Final, or gone from both without ever landing at
    /// Source, was not restored, whichever of Revert's own reasons is why
    /// (the "exists again" guard, a caught IOException from a lock, the
    /// not-there-to-move-back case above, or anything else).</summary>
    private async Task<(List<string> Problems, List<bool> Restored)> RevertAndClassify(List<RenameOutcome> batch) =>
        await _scheduler.Run(() =>
        {
            var problems = Revert(batch);
            var restored = batch.Select(o => File.Exists(o.Source) && !File.Exists(o.Final)).ToList();
            return (problems, restored);
        });

    /// <summary>Undoing an ADD, newest first — BulkRename.Revert's own
    /// contract. Restored rows come out of the grid and out of the undo
    /// record; UNrestored ones stay in both — so a locked (or vanished) file
    /// that failed to revert keeps its row, keeps UndoCommand armed, and the
    /// grid never contradicts what Status just said — rather than the whole
    /// batch's undo record vanishing the instant any single file in it can't
    /// be moved back.</summary>
    private async Task UndoLastAddAsync()
    {
        var batch = _lastOutcomes;
        var rows = _lastRenamedRows;

        var (problems, restored) = await RevertAndClassify(batch);

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

    /// <summary>Undoing a PEEL — the brief's own rule: the row stays in the
    /// grid (the file still exists and still belongs here; only its name
    /// moved), so a restored outcome puts its row's Result, CurrentPath and
    /// Status back to what its <see cref="PeelUndoEntry"/> captured
    /// immediately before that peel, rather than removing the row the way
    /// UndoLastAddAsync does. An unrestored one is left exactly as the peel
    /// itself set it, and stays armed for a retry, same as
    /// UndoLastAddAsync's own outstanding rows.</summary>
    private async Task UndoLastPeelAsync()
    {
        var batch = _lastOutcomes;
        var entries = _lastPeelEntries;

        var (problems, restored) = await RevertAndClassify(batch);

        var outstandingOutcomes = new List<RenameOutcome>();
        var outstandingEntries = new List<PeelUndoEntry>();
        for (var i = 0; i < batch.Count; i++)
        {
            if (restored[i])
            {
                var entry = entries[i];
                entry.Row.Result = entry.PreviousResult;
                entry.Row.CurrentPath = entry.PreviousCurrentPath;
                entry.Row.Status = entry.PreviousStatus;
            }
            else
            {
                outstandingOutcomes.Add(batch[i]);
                outstandingEntries.Add(entries[i]);
            }
        }
        _lastOutcomes = outstandingOutcomes;
        _lastPeelEntries = outstandingEntries;

        Status = problems.Count == 0 ? "Last segment restored." : string.Join("; ", problems);
    }
}
