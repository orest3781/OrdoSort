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

    public int RemovedCount => _allRows.Count(r => _excluded.Contains(r.FullPath));

    /// <summary>Pushed in by the window on SelectionChanged — DataGrid's
    /// SelectedItems is not bindable.</summary>
    private IReadOnlyList<string> _selectedPaths = Array.Empty<string>();
    public IReadOnlyList<string> SelectedPaths
    {
        get => _selectedPaths;
        set
        {
            _selectedPaths = value;
            Raise(nameof(SelectedPaths));
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
            Reproject();
        }
    }

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

    public string OutputText => FilenameList.ToText(Rows.ToList(), Columns);
    public string OutputCsv => FilenameList.ToCsv(Rows.ToList(), Columns);

    public FilenameListViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, int probeDelayMs = 300)
    {
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
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
        SaveCommand = new RelayCommand(Save);
        ClearCommand = new RelayCommand(() =>
        {
            _sources.Clear();
            _excluded.Clear();
            Refresh(immediate: true);
        });
        RemoveSelectedCommand = new RelayCommand(() =>
        {
            if (_selectedPaths.Count == 0) return;
            foreach (var path in _selectedPaths) _excluded.Add(path);
            SelectedPaths = Array.Empty<string>();
            Reproject();
        });
        RestoreRemovedCommand = new RelayCommand(() =>
        {
            if (_excluded.Count == 0) return;
            _excluded.Clear();
            Reproject();
        });
    }

    public void Dispose() => _listingProbe.Dispose();

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

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

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

        Rows.Clear();
        foreach (var row in projected) Rows.Add(row);

        CountsLine = _sources.Count == 0 ? "" : FormatCounts();
        Raise(nameof(OutputText));
        Raise(nameof(OutputCsv));
        Raise(nameof(RemovedCount));
    }

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
        var (filter, suggested) = IsTableShape
            ? ("CSV file (*.csv)|*.csv", "filenames.csv")
            : ("Text file (*.txt)|*.txt", "filenames.txt");
        var path = _dialogs.AskSaveFile(filter, suggested);
        if (path is null) return;
        var text = IsTableShape ? OutputCsv : OutputText;   // read on the UI thread
        try
        {
            await _scheduler.Run(() => File.WriteAllText(path, text));
            Status = $"Saved to {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            // never throw out of a command — the write failing (locked
            // file, gone folder, no permission) is feedback, not a crash
            Status = $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>Set by the window's code-behind after Clipboard.SetText
    /// succeeds — Clipboard itself is a WPF/COM type and must never appear
    /// in this class (it isn't safe to touch from the headless MTA tests
    /// run under).</summary>
    public void NoteCopied() => Status = $"Copied {Rows.Count} name{(Rows.Count == 1 ? "" : "s")}";

    /// <summary>Set by the window's code-behind when Clipboard.SetText
    /// throws COMException — the clipboard is a shared, single-owner OS
    /// resource another app can be holding for a moment; this just says so
    /// rather than losing the failure silently.</summary>
    public void NoteClipboardBusy() => Status = "Clipboard busy — try again";
}
