using System.Collections.ObjectModel;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

public enum UnlockResultKind { Ok, Skip, Fail }

public sealed record UnlockResultLine(string Text, UnlockResultKind Kind);

/// <summary>Unlock PDFs: add password-protected files, give (or pick a saved)
/// password, unlock. There is no setting — the unlocked file always keeps its
/// name and place, and the locked original always moves to a dated
/// locked_archive folder beside it. Several files run at once because the work
/// is spent waiting on I/O, but results are reported in the order they were
/// added. A typed "remember as" label is only saved when the password actually
/// unlocked something.</summary>
public sealed class UnlockViewModel : ObservableObject
{
    /// <summary>How many files are unlocked at once. Four overlaps most of the
    /// network waiting without turning a share into the bottleneck; the work
    /// itself is milliseconds, so more threads buy nothing.</summary>
    internal const int MaxConcurrentUnlocks = 4;

    private readonly Config _cfg;
    private readonly Action _saveCfg;

    // Test seams, following the fixture pattern the shell uses: the real app
    // passes neither, tests inject a scripted unlocker to make cancellation
    // and the runs-alone bound deterministic instead of timing-hopeful.
    private readonly Func<string, string, Unlock.UnlockResult> _unlock;
    private readonly Func<string, long> _fileSize;

    /// <summary>Set while a batch is being added, so the command re-queries
    /// once for the batch instead of once per file.</summary>
    private bool _bulkAdding;

    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<SavedPassword> Saved { get; }
    public ObservableCollection<UnlockResultLine> ResultLines { get; } = new();

    public UnlockViewModel(Config cfg, Action saveCfg,
        Func<string, string, Unlock.UnlockResult>? unlocker = null,
        Func<string, long>? fileSize = null)
    {
        _cfg = cfg;
        _saveCfg = saveCfg;
        _unlock = unlocker ?? ((path, password) => Unlock.UnlockPdf(path, password));
        _fileSize = fileSize ?? (path =>
        {
            // 0 on failure sends it down the ordinary path, where UnlockPdf
            // reports the real problem readably
            try { return new FileInfo(path).Length; } catch { return 0L; }
        });
        Saved = new ObservableCollection<SavedPassword>(cfg.SavedPasswords);
        UnlockCommand = new AsyncRelayCommand(UnlockAsync, () => Files.Count > 0);
        CancelCommand = new RelayCommand(CancelUnlock, () => IsUnlocking);
        ClearCommand = new RelayCommand(() =>
        {
            Files.Clear();
            ResultLines.Clear();
            Summary = "";
            AddNote = "";
        });
        Files.CollectionChanged += (_, _) =>
        {
            if (!_bulkAdding) UnlockCommand.RaiseCanExecuteChanged();
        };
    }

    private bool _isUnlocking;

    /// <summary>True while a batch runs — shows the Cancel button and gates it.</summary>
    public bool IsUnlocking
    {
        get => _isUnlocking;
        private set { if (Set(ref _isUnlocking, value)) CancelCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Stop the batch: files already done stay done (each one is
    /// individually safe), files not yet started report cancelled instead of
    /// silently running to the end. Also called when the window closes — a
    /// closed window must not keep moving files.</summary>
    internal void CancelUnlock() => _cts?.Cancel();

    private string _password = "";
    public string Password { get => _password; set => Set(ref _password, value); }

    private SavedPassword? _selectedSaved;
    public SavedPassword? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            if (Set(ref _selectedSaved, value) && value is not null)
                Password = PasswordVault.Reveal(value.Password);
        }
    }

    private string _rememberLabel = "";
    public string RememberLabel { get => _rememberLabel; set => Set(ref _rememberLabel, value); }

    /// <summary>The verdict line: live progress while running, then
    /// "3 unlocked · 1 already unlocked · 1 failed".</summary>
    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Feedback for the last add/drop ("2 added · 1 ignored…").</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    public AsyncRelayCommand UnlockCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        // File.Exists is a network round trip when the files live on a share,
        // and this used to run once per dropped path ON THE UI THREAD — which
        // is what made dropping a folder's worth of documents feel stuck. The
        // existence checks go off-thread; only the list update comes back.
        var candidates = paths.ToList();
        var already = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);

        var (keep, ignored) = await Task.Run(() =>
        {
            var keep = new List<string>();
            var ignored = 0;
            // a set, not ObservableCollection.Contains: that was a linear scan
            // per candidate, so a big drop cost quadratic time before it even
            // touched the disk
            var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
            foreach (var p in candidates)
            {
                if (p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    && seen.Add(p) && File.Exists(p))
                    keep.Add(p);
                else
                    ignored++;
            }
            return (keep, ignored);
        });

        // Re-checked against the LIVE list, not the snapshot taken before the
        // await: a second drop can have read the same list and be adding the
        // same file right now. Cheap — this is the handful that passed, not the
        // whole batch.
        var live = new HashSet<string>(Files, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        // one CanExecute re-query for the whole batch instead of one per file
        _bulkAdding = true;
        try
        {
            foreach (var p in keep)
                if (live.Add(p)) { Files.Add(p); added++; }
        }
        finally { _bulkAdding = false; }
        UnlockCommand.RaiseCanExecuteChanged();
        ignored += keep.Count - added;

        // a silently-shrinking drop reads as "it didn't work" — say what happened
        AddNote = added == 0 && ignored > 0
            ? $"nothing added — {ignored} item{(ignored == 1 ? " isn't a PDF" : "s aren't PDFs")} (or already listed)"
            : ignored > 0
                ? $"{added} added · {ignored} ignored (not PDFs, or already listed)"
                : "";
    }

    public void RemoveFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths.ToList()) Files.Remove(p);
        AddNote = "";
    }

    internal async Task UnlockAsync()
    {
        if (Files.Count == 0)
        {
            Summary = "Add at least one PDF first.";
            return;
        }
        ResultLines.Clear();
        var password = Password;
        var paths = Files.ToList();
        var results = new Unlock.UnlockResult[paths.Count];

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsUnlocking = true;
        try
        {
            var ct = cts.Token;
            var finished = 0;

            // A file at or over the large threshold runs ALONE, after the
            // rest: buffered files each cost ~3x their size in memory while in
            // flight, and a streamed giant saturates the share — either way,
            // four at once is how "large" becomes "out of memory" or "the
            // share crawls". Ordinary files still overlap their waiting.
            var small = new List<int>();
            var large = new List<int>();
            for (var i = 0; i < paths.Count; i++)
                (_fileSize(paths[i]) >= Unlock.LargeFileThresholdBytes ? large : small).Add(i);

            using var gate = new SemaphoreSlim(MaxConcurrentUnlocks);
            await Task.WhenAll(small.Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    // the check sits AFTER the gate: a cancelled batch drains
                    // its queue as "cancelled" instead of starting more work
                    if (ct.IsCancellationRequested) { results[i] = Cancelled(paths[i]); return; }
                    // always in place, under the original name: the locked one
                    // is moved to a dated archive folder, never overwritten
                    results[i] = await Task.Run(() => _unlock(paths[i], password));
                }
                finally
                {
                    gate.Release();
                }
                Summary = $"Unlocking {Interlocked.Increment(ref finished)} of {paths.Count}…";
            }));

            foreach (var i in large)
            {
                if (ct.IsCancellationRequested) { results[i] = Cancelled(paths[i]); continue; }
                Summary = $"Unlocking {finished + 1} of {paths.Count} (large file — running alone)…";
                results[i] = await Task.Run(() => _unlock(paths[i], password));
                finished++;
            }
        }
        finally
        {
            _cts = null;
            IsUnlocking = false;
        }

        // reported in the order they were added, not the order they finished —
        // a list that reshuffles itself is harder to read than a slower one
        int ok = 0, skip = 0, fail = 0, cancelled = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            var r = results[i];
            var name = Path.GetFileName(paths[i]);
            if (r.Ok)
            {
                ok++;
                ResultLines.Add(new UnlockResultLine(
                    r.ArchivedTo is { } kept
                        ? $"✓  {name} — unlocked, locked original kept in "
                          + $"{Path.GetFileName(Path.GetDirectoryName(kept)!)}"
                        : $"✓  {name}  →  {Path.GetFileName(r.NewPath!)}",
                    UnlockResultKind.Ok));
            }
            else if (r.Status == "not_encrypted")
            {
                skip++;
                ResultLines.Add(new UnlockResultLine($"•  {name} — {r.Message}",
                    UnlockResultKind.Skip));
            }
            else if (r.Status == "cancelled")
            {
                cancelled++;
                ResultLines.Add(new UnlockResultLine($"•  {name} — cancelled before it started",
                    UnlockResultKind.Skip));
            }
            else
            {
                fail++;
                ResultLines.Add(new UnlockResultLine($"✗  {name} — {r.Message}",
                    UnlockResultKind.Fail));
            }
        }

        var parts = new List<string> { $"{ok} unlocked" };
        if (skip > 0) parts.Add($"{skip} already unlocked");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        if (fail > 0) parts.Add($"{fail} failed");
        Summary = string.Join(" · ", parts);

        // never remember a password that opened nothing — a saved wrong
        // password would just fail again silently next session
        if (ok > 0) RememberIfAsked();
    }

    private static Unlock.UnlockResult Cancelled(string path) =>
        new("cancelled", path, Message: "Cancelled — the file was not touched.");

    /// <summary>"Remember this password" — stored DPAPI-protected, saved to
    /// config immediately so the Unlock tool works next session.</summary>
    private void RememberIfAsked()
    {
        var label = RememberLabel.Trim();
        if (label.Length == 0 || Password.Length == 0) return;
        var entry = new SavedPassword { Label = label, Password = PasswordVault.Protect(Password) };
        _cfg.SavedPasswords.Add(entry);
        Saved.Add(entry);
        RememberLabel = "";
        _saveCfg();
    }
}
