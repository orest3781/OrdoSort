using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

public sealed record MatchRow(string Source, string File, string Becomes, string Note,
    string Status);

/// <summary>One roster header in the column picker.</summary>
/// <summary>Match &amp; merge: load a roster CSV, map its headers, drop PDFs in,
/// merge each person's Control ID into the filename. Unambiguous matches merge
/// in one click; ambiguous or suggested ones go to Review matches. Header
/// mapping is auto-guessed, remembered in config, and restored next time.</summary>
public sealed class MatchMergeViewModel : ObservableObject
{
    // Splits "FirstName"/"ControlID" into "First"/"Name" and "Control"/"ID":
    // a boundary before an uppercase letter that follows a lowercase/digit,
    // or before the last uppercase letter of a run that's followed by a
    // lowercase one (so "IDNumber" splits "ID"/"Number", not "I"/"D"/"Number").
    private static readonly Regex CamelCaseBoundary = new(
        @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    private readonly Config _cfg;
    private readonly Action<Dictionary<string, string>> _saveHeaders;
    private readonly IDialogService _dialogs;
    private readonly Action? _saveCfg;
    private readonly IWorkScheduler _scheduler;
    private readonly Func<string, List<List<string>>> _readRoster;
    private readonly Func<IEnumerable<string>, MatchMerge.Roster, List<MatchMerge.MatchResult>> _matchFiles;

    /// <summary>Extension set in Intake's shape (dot-less, lowercase) rather
    /// than the EndsWith(".pdf") this used to inline — same rule, one place.</summary>
    private static readonly ISet<string> Pdfs = new HashSet<string> { "pdf" };

    private readonly List<string> _files = new();
    private MatchMerge.Roster? _roster;
    private List<MatchMerge.MatchResult> _results = new();
    private List<BulkRename.RenameOutcome> _outcomes = new();
    private bool _fillingHeaders;

    /// <summary>Sources whose last DoMerge attempt was rejected by
    /// Naming.RejectIllegal (Execute silently drops those plans — see
    /// DoMerge), keyed by source and valued by the NewStem that was
    /// rejected plus why. A row stays flagged only while its CURRENT
    /// proposed NewStem still matches what was rejected — a roster
    /// reload/re-match that retargets the row to a different candidate is
    /// never shown a stale rejection (checked in Refresh).</summary>
    private readonly Dictionary<string, (string NewStem, string Note)> _mergeRejectNotes = new();

    public ObservableCollection<string> Headers { get; } = new();
    public ObservableCollection<MatchRow> Rows { get; } = new();

    public MatchMergeViewModel(Config cfg, Action<Dictionary<string, string>> saveHeaders,
        IDialogService dialogs, Action? saveCfg = null, IWorkScheduler? scheduler = null,
        Func<string, List<List<string>>>? readRoster = null,
        Func<IEnumerable<string>, MatchMerge.Roster, List<MatchMerge.MatchResult>>? matchFiles = null)
    {
        _readRoster = readRoster ?? MatchMerge.ReadRosterTable;
        _matchFiles = matchFiles ?? MatchMerge.MatchFiles;
        _cfg = cfg;
        _saveHeaders = saveHeaders;
        _dialogs = dialogs;
        _saveCfg = saveCfg;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        LoadRosterCommand = new RelayCommand(BrowseRoster, () => !IsBusy);
        MergeCommand = new AsyncRelayCommand(DoMergeAsync, () => MergeCount > 0 && !IsBusy);
        UndoCommand = new AsyncRelayCommand(UndoBatchAsync, () => _outcomes.Count > 0 && !IsBusy);
        ClearCommand = new RelayCommand(() => { _clears++; _files.Clear(); _mergeRejectNotes.Clear(); Refresh(); }, () => !IsBusy);
        // A run that stops on something unexpected must not leave its last
        // "Merging 3 of 12…" line up as if it were still working.
        MergeCommand.OnError += ex => Status = $"The merge stopped unexpectedly: {ex.Message}";
        UndoCommand.OnError += ex => Status = $"Undo stopped unexpectedly: {ex.Message}";
    }

    private bool _isBusy;

    /// <summary>True while a merge or an undo is renaming files. Every action
    /// that touches the list waits for it: the renames run off the UI thread
    /// now, so the window stays usable (Q2-02).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanReview));
            LoadRosterCommand.RaiseCanExecuteChanged();
            MergeCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    private string _rosterPath = "";
    public string RosterPath { get => _rosterPath; private set => Set(ref _rosterPath, value); }

    private string? _firstHeader;
    public string? FirstHeader
    {
        get => _firstHeader;
        set { if (Set(ref _firstHeader, value)) ReloadRoster(); }
    }

    private string? _lastHeader;
    public string? LastHeader
    {
        get => _lastHeader;
        set { if (Set(ref _lastHeader, value)) ReloadRoster(); }
    }

    private string? _controlHeader;
    public string? ControlHeader
    {
        get => _controlHeader;
        set { if (Set(ref _controlHeader, value)) ReloadRoster(); }
    }

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
    public bool CanReview => ReviewCount > 0 && !IsBusy;

    public RelayCommand LoadRosterCommand { get; }
    public AsyncRelayCommand MergeCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>For the Review matches window: ambiguous and suggested files,
    /// in file order, plus the roster columns.</summary>
    public List<MatchMerge.MatchResult> ReviewItems =>
        _results.Where(r => r.Status is "ambiguous" or "suggested").ToList();
    public IReadOnlyList<string> RosterHeaders => _roster?.Headers ?? Array.Empty<string>();

    /// <summary>Every column Review matches can show: the spreadsheet's
    /// headers, once each, in spreadsheet order.</summary>
    public IReadOnlyList<string> ReviewColumnHeaders => Headers.Distinct().ToList();

    /// <summary>The columns mapped to Last, First and Control: Review matches
    /// locks them on, since they say who a row is.</summary>
    public IReadOnlyList<string> IdentityHeaders =>
        new[] { LastHeader, FirstHeader, ControlHeader }.Where(h => h is not null).Cast<string>().Distinct().ToList();

    /// <summary>Saves what Review matches has on show (its header menu or
    /// More columns…), for next time and for every station sharing the
    /// config.</summary>
    public void SetReviewColumns(IEnumerable<string> shown)
    {
        _cfg.MergeColumns = shown.Distinct().ToList();
        _saveCfg?.Invoke();
    }

    /// <summary>The headers Review matches shows. ALWAYS includes whichever
    /// headers are currently mapped to First/Last/Control — a saved pick
    /// list left over from a differently-headed roster can fail to mention
    /// this roster's mapped headers at all (its old spellings just aren't
    /// among this roster's columns), and Review matches (and its per-row
    /// identity) must never be handed a column set with no name/id field to
    /// file against. Any additionally-picked columns follow, in roster order
    /// (the saved list is walked in Headers order); with nothing extra picked,
    /// the identity headers alone come back in MAPPED order (Last, First,
    /// Control). Either way the result is de-duplicated: a roster with a
    /// repeated column name must never hand Review matches (and its per-row
    /// dictionary) a duplicate header.</summary>
    public IReadOnlyList<string> ChosenColumns
    {
        get
        {
            var saved = new HashSet<string>(_cfg.MergeColumns);
            var picked = Headers.Distinct().Where(saved.Contains);
            return IdentityHeaders.Concat(picked).Distinct().ToList();
        }
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
        List<List<string>> table;
        List<string> headers;
        try
        {
            table = _readRoster(path);
            headers = MatchMerge.HeadersOf(table);
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
        // Word/token matching, not a raw substring: "id" must not match
        // Paid Date, Resident or Video (each contains the letters "i","d"
        // adjacent, none of them AS a whole token), while First Name,
        // Given name, Surname, Last, MRN and Control ID must still be
        // recognised. Concatenated headers (FirstName, LastName, ControlID)
        // are split on camel/Pascal-case boundaries first, so they tokenize
        // the same as their spaced equivalents. '#' joins the usual
        // delimiters (Control#), and any Unicode whitespace — not just
        // ASCII space — separates words, so a non-breaking space copied out
        // of a spreadsheet still splits. A header with no case boundary and
        // no delimiter at all (plain lowercase "controlid") still tokenizes
        // to one word and, correctly, matches nothing — failing toward "ask
        // a human" is the safe direction, not a guess.
        static IEnumerable<string> Tokenize(string header)
        {
            var spaced = CamelCaseBoundary.Replace(header, " ");
            var sb = new StringBuilder(spaced.Length);
            foreach (var c in spaced)
                sb.Append(char.IsWhiteSpace(c) || c is '_' or '-' or '.' or '/' or '#' ? ' ' : c);
            return sb.ToString().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        // How well a header matches a role: (tokens that hit a needle, total
        // tokens in the header). "Control ID" is 2/2 — every token is
        // signal. "Guardian ID" is 1/2 — one token is signal, one is noise.
        // "First Visit Date" is 1/3 — even more noise. Ranking by this ratio
        // (not by position in the file) is what makes "Control ID" beat
        // "Guardian ID" and "First Name" beat "First Visit Date" regardless
        // of which one the roster happens to list first.
        static (int Matched, int Total) Score(string header, string[] needles)
        {
            var tokens = Tokenize(header).ToList();
            return (tokens.Count(needles.Contains), tokens.Count);
        }
        // Cross-multiplied rational comparison — a/b vs c/d without division
        // — so two headers with equal signal ratios compare exactly equal,
        // never off by floating-point rounding.
        static int CompareRatio((int Matched, int Total) a, (int Matched, int Total) b) =>
            (a.Matched * b.Total).CompareTo(b.Matched * a.Total);
        string? Pick(string key, params string[] needles)
        {
            var saved = _cfg.MergeHeaders.TryGetValue(key, out var s) && s.Length > 0 && headers.Contains(s)
                ? s : null;
            if (saved is not null) return saved;

            // No confident match means NO pick — never headers[0], and never
            // "whichever came first in the file". Every header that matches
            // at all is ranked by how much of it is signal; the single
            // best-ranked header wins. Two headers ranked EQUALLY well is
            // real ambiguity (e.g. both "MRN" and "Control ID" present), not
            // a coin flip to resolve silently — leave the role unmapped,
            // same as no match at all, so the picker asks a human instead.
            var candidates = headers
                .Select(h => (Header: h, Score: Score(h, needles)))
                .Where(c => c.Score.Matched > 0)
                .ToList();
            if (candidates.Count == 0) return null;
            var best = candidates[0].Score;
            foreach (var c in candidates)
                if (CompareRatio(c.Score, best) > 0) best = c.Score;
            var winners = candidates.Where(c => CompareRatio(c.Score, best) == 0).ToList();
            return winners.Count == 1 ? winners[0].Header : null;
        }
        _firstHeader = Pick("first", "first", "given");
        _lastHeader = Pick("last", "last", "surname");
        _controlHeader = Pick("control", "control", "id", "mrn");
        Raise(nameof(FirstHeader));
        Raise(nameof(LastHeader));
        Raise(nameof(ControlHeader));
        _fillingHeaders = false;
        // The table just read, not a second read of the file (DW-55).
        ReloadRoster(table);
    }

    /// <summary>Joins role names the way a sentence would: "Control",
    /// "First and Control", "First, Last and Control".</summary>
    private static string RoleList(IReadOnlyList<string> roles) => roles.Count switch
    {
        1 => roles[0],
        2 => $"{roles[0]} and {roles[1]}",
        _ => string.Join(", ", roles.Take(roles.Count - 1)) + " and " + roles[^1],
    };

    /// <summary>Re-matches against the roster file under the current column
    /// picks. <paramref name="table"/> is a read just made by the caller;
    /// without one (a column pick changed) the file is read afresh, so the
    /// pick sees the spreadsheet as it is now.</summary>
    private void ReloadRoster(List<List<string>>? table = null)
    {
        if (_fillingHeaders || RosterPath.Length == 0) return;

        // Every role must be picked, AND the three picks must be distinct —
        // neither condition is optional. Whichever fails, the mapping row
        // (Headers is already populated) stays up so the user can fix it
        // from the picker; nothing gets loaded, and nothing claims success.
        var picks = new (string Role, string? Header)[]
        {
            ("First", FirstHeader), ("Last", LastHeader), ("Control", ControlHeader),
        };
        var unmapped = picks.Where(p => p.Header is null).Select(p => p.Role).ToList();
        var collisions = picks.Where(p => p.Header is not null)
            .GroupBy(p => p.Header)
            .Where(g => g.Count() > 1)
            .ToList();
        if (unmapped.Count > 0 || collisions.Count > 0)
        {
            _roster = null;
            HasRoster = true;
            Status = collisions.Count > 0
                ? "Ambiguous roster headers — " + string.Join("; ", collisions.Select(g =>
                    $"{RoleList(g.Select(p => p.Role).ToList())} {(g.Count() == 2 ? "both" : "all")} " +
                    $"matched \"{g.Key}\""))
                  + ". Choose different columns for First, Last and Control above."
                : $"Couldn't guess {RoleList(unmapped)} from the roster headers — " +
                  $"choose {(unmapped.Count == 1 ? "it" : "them")} above.";
            Refresh();
            return;
        }

        try
        {
            _roster = MatchMerge.LoadRoster(table ?? _readRoster(RosterPath), FirstHeader!, LastHeader!, ControlHeader!);
        }
        catch (RosterException ex)
        {
            // Reaching here at all means Headers is already populated (the
            // guard above returns before this point otherwise) — this is a
            // RE-load with real, current headers the combos can still fix,
            // same class of problem as the unmapped/collision branch above,
            // which keeps the pickers visible for exactly that reason. Only
            // a failed READ of a brand-new file (LoadRosterFrom's own catch,
            // before Headers is ever touched) is unrecoverable enough to
            // leave the picker hidden.
            _roster = null;
            HasRoster = true;
            Status = ex.Message;
            Refresh();
            return;
        }
        HasRoster = true;
        _saveHeaders(new Dictionary<string, string>
        {
            ["first"] = FirstHeader!, ["last"] = LastHeader!, ["control"] = ControlHeader!,
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
        var exists = await _scheduler.Run(() => File.Exists(remembered));
        if (RosterPath.Length > 0) return;   // the user got there first
        if (exists) LoadRosterFrom(remembered);
        else Status = $"Last roster wasn't found: {remembered}";
    }

    /// <summary>Dedupe, canonicalisation and the status line come from
    /// Intake.Add — same reasoning as BulkRenameViewModel.AddFiles, and the
    /// same stakes: this tool moves files too.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        if (IsBusy) { AddNote = BusyNote; return; }
        var taken = Intake.Add(_files, paths, Pdfs, File.Exists);
        _files.AddRange(taken.Files);
        AddNote = taken.Note("PDF");
        Refresh();
    }

    // Bumped by Clear; see AddPathsAsync.
    private int _clears;

    /// <summary>For drops and Add folder: files and folders alike. The walk
    /// runs on the scheduler, not the UI thread (DW-52): a big folder on a
    /// share used to freeze the window while it was read. Clear pressed
    /// while the walk is still going drops this add too (Q2-05's rule).</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var clears = _clears;
        var expanded = await _scheduler.Run(() => Intake.Expand(candidates, recursive: true, Pdfs));
        if (clears != _clears) return;
        if (IsBusy) { AddNote = BusyNote; return; }

        // Expand has already seen every one of these on disk.
        var taken = Intake.Add(_files, expanded.Files);
        _files.AddRange(taken.Files);
        var note = (taken with { WrongType = expanded.Ignored }).Note("PDF");
        // A walk that stopped partway (a share dropping) still hands back what
        // it found; say it stopped, as Page counts does (Q2-09).
        AddNote = expanded.Error.Length == 0 ? note
            : note.Length == 0 ? expanded.Error
            : $"{expanded.Error} · {note}";
        Refresh();
    }

    // A drop or a Delete mid-run would change the list the run is still
    // renaming from, so it is refused out loud rather than ignored.
    private const string BusyNote = "Wait for the merge or undo to finish before changing the list.";

    public void RemoveFiles(IEnumerable<string> sources)
    {
        if (IsBusy) { AddNote = BusyNote; return; }
        foreach (var s in sources.ToList())
        {
            _files.Remove(s);
            _mergeRejectNotes.Remove(s);
        }
        AddNote = "";
        Refresh();
    }

    // Each file's match against _matchedRoster, by source path. A match
    // depends only on the file's name and the roster, so a file already
    // matched is not matched again when some other file is added, removed or
    // renamed; the token pass behind a "suggested" row walks the whole roster,
    // and redoing it for every listed file on every change made a long list
    // slow to touch (DW-54). A different roster starts a fresh cache.
    private readonly Dictionary<string, MatchMerge.MatchResult> _matchCache = new(StringComparer.Ordinal);
    private MatchMerge.Roster? _matchedRoster;

    private List<MatchMerge.MatchResult> MatchAll(MatchMerge.Roster roster)
    {
        if (!ReferenceEquals(roster, _matchedRoster))
        {
            _matchCache.Clear();
            _matchedRoster = roster;
        }
        var unmatched = _files.Where(f => !_matchCache.ContainsKey(f)).ToList();
        foreach (var result in _matchFiles(unmatched, roster)) _matchCache[result.Source] = result;
        return _files.Select(f => _matchCache[f]).ToList();
    }

    private void Refresh()
    {
        _results = _roster is null ? new() : MatchAll(_roster);
        Rows.Clear();
        int merges = 0, review = 0, suggested = 0;
        var display = _roster is null
            ? _files.Select(f => new MatchMerge.MatchResult(f, "no_roster")).ToList()
            : _results;
        int already = 0, noMatch = 0, noName = 0, rejected = 0;
        foreach (var r in display)
        {
            string becomes = "", note = "";
            switch (r.Status)
            {
                case "merge":
                    // A row a previous DoMerge tried and Naming.RejectIllegal
                    // rejected stays flagged only while the CURRENTLY
                    // proposed NewStem still matches what was rejected — a
                    // roster reload/re-match that retargets this file to a
                    // different candidate is never shown a stale rejection.
                    if (_mergeRejectNotes.TryGetValue(r.Source, out var reject)
                        && reject.NewStem == r.NewStem)
                    {
                        note = reject.Note;
                        rejected++;
                    }
                    else
                    {
                        becomes = r.NewStem + Path.GetExtension(r.Source);
                        merges++;
                    }
                    break;
                case "ambiguous":
                    note = $"{r.Candidates!.Count} candidates — decide in Review matches";
                    review++; break;
                case "suggested":
                    note = $"{r.Suggestions!.Count} suggested — confirm in Review matches";
                    suggested++; review++; break;
                case "already":
                    note = r.Note.Length > 0 ? r.Note : "already has the id";
                    already++; break;
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
        if (rejected > 0) parts.Add($"{rejected} couldn't be renamed");
        if (already > 0) parts.Add($"{already} already merged");
        if (noMatch > 0) parts.Add($"{noMatch} no match");
        if (noName > 0) parts.Add($"{noName} no name in the filename");
        BucketsLine = _roster is null || _files.Count == 0 ? "" : string.Join(" · ", parts);

        Raise(nameof(MergeButtonText));
        Raise(nameof(ReviewButtonText));
        Raise(nameof(CanReview));
        MergeCommand.RaiseCanExecuteChanged();
    }

    private async Task DoMergeAsync()
    {
        // MatchMerge.ExecuteMerges plans then Executes, and Execute silently
        // skips every Changed=false plan. For a "merge" row that's the ONLY
        // way Changed can come back false — Naming.RejectIllegal fired
        // inside Plan (a merge NewStem is always non-empty and never already
        // the current name) — so without inspecting the plans directly the
        // row would vanish from both the merged and the failed count, and
        // keep re-offering "ready to merge" forever. Plan the exact batch
        // ExecuteMerges would, so the rejected ones are visible.
        var toDo = _results.Where(r => r.Status == "merge").ToList();
        var overrides = toDo.ToDictionary(r => r.Source, r => r.NewStem);
        var plans = BulkRename.Plan(toDo.Select(r => r.Source), new BulkRename.RenameOp(), overrides);
        var rejected = plans.Where(p => p.Manual && !p.Changed).ToList();
        foreach (var p in rejected) _mergeRejectNotes[p.Source] = (overrides[p.Source], p.Note);
        var batch = plans.Where(p => p.Changed).ToList();
        var outcomes = new List<BulkRename.RenameOutcome>();
        IsBusy = true;
        try
        {
            // One file per hop, so the status line counts and whatever has
            // landed is recorded for Undo even if a later file throws.
            for (var i = 0; i < batch.Count; i++)
            {
                Status = $"Merging {i + 1} of {batch.Count}…";
                var one = new[] { batch[i] };
                outcomes.AddRange(await _scheduler.Run(() => BulkRename.Execute(one)));
            }
        }
        finally
        {
            outcomes.AddRange(rejected.Select(p => new BulkRename.RenameOutcome(p.Source, null, p.Note)));
            IsBusy = false;
            Absorb(outcomes);
        }
    }

    /// <summary>Adopt a batch of rename outcomes (one-click merges or review
    /// picks): follow renamed files, re-match, and REPLACE _outcomes when the
    /// batch renamed at least one file — this
    /// call's outcomes become the new "last merge" batch, the same replace
    /// (not accumulate) rule BulkRenameViewModel.Apply follows, so "Undo
    /// last merge" only ever undoes the last DoMerge or the last review
    /// batch, never everything since the tool was opened.</summary>
    public void Absorb(List<BulkRename.RenameOutcome> outcomes)
    {
        var renamed = outcomes.Where(o => o.Final != null).ToList();
        var finals = renamed.ToDictionary(o => o.Source, o => o.Final!);
        for (var i = 0; i < _files.Count; i++)
            if (finals.TryGetValue(_files[i], out var f)) _files[i] = f;
        // Guarded on this batch having renamed something (the same rule as
        // StandardiseNamesViewModel's "Fix round 1, item 1"): closing Review
        // matches with no picks, or a DoMerge where every file failed,
        // touched nothing on disk and must not wipe the undo record of the
        // real merge before it.
        if (renamed.Count > 0)
        {
            _outcomes = renamed;
            UndoCommand.RaiseCanExecuteChanged();
        }
        Refresh();
        var failed = outcomes.Where(o => o.Final == null).ToList();
        if (failed.Count > 0)
            Status = $"Merged {renamed.Count}; {failed.Count} failed — e.g. " +
                     $"{Path.GetFileName(failed[0].Source)}: {failed[0].Error}";
        else if (renamed.Count > 0)
            Status = $"Merged {renamed.Count} file{(renamed.Count == 1 ? "" : "s")}.";
    }

    private async Task UndoBatchAsync()
    {
        var batch = _outcomes;
        List<string> problems;
        IsBusy = true;
        Status = "Restoring the original names…";
        try
        {
            problems = await _scheduler.Run(() => BulkRename.Revert(batch));
        }
        finally
        {
            IsBusy = false;
        }
        // Revert is per-file fail-soft and doesn't say WHICH outcome
        // failed — but a successful restore always makes the merged file's
        // name vanish (moved back to Source), while every failure path in
        // Revert leaves it exactly where it was. That's enough to relabel
        // only the rows that actually came back, and keep the rest in
        // _outcomes (replacing the batch, per Absorb's rule) so Undo can be
        // retried on just what's still stuck.
        var succeeded = _outcomes.Where(o => o.Final != null && !File.Exists(o.Final)).ToList();
        var stillFailed = _outcomes.Where(o => o.Final != null && File.Exists(o.Final)).ToList();

        var restored = succeeded.ToDictionary(o => o.Final!, o => o.Source);
        for (var i = 0; i < _files.Count; i++)
            if (restored.TryGetValue(_files[i], out var s)) _files[i] = s;

        _outcomes = stillFailed;
        UndoCommand.RaiseCanExecuteChanged();
        Refresh();
        Status = stillFailed.Count == 0
            ? "Original names restored."
            : $"{succeeded.Count} restored, {stillFailed.Count} failed" +
              (problems.Count > 0 ? $" ({string.Join("; ", problems)})" : "") + ".";
    }
}
