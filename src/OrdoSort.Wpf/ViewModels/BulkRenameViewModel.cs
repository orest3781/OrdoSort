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
        string note, bool changed, bool manual, bool needsName, string editSeed)
    {
        Source = source;
        Current = current;
        NewName = newName;
        Note = note;
        Changed = changed;
        Manual = manual;
        NeedsName = needsName;
        EditSeed = editSeed;
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
/// probes (SettingsViewModel.cs:56-64,208-216).</summary>
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

    public ObservableCollection<RenameRow> Preview { get; } = new();

    public BulkRenameViewModel(
        Func<IEnumerable<string>, RenameOp, IReadOnlyDictionary<string, string>?, List<PlannedRename>>? plan = null,
        IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, int probeDelayMs = 300)
    {
        _plan = plan ?? Plan;
        _plansProbe = new DebouncedProbe<List<PlannedRename>>(
            scheduler ?? new TaskWorkScheduler(), uiContext, ApplyPlans, probeDelayMs);
        RenameCommand = new RelayCommand(Apply, () => _changed > 0);
        UndoCommand = new RelayCommand(UndoBatch, () => _lastOutcomes.Count > 0);
        ClearCommand = new RelayCommand(
            () => { _files.Clear(); _overrides.Clear(); Refresh(immediate: true); });
    }

    public void Dispose() => _plansProbe.Dispose();

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

    public RelayCommand RenameCommand { get; }
    public RelayCommand UndoCommand { get; }
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

    public void AddFiles(IEnumerable<string> paths)
    {
        int added = 0, ignored = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p) && !_files.Contains(p)) { _files.Add(p); added++; }
            else ignored++;
        }
        AddNote = added == 0 && ignored > 0
            ? $"nothing added — {ignored} item{(ignored == 1 ? "" : "s")} missing or already listed"
            : ignored > 0
                ? $"{added} added · {ignored} ignored (missing, or already listed)"
                : "";
        Refresh(immediate: true);
    }

    public void RemoveFiles(IEnumerable<string> sources)
    {
        foreach (var s in sources.ToList())
        {
            _files.Remove(s);
            _overrides.Remove(s);
        }
        AddNote = "";
        Refresh(immediate: true);
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
                needsName ? SeedFor(newName) : newName));
        }
        NeedsNameCount = Preview.Count(r => r.NeedsName);
        CountsLine = _files.Count == 0
            ? ""
            : $"{_files.Count} file{(_files.Count == 1 ? "" : "s")} · {_changed} will change"
              + (NeedsNameCount > 0 ? $" · {NeedsNameCount} need a name" : "");
        Raise(nameof(RenameButtonText));
        RenameCommand.RaiseCanExecuteChanged();
    }

    internal void Apply()
    {
        // Execute what Preview last rendered, not a fresh Plan() from
        // CurrentOp() — see the finding-1 note on _lastRenderedPlans. This is
        // the whole fix: it makes "the operation executed is the operation
        // last rendered" true unconditionally, rather than true only when no
        // debounce/compute is in flight at the moment of the click.
        var outcomes = Execute(_lastRenderedPlans);
        _overrides.Clear();
        var renamed = outcomes.Where(o => o.Final != null).ToList();
        var failed = outcomes.Where(o => o.Final == null).ToList();
        var finals = renamed.ToDictionary(o => o.Source, o => o.Final!);
        for (var i = 0; i < _files.Count; i++)
            if (finals.TryGetValue(_files[i], out var f)) _files[i] = f;
        _lastOutcomes = renamed;
        Find = Replace = Prefix = Suffix = "";
        CaseIndex = 0;
        ReviewMode = false;
        DeleteSeg1 = DeleteSeg2 = DeleteSeg3 = DeleteSeg4 = DeleteSegLast = false;
        Refresh(immediate: true);
        UndoCommand.RaiseCanExecuteChanged();
        Status = failed.Count > 0
            ? $"Renamed {renamed.Count}; {failed.Count} failed — e.g. " +
              $"{Path.GetFileName(failed[0].Source)}: {failed[0].Error}"
            : $"Renamed {renamed.Count} file{(renamed.Count == 1 ? "" : "s")}.";
    }

    internal void UndoBatch()
    {
        var problems = Revert(_lastOutcomes);
        var restored = _lastOutcomes.Where(o => o.Final != null)
            .ToDictionary(o => o.Final!, o => o.Source);
        for (var i = 0; i < _files.Count; i++)
            if (restored.TryGetValue(_files[i], out var s)) _files[i] = s;
        _lastOutcomes = new List<RenameOutcome>();
        Refresh(immediate: true);
        UndoCommand.RaiseCanExecuteChanged();
        Status = problems.Count > 0 ? string.Join("; ", problems) : "Original names restored.";
    }
}
