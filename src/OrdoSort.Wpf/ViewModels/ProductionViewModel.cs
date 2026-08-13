using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Production report: point it at a folder of hourly-swept daily
/// sweep CSVs, tick which columns to group by (typically SOURCE-FOLDER plus
/// the derived Employee) and which numeric columns to sum (typically
/// PDF-PAGE-COUNT), and read the grouped counts/sums. All computation is
/// OrdoSort.Core's own (SweptTable.Load + ProductionReport.WithDerived/Group);
/// this class only owns the sources list, the off-thread debounced load (the
/// same DebouncedProbe&lt;T&gt; shape TurnaroundViewModel/FilenameListViewModel
/// use for their own listing/table load — see TurnaroundViewModel's doc
/// comment for why the load must never run on the UI thread), the two pick
/// lists (restored from Config, or defaulted), the datetime column choice
/// (restored from Config, or guessed — the owner column feeding the derived
/// Employee has no config field in v1 and is always guessed), and turning
/// each computed GroupResult into the small display DTO the grid actually
/// binds. Deliberately duplicates TurnaroundViewModel's source/probe/status
/// plumbing rather than sharing a base class — see Task 7's brief and
/// TurnaroundViewModel's own doc comment: per-tool duplication is this
/// repo's established pattern for these report windows.</summary>
public sealed class ProductionViewModel : ObservableObject, IDisposable
{
    // csv + xlsx: the same two formats SweptTable.Load (Task 2) can read.
    private static readonly HashSet<string> ReportExtensions = new() { "csv", "xlsx" };

    private static readonly SweptTable.Table EmptyTable =
        new(Array.Empty<string>(), Array.Empty<SweptTable.Row>(), 0, Array.Empty<string>());

    private readonly Config _cfg;
    private readonly IDialogService _dialogs;
    private readonly Action? _saveCfg;
    private readonly IWorkScheduler _scheduler;

    // Dropped/browsed roots — same dedupe-by-path shape as
    // TurnaroundViewModel's own _sources.
    private readonly List<string> _sources = new();

    // Off-thread, debounced — SweptTable.Load walks the filesystem and
    // parses every file, which must never run on the UI thread.
    private readonly DebouncedProbe<LoadResult> _tableProbe;

    /// <summary>Bundles the loaded table with the Intake.Expand result that
    /// produced its file list — same shape as, and for the same reason as,
    /// TurnaroundViewModel's own LoadResult: Table alone can't explain WHY a
    /// load came back empty, and BuildStatus needs both.</summary>
    private sealed record LoadResult(SweptTable.Table Table, Intake.Expanded Expanded);

    private SweptTable.Table _table = EmptyTable;
    private SweptTable.Table _derived = EmptyTable;
    private Intake.Expanded _expanded = new(new List<string>(), 0);
    private List<ProductionReport.GroupResult> _results = new();

    // The order the user checked each pick in, per pick list — THIS defines
    // grouping order, not the (header-order) sequence GroupPicks/SumPicks
    // themselves are displayed in. Recomputed from cfg (intersected with the
    // just-derived headers) whenever the header set can have changed; a bare
    // tick change instead appends/removes against these directly and copies
    // the result back to cfg — see RebuildPicksAndResults' and
    // OnGroupTick/OnSumTick's own doc comments.
    private List<string> _groupOrder = new();
    private List<string> _sumOrder = new();

    public ObservableCollection<string> Headers { get; } = new();
    public ObservableCollection<HeaderPick> GroupPicks { get; } = new();
    public ObservableCollection<HeaderPick> SumPicks { get; } = new();

    /// <summary>Display rows for the grid, one Dictionary per group — keyed
    /// by the matching entry's INDEX in <see cref="ColumnNames"/> (an
    /// invariant-culture string, "0", "1", …), never by its NAME. A name key
    /// collides two ways a real CSV/user pick can genuinely produce: a
    /// header literally named "Records" (colliding with the reserved
    /// record-count entry every row also carries) or the SAME header ticked
    /// in both Group and Sum (colliding with itself) — either way one
    /// RecomputeResults write silently overwrote the other's displayed cell.
    /// Export (ProductionReport.ExportCsv, keyed by GroupResult.Key/Sums
    /// directly, never through this dictionary) was never affected — only
    /// this display projection was. An index can't collide: every position
    /// in ColumnNames gets its own slot regardless of how many group/sum
    /// columns share a name or happen to be named "Records".
    /// ProductionWindow.xaml.cs's own RebuildColumns binds each column to
    /// "[{index}]" to match, using Header (not Binding) for the name a
    /// person actually reads.</summary>
    public ObservableCollection<Dictionary<string, string>> Rows { get; } = new();

    /// <summary>Group column names, then "Records", then sum column names —
    /// the window builds one DataGridTextColumn per entry (TriageWindow.xaml.cs
    /// is the template for building DataGrid columns from a dynamic list in
    /// code-behind), Header set to the entry here and Binding set to its
    /// INDEX (see Rows' own doc comment for why). "Records" is ALSO the
    /// boundary the window uses to tell group columns (need a filler +
    /// ellipsis) from numeric ones (short, right-aligned, uncapped) apart by
    /// NAME match — unlike Rows' own keying, that positional boundary search
    /// is not collision-proofed against a real header literally named
    /// "Records" ahead of the reserved entry; ProductionReport.Group places
    /// no such restriction on a group/sum column's name.</summary>
    public IReadOnlyList<string> ColumnNames { get; private set; } = Array.Empty<string>();

    /// <summary>Bumped every time Rows/ColumnNames are rebuilt — the window
    /// hooks PropertyChanged for this name to know when to rebuild its
    /// DataGrid.Columns (TriageWindow's ShowCurrentAsync is the mechanism
    /// this mirrors, adapted to MVVM's PropertyChanged plumbing since,
    /// unlike TriageWindow, this window has a real view model/DataContext).</summary>
    public int ResultsVersion { get; private set; }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ExportCommand { get; }

    public ProductionViewModel(Config cfg, IDialogService dialogs, Action? saveCfg,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, int probeDelayMs = 300)
    {
        _cfg = cfg;
        _dialogs = dialogs;
        _saveCfg = saveCfg;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _tableProbe = new DebouncedProbe<LoadResult>(_scheduler, uiContext, ApplyTable, probeDelayMs);

        BrowseCommand = new RelayCommand(() =>
        {
            if (_dialogs.BrowseFolder(_cfg.ProductionCsvFolder) is { } folder)
            {
                _cfg.ProductionCsvFolder = folder;
                _saveCfg?.Invoke();
                AddPaths(new[] { folder });
            }
        });
        ClearCommand = new RelayCommand(() =>
        {
            _sources.Clear();
            Refresh(immediate: true);
        });
        ExportCommand = new RelayCommand(() => _ = ExportAsync());
    }

    public void Dispose() => _tableProbe.Dispose();

    // ------------------------------------------------------------- sources
    // Defaults on: reports live in dated subfolder trees, and a fresh window
    // should sweep the whole tree without the user needing to know this
    // checkbox exists — they can still untick it per-load.
    private bool _includeSubfolders = true;
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set { if (Set(ref _includeSubfolders, value)) Refresh(immediate: true); }
    }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _addNote = "";
    /// <summary>Says so when a drop or browse added nothing — see
    /// TurnaroundViewModel.AddNote, which this mirrors.</summary>
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Dedupe through Intake.Add — see TurnaroundViewModel.AddPaths
    /// for the reasoning, which applies here identically: a folder listed
    /// twice under two spellings would be swept twice and double every
    /// record count in the report.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var taken = Intake.Add(_sources, paths);
        _sources.AddRange(taken.Files);
        AddNote = taken.Note("file");
        Refresh(immediate: true);
    }

    /// <summary>Snapshot options/_sources on the UI thread and (re)arm the
    /// table probe. Empty _sources resolves synchronously — like
    /// TurnaroundViewModel's own empty-sources fast path — cancelling
    /// whatever's pending so a slow, now-stale probe can't repopulate the
    /// results after this.</summary>
    private void Refresh(bool immediate = false)
    {
        var sourcesSnapshot = _sources.ToList();
        var recursive = IncludeSubfolders;

        if (sourcesSnapshot.Count == 0)
        {
            _tableProbe.Cancel();
            ApplyTable(new LoadResult(EmptyTable, new Intake.Expanded(new List<string>(), 0)));
            return;
        }

        _tableProbe.Trigger(() =>
        {
            var expanded = Intake.Expand(sourcesSnapshot, recursive, ReportExtensions);
            return new LoadResult(SweptTable.Load(expanded.Files), expanded);
        }, immediate);
    }

    /// <summary>Only ever runs on the UI thread (DebouncedProbe's
    /// SynchronizationContext marshal, or the empty-sources fast path
    /// above), so mutating Headers/the mapping/the results here is safe.</summary>
    private void ApplyTable(LoadResult result)
    {
        _table = result.Table;
        _expanded = result.Expanded;
        Headers.Clear();
        foreach (var h in _table.Headers) Headers.Add(h);
        RestoreDatetimeColumn(_table.Headers);
        RebuildPicksAndResults();
    }

    // --------------------------------------------------------- datetime column
    private string _datetimeColumn = "";
    public string DatetimeColumn
    {
        get => _datetimeColumn;
        set
        {
            // ApplyTable's Headers.Clear() (every load after the first — a
            // second AddPaths, a re-browse, a subfolder toggle, or Clear
            // itself) empties the ComboBox's own ItemsSource out from under
            // a live Selector; WPF responds by pushing a NULL SelectedItem
            // back through this TwoWay binding, straight into this setter —
            // even though the property is typed non-nullable, nothing stops
            // a null reference at runtime from a XAML binding. Silently
            // ignoring it here (rather than persisting null into
            // cfg.ProductionDatetimeColumn and leaving _datetimeColumn null
            // for RestoreDatetimeColumn's own .Length read — the NRE that
            // used to surface the app-level crash dialog) is safe: ApplyTable
            // always calls RestoreDatetimeColumn immediately afterward, once
            // Headers is resettled, which re-selects a real header
            // unconditionally whenever the current choice no longer matches
            // anything loaded — same contract TurnaroundViewModel.
            // FilenameColumn's own guard holds.
            if (value is null) return;
            if (!Set(ref _datetimeColumn, value)) return;
            _cfg.ProductionDatetimeColumn = value;
            _saveCfg?.Invoke();
            // Date/Hour only exist in the derived headers when a datetime
            // column is picked — the pick lists themselves can gain or lose
            // entries here, not just the grouped results.
            RebuildPicksAndResults();
        }
    }

    /// <summary>Restore DatetimeColumn from cfg if it's among the just-loaded
    /// (raw) Headers, else auto-guess by name — only when the CURRENT choice
    /// isn't itself still valid, so a user's own pick survives a reload whose
    /// headers still include it, the same contract TurnaroundViewModel.
    /// RestoreMapping holds for its own column choices. Never persists or
    /// recomputes itself — ApplyTable calls RebuildPicksAndResults once,
    /// after this and Headers are both settled, exactly like
    /// TurnaroundViewModel.ApplyTable's own restore-then-recompute order.</summary>
    private void RestoreDatetimeColumn(IReadOnlyList<string> headers)
    {
        if (_datetimeColumn.Length > 0 && headers.Contains(_datetimeColumn)) return;
        var saved = _cfg.ProductionDatetimeColumn.Length > 0 && headers.Contains(_cfg.ProductionDatetimeColumn)
            ? _cfg.ProductionDatetimeColumn : null;
        var next = saved ?? Guess(headers, "date-time", "datetime", "date") ?? "";
        Set(ref _datetimeColumn, next, nameof(DatetimeColumn));
    }

    /// <summary>First header containing the FIRST needle that matches
    /// anything — needle priority, not header order — same shape as
    /// TurnaroundViewModel.Guess.</summary>
    private static string? Guess(IReadOnlyList<string> headers, params string[] needles) =>
        needles
            .Select(n => headers.FirstOrDefault(h => h.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(h => h is not null);

    // ------------------------------------------------------------ pick lists

    /// <summary>Rebuilds GroupPicks/SumPicks from the just-derived table's
    /// headers (raw headers plus Employee/Date/Hour, per ProductionReport.
    /// WithDerived) and recomputes the grouped results. Called whenever the
    /// header set behind the pick lists can have changed — a fresh load
    /// (ApplyTable) or a DatetimeColumn change (which adds/drops the Date/
    /// Hour derived columns) — but NOT by a bare tick change
    /// (OnGroupTick/OnSumTick): the pick lists themselves don't need
    /// rebuilding for that, only the grouped results (RecomputeResults) do,
    /// and rebuilding the ObservableCollection on every checkbox click would
    /// tear down and recreate every CheckBox's binding for no reason.
    ///
    /// The owner column feeding the derived Employee has no config field in
    /// v1 (Task 7 brief) — it is always guessed fresh from the raw headers,
    /// same needle-priority shape as the datetime guess.</summary>
    private void RebuildPicksAndResults()
    {
        var ownerColumn = Guess(_table.Headers, "owner", "user", "employee") ?? "";
        _derived = ProductionReport.WithDerived(_table, ownerColumn, _datetimeColumn);

        _groupOrder = _cfg.ProductionGroupColumns.Count > 0
            ? _cfg.ProductionGroupColumns.Where(h => _derived.Headers.Contains(h)).ToList()
            : DefaultColumns(_derived.Headers, "SOURCE-FOLDER", "Employee");
        _sumOrder = _cfg.ProductionSumColumns.Count > 0
            ? _cfg.ProductionSumColumns.Where(h => _derived.Headers.Contains(h)).ToList()
            : DefaultColumns(_derived.Headers, "PDF-PAGE-COUNT");

        RebuildPickList(GroupPicks, _derived.Headers, _groupOrder, OnGroupTick);
        RebuildPickList(SumPicks, _derived.Headers, _sumOrder, OnSumTick);

        RecomputeResults();
    }

    private static List<string> DefaultColumns(IReadOnlyList<string> headers, params string[] wanted) =>
        wanted.Where(w => headers.Contains(w)).ToList();

    private static void RebuildPickList(ObservableCollection<HeaderPick> picks, IReadOnlyList<string> headers,
        List<string> checkedOrder, Action<HeaderPick> onChanged)
    {
        picks.Clear();
        foreach (var h in headers)
        {
            HeaderPick? pick = null;
            pick = new HeaderPick(h, checkedOrder.Contains(h), () => onChanged(pick!));
            picks.Add(pick);
        }
    }

    /// <summary>A ticked pick's Name is appended to _groupOrder (unless
    /// already present, defensive against a double-fire); an unticked one is
    /// removed — _groupOrder, not header order, is what defines the grouping
    /// order a person sees, so this is the one place that order can change.
    /// Persists a COPY to cfg (so a later mutation of _groupOrder can never
    /// alias — and silently rewrite — a list the caller/config layer still
    /// holds a reference to) and recomputes; deliberately does NOT call
    /// RebuildPicksAndResults — see that method's own doc comment.</summary>
    private void OnGroupTick(HeaderPick pick)
    {
        if (pick.IsChosen) { if (!_groupOrder.Contains(pick.Name)) _groupOrder.Add(pick.Name); }
        else _groupOrder.Remove(pick.Name);
        _cfg.ProductionGroupColumns = _groupOrder.ToList();
        _saveCfg?.Invoke();
        RecomputeResults();
    }

    private void OnSumTick(HeaderPick pick)
    {
        if (pick.IsChosen) { if (!_sumOrder.Contains(pick.Name)) _sumOrder.Add(pick.Name); }
        else _sumOrder.Remove(pick.Name);
        _cfg.ProductionSumColumns = _sumOrder.ToList();
        _saveCfg?.Invoke();
        RecomputeResults();
    }

    // ---------------------------------------------------------------- results

    /// <summary>Groups _derived per the current pick order, then turns each
    /// GroupResult into a display row keyed by its column's INDEX in
    /// ColumnNames — Binding($"[{index}]") over a dictionary is the
    /// TriageWindow way (its own doc comment) adapted to sidestep the
    /// name-collision class Rows' own doc comment describes, which is what
    /// lets the window's dynamically-built columns bind without a display
    /// DTO type of their own AND without a literal "Records" header or a
    /// column ticked in both Group and Sum silently clobbering another
    /// cell's value.</summary>
    private void RecomputeResults()
    {
        _results = ProductionReport.Group(_derived, _groupOrder, _sumOrder);

        var columns = new List<string>(_groupOrder) { "Records" };
        columns.AddRange(_sumOrder);
        ColumnNames = columns;

        Rows.Clear();
        foreach (var r in _results)
        {
            // Index math mirrors the `columns` list built just above
            // (_groupOrder, then the single reserved "Records" slot, then
            // _sumOrder) exactly — every position gets its own dictionary
            // key regardless of what any of these columns are NAMED.
            var row = new Dictionary<string, string>();
            for (var i = 0; i < _groupOrder.Count; i++)
                row[i.ToString(CultureInfo.InvariantCulture)] = r.Key[i];
            row[_groupOrder.Count.ToString(CultureInfo.InvariantCulture)] =
                r.Count.ToString(CultureInfo.InvariantCulture);
            for (var i = 0; i < _sumOrder.Count; i++)
                row[(_groupOrder.Count + 1 + i).ToString(CultureInfo.InvariantCulture)] =
                    r.Sums[_sumOrder[i]].ToString("0.##", CultureInfo.InvariantCulture);
            Rows.Add(row);
        }

        Status = BuildStatus();
        ResultsVersion++;
        Raise(nameof(ResultsVersion));
        Raise(nameof(ColumnNames));
    }

    private string BuildStatus()
    {
        var text = $"{_table.FilesRead} files · {_table.Rows.Count} rows · {_results.Count} groups";
        if (_table.FileErrors.Count > 0)
            text += $" · {_table.FileErrors.Count} file errors: {_table.FileErrors[0]}";
        // Intake.Expand's own Error — e.g. a subfolder Directory.EnumerateFiles
        // couldn't open — is a DIFFERENT failure than FileErrors above (those
        // are SweptTable.Load's per-file parse failures, after Intake already
        // successfully found the file). Surfaced here, not swallowed, so "0
        // files · 0 rows" has an answer instead of leaving a reviewer to guess
        // whether the folder was really empty or something went wrong walking it.
        if (_expanded.Error.Length > 0)
            text += $" · {_expanded.Error}";
        // Only on an EMPTY load: this answers "why is this empty" — every
        // file Intake found was the wrong extension for a report (csv/xlsx)
        // — but would just be noise appended to every successful load that
        // also happens to skip some unrelated file (a .DS_Store, a lock
        // file) alongside real report data.
        if (_table.FilesRead == 0 && _expanded.Ignored > 0)
            text += $" · {_expanded.Ignored} skipped (not csv/xlsx)";
        return text;
    }

    // ---------------------------------------------------------------- export
    internal async Task ExportAsync()
    {
        var suggested = $"production-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";
        var dest = _dialogs.AskSaveFile("Spreadsheet files (*.csv)|*.csv", suggested);
        if (dest is null) return;
        // read on the UI thread before offloading — same reasoning as
        // TurnaroundViewModel.ExportAsync's own rows snapshot
        var results = _results;
        var groupCols = _groupOrder.ToList();
        var sumCols = _sumOrder.ToList();
        try
        {
            var count = await _scheduler.Run(() => ProductionReport.ExportCsv(results, groupCols, sumCols, dest));
            // count is results.Count — GROUPS, not source rows (ExportCsv's
            // own doc comment already calls this "N groups exported"; the
            // dialog text just hadn't matched it).
            _dialogs.Info($"Exported {count} groups to {dest}", "OrdoSort");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn("Couldn't save it: " + ex.Message, "OrdoSort");
        }
    }
}
