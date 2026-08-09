using System.Collections.ObjectModel;
using System.Globalization;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One computed document row, ready for the Documents grid — every
/// value pre-formatted (InvariantCulture, "—" for anything unparseable) and
/// IsOverThreshold computed here so the XAML binding stays a straight
/// property read, never logic. A negative TatDaysText (a document dated
/// after its own report's upload) is shown as-is, not clamped — see
/// TurnaroundTime.DocRow's own doc comment for why that's honest data a
/// reviewer needs to see, not an error to hide.</summary>
public sealed record DocumentRow(
    string FileName, string Category, string DocDateText, string UploadDateText,
    string TatDaysText, bool IsOverThreshold);

/// <summary>Turn-around Time report: point it at a folder (or drag files in)
/// of PECF report spreadsheets, map which column names the document and
/// (optionally) its category, and read four views of the result — the raw
/// per-document rows, and three aggregates. All computation is
/// OrdoSort.Core's own (SweptTable.Load + TurnaroundTime); this class only
/// owns the sources list, the off-thread debounced load (the same
/// DebouncedProbe&lt;T&gt; shape FilenameListViewModel uses for its own
/// listing — see that class's doc comment for why the load must never run
/// on the UI thread), the column-mapping choice (restored from Config, or
/// guessed), and turning each computed DocRow into the small display DTO
/// the grid actually binds.</summary>
public sealed class TurnaroundViewModel : ObservableObject, IDisposable
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
    // FilenameListViewModel's own _sources.
    private readonly List<string> _sources = new();

    // Off-thread, debounced — SweptTable.Load walks the filesystem and
    // parses every file, which must never run on the UI thread.
    private readonly DebouncedProbe<SweptTable.Table> _tableProbe;

    private SweptTable.Table _table = EmptyTable;
    private IReadOnlyList<TurnaroundTime.DocRow> _docRows = Array.Empty<TurnaroundTime.DocRow>();

    public ObservableCollection<string> Headers { get; } = new();
    public ObservableCollection<DocumentRow> Documents { get; } = new();
    public ObservableCollection<TurnaroundTime.PeriodAverage> Daily { get; } = new();
    public ObservableCollection<TurnaroundTime.PeriodAverage> Weekly { get; } = new();
    public ObservableCollection<TurnaroundTime.CategoryBreakdown> Categories { get; } = new();

    public RelayCommand BrowseCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ExportCommand { get; }

    public TurnaroundViewModel(Config cfg, IDialogService dialogs, Action? saveCfg,
        IWorkScheduler? scheduler = null, SynchronizationContext? uiContext = null, int probeDelayMs = 300)
    {
        _cfg = cfg;
        _dialogs = dialogs;
        _saveCfg = saveCfg;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _tableProbe = new DebouncedProbe<SweptTable.Table>(_scheduler, uiContext, ApplyTable, probeDelayMs);
        _thresholdDays = cfg.TatThresholdDays;

        BrowseCommand = new RelayCommand(() =>
        {
            if (_dialogs.BrowseFolder(_cfg.TatReportFolder) is { } folder)
            {
                _cfg.TatReportFolder = folder;
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
    private bool _includeSubfolders;
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set { if (Set(ref _includeSubfolders, value)) Refresh(immediate: true); }
    }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Dedupe by path (OrdinalIgnoreCase, same as
    /// FilenameListViewModel.AddPaths) and always rebuild — called by
    /// BrowseCommand's pick and by the window's drag-drop handler.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!_sources.Contains(p, StringComparer.OrdinalIgnoreCase)) _sources.Add(p);
        Refresh(immediate: true);
    }

    /// <summary>Snapshot options/_sources on the UI thread and (re)arm the
    /// table probe. Empty _sources resolves synchronously — like
    /// FilenameListViewModel's own empty-sources fast path — cancelling
    /// whatever's pending so a slow, now-stale probe can't repopulate the
    /// results after this.</summary>
    private void Refresh(bool immediate = false)
    {
        var sourcesSnapshot = _sources.ToList();
        var recursive = IncludeSubfolders;

        if (sourcesSnapshot.Count == 0)
        {
            _tableProbe.Cancel();
            ApplyTable(EmptyTable);
            return;
        }

        _tableProbe.Trigger(() =>
        {
            var expanded = Intake.Expand(sourcesSnapshot, recursive, ReportExtensions);
            return SweptTable.Load(expanded.Files);
        }, immediate);
    }

    /// <summary>Only ever runs on the UI thread (DebouncedProbe's
    /// SynchronizationContext marshal, or the empty-sources fast path
    /// above), so mutating Headers/the mapping/the results here is safe.</summary>
    private void ApplyTable(SweptTable.Table table)
    {
        _table = table;
        Headers.Clear();
        foreach (var h in table.Headers) Headers.Add(h);
        Raise(nameof(CategoryChoices));
        RestoreMapping(table.Headers);
        RecomputeDocRows();
    }

    // --------------------------------------------------------- column mapping
    private string _filenameColumn = "";
    public string FilenameColumn
    {
        get => _filenameColumn;
        set
        {
            // ApplyTable's Headers.Clear() (every load after the first —
            // a second AddPaths, a re-browse, a subfolder toggle, or Clear
            // itself) empties the ComboBox's own ItemsSource out from under
            // a live Selector; WPF responds by pushing a NULL SelectedItem
            // back through this TwoWay binding, straight into this setter —
            // even though the property is typed non-nullable, nothing stops
            // a null reference at runtime from a XAML binding. Silently
            // ignoring it here (rather than persisting null into
            // cfg.TatHeaders and leaving _filenameColumn null for
            // RecomputeDocRows' own .Length read — the NRE that used to
            // surface the app-level crash dialog) is safe: ApplyTable always
            // calls RestoreMapping immediately afterward, once Headers is
            // resettled, which re-selects a real header unconditionally
            // whenever the current choice no longer matches anything loaded.
            if (value is null) return;
            if (!Set(ref _filenameColumn, value)) return;
            _cfg.TatHeaders["filename"] = value;
            _saveCfg?.Invoke();
            RecomputeDocRows();
        }
    }

    private string _categoryColumn = "";
    public string CategoryColumn
    {
        get => _categoryColumn;
        set
        {
            // Same WPF Selector null-push as FilenameColumn's own guard
            // above — see its doc comment for the mechanism (here it's also
            // provoked by Raise(nameof(CategoryChoices)) in ApplyTable,
            // which resets the Category ComboBox's ItemsSource the same way
            // Headers.Clear() resets the File name one). "" (no category —
            // CategoryChoices' own leading sentinel) is still a perfectly
            // legitimate, explicit VALUE here, distinct from an actual null;
            // only null (never a real user choice — NoneSentinelConverter
            // only ever displays "" as "(none)", it never IS null) is
            // ignored.
            if (value is null) return;
            if (!Set(ref _categoryColumn, value)) return;
            _cfg.TatHeaders["category"] = value;
            _saveCfg?.Invoke();
            RecomputeDocRows();
        }
    }

    /// <summary>ItemsSource for the category combo: "" (no category) plus
    /// every loaded header. The window's ItemTemplate renders "" as
    /// "(none)" (NoneSentinelConverter) — SelectedItem still binds straight
    /// to CategoryColumn, a plain string, so nothing here needs its own
    /// value converter.</summary>
    public IReadOnlyList<string> CategoryChoices => new[] { "" }.Concat(Headers).ToList();

    /// <summary>Restore each column choice from cfg.TatHeaders if it's
    /// among the just-loaded Headers, else auto-guess by name — only when
    /// the CURRENT choice isn't itself still valid, so a user's own pick
    /// survives a reload whose headers still include it, and only a
    /// genuinely new/changed header set re-triggers the guess.</summary>
    private void RestoreMapping(IReadOnlyList<string> headers)
    {
        if (_filenameColumn.Length == 0 || !headers.Contains(_filenameColumn))
        {
            var saved = _cfg.TatHeaders.TryGetValue("filename", out var s) && headers.Contains(s) ? s : null;
            var next = saved ?? Guess(headers, "filename", "file", "document") ?? headers.FirstOrDefault() ?? "";
            Set(ref _filenameColumn, next, nameof(FilenameColumn));
        }
        if (_categoryColumn.Length == 0 || !headers.Contains(_categoryColumn))
        {
            // Unlike filename, "" is a real, savable CHOICE here (an
            // explicit "(none)" pick — CategoryChoices' own leading
            // sentinel), not just "nothing decided yet". cfg.TatHeaders
            // ["category"] being PRESENT with an empty value means the user
            // picked "(none)" last time and that must round-trip across a
            // restart exactly like any other saved header would — never
            // falling through to Guess and re-populating a column the user
            // deliberately turned off. Only a genuinely ABSENT "category"
            // key (never saved at all, e.g. a first-ever load) still
            // auto-guesses. A saved NON-empty value that no longer names a
            // loaded header (a differently-shaped report swapped in) still
            // falls through to Guess, same as filename's own saved-but-stale
            // case above.
            if (_cfg.TatHeaders.TryGetValue("category", out var saved))
            {
                var next = saved.Length == 0 || headers.Contains(saved)
                    ? saved
                    : Guess(headers, "sourcetype", "category", "dest", "type") ?? "";
                Set(ref _categoryColumn, next, nameof(CategoryColumn));
            }
            else
            {
                var next = Guess(headers, "sourcetype", "category", "dest", "type") ?? "";
                Set(ref _categoryColumn, next, nameof(CategoryColumn));
            }
        }
    }

    /// <summary>First header containing the FIRST needle that matches
    /// anything — needle priority, not header order: every header is
    /// checked for "filename" before "file" is tried at all.</summary>
    private static string? Guess(IReadOnlyList<string> headers, params string[] needles) =>
        needles
            .Select(n => headers.FirstOrDefault(h => h.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(h => h is not null);

    // -------------------------------------------------------------- threshold
    private int _thresholdDays;
    public int ThresholdDays
    {
        get => _thresholdDays;
        set
        {
            if (!Set(ref _thresholdDays, value)) return;
            _cfg.TatThresholdDays = value;
            _saveCfg?.Invoke();
            RebuildDisplay();
        }
    }

    // ---------------------------------------------------------------- results
    private bool _hasCategories;
    public bool HasCategories { get => _hasCategories; private set => Set(ref _hasCategories, value); }

    /// <summary>DocRows depend on the table and the column mapping; a
    /// threshold-only change (ThresholdDays' setter) skips straight to
    /// RebuildDisplay since the underlying DocRows haven't moved.</summary>
    private void RecomputeDocRows()
    {
        var categoryColumn = _categoryColumn.Length == 0 ? null : _categoryColumn;
        _docRows = _filenameColumn.Length == 0
            ? Array.Empty<TurnaroundTime.DocRow>()
            : TurnaroundTime.ComputeAll(_table, _filenameColumn, categoryColumn);
        RebuildDisplay();
    }

    /// <summary>Rebuilds every displayed collection from _docRows wholesale
    /// — matching the rest of this app's view-model convention
    /// (HistoryViewModel.ApplyFilter, MatchMergeViewModel.Refresh) rather
    /// than hand-tracking exactly which piece a given change actually touched.
    /// Daily/Weekly don't depend on ThresholdDays and get rebuilt on a
    /// threshold-only change anyway — a deliberately cheap redundancy, not a
    /// bug: the row counts here are small report batches, never the
    /// unbounded-growth shape History's own Rows is.</summary>
    private void RebuildDisplay()
    {
        Documents.Clear();
        foreach (var d in _docRows) Documents.Add(ToDisplay(d));

        Daily.Clear();
        foreach (var p in TurnaroundTime.DailyAverages(_docRows)) Daily.Add(p);

        Weekly.Clear();
        foreach (var p in TurnaroundTime.WeeklyAverages(_docRows)) Weekly.Add(p);

        Categories.Clear();
        foreach (var c in TurnaroundTime.ByCategory(_docRows, ThresholdDays)) Categories.Add(c);
        // Only meaningful once a real category column is picked — otherwise
        // ByCategory still returns one "" group for every row that has a
        // TatDays, and a By-category tab showing a single blank-named row
        // would be noise, not information.
        HasCategories = _categoryColumn.Length > 0 && Categories.Count > 0;

        Status = BuildStatus();
    }

    private DocumentRow ToDisplay(TurnaroundTime.DocRow d) => new(
        d.FileName,
        d.Category,
        d.DocDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—",
        d.UploadDate?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—",
        d.TatDays?.ToString(CultureInfo.InvariantCulture) ?? "—",
        d.TatDays is { } t && t > ThresholdDays);

    private string BuildStatus()
    {
        var noTat = _docRows.Count(r => r.TatDays is null);
        var text = $"{_table.FilesRead} files · {_table.Rows.Count} rows · {noTat} without TAT";
        // TurnaroundTime.ExceedingThreshold was computed nowhere until this
        // fix — every row already carries IsOverThreshold for the grid's own
        // Warning styling, but the status line (the one place a reviewer
        // sees a COUNT without opening the Documents tab) never surfaced it.
        // Omitted entirely at 0 rather than reading "0 over threshold" —
        // the other segments (files/rows/without TAT) are always-meaningful
        // totals, but "over threshold" is a call-out, and a call-out that's
        // always present even when there's nothing to call out just becomes
        // noise.
        var overThreshold = TurnaroundTime.ExceedingThreshold(_docRows, ThresholdDays).Count;
        if (overThreshold > 0)
            text += $" · {overThreshold} over threshold";
        if (_table.FileErrors.Count > 0)
            text += $" · {_table.FileErrors.Count} file errors: {_table.FileErrors[0]}";
        return text;
    }

    // ---------------------------------------------------------------- export
    internal async Task ExportAsync()
    {
        var suggested = $"turnaround-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";
        var dest = _dialogs.AskSaveFile("Spreadsheet files (*.csv)|*.csv", suggested);
        if (dest is null) return;
        var rows = _docRows;   // read on the UI thread before offloading
        try
        {
            var count = await _scheduler.Run(() => TurnaroundTime.ExportCsv(rows, dest));
            _dialogs.Info($"Exported {count} rows to {dest}", "OrdoSort");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn("Couldn't save it: " + ex.Message, "OrdoSort");
        }
    }
}
