using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Drop or browse files/folders, see a natural-sorted list of their
/// filenames, copy it or save it as .txt or (once a column is on) .csv. The
/// whole tool is a thin UI shell over FilenameList.Build — this class owns
/// only the roots people have added and the debounced recompute of the list
/// from them, the same snapshot-off-thread-apply shape BulkRenameViewModel
/// uses for its own preview (see that class's doc comment for why the
/// compute must never run on the UI thread, and why the applied result must
/// be exactly what a caller last saw rendered). Everything downstream of
/// that recompute — the name filter, the sort direction, the column set —
/// is a Reproject: a re-render of the last Build result in memory, never a
/// second walk of the filesystem.</summary>
public sealed class FilenameListViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<string, PageCounts.CountResult> _counter;

    // Dropped/browsed roots (files and/or folders) — deduped on full path,
    // OrdinalIgnoreCase (Windows paths). Existence isn't checked here; a
    // root that turns out missing or unreadable by the time Build() runs
    // is FilenameList's own Ignored/Error to report, not this list's.
    private readonly List<string> _sources = new();

    // Off-thread, debounced — same reasoning as BulkRenameViewModel's
    // _plansProbe: FilenameList.Build walks the filesystem (Intake.Expand),
    // which must never run on the UI thread per keystroke of ExtensionFilter.
    private readonly DebouncedProbe<FilenameList.Listing> _listingProbe;

    // The last Build result, unfiltered. Rows below is the VISIBLE projection
    // of it — everything the user sees, copies and saves comes off that, which
    // is what keeps "what you see is what you copy" true of the name filter and
    // the sort direction and not only of the columns.
    private IReadOnlyList<FilenameList.FileRow> _allRows = Array.Empty<FilenameList.FileRow>();
    private int _lastIgnored;
    private string _lastError = "";

    // Full paths the user has removed. Keyed on PATH rather than on the row,
    // because a rebuild produces NEW FileRow instances for the same files —
    // holding row references would let every removed row reappear on the next
    // keystroke in the extension box.
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same removals as <see cref="_excluded"/>, but kept in the
    /// order they happened so Ctrl+Z can undo ONE of them (audit FL-05). The set
    /// answers "is this row hidden?" on every reproject and the list answers
    /// "what was the last thing I removed?"; keeping both beats deriving either,
    /// because the set is hit once per row per keystroke. A path appears in at
    /// most one batch — RemoveSelected only records paths the set did not
    /// already hold — so popping a batch can never resurrect a row an earlier
    /// batch removed.</summary>
    private readonly List<List<string>> _removalBatches = new();

    /// <summary>How many PDFs are opened at once — the same figure and the same
    /// reasoning as PageCountsViewModel.MaxConcurrentCounts and
    /// UnlockViewModel.MaxConcurrentUnlocks: the work is spent waiting on I/O,
    /// PDFs may live on a slow share, and four overlaps most of that waiting
    /// without turning the share itself into the bottleneck.</summary>
    internal const int MaxConcurrentCounts = 4;

    /// <summary>Page counts already known, keyed on full path — the same
    /// identity _excluded and the selection restore use, and for the same
    /// reason: a reproject hands out NEW FileRow instances for the same files,
    /// so anything keyed on the row itself would be thrown away on every Find
    /// keystroke. Keyed this way, the Find box re-renders what is already known
    /// instead of reopening every PDF in the list once per character.</summary>
    private readonly Dictionary<string, PageCounts.CountResult> _pageCounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose count is running right now. Reproject fires on
    /// every keystroke and asks for counts each time; without this, a slow
    /// share would collect a fresh request per character for the same file.</summary>
    private readonly HashSet<string> _countsInFlight = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _countGate = new(MaxConcurrentCounts);
    private CancellationTokenSource _countCts = new();

    public int RemovedCount => _allRows.Count(r => _excluded.Contains(r.FullPath));

    /// <summary>Pushed in by the window on SelectionChanged — DataGrid's
    /// SelectedItems is not bindable.</summary>
    private IReadOnlyList<string> _selectedPaths = Array.Empty<string>();
    public IReadOnlyList<string> SelectedPaths
    {
        get => _selectedPaths;
        set
        {
            // Task 9 wires this from DataGrid.SelectionChanged, and a WPF
            // selection handler can hand over an empty-or-null sequence
            // (e.g. SelectedItems.Cast<...>() on a momentarily empty
            // selection) — never let a null reach _selectedPaths, since
            // every reader here (.Count, SelectedRows' HashSet ctor) assumes
            // a real, if possibly empty, list.
            _selectedPaths = value ?? Array.Empty<string>();
            Raise(nameof(SelectedPaths));
            Raise(nameof(CopyText));
            // "Remove selected" is gated on there BEING a selection, and
            // RelayCommand only re-asks when told to — without this the button
            // would sit disabled no matter what the user picked, which is the
            // stuck-disabled failure RefreshCommandStates' own comment warns
            // about (UI-12).
            RemoveSelectedCommand?.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<FilenameList.FileRow> Rows { get; } = new();

    private FilenameList.Columns _columns = FilenameList.Columns.None;
    public FilenameList.Columns Columns
    {
        get => _columns;
        set
        {
            if (!Set(ref _columns, value)) return;
            Raise(nameof(IsTableShape));
            Raise(nameof(SaveLabel));
            Raise(nameof(ShowNumber)); Raise(nameof(ShowSize)); Raise(nameof(ShowModified));
            Raise(nameof(ShowFolder)); Raise(nameof(ShowFullPath)); Raise(nameof(ShowPages));

            // Unticking Pages IS the cancel affordance — there is no separate
            // button. Counts already gathered stay in _pageCounts, so ticking it
            // back on is instant rather than a second trip to the disk.
            if (!ShowPages) CancelCounting();

            Reproject();   // starts counting again when the column is on
        }
    }

    // MenuItem.IsChecked is a bool, so each flag needs its own two-way adapter
    // over Columns; the enum stays the single source of truth. The window's
    // code-behind also reads these (not Columns' bits directly) to drive each
    // optional DataGridColumn's Visibility — see FilenameListWindow.xaml.cs.
    private bool Has(FilenameList.Columns flag) => (Columns & flag) != 0;
    private void Toggle(FilenameList.Columns flag, bool on) =>
        Columns = on ? Columns | flag : Columns & ~flag;

    public bool ShowNumber   { get => Has(FilenameList.Columns.Number);   set => Toggle(FilenameList.Columns.Number, value); }
    public bool ShowSize     { get => Has(FilenameList.Columns.Size);     set => Toggle(FilenameList.Columns.Size, value); }
    public bool ShowModified { get => Has(FilenameList.Columns.Modified); set => Toggle(FilenameList.Columns.Modified, value); }
    public bool ShowFolder   { get => Has(FilenameList.Columns.Folder);   set => Toggle(FilenameList.Columns.Folder, value); }
    public bool ShowFullPath { get => Has(FilenameList.Columns.FullPath); set => Toggle(FilenameList.Columns.FullPath, value); }
    public bool ShowPages    { get => Has(FilenameList.Columns.Pages);    set => Toggle(FilenameList.Columns.Pages, value); }

    private string _nameFilter = "";
    public string NameFilter
    {
        get => _nameFilter;
        set { if (Set(ref _nameFilter, value)) Reproject(); }
    }

    private bool _descending;
    public bool Descending
    {
        get => _descending;
        set { if (Set(ref _descending, value)) Reproject(); }
    }

    /// <summary>Drives the Save dialog's filter. Delegates to Core's own
    /// IsTable rather than re-deriving the rule here, so the file extension
    /// SaveAsync picks and the content shape ToText/ToCsv actually produce
    /// can never disagree.</summary>
    public bool IsTableShape => FilenameList.IsTable(Columns);

    /// <summary>The Save button's own text, which has to follow the shape for
    /// the same reason the save dialog's filter does. Turn one data column on
    /// and Save writes a .csv — a button hardcoded to "Save as .txt…" would
    /// then name a format the click does not produce, which is the one thing
    /// this tool's rule (nothing displays one value and produces another)
    /// forbids, on the single control the whole export hangs off.</summary>
    public string SaveLabel => IsTableShape ? "Save as .csv…" : "Save as .txt…";

    public string OutputText => FilenameList.ToText(Rows.ToList(), Columns);
    public string OutputCsv => FilenameList.ToCsv(Rows.ToList(), Columns);

    /// <summary>What the Copy button puts on the clipboard: the selected rows
    /// when there are any, everything otherwise. This is what makes the button
    /// agree with the Ctrl+C the grid already supports — before this, the
    /// button copied all 200 rows while Ctrl+C copied the 5 you had picked.</summary>
    public string CopyText => FilenameList.ToText(SelectedRows(), Columns);

    private List<FilenameList.FileRow> SelectedRows()
    {
        if (_selectedPaths.Count == 0) return Rows.ToList();
        var wanted = new HashSet<string>(_selectedPaths, StringComparer.OrdinalIgnoreCase);
        return Rows.Where(r => wanted.Contains(r.FullPath)).ToList();
    }

    public FilenameListViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, int probeDelayMs = 300,
        Func<string, PageCounts.CountResult>? counter = null)
    {
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _uiContext = uiContext;
        _counter = counter ?? PageCounts.Count;
        _listingProbe = new DebouncedProbe<FilenameList.Listing>(_scheduler, uiContext, ApplyListing, probeDelayMs);

        BrowseFolderCommand = new RelayCommand(() =>
        {
            if (_dialogs.BrowseFolder(null) is { } folder) AddPaths(new[] { folder });
        });
        BrowseFilesCommand = new RelayCommand(() =>
        {
            var files = _dialogs.AskOpenFiles("All files (*.*)|*.*");
            if (files.Length > 0) AddPaths(files);
        });
        // Same gate-and-wire as PageCountsViewModel — see its comment for why
        // the RaiseCanExecuteChanged half is not optional (UI-12).
        SaveCommand = new RelayCommand(Save, () => Rows.Count > 0);
        ClearCommand = new RelayCommand(() =>
        {
            _sources.Clear();
            _excluded.Clear();
            _removalBatches.Clear();
            CancelCounting();
            _pageCounts.Clear();   // a re-added file may have changed on disk
            Refresh(immediate: true);
        });
        RemoveSelectedCommand = new RelayCommand(() =>
        {
            if (_selectedPaths.Count == 0) return;

            var batch = new List<string>();
            foreach (var path in _selectedPaths)
                if (_excluded.Add(path)) batch.Add(path);   // Add() is false if already hidden
            if (batch.Count == 0) return;

            _removalBatches.Add(batch);
            SelectedPaths = Array.Empty<string>();
            Reproject();
        }, () => _selectedPaths.Count > 0);
        UndoRemovalCommand = new RelayCommand(() =>
        {
            if (_removalBatches.Count == 0) return;
            var last = _removalBatches[^1];
            _removalBatches.RemoveAt(_removalBatches.Count - 1);
            foreach (var path in last) _excluded.Remove(path);
            Reproject();
        }, () => _removalBatches.Count > 0);
        RestoreRemovedCommand = new RelayCommand(() =>
        {
            if (_excluded.Count == 0) return;
            _excluded.Clear();
            _removalBatches.Clear();
            Reproject();
        }, () => _excluded.Count > 0);
    }

    /// <summary>Re-asks every gated command whether it can still run.
    ///
    /// RelayCommand raises CanExecuteChanged only when told to — there is no
    /// CommandManager hookup — so a CanExecute predicate without a call to this
    /// leaves its button stuck in whatever state it was born in. That is worse
    /// than the ungated button it replaces: a permanently disabled Restore is
    /// unreachable, where a permanently enabled one at least still worked.
    ///
    /// Called from the two funnels every mutation passes through (Refresh and
    /// Reproject), rather than from each handler, so a new mutation path
    /// inherits it instead of having to remember it (UI-12).</summary>
    private void RefreshCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        RemoveSelectedCommand.RaiseCanExecuteChanged();
        UndoRemovalCommand.RaiseCanExecuteChanged();
        RestoreRemovedCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        CancelCounting();
        _countCts.Dispose();
        _countGate.Dispose();
        _listingProbe.Dispose();
    }

    // ------------------------------------------------------------- options
    private bool _includeSubfolders;
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set { if (Set(ref _includeSubfolders, value)) Refresh(immediate: true); }
    }

    private bool _includeExtension = true;
    public bool IncludeExtension
    {
        get => _includeExtension;
        set { if (Set(ref _includeExtension, value)) Refresh(immediate: true); }
    }

    // Typed field, like BulkRename's Find/Replace — debounced, not immediate.
    private string _extensionFilter = "";
    public string ExtensionFilter
    {
        get => _extensionFilter;
        set { if (Set(ref _extensionFilter, value)) Refresh(); }
    }

    private string _countsLine = "";
    public string CountsLine { get => _countsLine; private set => Set(ref _countsLine, value); }

    /// <summary>The two empty states, kept apart for exactly the reason
    /// HistoryViewModel keeps its own IsEmpty/NoMatches apart: a listing with
    /// nothing in it must not read the same as one whose Find box or removals
    /// have hidden every row. The window's empty-state text used to bind
    /// straight to Rows.Count, so typing "zzz" in Find replaced a 200-file
    /// listing with "Drag files or folders here, or browse…" — an invitation
    /// to drop files that are already dropped. CountsLine still tells the
    /// truth underneath, but the Find box made that state easy to reach.
    ///
    /// Both are false whenever a row is actually visible, so a Rows collection
    /// seeded directly with no Build behind it (WindowOverflowTests does that)
    /// still shows neither message, exactly as before.
    ///
    /// Keyed on _sources, NOT on _allRows (audit FL-01). _allRows is what Build
    /// RETURNED, and ExtensionFilter is applied inside Build — so a type filter
    /// matching nothing empties _allRows too, and this flag went true, sending a
    /// user with a folder already added back to "drag files in": precisely the
    /// state the split exists to rule out, reached by the other filter. _sources
    /// answers the question the empty state actually asks — has anything been
    /// added at all? The Find box escaped this only because it filters
    /// downstream, in Reproject, leaving _allRows intact.</summary>
    public bool IsEmpty => Rows.Count == 0 && _sources.Count == 0;

    /// <summary>Something was added, but the projection is showing none of it —
    /// the name filter, the type filter, everything having been removed, or a
    /// folder that turned out to hold no files. See <see cref="IsEmpty"/>.</summary>
    public bool NoMatches => Rows.Count == 0 && _sources.Count > 0;

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Whether <see cref="Status"/> is reporting a FAILURE rather than
    /// a completed action (audit FL-06). The window colours the line from this:
    /// amber is this app's "needs attention", and spending it on "Copied 12
    /// names" left a genuine save failure looking identical to a success.
    /// PageCountsWindow already draws that distinction for the same footer.</summary>
    private bool _statusIsProblem;
    public bool StatusIsProblem { get => _statusIsProblem; private set => Set(ref _statusIsProblem, value); }

    /// <summary>The one way Status is written, so the message and its severity
    /// can never be set apart from each other — a stale problem flag under a
    /// success message would be worse than the single colour it replaces.</summary>
    private void SetStatus(string message, bool problem = false)
    {
        Status = message;
        StatusIsProblem = problem;
    }

    /// <summary>Feedback for the last AddPaths call ("nothing new — already
    /// listed"); blank when it added something.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand BrowseFilesCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand RestoreRemovedCommand { get; }
    public RelayCommand UndoRemovalCommand { get; }

    /// <summary>Called by drag-drop and both pickers. Dedupes against what's
    /// already listed (OrdinalIgnoreCase on the path as given — the same
    /// string a folder/file dialog or WPF's own file-drop handed over) and
    /// always rebuilds, even when nothing new landed, so a browse of an
    /// already-added folder still feels like it did something (the AddNote
    /// itself, not a silently unchanged grid).</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var taken = Intake.Add(_sources, paths);
        _sources.AddRange(taken.Files);
        AddNote = taken.Note("file");
        Refresh(immediate: true);
    }

    /// <summary>Snapshot options/_sources on the UI thread and (re)arm the
    /// listing probe. Empty _sources resolves synchronously — like
    /// BulkRenameViewModel's empty-files fast path, there is genuinely no
    /// I/O to defer, and cancelling whatever's pending stops a slow, now-
    /// stale probe from repopulating Rows after this.</summary>
    private void Refresh(bool immediate = false)
    {
        var opt = new FilenameList.Options(IncludeSubfolders, IncludeExtension, ExtensionFilter);
        var sourcesSnapshot = _sources.ToList();

        if (sourcesSnapshot.Count == 0)
        {
            _listingProbe.Cancel();
            ApplyListing(new FilenameList.Listing(Array.Empty<FilenameList.FileRow>(), 0, ""));
            return;
        }

        _listingProbe.Trigger(() => FilenameList.Build(sourcesSnapshot, opt), immediate);
    }

    /// <summary>Only ever runs on the UI thread (DebouncedProbe's
    /// SynchronizationContext marshal, or the empty-sources fast path
    /// above), so assigning _allRows/_lastIgnored/_lastError here is safe.
    /// This is the only place that APPLIES a disk read — the walk itself
    /// (FilenameList.Build) runs on a worker thread inside Refresh's Trigger
    /// closure, never here.</summary>
    private void ApplyListing(FilenameList.Listing listing)
    {
        _allRows = listing.Rows;
        _lastIgnored = listing.Ignored;
        _lastError = listing.Error;
        Reproject();
    }

    /// <summary>Rebuilds Rows from _allRows in memory — the name filter, the
    /// sort direction and the columns all land here, never a new Build.
    /// Deliberately never touches _listingProbe: only the roots and the
    /// three intake filters (IncludeSubfolders, IncludeExtension,
    /// ExtensionFilter) justify going back to the disk.
    ///
    /// Six call sites reach this: ApplyListing (the probe's marshalled
    /// callback), the Columns/NameFilter/Descending setters (driven by
    /// bindings, so the caller's thread), and RemoveSelectedCommand/
    /// RestoreRemovedCommand (driven by a button click, also the UI thread).
    /// Mutating the ObservableCollection
    /// Rows from more than one thread at once would be unsafe, but in
    /// production every one of those six sites runs on the dispatcher —
    /// the setters and commands because WPF raises property changes and
    /// command execution from bound controls on the UI thread, ApplyListing
    /// because DebouncedProbe marshals through the SynchronizationContext
    /// passed to this class's
    /// constructor. That guarantee comes from the constructor argument, not
    /// from anything in this method. Tests that construct with
    /// uiContext: null do NOT get it: a probe-driven Reproject there runs on
    /// a threadpool thread, and those tests rely on WaitFor to let the probe
    /// settle before touching a setter — nothing in this code enforces
    /// that ordering.</summary>
    private void Reproject()
    {
        IEnumerable<FilenameList.FileRow> visible =
            _allRows.Where(r => !_excluded.Contains(r.FullPath));

        if (NameFilter.Length > 0)
            visible = visible.Where(r =>
                r.Name.Contains(NameFilter, StringComparison.OrdinalIgnoreCase));

        var projected = visible.ToList();
        if (Descending) projected.Reverse();

        // Snapshot BEFORE the rebuild (audit FL-03). Rows.Clear() below raises a
        // Reset, the DataGrid drops its selection on it, and the window pushes
        // that empty selection straight back into SelectedPaths — so by the time
        // the adds finish, _selectedPaths is gone. Keyed on FullPath because a
        // rebuild hands out NEW FileRow instances for the same files; the path is
        // the only stable identity, the same reason _excluded is keyed that way.
        var wasSelected = _selectedPaths;

        Rows.Clear();
        foreach (var row in projected) Rows.Add(WithKnownCount(row));

        RestoreSelection(wasSelected);

        CountsLine = _sources.Count == 0 ? "" : FormatCounts();
        Raise(nameof(IsEmpty));
        Raise(nameof(NoMatches));
        Raise(nameof(OutputText));
        Raise(nameof(OutputCsv));
        Raise(nameof(CopyText));
        Raise(nameof(RemovedCount));
        RefreshCommandStates();

        // Only ever counts what is VISIBLE and not yet known, so narrowing with
        // the type box or the Find box scopes the work before it starts. Once
        // everything on screen is counted this finds nothing to do and returns
        // immediately, which is what makes it safe to call per keystroke.
        if (ShowPages) _ = CountVisiblePdfsAsync();
    }

    /// <summary>Re-attaches a page count this row already earned. FileRow is an
    /// immutable record, so this is a with-copy rather than a mutation: the row
    /// objects are rebuilt on every reproject and the dictionary is what
    /// actually persists.</summary>
    private FilenameList.FileRow WithKnownCount(FilenameList.FileRow row)
    {
        if (_pageCounts.TryGetValue(row.FullPath, out var known))
            return row with { Pages = known.Pages, PageNote = known.Error };

        // A row whose count is running says so. Without this it is blank, which
        // is exactly what a non-PDF looks like — PageCountRow has modelled a
        // Pending flag since the page-counts tool shipped and never rendered it,
        // so "counting" and "nothing to count" were indistinguishable on screen.
        return _countsInFlight.Contains(row.FullPath)
            ? row with { PageNote = CountingMarker }
            : row;
    }

    internal const string CountingMarker = "…";

    private static bool IsPdf(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Counts every visible PDF whose count is neither already known
    /// nor already running, four at a time. Opening a .txt as a PDF is a
    /// guaranteed failure and a wasted disk read, so non-PDFs are never even
    /// asked about — they simply have no page count, which is not an error.</summary>
    private async Task CountVisiblePdfsAsync()
    {
        var token = _countCts.Token;

        var todo = Rows.Select(r => r.FullPath)
            .Where(IsPdf)
            .Where(p => !_pageCounts.ContainsKey(p) && !_countsInFlight.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (todo.Count == 0) return;

        foreach (var path in todo)
        {
            _countsInFlight.Add(path);
            PublishCount(path, new PageCounts.CountResult(path, null, CountingMarker));
        }

        await Task.WhenAll(todo.Select(path => CountOneAsync(path, token)));
    }

    private async Task CountOneAsync(string path, CancellationToken token)
    {
        await _countGate.WaitAsync();
        try
        {
            // Checked between files, not mid-file: _counter is synchronous and
            // runs to completion once started, so this only stops a count that
            // has not begun — the same contract PageCountsViewModel documents.
            if (token.IsCancellationRequested) return;
            var result = await _scheduler.Run(() => _counter(path));
            Apply(() =>
            {
                if (token.IsCancellationRequested) return;
                _countsInFlight.Remove(path);
                _pageCounts[path] = result;
                PublishCount(path, result);
            });
        }
        finally
        {
            _countGate.Release();
        }
    }

    /// <summary>Replaces just the one row this count belongs to, rather than
    /// reprojecting the whole list: a reproject per result would be quadratic on
    /// a big listing and would throw away the scroll position every time a count
    /// landed. ObservableCollection raises Replace, so the grid re-renders that
    /// single row.</summary>
    private void PublishCount(string path, PageCounts.CountResult result)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (!string.Equals(Rows[i].FullPath, path, StringComparison.OrdinalIgnoreCase)) continue;
            Rows[i] = Rows[i] with { Pages = result.Pages, PageNote = result.Error };
            break;
        }

        Raise(nameof(OutputText));
        Raise(nameof(OutputCsv));
        Raise(nameof(CopyText));
    }

    /// <summary>Marshals onto the UI context when there is one — the same shape
    /// DebouncedProbe's apply step uses, because a thread-pool continuation has
    /// no synchronization context of its own to inherit. Mutating Rows off the
    /// dispatcher would be a crash waiting to happen.</summary>
    private void Apply(Action action)
    {
        if (_uiContext is null) action();
        else _uiContext.Post(_ => action(), null);
    }

    /// <summary>Stops anything not yet started and abandons whatever is in
    /// flight. Known counts are deliberately KEPT — they are still true.</summary>
    private void CancelCounting()
    {
        _countCts.Cancel();
        _countCts.Dispose();
        _countCts = new CancellationTokenSource();
        _countsInFlight.Clear();
    }

    /// <summary>Re-establishes the selection a rebuild just destroyed, keeping
    /// only paths that are still visible — restoring a row the filter has since
    /// hidden would let Copy emit a name that is not on screen, which is the
    /// same "shows one thing, produces another" failure this fix exists to end.
    /// Assigns the field rather than the property so the window's own
    /// SelectionChanged push isn't mistaken for a user action.</summary>
    private void RestoreSelection(IReadOnlyList<string> wanted)
    {
        if (wanted.Count == 0) return;

        var visible = new HashSet<string>(
            Rows.Select(r => r.FullPath), StringComparer.OrdinalIgnoreCase);
        var kept = wanted.Where(visible.Contains).ToList();

        _selectedPaths = kept;
        Raise(nameof(SelectedPaths));
        Raise(nameof(CopyText));
        SelectionRestored?.Invoke(this, kept);
    }

    /// <summary>Raised after a reproject has re-established which paths are
    /// selected, so the window can re-apply it to the grid. DataGrid.SelectedItems
    /// is not bindable, so this is the same push shape SelectedPaths already uses
    /// in the other direction — the window pushes down, this pushes back up.</summary>
    public event EventHandler<IReadOnlyList<string>>? SelectionRestored;

    private string FormatCounts()
    {
        var total = _allRows.Count;
        var line = $"{total} file{(total == 1 ? "" : "s")}";
        var removed = RemovedCount;
        if (removed > 0) line += $" · {removed} removed";
        var hidden = total - removed - Rows.Count;
        if (hidden > 0) line += $" · {hidden} filtered out";
        if (_lastIgnored > 0) line += $" · {_lastIgnored} ignored";
        if (_lastError.Length > 0) line += $" · {_lastError}";
        return line;
    }

    private void Save() => _ = SaveAsync();

    internal async Task SaveAsync()
    {
        var table = IsTableShape;
        var (filter, suggested) = table
            ? ("CSV file (*.csv)|*.csv", "filenames.csv")
            : ("Text file (*.txt)|*.txt", "filenames.txt");
        var path = _dialogs.AskSaveFile(filter, suggested);
        if (path is null) return;
        var text = table ? OutputCsv : OutputText;   // read on the UI thread
        try
        {
            // The CSV goes out WITH a byte-order mark; the .txt stays exactly
            // as it was, without one. The difference is Excel: it reads a
            // BOM-less CSV in the system ANSI codepage, so "café.pdf" and
            // "文件.pdf" arrive as mojibake — and filenames are precisely
            // the field this export exists to carry. History.ExportCsv already
            // writes through UTF8Encoding(true) for this exact reason. The
            // two-argument WriteAllText below is left alone deliberately: its
            // default is UTF-8 with no BOM, which is what every .txt this tool
            // has ever written contains.
            await _scheduler.Run(() =>
            {
                if (table) File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                else File.WriteAllText(path, text);
            });
            SetStatus($"Saved to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            // never throw out of a command — the write failing (locked
            // file, gone folder, no permission) is feedback, not a crash
            SetStatus($"Couldn't save: {ex.Message}", problem: true);
        }
    }

    /// <summary>Set by the window's code-behind after Clipboard.SetText
    /// succeeds — Clipboard itself is a WPF/COM type and must never appear in
    /// this class (it isn't safe to touch from the headless MTA tests run
    /// under).</summary>
    public void NoteCopied()
    {
        var copied = SelectedRows().Count;
        SetStatus(_selectedPaths.Count == 0
            ? $"Copied {copied} name{(copied == 1 ? "" : "s")}"
            : $"Copied {copied} of {Rows.Count}");
    }

    /// <summary>Set by the window's code-behind when Clipboard.SetText
    /// throws COMException — the clipboard is a shared, single-owner OS
    /// resource another app can be holding for a moment; this just says so
    /// rather than losing the failure silently.</summary>
    public void NoteClipboardBusy() => SetStatus("Clipboard busy — try again", problem: true);
}
