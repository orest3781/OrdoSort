using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Drop or browse files/folders, see a natural-sorted list of their
/// filenames, copy it or save it as .txt. The whole tool is a thin UI shell
/// over FilenameList.Build — this class owns only the roots people have
/// added and the debounced recompute of the list from them, the same
/// snapshot-off-thread-apply shape BulkRenameViewModel uses for its own
/// preview (see that class's doc comment for why the compute must never run
/// on the UI thread, and why the applied result must be exactly what a
/// caller last saw rendered).</summary>
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

    public ObservableCollection<string> Rows { get; } = new();

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
        SaveCommand = new RelayCommand(Save);
        ClearCommand = new RelayCommand(() =>
        {
            _sources.Clear();
            Refresh(immediate: true);
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

    /// <summary>Pure — no backing field. The window's Copy button and Save
    /// both read this directly rather than binding to it.</summary>
    public string OutputText => FilenameList.ToText(Rows);

    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>Called by drag-drop and both pickers. Dedupes against what's
    /// already listed (OrdinalIgnoreCase on the path as given — the same
    /// string a folder/file dialog or WPF's own file-drop handed over) and
    /// always rebuilds, even when nothing new landed, so a browse of an
    /// already-added folder still feels like it did something (the AddNote
    /// itself, not a silently unchanged grid).</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths)
        {
            if (_sources.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            _sources.Add(p);
            added++;
        }
        AddNote = added == 0 ? "nothing new — already listed" : "";
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
            ApplyListing(new FilenameList.Listing(Array.Empty<string>(), 0, ""));
            return;
        }

        _listingProbe.Trigger(() => FilenameList.Build(sourcesSnapshot, opt), immediate);
    }

    /// <summary>Only ever runs on the UI thread (DebouncedProbe's
    /// SynchronizationContext marshal, or the empty-sources fast path
    /// above), so mutating Rows here is safe.</summary>
    private void ApplyListing(FilenameList.Listing listing)
    {
        Rows.Clear();
        foreach (var name in listing.Names) Rows.Add(name);
        CountsLine = _sources.Count == 0 ? "" : FormatCounts(listing);
        Raise(nameof(OutputText));
    }

    private static string FormatCounts(FilenameList.Listing listing)
    {
        var count = listing.Names.Count;
        var line = $"{count} file{(count == 1 ? "" : "s")}";
        if (listing.Ignored > 0) line += $" · {listing.Ignored} ignored";
        if (listing.Error.Length > 0) line += $" · {listing.Error}";
        return line;
    }

    private void Save() => _ = SaveAsync();

    internal async Task SaveAsync()
    {
        var path = _dialogs.AskSaveFile("Text file (*.txt)|*.txt", "filenames.txt");
        if (path is null) return;
        var text = OutputText;   // read on the UI thread before offloading
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
