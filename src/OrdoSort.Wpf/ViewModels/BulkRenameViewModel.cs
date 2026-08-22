using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One preview row: current name → new name. NewName is settable so
/// the DataGrid can commit a hand edit (routed back via SetOverride).</summary>
public sealed class RenameRow
{
    public RenameRow(string source, string current, string newName,
        string note, bool changed, bool manual, bool needsName, string editSeed,
        bool noteIsProblem)
    {
        Source = source;
        Current = current;
        NewName = newName;
        Note = note;
        Changed = changed;
        Manual = manual;
        NeedsName = needsName;
        EditSeed = editSeed;
        NoteIsProblem = noteIsProblem;
    }

    public string Source { get; }
    public string Current { get; }
    public string NewName { get; set; }
    public string Note { get; }
    public bool Changed { get; }
    public bool Manual { get; }

    /// <summary>The operation couldn't produce a name for this one, so it needs
    /// a human. These are the handful in a batch worth navigating between.</summary>
    public bool NeedsName { get; }

    /// <summary>True when Plan() itself had something to say about this row —
    /// its Note carries text ("doesn't match the review-file layout —
    /// skipped", "name was taken — using a counter", an illegal-name
    /// rejection, …) rather than only the ViewModel's own "edited by hand"/
    /// "(no change)" annotations. NOT the same set as <see cref="NeedsName"/>:
    /// that's narrower (only the sub-case where nothing was renamed at all),
    /// while a taken-name collision still gets auto-resolved (Changed stays
    /// true) yet is exactly as worth a glance — the rename didn't produce
    /// what was configured. Used by BulkRenameWindow.xaml's Note column
    /// ElementStyle (status-colour-vocabulary plan, Task 2 Step 2) to tell
    /// "reporting a problem" (amber) apart from the two purely informational
    /// phrases (subtle) the vocabulary names explicitly — a distinction the
    /// three EXISTING booleans here can't make on their own, since Manual +
    /// Changed alike hold whether the row's Note is empty ("edited by hand")
    /// or a real problem plus that suffix ("name was taken … — edited by
    /// hand").</summary>
    public bool NoteIsProblem { get; }

    /// <summary>What the editor should open with. For a stray in review mode
    /// that's the batch's date prefix, so only the name has to be typed and it
    /// can't drift out of format; otherwise it's what the row already says.</summary>
    public string EditSeed { get; }
}

/// <summary>Bulk rename: drop files, describe the change once, watch the live
/// current → new preview, rename. Hand-edited targets survive op changes;
/// never overwrites; one batch undo. The logic is fully unit-testable.
///
/// The preview's only I/O is Plan's File.Exists check (BulkRename.cs:159-161,
/// more per collision) — expensive enough on an SMB destination that it must
/// never run on the UI thread per keystroke. Refresh is split into a compute
/// closure (Plan — pure plus File.Exists, touches no bound state, safe off
/// thread) and an apply step (ApplyPlans — mutates Preview and friends, only
/// ever run on the UI thread via DebouncedProbe's marshal), the same
/// gather/apply shape RouteEditVm/WatchEditVm already use for their own
/// probes (SettingsViewModel.cs:56-64,208-216).
///
/// The renames themselves, the undo and the intake's existence checks are
/// off-thread for the same reason (audit QC-04) — they are the same I/O, only
/// a batch of it — through ApplyAsync/UndoBatchAsync/AddFilesAsync rather than
/// through the probe, since they are single deliberate actions with nothing to
/// debounce and a progress line and a cancel of their own.</summary>
public sealed class BulkRenameViewModel : ObservableObject, IDisposable
{
    private readonly List<string> _files = new();
    private readonly Dictionary<string, string> _overrides = new();   // source -> hand-edited stem
    private List<RenameOutcome> _lastOutcomes = new();

    // Final-review finding 1 (2026-08-05 debounce pair): the plan Apply()
    // executes must be the SAME plan Preview last rendered, never a fresh
    // Plan(_files, CurrentOp(), _overrides) call. Once Refresh became
    // debounced, CurrentOp() reads whatever Find/Replace/etc. say RIGHT NOW —
    // which, inside the ~300ms-plus-compute window between a keystroke and
    // ApplyPlans landing, is newer than what Preview is showing. Re-planning
    // from CurrentOp() in Apply() therefore let a click execute an operation
    // the user never saw previewed: on the SMB shares this app targets, that
    // window is seconds, easily long enough for "type s toward scan, click
    // the still-enabled Rename button" to rename files against Find="s"
    // while the screen still reads the Find="scan" preview. Retaining the
    // exact plan ApplyPlans rendered and executing THAT (instead of gating
    // RenameCommand's CanExecute on "no armed/in-flight probe", the other
    // shape the review named) keeps the button live with no typing-flicker
    // and makes what-you-see-is-what-you-get structural rather than timing-
    // dependent: there is no code path where Apply() can run a plan Preview
    // didn't already show. Execute() re-checks File.Exists on each target
    // immediately before its File.Move (BulkRename.cs:192), so a stale plan
    // is still safe against anything that changed on disk since Preview was
    // rendered — only the OPERATION is pinned to what was shown, not the
    // filesystem state.
    private List<PlannedRename> _lastRenderedPlans = new();

    // Off-thread, debounced: Plan's File.Exists check must never run per
    // keystroke of Find/Replace/Prefix/Suffix, and must never block the UI
    // thread — see DebouncedProbe. One shared probe for the whole preview
    // (not one per field, unlike SettingsViewModel's several independent
    // notes): only the latest edit's Plan() result is ever worth applying,
    // so coalescing every trigger onto one timer is exactly the wanted
    // behavior, not a shortcut.
    private readonly DebouncedProbe<List<PlannedRename>> _plansProbe;

    // Seam over BulkRename.Plan (the same shape as RouteEditVm's
    // _validateRoute/WatchEditVm's _directoryExists) so a test can inject
    // latency into the COMPUTE itself — not the scheduler that dispatches
    // it. Latency injected at the scheduler only proves the scheduler is
    // async; it doesn't prove a REGRESSION to a synchronous Plan() call in
    // the setter would be caught, because a synchronous call bypasses the
    // scheduler entirely. This is what actually stands in for the real
    // File.Exists cost finding 5.2 is about.
    private readonly Func<IEnumerable<string>, RenameOp,
        IReadOnlyDictionary<string, string>?, List<PlannedRename>> _plan;

    // The renames and the undo run here too, not only the preview (audit
    // QC-04). One File.Move per file against a share is the same cost the
    // class comment above says the preview must never pay on the UI thread,
    // and a batch is a hundred of them in a row.
    private readonly IWorkScheduler _scheduler;

    // The batch's brake. One source PER BATCH, unlike ZipListViewModel's
    // single window-lifetime source: cancelling one run here must leave the
    // next one able to start, since the natural thing to do after stopping a
    // batch is to fix the rule and run it again. Also cancelled from Dispose
    // — MainWindow disposes this view model as the dialog closes, and a
    // closed window must not keep moving files (UnlockViewModel.CancelUnlock
    // states the same rule).
    private CancellationTokenSource? _batchCts;

    public ObservableCollection<RenameRow> Preview { get; } = new();

    public BulkRenameViewModel(
        Func<IEnumerable<string>, RenameOp, IReadOnlyDictionary<string, string>?, List<PlannedRename>>? plan = null,
        IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, int probeDelayMs = 300)
    {
        _plan = plan ?? Plan;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _plansProbe = new DebouncedProbe<List<PlannedRename>>(
            _scheduler, uiContext, ApplyPlans, probeDelayMs);
        RenameCommand = new AsyncRelayCommand(ApplyAsync, () => _changed > 0 && !IsBusy);
        UndoCommand = new AsyncRelayCommand(UndoBatchAsync, () => _lastOutcomes.Count > 0 && !IsBusy);
        CancelCommand = new RelayCommand(() => _batchCts?.Cancel(), () => IsBusy);
        // AsyncRelayCommand reports a faulted run here and then swallows it,
        // so without a handler a batch that stopped on something unexpected
        // would leave its last "Renaming 3 of 12…" line up as if it were
        // still working — the same vanishing-failure defect
        // FireAndForgetGuardTests exists for. Anything reaching here IS
        // unexpected: Execute and Revert are already per-file fail-soft for
        // IO and access errors, which is why this says so plainly instead of
        // claiming a count. What the batch did finish is still in
        // _lastOutcomes, so Undo remains offered.
        RenameCommand.OnError += ex => Status = $"The rename stopped unexpectedly: {ex.Message}";
        UndoCommand.OnError += ex => Status = $"Undo stopped unexpectedly: {ex.Message}";
        // Gated on !IsBusy like the two above, and for a reason this task
        // created: while the renames froze the UI thread, Clear and Remove
        // selected were unreachable during a batch. A responsive window makes
        // pressing Clear while two hundred files move an ordinary thing to
        // do — and the batch would go on renaming files no longer listed,
        // leaving the fixup matching nothing and an empty grid under
        // "Renamed 12 files.". Same defect QC-05 describes in ZipTools and
        // Unlock; this is the third place it can happen.
        ClearCommand = new RelayCommand(
            () => { _files.Clear(); _overrides.Clear(); Refresh(immediate: true); },
            () => !IsBusy);
    }

    public void Dispose()
    {
        _plansProbe.Dispose();
        _batchCts?.Cancel();
    }

    // ------------------------------------------------------------ op fields
    // ReviewMode/ReceivedDate/CaseIndex/DeleteSeg* are single clicks, not a
    // keystroke burst — immediate keeps them feeling as responsive as they
    // did when Refresh ran synchronously. Find/Replace/Prefix/Suffix are
    // typed, so THEY debounce — that's the literal per-keystroke churn
    // finding 5.2 is about.
    private bool _reviewMode;
    public bool ReviewMode { get => _reviewMode; set { if (Set(ref _reviewMode, value)) Refresh(immediate: true); } }

    private DateTime _receivedDate = DateTime.Today;
    public DateTime ReceivedDate { get => _receivedDate; set { if (Set(ref _receivedDate, value)) Refresh(immediate: true); } }

    private string _find = "";
    public string Find { get => _find; set { if (Set(ref _find, value)) Refresh(); } }

    private string _replace = "";
    public string Replace { get => _replace; set { if (Set(ref _replace, value)) Refresh(); } }

    private string _prefix = "";
    public string Prefix { get => _prefix; set { if (Set(ref _prefix, value)) Refresh(); } }

    private string _suffix = "";
    public string Suffix { get => _suffix; set { if (Set(ref _suffix, value)) Refresh(); } }

    /// <summary>0 keep, 1 UPPERCASE, 2 lowercase.</summary>
    private int _caseIndex;
    public int CaseIndex { get => _caseIndex; set { if (Set(ref _caseIndex, value)) Refresh(immediate: true); } }

    private bool _deleteSeg1;
    public bool DeleteSeg1 { get => _deleteSeg1; set { if (Set(ref _deleteSeg1, value)) Refresh(immediate: true); } }

    private bool _deleteSeg2;
    public bool DeleteSeg2 { get => _deleteSeg2; set { if (Set(ref _deleteSeg2, value)) Refresh(immediate: true); } }

    private bool _deleteSeg3;
    public bool DeleteSeg3 { get => _deleteSeg3; set { if (Set(ref _deleteSeg3, value)) Refresh(immediate: true); } }

    private bool _deleteSeg4;
    public bool DeleteSeg4 { get => _deleteSeg4; set { if (Set(ref _deleteSeg4, value)) Refresh(immediate: true); } }

    private bool _deleteSegLast;
    public bool DeleteSegLast { get => _deleteSegLast; set { if (Set(ref _deleteSegLast, value)) Refresh(immediate: true); } }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Feedback for the last add/drop ("2 added · 1 ignored…").</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>"12 files · 3 will change · 2 won't (name didn't parse)".</summary>
    private string _countsLine = "";
    public string CountsLine { get => _countsLine; private set => Set(ref _countsLine, value); }

    private int _changed;
    public string RenameButtonText =>
        _changed > 0 ? $"Rename {_changed} file{(_changed == 1 ? "" : "s")}" : "Rename";

    private bool _isBusy;

    /// <summary>True while a rename or an undo batch is running: shows the
    /// Cancel button and gates every other action that touches the file list.
    /// AsyncRelayCommand's own guard only blocks re-entering the SAME command,
    /// so this is what makes WPF disable Undo, Clear and Remove selected while
    /// a rename runs — each of them would otherwise act on a list the batch is
    /// halfway through rewriting.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RenameCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
            Raise(nameof(IsIdle));
        }
    }

    /// <summary>The inverse of <see cref="IsBusy"/>, for the one button that
    /// is a Click handler rather than a command and so has no CanExecute of
    /// its own to disable it — Remove selected. See ClearCommand's comment
    /// for why removing files mid-batch has to be off the table.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>What Undo would put back. Exposed so a test can observe it
    /// MID-batch — the durability half of QC-04 is a claim about what this
    /// holds while the batch is still running, which a check of the finished
    /// state cannot tell apart from the defect.</summary>
    internal IReadOnlyList<RenameOutcome> LastOutcomes => _lastOutcomes;

    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }

    private RenameOp CurrentOp()
    {
        var deletePositions = new List<int>();
        if (DeleteSeg1) deletePositions.Add(1);
        if (DeleteSeg2) deletePositions.Add(2);
        if (DeleteSeg3) deletePositions.Add(3);
        if (DeleteSeg4) deletePositions.Add(4);

        return new(
            Find: Find, Replace: Replace, Prefix: Prefix, Suffix: Suffix,
            Case: CaseIndex switch { 1 => "upper", 2 => "lower", _ => "keep" },
            // Invariant: this stem is rebuilt into the actual on-disk file
            // name (BulkRename.TransformStem), so it can't vary by station.
            ReceivedDate: ReviewMode ? ReceivedDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "",
            DeleteSegments: deletePositions.Count > 0 ? deletePositions : null,
            DeleteLastSegment: DeleteSegLast);
    }

    /// <summary>Dedupe, canonicalisation and the status line all come from
    /// Intake.Add. This tool is why the dedupe has to be spelling-proof
    /// rather than merely tidy: two rows over one file both reach
    /// BulkRename.Execute as File.Moves, and the second fails on bytes the
    /// first already moved. See PathIdentity.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var already = _files.ToList();

        // Intake's existence check is a File.Exists per dropped path, and
        // that is a network round trip on the shares this tool targets —
        // "Add a folder's files…" hands over a folder's worth of them at
        // once, so it used to cost the UI thread one stall per file before
        // the first preview row ever appeared. Same defect and same fix as
        // ZipListViewModel.AddPaths and UnlockViewModel.AddFilesAsync.
        Intake.Added offThread;
        try
        {
            offThread = await _scheduler.Run(() => Intake.Add(already, candidates, exists: File.Exists));
        }
        catch (Exception ex)
        {
            // The window calls this as `_ = AddFilesAsync(…)`, and a discarded
            // Task discards its failure with it — the vanishing-failure defect
            // FireAndForgetGuardTests exists for. Caught HERE rather than at
            // that one call site so every caller gets the net, and reported
            // through the line that already exists for intake feedback.
            AddNote = $"Couldn't read what was dropped: {ex.Message}";
            return;
        }

        // Re-checked against the LIVE list, not the snapshot taken before
        // the await — otherwise a second drop landing mid-await gets two
        // rows over one file, which is exactly what the dedupe note above
        // exists to prevent.
        var settled = Intake.Add(_files, offThread.Files);
        _files.AddRange(settled.Files);
        AddNote = (offThread with
        {
            Files = settled.Files,
            AlreadyListed = offThread.AlreadyListed + settled.AlreadyListed,
        }).Note("file");
        Refresh(immediate: true);
    }

    public void RemoveFiles(IEnumerable<string> sources)
    {
        // The button is disabled mid-batch (IsIdle), but the guard lives here
        // too: this is public, and dropping a file from the list while the
        // batch is renaming it would leave the post-loop fixup with nothing to
        // match and the row gone from a grid that still claims the rename.
        if (IsBusy) return;
        foreach (var s in sources.ToList())
        {
            _files.Remove(s);
            _overrides.Remove(s);
        }
        AddNote = "";
        Refresh(immediate: true);
    }

    /// <summary>Drop the rows the grid selection holds — read from the
    /// selection THIS class preserved across reprojections rather than from
    /// the grid at click time (audit QC-11). The window pushes its selection
    /// down through <see cref="SelectedSources"/>; see the snapshot in
    /// ApplyPlans for why reading the grid at click time removed nothing at
    /// all after any keystroke in one of the operation fields.</summary>
    public void RemoveSelected()
    {
        if (_selectedSources.Count == 0) return;
        RemoveFiles(_selectedSources);
    }

    /// <summary>Pushed in by the window on SelectionChanged — DataGrid's
    /// SelectedItems is not bindable, so this is the same push shape
    /// FilenameListViewModel.SelectedPaths uses. Keyed on RenameRow.Source:
    /// a rebuilt preview hands out NEW RenameRow instances for the same
    /// files, so the row object is no identity at all.</summary>
    private IReadOnlyList<string> _selectedSources = Array.Empty<string>();
    public IReadOnlyList<string> SelectedSources
    {
        get => _selectedSources;
        set
        {
            // A WPF selection handler can hand over an empty-or-null
            // sequence (SelectedItems.OfType<…>() on a momentarily empty
            // selection); every reader here assumes a real, if empty, list.
            _selectedSources = value ?? Array.Empty<string>();
            Raise(nameof(SelectedSources));
        }
    }

    /// <summary>Raised after a rebuild has re-established which sources are
    /// selected, so the window can re-apply it to the grid — the other half
    /// of the push <see cref="SelectedSources"/> makes in the opposite
    /// direction. Same shape as FilenameListViewModel.SelectionRestored.</summary>
    public event EventHandler<IReadOnlyList<string>>? SelectionRestored;

    /// <summary>Re-establishes the selection a rebuild just destroyed,
    /// keeping only rows that are still listed — restoring one the batch has
    /// since dropped would let Remove selected try to remove a file that
    /// isn't on screen. Assigns the field rather than the property so the
    /// window's own SelectionChanged echo isn't mistaken for a user
    /// action.</summary>
    private void RestoreSelection(IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0) return;

        var listed = new HashSet<string>(
            Preview.Select(r => r.Source), StringComparer.OrdinalIgnoreCase);
        var kept = wanted.Where(listed.Contains).ToList();

        _selectedSources = kept;
        Raise(nameof(SelectedSources));
        SelectionRestored?.Invoke(this, kept);
    }

    /// <summary>A hand-edited "New name" cell. Empty text clears the override;
    /// a typed extension is stripped (extensions never change).</summary>
    public void SetOverride(string source, string text)
    {
        text = text.Trim();
        var ext = Path.GetExtension(source);
        if (text.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            text = text[..^ext.Length].Trim();
        if (text.Length == 0) _overrides.Remove(source);
        else _overrides[source] = text;
        Refresh(immediate: true);
    }

    private int _needsNameCount;

    /// <summary>How many rows the operation couldn't name. This is the number
    /// worth acting on — a batch is finished when it reaches zero.</summary>
    public int NeedsNameCount
    {
        get => _needsNameCount;
        private set => Set(ref _needsNameCount, value);
    }

    /// <summary>What a stray's editor opens with. In review mode the batch
    /// already has a date, so seed the prefix and let the caret sit after it —
    /// the typing left to do is the name, which is the part only a person can
    /// supply. Invariant, like the op's own ReceivedDate above: this seed
    /// becomes the file name unless the person changes it, and it must match
    /// the shape every other file in the same batch just got.</summary>
    private string SeedFor(string fallback) =>
        ReviewMode ? ReceivedDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" : fallback;

    /// <summary>The next row still waiting on a name, wrapping. -1 when there
    /// are none, so Enter simply commits on a finished batch.</summary>
    public int IndexOfNextNeedingName(int after)
    {
        for (var step = 1; step <= Preview.Count; step++)
        {
            var i = ((after + step) % Preview.Count + Preview.Count) % Preview.Count;
            if (Preview[i].NeedsName) return i;
        }
        return -1;
    }

    /// <summary>Snapshot the current op/files/overrides on the UI thread (all
    /// three are cheap, no-I/O reads) and (re)arm the plan probe. The compute
    /// closure below captures only these snapshots — never <c>this</c>,
    /// <see cref="Preview"/>, or any bound property — so it stays safe to run
    /// off the UI thread no matter when the probe actually fires.</summary>
    private void Refresh(bool immediate = false)
    {
        var op = CurrentOp();
        var filesSnapshot = _files.ToList();
        var overridesSnapshot = new Dictionary<string, string>(_overrides);

        if (filesSnapshot.Count == 0)
        {
            // Plan's only I/O is a File.Exists per file — with nothing to
            // iterate there's genuinely no I/O to defer, so (like a blank
            // path in RouteEditVm/WatchEditVm) this resolves synchronously,
            // cancelling anything still in flight so a slow, now-stale probe
            // can never land after this and repopulate Preview.
            _plansProbe.Cancel();
            ApplyPlans(new List<PlannedRename>());
            return;
        }

        _plansProbe.Trigger(() => _plan(filesSnapshot, op, overridesSnapshot), immediate);
    }

    /// <summary>Everything from the old synchronous Refresh from
    /// Preview.Clear() onward. Only ever runs on the UI thread — either
    /// directly (the empty-batch fast path above) or via DebouncedProbe's
    /// SynchronizationContext marshal — so mutating Preview (an
    /// ObservableCollection bound to the DataGrid) here is safe.</summary>
    private void ApplyPlans(List<PlannedRename> plans)
    {
        // Retained verbatim so Apply() has exactly what Preview is about to
        // show, not a re-plan from whatever the op fields say by the time the
        // button is clicked — see the finding-1 note on _lastRenderedPlans.
        _lastRenderedPlans = plans;

        // Snapshot BEFORE the rebuild (audit QC-11 — the same defect FL-03
        // fixed in FilenameListViewModel, never applied here). Preview.Clear()
        // raises a Reset, the DataGrid drops its selection on it, and the
        // window pushes that now-empty selection straight back into
        // SelectedSources — so eight rows picked out for Remove selected are
        // gone by the time one more character typed into "At start:" finishes
        // debouncing, and the button then removes nothing, with no error and
        // nothing on screen to tell the difference from a real removal.
        var wasSelected = _selectedSources;

        Preview.Clear();
        _changed = 0;
        foreach (var pr in plans)
        {
            var newName = Path.GetFileName(pr.Changed ? pr.Target : pr.Source);
            var notes = new List<string>();
            if (pr.Note.Length > 0) notes.Add(pr.Note);
            if (pr.Manual) notes.Add("edited by hand");
            if (!pr.Changed && pr.Note.Length == 0) notes.Add("(no change)");
            if (pr.Changed) _changed++;
            // the operation had something to say about why it produced nothing:
            // that is a file waiting on a person, not one that simply matched
            var needsName = !pr.Changed && pr.Note.Length > 0;
            Preview.Add(new RenameRow(pr.Source, Path.GetFileName(pr.Source), newName,
                string.Join(" — ", notes), pr.Changed, pr.Manual, needsName,
                needsName ? SeedFor(newName) : newName, noteIsProblem: pr.Note.Length > 0));
        }
        RestoreSelection(wasSelected);
        NeedsNameCount = Preview.Count(r => r.NeedsName);
        CountsLine = _files.Count == 0
            ? ""
            : $"{_files.Count} file{(_files.Count == 1 ? "" : "s")} · {_changed} will change"
              + (NeedsNameCount > 0 ? $" · {NeedsNameCount} need a name" : "");
        Raise(nameof(RenameButtonText));
        RenameCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Rename what Preview last rendered, one file at a time and off
    /// the UI thread (audit QC-04). The loop is the sequential,
    /// cancelled-between-items shape ZipListViewModel.RunBatchAsync runs its
    /// own batches with, for the same two reasons: several File.Moves at once
    /// against one share buys contention rather than speed, and a batch
    /// stopped between files leaves every file it did touch in a finished
    /// state.
    ///
    /// What is different here, and is the reason QC-04 is High, is WHERE the
    /// outcome is recorded. <see cref="_lastOutcomes"/> — the only thing Undo
    /// reads — is published BEFORE the first move and appended to as each one
    /// lands, so it names the work already done at every instant of the
    /// batch. It used to be assigned only after the loop, which meant a batch
    /// that didn't reach its last file left files renamed on disk with no
    /// undo path at all: not a theoretical window, since the loop ran on the
    /// UI thread and the frozen window is precisely what made people kill the
    /// app.
    ///
    /// Everything after the loop stays on the UI thread by the ordinary
    /// route: this only ever starts from RenameCommand on the dispatcher
    /// thread, so each await resumes on the context captured there — the same
    /// shape ShellViewModel.OnUndoAsync uses for its own scheduler hop.
    /// (ZipListViewModel posts explicitly instead because its batch marshals
    /// per-row applies; there is no such hand-off here.)</summary>
    internal async Task ApplyAsync()
    {
        // Execute what Preview last rendered, not a fresh Plan() from
        // CurrentOp() — see the finding-1 note on _lastRenderedPlans. This is
        // the whole fix: it makes "the operation executed is the operation
        // last rendered" true unconditionally, rather than true only when no
        // debounce/compute is in flight at the moment of the click.
        // Filtered to the rows that actually change, which is both what
        // Execute would have skipped anyway and what "3 of 12" has to count
        // to match the number on the button.
        var batch = _lastRenderedPlans.Where(p => p.Changed).ToList();

        var renamed = new List<RenameOutcome>();
        var failed = new List<RenameOutcome>();
        _lastOutcomes = renamed;   // published before the first move — see above

        using var cts = new CancellationTokenSource();
        _batchCts = cts;
        IsBusy = true;
        var cancelled = false;
        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                // Checked BETWEEN files, never mid-move: a File.Move is not
                // interruptible and a half-done one is worse than a late one.
                if (cts.IsCancellationRequested) { cancelled = true; break; }
                Status = $"Renaming {i + 1} of {batch.Count}…";
                var outcome = await _scheduler.Run(() => RenameOne(batch[i]));
                (outcome.Final is null ? failed : renamed).Add(outcome);
            }
        }
        // In the finally, not after it: a batch that stops on something
        // unexpected must not leave _files naming paths that no longer exist.
        // Skipping this on that path left the grid listing files that had
        // already been renamed, and the "stopped unexpectedly" message then
        // invited a second Rename over rows every one of which would fail.
        // Status stays OUTSIDE, last, so it reports only a run that finished
        // its loop — the faulted path's message belongs to OnError.
        finally
        {
            _batchCts = null;
            _overrides.Clear();
            var finals = renamed.ToDictionary(o => o.Source, o => o.Final!);
            for (var i = 0; i < _files.Count; i++)
                if (finals.TryGetValue(_files[i], out var f)) _files[i] = f;
            // The operation fields are cleared even on a cancel, deliberately.
            // Leaving them set would re-render the same rule over a list where
            // some files already carry its result, so finishing the job by
            // pressing Rename again would prefix the prefixed ones twice. A
            // clean slate over the names that are actually on disk is the only
            // state that can't quietly do that; Undo covers the half that ran.
            Find = Replace = Prefix = Suffix = "";
            CaseIndex = 0;
            ReviewMode = false;
            DeleteSeg1 = DeleteSeg2 = DeleteSeg3 = DeleteSeg4 = DeleteSegLast = false;
            Refresh(immediate: true);
            UndoCommand.RaiseCanExecuteChanged();
            // Last, so the tool only becomes usable again once its list agrees
            // with the disk.
            IsBusy = false;
        }

        Status = cancelled
            // Names what completed, not what was asked for: that is both the
            // honest count and exactly what Undo can still put back.
            ? $"Cancelled — renamed {renamed.Count} of {batch.Count}" +
              (failed.Count > 0 ? $", {failed.Count} failed" : "") + "."
            : failed.Count > 0
                ? $"Renamed {renamed.Count}; {failed.Count} failed — e.g. " +
                  $"{Path.GetFileName(failed[0].Source)}: {failed[0].Error}"
                : $"Renamed {renamed.Count} file{(renamed.Count == 1 ? "" : "s")}.";
    }

    /// <summary>One plan through BulkRename.Execute. Its loop body is
    /// self-contained per plan — the target collision is re-checked against
    /// the disk at the last instant (BulkRename.cs:195) and nothing carries
    /// between iterations — so running it a plan at a time is the same
    /// execution, just with a seam between files for the progress line, the
    /// cancel check and the outcome record. Every plan reaching here has
    /// Changed set, which is what makes the single outcome certain.</summary>
    private static RenameOutcome RenameOne(PlannedRename plan) => Execute(new[] { plan })[0];

    /// <summary>Put a batch back, newest first, one file at a time and off
    /// the UI thread — BulkRename.Revert is the same foreach of File.Moves
    /// Execute is, and it was on the UI thread for the same reason.
    ///
    /// Each restored file is dropped from <see cref="_lastOutcomes"/> as it
    /// lands, the mirror of Apply's recording rule: what is left there is
    /// always exactly what is still renamed on disk, so a cancelled undo can
    /// be resumed rather than leaving the list claiming files it already put
    /// back. A file that could NOT be restored is deliberately kept for the
    /// same reason MatchMerge keeps its failed outcomes — it really is still
    /// renamed, and retrying is the only way out.</summary>
    internal async Task UndoBatchAsync()
    {
        var batch = _lastOutcomes;
        var total = batch.Count;
        var problems = new List<string>();
        var restored = new Dictionary<string, string>();

        using var cts = new CancellationTokenSource();
        _batchCts = cts;
        IsBusy = true;
        var cancelled = false;
        try
        {
            for (var i = total - 1; i >= 0; i--)
            {
                if (cts.IsCancellationRequested) { cancelled = true; break; }
                var outcome = batch[i];
                Status = $"Restoring {total - i} of {total}…";
                var problem = await _scheduler.Run(() => RevertOne(outcome));
                if (problem is null)
                {
                    restored[outcome.Final!] = outcome.Source;
                    batch.RemoveAt(i);
                }
                else
                {
                    problems.Add(problem);
                }
            }
        }
        // Same shape as ApplyAsync's: the list has to agree with the disk even
        // when the undo stops on something unexpected, and Status stays
        // outside so the faulted path's message belongs to OnError.
        finally
        {
            _batchCts = null;
            for (var i = 0; i < _files.Count; i++)
                if (restored.TryGetValue(_files[i], out var s)) _files[i] = s;
            Refresh(immediate: true);
            UndoCommand.RaiseCanExecuteChanged();
            IsBusy = false;
        }

        Status = cancelled
            ? $"Cancelled — restored {restored.Count} of {total}."
            : problems.Count > 0 ? string.Join("; ", problems) : "Original names restored.";
    }

    /// <summary>One outcome through BulkRename.Revert — self-contained per
    /// outcome for the same reason <see cref="RenameOne"/> is — as the
    /// readable problem it reported, or null when the name was restored.</summary>
    private static string? RevertOne(RenameOutcome outcome) =>
        Revert(new[] { outcome }).FirstOrDefault();
}
