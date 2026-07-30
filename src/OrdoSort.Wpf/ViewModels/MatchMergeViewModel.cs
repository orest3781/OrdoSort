using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

public sealed record MatchRow(string Source, string File, string Becomes, string Note,
    string Status);

/// <summary>One roster header in the column picker.</summary>
public sealed class HeaderPick : ObservableObject
{
    private readonly Action _changed;
    public HeaderPick(string name, bool chosen, Action changed)
    {
        Name = name;
        _isChosen = chosen;
        _changed = changed;
    }
    public string Name { get; }
    private bool _isChosen;
    public bool IsChosen
    {
        get => _isChosen;
        set { if (Set(ref _isChosen, value)) _changed(); }
    }
}

/// <summary>Match &amp; merge: load a roster CSV, map its headers, drop PDFs in,
/// merge each person's Control ID into the filename. Unambiguous matches merge
/// in one click; ambiguous or suggested ones go to Review matches. Header
/// mapping is auto-guessed, remembered in config, and restored next time.</summary>
public sealed class MatchMergeViewModel : ObservableObject
{
    private readonly Config _cfg;
    private readonly Action<Dictionary<string, string>> _saveHeaders;
    private readonly IDialogService _dialogs;
    private readonly Action? _saveCfg;

    private readonly List<string> _files = new();
    private MatchMerge.Roster? _roster;
    private List<MatchMerge.MatchResult> _results = new();
    private List<BulkRename.RenameOutcome> _outcomes = new();
    private bool _fillingHeaders;

    public ObservableCollection<string> Headers { get; } = new();
    public ObservableCollection<MatchRow> Rows { get; } = new();

    public MatchMergeViewModel(Config cfg, Action<Dictionary<string, string>> saveHeaders,
        IDialogService dialogs, Action? saveCfg = null)
    {
        _cfg = cfg;
        _saveHeaders = saveHeaders;
        _dialogs = dialogs;
        _saveCfg = saveCfg;
        LoadRosterCommand = new RelayCommand(BrowseRoster);
        MergeCommand = new RelayCommand(DoMerge, () => MergeCount > 0);
        UndoCommand = new RelayCommand(UndoBatch, () => _outcomes.Count > 0);
        ClearCommand = new RelayCommand(() => { _files.Clear(); Refresh(); });
    }

    private string _rosterPath = "";
    public string RosterPath { get => _rosterPath; private set => Set(ref _rosterPath, value); }

    private string? _firstHeader;
    public string? FirstHeader { get => _firstHeader; set { if (Set(ref _firstHeader, value)) ReloadRoster(); } }

    private string? _lastHeader;
    public string? LastHeader { get => _lastHeader; set { if (Set(ref _lastHeader, value)) ReloadRoster(); } }

    private string? _controlHeader;
    public string? ControlHeader { get => _controlHeader; set { if (Set(ref _controlHeader, value)) ReloadRoster(); } }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The header-mapping row only means something once a roster is
    /// loaded; before that the combos are empty noise.</summary>
    private bool _hasRoster;
    public bool HasRoster { get => _hasRoster; private set => Set(ref _hasRoster, value); }

    /// <summary>Feedback for the last add/drop.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>"3 ready to merge · 2 to review · 1 already merged · 4 no match".</summary>
    private string _bucketsLine = "";
    public string BucketsLine { get => _bucketsLine; private set => Set(ref _bucketsLine, value); }

    public int MergeCount { get; private set; }

    /// <summary>Ambiguous + suggested: everything that needs a human's eye.</summary>
    public int ReviewCount { get; private set; }
    public string MergeButtonText => MergeCount > 0 ? $"Merge {MergeCount} matched" : "Merge";
    public string ReviewButtonText => ReviewCount > 0
        ? $"Review {ReviewCount} match{(ReviewCount == 1 ? "" : "es")}…"
        : "Review matches…";
    public bool CanReview => ReviewCount > 0;

    public RelayCommand LoadRosterCommand { get; }
    public RelayCommand MergeCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>For the Review matches window: ambiguous and suggested files,
    /// in file order, plus the roster columns.</summary>
    public List<MatchMerge.MatchResult> ReviewItems =>
        _results.Where(r => r.Status is "ambiguous" or "suggested").ToList();
    public IReadOnlyList<string> RosterHeaders => _roster?.Headers ?? Array.Empty<string>();

    public ObservableCollection<HeaderPick> ColumnPicks { get; } = new();

    /// <summary>The headers Review matches shows. Picked columns come back in
    /// roster order (ColumnPicks is built by walking Headers); the fallback —
    /// nothing picked — returns the mapped name and id columns in MAPPED
    /// order (Last, First, Control), not roster order. Either way the result
    /// is de-duplicated: a roster with a repeated column name must never hand
    /// Review matches (and its per-row dictionary) a duplicate header.</summary>
    public IReadOnlyList<string> ChosenColumns
    {
        get
        {
            var picked = ColumnPicks.Where(p => p.IsChosen).Select(p => p.Name).Distinct().ToList();
            if (picked.Count > 0) return picked;
            return new[] { LastHeader, FirstHeader, ControlHeader }
                .Where(h => h is not null).Cast<string>().Distinct().ToList();
        }
    }

    private void RebuildColumnPicks()
    {
        // no saved choice = the mapped name and id columns come pre-ticked, so
        // the picker always shows the truth of what Review matches will show —
        // and the first extra tick ADDS to them rather than replacing them.
        // Distinct: a roster with a repeated column name must get one pick
        // per NAME, not one per column.
        ColumnPicks.Clear();
        foreach (var h in Headers.Distinct())
        {
            var chosen = _cfg.MergeColumns.Count == 0
                ? h == LastHeader || h == FirstHeader || h == ControlHeader
                : _cfg.MergeColumns.Contains(h);
            ColumnPicks.Add(new HeaderPick(h, chosen, OnColumnPicked));
        }
    }

    private void OnColumnPicked()
    {
        _cfg.MergeColumns = ColumnPicks.Where(p => p.IsChosen).Select(p => p.Name).ToList();
        _saveCfg?.Invoke();
    }

    private void BrowseRoster()
    {
        var path = _dialogs.AskOpenFile(
            "Spreadsheets (*.csv;*.xlsx)|*.csv;*.xlsx|All files (*.*)|*.*");
        if (path is null) return;
        LoadRosterFrom(path);
    }

    public void LoadRosterFrom(string path)
    {
        List<string> headers;
        try
        {
            headers = MatchMerge.ReadHeaders(path);
        }
        catch (RosterException ex)
        {
            Status = ex.Message;
            return;
        }
        // only commit to the new path once the read actually succeeded — a
        // failed browse (file moved, bad format) must leave the good roster,
        // its path box, and the auto-load guard all untouched
        RosterPath = path;

        _fillingHeaders = true;
        HasRoster = true;   // headers are in — show the mapping row
        Headers.Clear();
        foreach (var h in headers) Headers.Add(h);
        string Pick(string key, params string[] needles)
        {
            var saved = _cfg.MergeHeaders.TryGetValue(key, out var s) && headers.Contains(s) ? s : null;
            return saved
                ?? headers.FirstOrDefault(h => needles.Any(n => h.ToLowerInvariant().Contains(n)))
                ?? headers.FirstOrDefault() ?? "";
        }
        _firstHeader = Pick("first", "first");
        _lastHeader = Pick("last", "last");
        _controlHeader = Pick("control", "control", "id");
        Raise(nameof(FirstHeader));
        Raise(nameof(LastHeader));
        Raise(nameof(ControlHeader));
        _fillingHeaders = false;
        ReloadRoster();
        RebuildColumnPicks();
    }

    private void ReloadRoster()
    {
        if (_fillingHeaders || RosterPath.Length == 0
            || FirstHeader is null || LastHeader is null || ControlHeader is null) return;
        try
        {
            _roster = MatchMerge.LoadRoster(RosterPath, FirstHeader, LastHeader, ControlHeader);
        }
        catch (RosterException ex)
        {
            _roster = null;
            HasRoster = false;
            Status = ex.Message;
            Refresh();
            return;
        }
        HasRoster = true;
        _saveHeaders(new Dictionary<string, string>
        {
            ["first"] = FirstHeader, ["last"] = LastHeader, ["control"] = ControlHeader,
        });
        if (_cfg.MergeRoster != RosterPath)
        {
            _cfg.MergeRoster = RosterPath;
            _saveCfg?.Invoke();
        }
        Status = $"Roster loaded: {_roster.People.Count} people.";
        Refresh();
    }

    /// <summary>Load the roster used last time, if one is remembered and still
    /// on disk. The existence check runs off-thread — the path is usually a
    /// network share, and the window is opening right now.</summary>
    public async Task AutoLoadRosterAsync()
    {
        var remembered = _cfg.MergeRoster;
        if (remembered.Length == 0 || RosterPath.Length > 0) return;
        var exists = await Task.Run(() => File.Exists(remembered));
        if (RosterPath.Length > 0) return;   // the user got there first
        if (exists) LoadRosterFrom(remembered);
        else Status = $"Last roster wasn't found: {remembered}";
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        int added = 0, ignored = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p) && p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                && !_files.Contains(p))
            {
                _files.Add(p);
                added++;
            }
            else
            {
                ignored++;
            }
        }
        AddNote = added == 0 && ignored > 0
            ? $"nothing added — {ignored} item{(ignored == 1 ? " isn't a PDF" : "s aren't PDFs")} (or already listed)"
            : ignored > 0
                ? $"{added} added · {ignored} ignored (not PDFs, or already listed)"
                : "";
        Refresh();
    }

    public void RemoveFiles(IEnumerable<string> sources)
    {
        foreach (var s in sources.ToList()) _files.Remove(s);
        AddNote = "";
        Refresh();
    }

    private void Refresh()
    {
        _results = _roster is null ? new() : MatchMerge.MatchFiles(_files, _roster);
        Rows.Clear();
        int merges = 0, review = 0, suggested = 0;
        var display = _roster is null
            ? _files.Select(f => new MatchMerge.MatchResult(f, "no_roster")).ToList()
            : _results;
        int already = 0, noMatch = 0, noName = 0;
        foreach (var r in display)
        {
            string becomes = "", note = "";
            switch (r.Status)
            {
                case "merge": becomes = r.NewStem + Path.GetExtension(r.Source); merges++; break;
                case "ambiguous":
                    note = $"{r.Candidates!.Count} candidates — decide in Review matches";
                    review++; break;
                case "suggested":
                    note = $"{r.Suggestions!.Count} suggested — confirm in Review matches";
                    suggested++; review++; break;
                case "already": note = "already has the id"; already++; break;
                case "no_match": note = "no roster match"; noMatch++; break;
                case "no_name": note = "no name found in the filename"; noName++; break;
                case "no_roster": note = "load a roster first"; break;
            }
            Rows.Add(new MatchRow(r.Source, Path.GetFileName(r.Source), becomes, note, r.Status));
        }
        MergeCount = merges;
        ReviewCount = review;

        var parts = new List<string>();
        if (merges > 0) parts.Add($"{merges} ready to merge");
        if (suggested > 0) parts.Add($"{suggested} suggested");
        if (review > 0) parts.Add($"{review} to review");
        if (already > 0) parts.Add($"{already} already merged");
        if (noMatch > 0) parts.Add($"{noMatch} no match");
        if (noName > 0) parts.Add($"{noName} no name in the filename");
        BucketsLine = _roster is null || _files.Count == 0 ? "" : string.Join(" · ", parts);

        Raise(nameof(MergeButtonText));
        Raise(nameof(ReviewButtonText));
        Raise(nameof(CanReview));
        MergeCommand.RaiseCanExecuteChanged();
    }

    private void DoMerge() => Absorb(MatchMerge.ExecuteMerges(_results));

    /// <summary>Adopt a batch of rename outcomes (one-click merges or review
    /// picks): follow renamed files, extend the undo batch, re-match.</summary>
    public void Absorb(List<BulkRename.RenameOutcome> outcomes)
    {
        var renamed = outcomes.Where(o => o.Final != null).ToList();
        var finals = renamed.ToDictionary(o => o.Source, o => o.Final!);
        for (var i = 0; i < _files.Count; i++)
            if (finals.TryGetValue(_files[i], out var f)) _files[i] = f;
        _outcomes.AddRange(renamed);
        UndoCommand.RaiseCanExecuteChanged();
        Refresh();
        var failed = outcomes.Count(o => o.Final == null);
        if (failed > 0) Status = $"Merged {renamed.Count}; {failed} failed.";
        else if (renamed.Count > 0)
            Status = $"Merged {renamed.Count} file{(renamed.Count == 1 ? "" : "s")}.";
    }

    private void UndoBatch()
    {
        var problems = BulkRename.Revert(_outcomes);
        var restored = _outcomes.Where(o => o.Final != null)
            .ToDictionary(o => o.Final!, o => o.Source);
        for (var i = 0; i < _files.Count; i++)
            if (restored.TryGetValue(_files[i], out var s)) _files[i] = s;
        _outcomes = new List<BulkRename.RenameOutcome>();
        UndoCommand.RaiseCanExecuteChanged();
        Refresh();
        Status = problems.Count > 0 ? string.Join("; ", problems) : "Original names restored.";
    }
}
