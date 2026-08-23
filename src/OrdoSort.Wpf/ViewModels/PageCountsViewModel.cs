using System.Collections;
using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One row: a PDF plus its page count once known, or a short note
/// if it couldn't be counted. Modeled on UnlockFileRow (see that class's doc
/// comment for the FileName/Path split reasoning) but with no probe
/// generation to guard — a count never re-runs for a row once requested, so
/// there is no "which of two in-flight answers wins" question to answer the
/// way a re-probed Unlock row has.</summary>
public sealed class PageCountRow : ObservableObject
{
    public string Path { get; }

    // System.IO.Path is qualified because this type's own Path property
    // would otherwise shadow it — same reasoning as UnlockFileRow.FileName.
    public string FileName => System.IO.Path.GetFileName(Path);

    public PageCountRow(string path) => Path = path;

    private int? _pages;
    public int? Pages { get => _pages; private set => Set(ref _pages, value); }

    private string _note = "";
    public string Note { get => _note; private set => Set(ref _note, value); }

    private bool _pending = true;
    public bool Pending { get => _pending; private set => Set(ref _pending, value); }

    internal void Apply(PageCounts.CountResult result)
    {
        Pages = result.Pages;
        Note = result.Error;
        Pending = false;
    }
}

/// <summary>PDF page counts: add files or a folder's PDFs, see a grid of
/// filename -&gt; page count with a short note on whatever couldn't be
/// counted, and a running total. Several files are counted at once because
/// the work is spent waiting on I/O — PDFs may live on a slow network share —
/// and one file's error never stops the rest of the batch: PageCounts.Count
/// itself never throws, and AddFilesAsync applies each result independently
/// as it lands.</summary>
public sealed class PageCountsViewModel : ObservableObject
{
    /// <summary>How many files are counted at once — same figure and the
    /// same reasoning as UnlockViewModel.MaxConcurrentUnlocks: PDFs may live
    /// on a slow network share, and four overlaps most of the waiting
    /// without turning the share into the bottleneck.</summary>
    internal const int MaxConcurrentCounts = 4;

    private readonly IDialogService _dialogs;
    private readonly IWorkScheduler _scheduler;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<string, PageCounts.CountResult> _counter;
    private readonly SemaphoreSlim _countGate = new(MaxConcurrentCounts);

    // Cancelled once, from the window's OnClosed — see Cancel()'s own doc
    // comment for why this needs no cancel-and-replace dance the way
    // UnlockViewModel's probe token does.
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<PageCountRow> Rows { get; } = new();

    public PageCountsViewModel(IDialogService dialogs, IWorkScheduler? scheduler = null,
        SynchronizationContext? uiContext = null, Func<string, PageCounts.CountResult>? counter = null)
    {
        _dialogs = dialogs;
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _uiContext = uiContext;
        _counter = counter ?? PageCounts.Count;

        // Gated, and the gate is WIRED: RelayCommand has no CommandManager
        // hookup, so a predicate without a matching RaiseCanExecuteChanged
        // leaves a button stuck in whatever state it was born in — which is
        // worse than the ungated button it replaces. Rows is the only thing
        // this depends on, so its own CollectionChanged is the complete
        // trigger. Ungated before this, "Save as .txt…" on an empty list
        // opened a save dialog and wrote an empty file (UI-12).
        SaveCommand = new RelayCommand(Save, () => Rows.Count > 0);
        Rows.CollectionChanged += (_, _) => SaveCommand.RaiseCanExecuteChanged();
        ClearCommand = new RelayCommand(() =>
        {
            Rows.Clear();
            Status = "";
            AddNote = "";
            RaiseTotals();
        });
    }

    /// <summary>Feedback for the last AddFilesAsync call ("2 added · 1
    /// ignored…"); blank when it added something with nothing to complain
    /// about.</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    /// <summary>Feedback for the last Save/Copy action ("Saved to X.txt",
    /// "Couldn't save: …", "Clipboard busy — try again"). Separate from
    /// TotalLine, which reports the counting batch itself.</summary>
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>"12 PDFs · 345 pages · 2 unreadable" — the unreadable clause
    /// is omitted entirely when nothing failed. A row still Pending counts
    /// toward the PDF total but not yet toward pages/unreadable, so this
    /// reads as a running total while a batch is still being counted.</summary>
    public string TotalLine
    {
        get
        {
            if (Rows.Count == 0) return "";
            var pages = 0;
            var unreadable = 0;
            foreach (var row in Rows)
            {
                if (row.Pending) continue;
                if (row.Pages is { } p) pages += p;
                else unreadable++;
            }
            var line = $"{Rows.Count} PDF{(Rows.Count == 1 ? "" : "s")} · " +
                       $"{pages} page{(pages == 1 ? "" : "s")}";
            if (unreadable > 0) line += $" · {unreadable} unreadable";
            return line;
        }
    }

    /// <summary>Pure — no backing field, like FilenameListViewModel.OutputText.
    /// Per row: "FileName\tPages", or "FileName\t&lt;note&gt;" for a row that
    /// couldn't be counted; then a blank line; then "Total\t&lt;sum&gt;". Empty
    /// with zero rows — not even the trailing "Total\t0" — so PageCountsWindow's
    /// OnCopy zero-length guard actually fires instead of copying a junk
    /// "Total\t0" blob, and Save writes an empty file rather than a
    /// Total-only one.</summary>
    public string OutputText
    {
        get
        {
            if (Rows.Count == 0) return "";
            var lines = new List<string>();
            var total = 0;
            foreach (var row in Rows)
            {
                if (row.Pages is { } p)
                {
                    lines.Add($"{row.FileName}\t{p}");
                    total += p;
                }
                else
                {
                    lines.Add($"{row.FileName}\t{row.Note}");
                }
            }
            lines.Add("");
            lines.Add($"Total\t{total}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>Called by drag-drop, Add PDFs… and Add a folder's PDFs….
    /// Expands off-thread (Intake.Expand walks folders and filters to .pdf —
    /// the same shared plumbing FilenameListViewModel's Build uses), dedupes
    /// against what's already listed, then counts each new row through the
    /// injected counter, bounded by _countGate so a big drop from a slow
    /// share doesn't flood it with dozens of simultaneous opens. Results are
    /// applied one row at a time as they land, not as a single batch at the
    /// end, so the grid fills in progressively.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var candidates = paths.ToList();
        var expanded = await _scheduler.Run(() =>
            Intake.Expand(candidates, recursive: true, new HashSet<string> { "pdf" }));

        // Expand did the folder walk and the extension filter; Intake.Add does
        // the dedupe half, so this tool shares the one policy rather than
        // keeping its own set.
        var settled = Intake.Add(Rows.Select(r => r.Path), expanded.Files);
        var newRows = new List<PageCountRow>();
        foreach (var p in settled.Files)
        {
            var row = new PageCountRow(p);
            Rows.Add(row);
            newRows.Add(row);
        }

        // Expand reports ONE Ignored count that mixes "wrong extension" with
        // "neither a file nor a folder" and has no breakdown to hand on, so
        // both land under WrongType here. That's no worse than what this note
        // said before, which hedged across the same two cases anyway.
        AddNote = (settled with { WrongType = expanded.Ignored }).Note("PDF");

        RaiseTotals();
        if (newRows.Count == 0) return;

        var token = _cts.Token;
        await Task.WhenAll(newRows.Select(row => CountOneAsync(row, token)));
    }

    private async Task CountOneAsync(PageCountRow row, CancellationToken token)
    {
        await _countGate.WaitAsync();
        try
        {
            // Checked between rows, not mid-row: _counter is synchronous and
            // runs to completion once started — there is nothing to abort
            // partway through. This only stops a row that hasn't started yet.
            if (token.IsCancellationRequested) return;
            var result = await _scheduler.Run(() => _counter(row.Path));
            ApplyResult(row, result);
        }
        finally
        {
            _countGate.Release();
        }
    }

    /// <summary>Marshals onto _uiContext when one is set, same shape as
    /// DebouncedProbe's own apply step — a raw thread-pool continuation has
    /// no synchronization context of its own to inherit.</summary>
    private void ApplyResult(PageCountRow row, PageCounts.CountResult result)
    {
        void Do()
        {
            row.Apply(result);
            RaiseTotals();
        }
        if (_uiContext is null) Do();
        else _uiContext.Post(_ => Do(), null);
    }

    private void RaiseTotals()
    {
        Raise(nameof(TotalLine));
        Raise(nameof(OutputText));
    }

    /// <summary>Removes exactly the rows the window's grid selection holds.
    /// Takes an IList (DataGrid.SelectedItems' own type) rather than a path
    /// collection: the window already has the PageCountRow objects in hand,
    /// and converting to paths first would only have to be undone here.</summary>
    public void RemoveSelected(IList rows)
    {
        foreach (var item in rows.Cast<PageCountRow>().ToList())
            Rows.Remove(item);
        RaiseTotals();
    }

    private void Save() => _ = SaveAsync();

    internal async Task SaveAsync()
    {
        var path = _dialogs.AskSaveFile("Text file (*.txt)|*.txt", "page-counts.txt");
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
    public void NoteCopied() => Status = $"Copied {Rows.Count} row{(Rows.Count == 1 ? "" : "s")}";

    /// <summary>Set by the window's code-behind when Clipboard.SetText
    /// throws COMException — the clipboard is a shared, single-owner OS
    /// resource another app can be holding for a moment; this just says so
    /// rather than losing the failure silently.</summary>
    public void NoteClipboardBusy() => Status = "Clipboard busy — try again";

    /// <summary>Stops any not-yet-started count from starting; a count
    /// already under way finishes (see CountOneAsync's own comment) — same
    /// reasoning as CancelUnlock's own doc comment for why that's the right
    /// contract, not an attempt to abort in-flight work. Called from the
    /// window's OnClosed: a closed window must not keep counting PDFs
    /// invisibly. Unlike UnlockViewModel's probe token, ClearCommand never
    /// touches this — counts never re-run for a cleared row, so there is no
    /// stale-verdict risk a fresh token would need to guard against, and
    /// this class has no equivalent of RequeueAllFilesForProbing to restart.</summary>
    public void Cancel() => _cts.Cancel();
}
