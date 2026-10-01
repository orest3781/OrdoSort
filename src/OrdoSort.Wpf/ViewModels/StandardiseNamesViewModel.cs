using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>What a row's Result column says, and its colour: Pending (the
/// new name, to be applied by Rename) and Renamed read as plain text;
/// Unchanged (already standardised) is SubtleText; Skipped (the name can't
/// be built: empty, illegal) is StatusAmber; Failed (the move itself failed:
/// locked, in use, access denied) is StatusRed — the same vocabulary as every
/// sibling tool's Result column.</summary>
public enum StandardiseRowStatus { Renamed, Unchanged, Skipped, Failed, Pending }

/// <summary>One file in the Standardise names list.</summary>
public sealed class StandardiseNameRow : ObservableObject
{
    public StandardiseNameRow(string current, string result, string currentPath, StandardiseRowStatus status)
    {
        _current = current;
        _result = result;
        CurrentPath = currentPath;
        _status = status;
    }

    private string _current;
    /// <summary>The file's name as it is on disk now.</summary>
    public string Current { get => _current; internal set => Set(ref _current, value); }

    private string _result;
    /// <summary>The name it will get (Pending), got (Renamed), or why not.</summary>
    public string Result { get => _result; internal set => Set(ref _result, value); }

    /// <summary>Where the file is on disk now; follows it through Rename and Undo.</summary>
    public string CurrentPath { get; internal set; }

    private StandardiseRowStatus _status;
    public StandardiseRowStatus Status { get => _status; internal set => Set(ref _status, value); }

    /// <summary>The file's last-write time, read when it was added; the
    /// date source "File's modified date" uses it.</summary>
    internal DateTime? Modified { get; set; }

    /// <summary>Words (1-based, as <see cref="Standardise.Read"/> counts
    /// them) left out of this file's new name: the segment chips.</summary>
    internal HashSet<int> Dropped { get; } = new();
}

/// <summary>A labelled choice for one of the window's drop-downs.</summary>
public sealed record StandardiseChoice<T>(T Value, string Label)
{
    /// <summary>The label: a screen reader names a drop-down's items by
    /// their text, which for a record would otherwise be its type and
    /// fields (AccessibleNameTests).</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The Standardise names window (owner request 2026-09-29: preview, then
/// Rename; letter case; separator; date options; segment chips). Files
/// dropped or added are listed with the name each will get, and nothing is
/// renamed until Rename is pressed. The rule is <see cref="Standardise"/>;
/// its defaults are the fixed rule this tool had before (UPPERCASE, dashes,
/// the typed date in front), so an untouched window names files as before.
///
/// The preview is planned off the UI thread (the plan checks the disk for
/// name clashes, a network round trip on a share); a newer request makes an
/// older, slower result stale, and it is dropped. Rename and Undo run off
/// the UI thread too, and a close is refused while either runs.
/// </summary>
public sealed class StandardiseNamesViewModel : ObservableObject
{
    private readonly IWorkScheduler _scheduler;

    private List<RenameOutcome> _lastOutcomes = new();
    /// <summary>Each file the last rename moved, by its new path: the words
    /// it had dropped, so Undo can put them back with the name.</summary>
    private Dictionary<string, HashSet<int>> _lastDropped = new(PathIdentity.PathComparer.Instance);
    private int _previewGeneration;

    /// <summary>The preview on screen: the rows it covers and the plan
    /// each shows. Rename carries out exactly this, so it can never apply a
    /// name the grid didn't show.</summary>
    private List<StandardiseNameRow> _shownRows = new();
    private List<PlannedRename> _shownPlans = new();
    private SegmentJoin _shownSeparator = SegmentJoin.Dash;

    public ObservableCollection<StandardiseNameRow> Results { get; } = new();

    public ObservableCollection<SegmentChip> SegmentChips { get; } = new();

    public static IReadOnlyList<StandardiseChoice<NameCase>> CaseChoices { get; } = new[]
    {
        new StandardiseChoice<NameCase>(NameCase.Upper, "UPPERCASE"),
        new StandardiseChoice<NameCase>(NameCase.Title, "Title Case"),
        new StandardiseChoice<NameCase>(NameCase.AsIs, "As it is"),
    };

    public static IReadOnlyList<StandardiseChoice<SegmentJoin>> SeparatorChoices { get; } = new[]
    {
        new StandardiseChoice<SegmentJoin>(SegmentJoin.Dash, "Dash  -"),
        new StandardiseChoice<SegmentJoin>(SegmentJoin.Underscore, "Underscore  _"),
        new StandardiseChoice<SegmentJoin>(SegmentJoin.Space, "Space"),
    };

    public static IReadOnlyList<StandardiseChoice<DatePlacement>> DatePlacementChoices { get; } = new[]
    {
        new StandardiseChoice<DatePlacement>(DatePlacement.Front, "At the front"),
        new StandardiseChoice<DatePlacement>(DatePlacement.Back, "At the end"),
        new StandardiseChoice<DatePlacement>(DatePlacement.None, "No date"),
    };

    public static IReadOnlyList<StandardiseChoice<DateSource>> DateSourceChoices { get; } = new[]
    {
        new StandardiseChoice<DateSource>(DateSource.Typed, "This date"),
        new StandardiseChoice<DateSource>(DateSource.InName, "The date already in the name"),
        new StandardiseChoice<DateSource>(DateSource.Modified, "The file's modified date"),
    };

    public StandardiseNamesViewModel(IWorkScheduler? scheduler = null)
    {
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _dateText = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        RenameCommand = new AsyncRelayCommand(RenameAsync,
            () => PendingCount > 0 && !IsBusy && !IsPreviewing && IsDateValid);
        UndoCommand = new AsyncRelayCommand(UndoLastRenameAsync, () => _lastOutcomes.Count > 0 && !IsBusy);
        ClearCommand = new RelayCommand(ClearList, () => Results.Count > 0 && !IsBusy);
        ResetSegmentsCommand = new RelayCommand(ResetSegments, () => Results.Count > 0 && !IsBusy);
        // AsyncRelayCommand routes a faulted run here and would otherwise
        // swallow it (FireAndForgetGuardTests); Execute and Revert are
        // per-file fail-soft, so anything reaching these is unexpected
        RenameCommand.OnError += ex => Status = $"Renaming stopped unexpectedly: {ex.Message}";
        UndoCommand.OnError += ex => Status = $"Undo stopped unexpectedly: {ex.Message}";
        Results.CollectionChanged += (_, _) =>
        {
            ClearCommand.RaiseCanExecuteChanged();
            ResetSegmentsCommand.RaiseCanExecuteChanged();
        };
        RebuildChips();
    }

    // ------------------------------------------------------------ controls

    private NameCase _case = NameCase.Upper;
    public NameCase Case
    {
        get => _case;
        set { if (Set(ref _case, value)) RequestPreview(); }
    }

    private SegmentJoin _separator = SegmentJoin.Dash;
    public SegmentJoin Separator
    {
        get => _separator;
        set { if (Set(ref _separator, value)) RequestPreview(); }
    }

    private DatePlacement _datePlacement = DatePlacement.Front;
    public DatePlacement DatePlacement
    {
        get => _datePlacement;
        set
        {
            if (!Set(ref _datePlacement, value)) return;
            Raise(nameof(UsesDate));
            Raise(nameof(UsesTypedDate));
            RaiseDateState();
            // a date at the end is read off the name, so the words renumber
            RebuildChips();
            RequestPreview();
        }
    }

    private DateSource _dateSource = DateSource.Typed;
    public DateSource DateSource
    {
        get => _dateSource;
        set
        {
            if (!Set(ref _dateSource, value)) return;
            Raise(nameof(UsesTypedDate));
            RequestPreview();
        }
    }

    /// <summary>The date controls apply only while a date is placed.</summary>
    public bool UsesDate => DatePlacement != DatePlacement.None;

    /// <summary>The date box matters for "This date", and as the fallback
    /// for a name with no date of its own or an unreadable modified date.</summary>
    public bool UsesTypedDate => UsesDate;

    private string _dateText;
    /// <summary>YYYYMMDD; today when the window opens.</summary>
    public string DateText
    {
        get => _dateText;
        set
        {
            if (!Set(ref _dateText, value ?? "")) return;
            RaiseDateState();
            if (IsDateValid) RequestPreview();
        }
    }

    /// <summary>With no date placed, the date box isn't used, so it can't
    /// hold Rename back.</summary>
    public bool IsDateValid => !UsesDate || IsRealDate(DateText.Trim());

    public string DateError => IsDateValid ? "" : "Type the date as YYYYMMDD, for example 20260929.";

    public bool HasDateError => !IsDateValid;

    private void RaiseDateState()
    {
        Raise(nameof(IsDateValid));
        Raise(nameof(DateError));
        Raise(nameof(HasDateError));
        RenameCommand.RaiseCanExecuteChanged();
    }

    private StandardiseOptions Options() =>
        new(DateText.Trim(), Case, Separator, DatePlacement, DateSource);

    // ------------------------------------------------------------ state

    private bool _isBusy;
    /// <summary>A rename or undo is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(IsIdle));
            RenameCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
            ResetSegmentsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !IsBusy;

    private bool _isPreviewing;
    /// <summary>A newer preview is being worked out: the grid is about to
    /// change, so Rename waits for it.</summary>
    public bool IsPreviewing
    {
        get => _isPreviewing;
        private set
        {
            if (Set(ref _isPreviewing, value)) RenameCommand.RaiseCanExecuteChanged();
        }
    }

    private int _pendingCount;
    /// <summary>How many listed files Rename would change.</summary>
    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (!Set(ref _pendingCount, value)) return;
            Raise(nameof(RenameButtonText));
            RenameCommand.RaiseCanExecuteChanged();
        }
    }

    public string RenameButtonText =>
        PendingCount > 0 ? $"Rename {PendingCount} file{(PendingCount == 1 ? "" : "s")}" : "Rename";

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    private string _segmentBarCaption = "";
    public string SegmentBarCaption { get => _segmentBarCaption; private set => Set(ref _segmentBarCaption, value); }

    private IReadOnlyList<StandardiseNameRow> _selectedRows = Array.Empty<StandardiseNameRow>();
    /// <summary>The grid's selection (set by the window: DataGrid.SelectedItems
    /// is not bindable). The segment chips show the first selected file's
    /// words and change every selected file; with nothing selected, the
    /// first file's words, changing every file (as Bulk rename's chips).</summary>
    public IReadOnlyList<StandardiseNameRow> SelectedRows
    {
        get => _selectedRows;
        set
        {
            _selectedRows = value ?? Array.Empty<StandardiseNameRow>();
            RebuildChips();
        }
    }

    public AsyncRelayCommand RenameCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ResetSegmentsCommand { get; }

    internal void ExplainCloseWasRefused() =>
        Status = "Still renaming — please wait for this batch to finish before closing.";

    // ------------------------------------------------------------ adding

    /// <summary>Adds files to the list (a drop or Add files…) and shows the
    /// name each would get. Renames nothing.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy)
        {
            // the drop target is the whole window, so a drop still lands here mid-batch
            Status = "Still renaming — drop again when this batch finishes.";
            return;
        }
        var existing = Results.Select(r => r.CurrentPath).ToList();
        List<(string Path, DateTime? Modified)> added;
        Intake.Added intake;
        try
        {
            // existence checks and modified dates are network round trips on a share
            (intake, added) = await _scheduler.Run(() =>
            {
                var result = Intake.Add(existing, paths, exists: File.Exists);
                var files = result.Files.Select(path => (path, ReadModified(path))).ToList();
                return (result, files);
            });
        }
        catch (Exception ex)
        {
            // the window discards this task: say what went wrong rather than
            // letting it vanish (FireAndForgetGuardTests)
            AddNote = $"Couldn't read what was dropped: {ex.Message}";
            return;
        }
        AddNote = intake.Note("file");
        foreach (var (path, modified) in added)
        {
            // two drops close together each checked against the list as it
            // was before either landed
            if (Results.Any(r => PathIdentity.PathComparer.Instance.Equals(r.CurrentPath, path))) continue;
            Results.Add(new StandardiseNameRow(Path.GetFileName(path), "", path, StandardiseRowStatus.Pending)
            {
                Modified = modified,
            });
        }
        RebuildChips();
        await PreviewAsync();
    }

    private static DateTime? ReadModified(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (Exception) { return null; }
    }

    private void ClearList()
    {
        if (IsBusy) return;
        // a preview still in flight is for rows no longer listed
        _previewGeneration++;
        IsPreviewing = false;
        ForgetShownPlan();
        Results.Clear();
        _selectedRows = Array.Empty<StandardiseNameRow>();
        PendingCount = 0;
        AddNote = "";
        Status = "";
        RebuildChips();
    }

    // ------------------------------------------------------------ preview

    private void RequestPreview() => _ = PreviewAsync();

    /// <summary>Plans every listed file's new name and shows it. A newer
    /// preview makes this one's result stale; a preview while a rename runs
    /// waits for the rename's own refresh.</summary>
    internal async Task PreviewAsync()
    {
        if (IsBusy) return;
        if (Results.Count == 0 || !IsDateValid)
        {
            // nothing to show, or no date to show it with: Rename is held
            // back by PendingCount or IsDateValid, and a preview in flight
            // is for settings no longer current
            _previewGeneration++;
            IsPreviewing = false;
            if (Results.Count == 0) ForgetShownPlan();
            return;
        }
        var generation = ++_previewGeneration;
        IsPreviewing = true;
        var rows = Results.ToList();
        var options = Options();
        var request = PlanInputs(rows);
        List<PlannedRename> plans;
        try
        {
            plans = await _scheduler.Run(() => Standardise.Plan(request.Paths, options, request.Dropped, request.Modified));
        }
        catch (Exception ex)
        {
            if (generation != _previewGeneration) return;
            IsPreviewing = false;
            ForgetShownPlan();
            Status = $"Couldn't work out the new names: {ex.Message}";
            return;
        }
        if (generation != _previewGeneration || IsBusy) return;
        var pending = 0;
        for (var i = 0; i < rows.Count && i < plans.Count; i++)
        {
            ShowPlan(rows[i], plans[i]);
            if (rows[i].Status == StandardiseRowStatus.Pending) pending++;
        }
        _shownRows = rows;
        _shownPlans = plans;
        _shownSeparator = options.Separator;
        PendingCount = pending;
        IsPreviewing = false;
    }

    private void ForgetShownPlan()
    {
        _shownRows = new();
        _shownPlans = new();
        PendingCount = 0;
    }

    private sealed record PlanRequest(
        List<string> Paths,
        Dictionary<string, IReadOnlySet<int>> Dropped,
        Dictionary<string, DateTime> Modified);

    /// <summary>Copies of what the plan needs, taken on the UI thread: the
    /// rows' own sets change as chips are clicked.</summary>
    private static PlanRequest PlanInputs(List<StandardiseNameRow> rows)
    {
        var paths = rows.Select(r => r.CurrentPath).ToList();
        var dropped = new Dictionary<string, IReadOnlySet<int>>(PathIdentity.PathComparer.Instance);
        var modified = new Dictionary<string, DateTime>(PathIdentity.PathComparer.Instance);
        foreach (var row in rows)
        {
            dropped[row.CurrentPath] = new HashSet<int>(row.Dropped);
            if (row.Modified is { } when) modified[row.CurrentPath] = when;
        }
        return new PlanRequest(paths, dropped, modified);
    }

    private static void ShowPlan(StandardiseNameRow row, PlannedRename plan)
    {
        if (plan.Changed)
        {
            row.Status = StandardiseRowStatus.Pending;
            row.Result = plan.Note.Length > 0
                ? $"{Path.GetFileName(plan.Target)}  ({plan.Note})"
                : Path.GetFileName(plan.Target);
        }
        else if (plan.Note.Length > 0)
        {
            row.Status = StandardiseRowStatus.Skipped;
            row.Result = "Skipped: " + plan.Note;
        }
        else
        {
            row.Status = StandardiseRowStatus.Unchanged;
            row.Result = "Already standardised";
        }
    }

    // ------------------------------------------------------------ segments

    /// <summary>The files a chip click changes: the selection, or every
    /// file when nothing is selected.</summary>
    private IReadOnlyList<StandardiseNameRow> ChipTargets() =>
        _selectedRows.Count > 0 ? _selectedRows : Results.ToList();

    private void RebuildChips()
    {
        SegmentChips.Clear();
        if (Results.Count == 0)
        {
            SegmentBarCaption = "Add files to see the words in their names.";
            return;
        }
        var targets = ChipTargets();
        var shown = targets[0];
        var words = Standardise.Read(Path.GetFileNameWithoutExtension(shown.CurrentPath),
            dateAtEnd: DatePlacement == DatePlacement.Back).Words;
        for (var i = 0; i < words.Count; i++)
            SegmentChips.Add(new SegmentChip(i + 1, words[i], !shown.Dropped.Contains(i + 1), SetWordKept));
        var reach = targets.Count == 1
            ? "this file"
            : _selectedRows.Count > 0 ? $"{targets.Count} selected files" : $"all {targets.Count} files";
        SegmentBarCaption = words.Count == 0
            ? $"{shown.Current} has no words to drop."
            : $"Words in {shown.Current} · click one to drop it from {reach}";
    }

    /// <summary>A chip clicked: the word at <paramref name="position"/> is
    /// kept or dropped in every file the chips reach.</summary>
    private void SetWordKept(int position, bool kept)
    {
        if (IsBusy) return;
        foreach (var row in ChipTargets())
        {
            if (kept) row.Dropped.Remove(position);
            else row.Dropped.Add(position);
        }
        RequestPreview();
    }

    private void ResetSegments()
    {
        if (IsBusy) return;
        foreach (var row in ChipTargets()) row.Dropped.Clear();
        RebuildChips();
        RequestPreview();
    }

    // ------------------------------------------------------------ rename, undo

    private async Task RenameAsync()
    {
        if (IsBusy || IsPreviewing || !IsDateValid || _shownPlans.Count == 0) return;
        var rows = _shownRows;
        var plans = _shownPlans;
        var counterStyle = Standardise.CounterStyle(_shownSeparator);
        // a preview that lands after this must not repaint the renamed rows
        _previewGeneration++;
        IsBusy = true;
        try
        {
            // Execute re-checks the disk at the last instant and moves to a
            // counter if a shown name was taken meanwhile
            var outcomes = await _scheduler.Run(() => Execute(plans, counterStyle));
            var bySource = outcomes.ToDictionary(o => o.Source, PathIdentity.PathComparer.Instance);
            var dropped = new Dictionary<string, HashSet<int>>(PathIdentity.PathComparer.Instance);
            int renamed = 0, failed = 0;
            for (var i = 0; i < rows.Count && i < plans.Count; i++)
            {
                var row = rows[i];
                if (!bySource.TryGetValue(plans[i].Source, out var outcome)) continue;
                if (outcome.Final is { } final)
                {
                    dropped[final] = new HashSet<int>(row.Dropped);
                    row.CurrentPath = final;
                    row.Current = Path.GetFileName(final);
                    row.Result = "Renamed";
                    row.Status = StandardiseRowStatus.Renamed;
                    // the words are renumbered from the new name
                    row.Dropped.Clear();
                    renamed++;
                }
                else
                {
                    row.Result = "Couldn't rename: " + outcome.Error;
                    row.Status = StandardiseRowStatus.Failed;
                    failed++;
                }
            }
            // Only a rename that moved something replaces the undo record: one
            // that moved nothing (every file in use) must not wipe the record
            // of the rename before it, whose files are still under their new
            // names (the rule Bulk rename follows, Q2-01).
            var moved = outcomes.Where(o => o.Final is not null).ToList();
            if (moved.Count > 0)
            {
                _lastOutcomes = moved;
                _lastDropped = dropped;
            }
            ForgetShownPlan();
            Status = $"Renamed {renamed} file{(renamed == 1 ? "" : "s")}" +
                     (failed > 0 ? $" · {failed} couldn't be renamed" : "") + ".";
        }
        finally
        {
            IsBusy = false;
        }
        RebuildChips();
    }

    private async Task UndoLastRenameAsync()
    {
        if (IsBusy || _lastOutcomes.Count == 0) return;
        var outcomes = _lastOutcomes;
        var dropped = _lastDropped;
        _previewGeneration++;
        IsBusy = true;
        List<RevertOutcome> reverted;
        try
        {
            reverted = await _scheduler.Run(() => RevertEach(outcomes));
            foreach (var result in reverted.Where(r => r.Restored))
            {
                var final = result.Outcome.Final!;
                // by path, not the row objects renamed: the list may have
                // been cleared and the files added again since
                foreach (var row in Results.Where(r => PathIdentity.PathComparer.Instance.Equals(r.CurrentPath, final)))
                {
                    row.CurrentPath = result.Outcome.Source;
                    row.Current = Path.GetFileName(result.Outcome.Source);
                    row.Dropped.Clear();
                    if (dropped.TryGetValue(final, out var words)) row.Dropped.UnionWith(words);
                }
            }
            // A file that could not be put back is still under its new name,
            // so it stays in the record and Undo can be pressed again once
            // whatever was in the way is gone. RevertEach answers newest
            // first; the record is kept oldest first, as Rename wrote it.
            var stillRenamed = reverted.Where(r => !r.Restored).Select(r => r.Outcome).Reverse().ToList();
            var stillDropped = new Dictionary<string, HashSet<int>>(PathIdentity.PathComparer.Instance);
            foreach (var outcome in stillRenamed)
                if (dropped.TryGetValue(outcome.Final!, out var words)) stillDropped[outcome.Final!] = words;
            _lastOutcomes = stillRenamed;
            _lastDropped = stillDropped;
            var problems = reverted.Where(r => !r.Restored).Select(r => r.Problem).ToList();
            Status = problems.Count == 0
                ? $"Undid the last rename ({outcomes.Count} file{(outcomes.Count == 1 ? "" : "s")})."
                : "Undo finished with problems: " + string.Join("; ", problems);
        }
        finally
        {
            IsBusy = false;
        }
        RebuildChips();
        await PreviewAsync();
    }
}
