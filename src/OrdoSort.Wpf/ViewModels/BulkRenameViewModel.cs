using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
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
/// never overwrites; one batch undo. The logic is fully unit-testable.</summary>
public sealed class BulkRenameViewModel : ObservableObject
{
    private readonly List<string> _files = new();
    private readonly Dictionary<string, string> _overrides = new();   // source -> hand-edited stem
    private List<RenameOutcome> _lastOutcomes = new();

    public ObservableCollection<RenameRow> Preview { get; } = new();

    public BulkRenameViewModel()
    {
        RenameCommand = new RelayCommand(Apply, () => _changed > 0);
        UndoCommand = new RelayCommand(UndoBatch, () => _lastOutcomes.Count > 0);
        ClearCommand = new RelayCommand(() => { _files.Clear(); _overrides.Clear(); Refresh(); });
    }

    // ------------------------------------------------------------ op fields
    private bool _reviewMode;
    public bool ReviewMode { get => _reviewMode; set { if (Set(ref _reviewMode, value)) Refresh(); } }

    private DateTime _receivedDate = DateTime.Today;
    public DateTime ReceivedDate { get => _receivedDate; set { if (Set(ref _receivedDate, value)) Refresh(); } }

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
    public int CaseIndex { get => _caseIndex; set { if (Set(ref _caseIndex, value)) Refresh(); } }

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

    private RenameOp CurrentOp() => new(
        Find: Find, Replace: Replace, Prefix: Prefix, Suffix: Suffix,
        Case: CaseIndex switch { 1 => "upper", 2 => "lower", _ => "keep" },
        ReceivedDate: ReviewMode ? ReceivedDate.ToString("yyyyMMdd") : "");

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
        Refresh();
    }

    public void RemoveFiles(IEnumerable<string> sources)
    {
        foreach (var s in sources.ToList())
        {
            _files.Remove(s);
            _overrides.Remove(s);
        }
        AddNote = "";
        Refresh();
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
        Refresh();
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
    /// supply.</summary>
    private string SeedFor(string fallback) =>
        ReviewMode ? ReceivedDate.ToString("yyyyMMdd") + "-" : fallback;

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

    private void Refresh()
    {
        var plans = Plan(_files, CurrentOp(), _overrides);
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
        var outcomes = Execute(Plan(_files, CurrentOp(), _overrides));
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
        Refresh();
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
        Refresh();
        UndoCommand.RaiseCanExecuteChanged();
        Status = problems.Count > 0 ? string.Join("; ", problems) : "Original names restored.";
    }
}
