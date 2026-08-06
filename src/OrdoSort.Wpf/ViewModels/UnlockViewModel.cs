using System.Collections.ObjectModel;
using System.Security.Cryptography;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

public enum UnlockResultKind { Ok, Skip, Fail }

public sealed record UnlockResultLine(string Text, UnlockResultKind Kind);

/// <summary>Unlock PDFs: add password-protected files, type a password,
/// unlock. There is no picker — every saved password is tried automatically,
/// so the box is only for a password that isn't saved yet. The unlocked file
/// always keeps its name and place, and the locked original always moves to a
/// dated locked_archive folder beside it. Several files run at once because
/// the work is spent waiting on I/O, but results are reported in the order
/// they were added. A typed password that unlocked something and isn't
/// already saved earns a one-click save offer after the run.</summary>
public sealed class UnlockViewModel : ObservableObject
{
    /// <summary>How many files are unlocked at once. Four overlaps most of the
    /// network waiting without turning a share into the bottleneck; the work
    /// itself is milliseconds, so more threads buy nothing.</summary>
    internal const int MaxConcurrentUnlocks = 4;

    private readonly Config _cfg;
    // True return means the write reached disk — the constructor's
    // load-time notice (below) and the minor finding in fix round 1 both
    // depend on knowing that, not just that a save was attempted.
    private readonly Func<bool> _trySaveCfg;
    // Null in the handful of tests that don't care about the load-time
    // notice (Step 4) — every call site is a null-conditional ?.Info, so a
    // missing dialog service just means the notice is silently skipped,
    // never a crash.
    private readonly IDialogService? _dialogs;

    // Test seams, following the fixture pattern the shell uses: the real app
    // passes neither, tests inject a scripted unlocker to make cancellation
    // and the runs-alone bound deterministic instead of timing-hopeful.
    private readonly Func<string, string, Unlock.UnlockResult> _unlock;
    private readonly Func<string, long> _fileSize;
    // Same pattern: the real app never passes this (defaults to the real
    // DPAPI call), but real DPAPI essentially never fails, so a test proving
    // ReprotectLegacyPlaintext's failure handling (fix round 1, Important 1)
    // needs a way to force that failure deterministically instead of hoping
    // to provoke a real CryptographicException.
    private readonly Func<string, string> _protect;

    /// <summary>Set while a batch is being added, so the command re-queries
    /// once for the batch instead of once per file.</summary>
    private bool _bulkAdding;

    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<SavedPassword> Saved { get; }
    public ObservableCollection<UnlockResultLine> ResultLines { get; } = new();

    public UnlockViewModel(Config cfg, Func<bool> trySaveCfg,
        Func<string, string, Unlock.UnlockResult>? unlocker = null,
        Func<string, long>? fileSize = null,
        IDialogService? dialogs = null,
        Func<string, string>? protect = null)
    {
        _cfg = cfg;
        _trySaveCfg = trySaveCfg;
        _dialogs = dialogs;
        _unlock = unlocker ?? ((path, password) => Unlock.UnlockPdf(path, password));
        _fileSize = fileSize ?? (path =>
        {
            // 0 on failure sends it down the ordinary path, where UnlockPdf
            // reports the real problem readably
            try { return new FileInfo(path).Length; } catch { return 0L; }
        });
        _protect = protect ?? PasswordVault.Protect;
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
        SaveBannerCommand = new RelayCommand(SaveBannerPassword, () => SaveBannerName.Trim().Length > 0);
        RemoveSavedCommand = new RelayCommand(RemoveSelectedSaved, () => SelectedSavedEntry is not null);
        Files.CollectionChanged += (_, _) =>
        {
            if (!_bulkAdding) UnlockCommand.RaiseCanExecuteChanged();
        };

        // Load-time sweep (audit finding 4.3[A]): ReprotectLegacyPlaintext
        // used to run ONLY on the three save paths above, so a hand-edited
        // or legacy plaintext entry sat readable in config.json forever if
        // nobody happened to touch the saved-password list. This surface
        // owns that list, so its own construction — which runs every time
        // the Unlock window opens, i.e. effectively "on load" — is where
        // the sweep belongs.
        //
        // Gated on the sweep's own Converted flag: a config with nothing to
        // convert must not be re-saved. Saving is not a by-the-way action
        // on a shared config.json — it is a real, conflict-prone write on
        // an SMB share, and this constructor runs on every single open, not
        // once. An unconditional save here would rewrite (and risk
        // write-conflicting) that shared file every time anyone so much as
        // opened the Unlock window, for zero content change (see
        // UnlockViewModelTests.LoadDoesNotRewriteAnAlreadyProtectedConfig).
        //
        // Protecting converts an entry every station could read into one
        // only THIS Windows account can (DPAPI CurrentUser scope) — the
        // point of the fix, since plaintext-readable-by-everyone is the
        // exposure, but it silently breaks a shared password for a
        // colleague on another machine unless they're told. Hence the
        // one-time notice, fired only when a conversion actually reached
        // disk (fix round 1: _trySaveCfg's own failure path already warns
        // the user "not saved" — piling an unconditional "now protected" on
        // top of that would be actively misleading).
        //
        // ReprotectLegacyPlaintext itself never throws (fix round 1,
        // Important 1) — see its doc comment — so nothing here needs its
        // own try/catch to keep the window openable.
        var sweep = ReprotectLegacyPlaintext();
        if (sweep.Converted)
        {
            if (_trySaveCfg())
            {
                _dialogs?.Info(
                    "Some saved passwords in the Unlock tool were stored in plain text " +
                    "and have now been protected for this Windows account. If this saved-" +
                    "password list is shared with a colleague on another computer, their " +
                    "copy of the password will no longer work — they'll need to re-enter " +
                    "and re-save it on their own machine.",
                    "OrdoSort — saved passwords protected");
            }
        }
        else if (sweep.Failed)
        {
            _dialogs?.Warn(
                "One or more saved passwords could not be protected just now — a " +
                "Windows security error, not a problem with the passwords themselves. " +
                "They were left exactly as they were and will still work; this will " +
                "be retried the next time the Unlock window opens.",
                "OrdoSort — some saved passwords could not be protected");
        }
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
    public string Password
    {
        get => _password;
        set
        {
            // Retyping the box after a run invalidates the offer that run
            // earned: an unvalidated password must never look like it's
            // covered by the "save it as:" banner. Hide-only (not a full
            // ResetBanner) — the name the user was typing stays put, and
            // the real protection is _bannerPassword below, which still
            // snapshots the password that actually earned the offer even
            // if a click slips through before the UI catches up.
            if (Set(ref _password, value) && SaveBannerVisible) SaveBannerVisible = false;
        }
    }

    /// <summary>The verdict line: live progress while running, then
    /// "3 unlocked · 1 already unlocked · 1 failed".</summary>
    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Feedback for the last add/drop ("2 added · 1 ignored…").</summary>
    private string _addNote = "";
    public string AddNote { get => _addNote; private set => Set(ref _addNote, value); }

    // ------------------------------------------------------------ save banner
    // Appears after a run where the TYPED password (not a saved one) is what
    // unlocked something, and it isn't already one of the saved passwords —
    // there's nothing new to offer saving otherwise. Resets at the start of
    // every run and when the window closes, so a stale offer from a previous
    // batch never lingers into one that didn't earn it.
    private bool _saveBannerVisible;
    public bool SaveBannerVisible { get => _saveBannerVisible; private set => Set(ref _saveBannerVisible, value); }

    /// <summary>The typed password that actually earned the banner — NOT
    /// necessarily what <see cref="Password"/> holds by the time Save is
    /// clicked. Saved is protected from this snapshot, not from the live
    /// box, so retyping after a run (even mid-race with a queued click)
    /// can never cause an unvalidated password to be stored — the spec
    /// invariant is "a password is saved only when it unlocks something".</summary>
    private string _bannerPassword = "";

    private string _saveBannerText = "";
    public string SaveBannerText { get => _saveBannerText; private set => Set(ref _saveBannerText, value); }

    private string _saveBannerName = "";
    public string SaveBannerName
    {
        get => _saveBannerName;
        set { if (Set(ref _saveBannerName, value)) SaveBannerCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand SaveBannerCommand { get; private set; } = null!;

    /// <summary>Reset the offer — called at the start of every run and when
    /// the Unlock window closes, so it never survives past the batch that
    /// earned it.</summary>
    internal void ResetBanner()
    {
        SaveBannerVisible = false;
        SaveBannerText = "";
        SaveBannerName = "";
        _bannerPassword = "";
    }

    /// <summary>Saves the password that WON THE RUN (the <see
    /// cref="_bannerPassword"/> snapshot), never whatever happens to be
    /// sitting in the live <see cref="Password"/> box — the same
    /// protect-and-persist flow the old "remember this password" row used,
    /// just triggered by the banner instead of a standing field.</summary>
    private void SaveBannerPassword()
    {
        var label = SaveBannerName.Trim();
        if (label.Length == 0) return;
        var entry = new SavedPassword { Label = label, Password = PasswordVault.Protect(_bannerPassword) };
        _cfg.SavedPasswords.Add(entry);
        Saved.Add(entry);
        ReprotectLegacyPlaintext();
        _trySaveCfg();
        SaveBannerVisible = false;
        SaveBannerName = "";
        _bannerPassword = "";
    }

    // ------------------------------------------------------- manage saved…
    // Backs the Manage saved… dialog (a small modal owned by the Unlock
    // window): add/remove saved passwords directly, persisting immediately —
    // there's no separate OK step, unlike Settings' old build-on-OK editor.
    private SavedPassword? _selectedSavedEntry;
    public SavedPassword? SelectedSavedEntry
    {
        get => _selectedSavedEntry;
        set { if (Set(ref _selectedSavedEntry, value)) RemoveSavedCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand RemoveSavedCommand { get; private set; } = null!;

    private void RemoveSelectedSaved()
    {
        if (SelectedSavedEntry is { } p)
        {
            _cfg.SavedPasswords.Remove(p);
            Saved.Remove(p);
            ReprotectLegacyPlaintext();
            _trySaveCfg();
        }
        SelectedSavedEntry = Saved.FirstOrDefault();
    }

    /// <summary>False when either field is blank — the dialog shows a nudge
    /// instead of silently doing nothing.</summary>
    public bool AddSavedPassword(string label, string plain)
    {
        if (label.Trim().Length == 0 || plain.Length == 0) return false;
        var entry = new SavedPassword { Label = label.Trim(), Password = PasswordVault.Protect(plain) };
        _cfg.SavedPasswords.Add(entry);
        Saved.Add(entry);
        ReprotectLegacyPlaintext();
        _trySaveCfg();
        return true;
    }

    /// <summary>Opportunistic upgrade: any saved password not yet
    /// DPAPI-protected (typically a hand-edited plaintext value in
    /// config.json) is protected in place. Called both right before this
    /// surface persists (the three save-path callers below) and once from
    /// the constructor as a load-time sweep, so a plaintext entry cannot
    /// outlive being noticed just because the saved-password list is never
    /// otherwise touched. Mirrors the self-healing Settings used to do on
    /// every save — now anchored to the surface that actually owns the
    /// saved-passwords list. Mutates the SAME <see cref="SavedPassword"/>
    /// instances that <c>Saved</c> holds (Saved is seeded from
    /// <c>_cfg.SavedPasswords</c> by reference, not by copy), so the
    /// in-memory list is automatically in sync — no separate pass over
    /// <c>Saved</c> is needed.
    ///
    /// DPAPI's <see cref="PasswordVault.Protect"/> can itself fail
    /// (<see cref="CryptographicException"/> — rare, but real: profile-load
    /// edge cases and similar). Before the constructor called this, that
    /// could only ever happen inside a deliberate save action; now it can
    /// happen every time the Unlock window opens, and an unhandled throw
    /// here would escape the constructor and leave the window unable to
    /// open, deterministically, forever (fix round 1, Important 1). So this
    /// never lets an exception escape: on a failure, it rolls back every
    /// entry THIS call touched, restoring their original plaintext, rather
    /// than leave a half-swept <c>_cfg</c> sitting in memory, unpersisted —
    /// a secret that becomes unrecoverable is far worse than one that stays
    /// plaintext, and a half-converted <c>_cfg</c> is exactly what would
    /// silently reach disk the next time ANYTHING else saves this
    /// config.</summary>
    /// <returns><c>Converted</c> is true if at least one entry was
    /// protected and none failed — callers that persist unconditionally
    /// (the three above) can ignore this; the constructor's load-time
    /// sweep uses it to skip saving (and notifying) a config that had
    /// nothing to convert. <c>Failed</c> is true if DPAPI itself failed on
    /// at least one entry, in which case <c>Converted</c> is always false
    /// (a failure rolls back the whole attempt) — the constructor uses this
    /// to warn instead of claiming success.</returns>
    private (bool Converted, bool Failed) ReprotectLegacyPlaintext()
    {
        var touched = new List<(SavedPassword Entry, string Original)>();
        try
        {
            foreach (var sp in _cfg.SavedPasswords)
            {
                if (!PasswordVault.IsProtected(sp.Password))
                {
                    var original = sp.Password;
                    sp.Password = _protect(sp.Password);
                    touched.Add((sp, original));
                }
            }
            return (touched.Count > 0, false);
        }
        catch (CryptographicException)
        {
            foreach (var (entry, original) in touched)
                entry.Password = original;
            return (false, true);
        }
    }

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
        // a stale offer from the previous run must not survive into this one
        ResetBanner();

        ResultLines.Clear();
        var password = Password;
        var paths = Files.ToList();
        var results = new Unlock.UnlockResult[paths.Count];
        var viaTyped = new bool[paths.Count];

        // Per-file candidates: the typed password first (if non-blank), then
        // every saved password's revealed value — skipping one that's
        // already equal to what was typed, so it's never tried twice. Built
        // ONCE (not per file): Password and Saved don't change mid-run.
        var typedNonBlank = password.Length > 0;
        var candidates = new List<string>();
        if (typedNonBlank) candidates.Add(password);
        var typedAlreadySaved = false;
        foreach (var sp in Saved)
        {
            var revealed = PasswordVault.Reveal(sp.Password);
            if (typedNonBlank && revealed == password) { typedAlreadySaved = true; continue; }
            candidates.Add(revealed);
        }
        // nothing typed and nothing saved: still probe once with "" so a
        // not_encrypted file is still recognized and an encrypted one still
        // gets a definite (wrong_password) outcome, exactly as before
        if (candidates.Count == 0) candidates.Add("");
        var typedCount = typedNonBlank ? 1 : 0;

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
                    (results[i], viaTyped[i]) =
                        await Task.Run(() => TryCandidates(paths[i], candidates, typedCount));
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
                (results[i], viaTyped[i]) =
                    await Task.Run(() => TryCandidates(paths[i], candidates, typedCount));
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
        int ok = 0, skip = 0, fail = 0, cancelled = 0, okViaTyped = 0;
        for (var i = 0; i < paths.Count; i++)
        {
            var r = results[i];
            var name = Path.GetFileName(paths[i]);
            if (r.Ok)
            {
                ok++;
                if (viaTyped[i]) okViaTyped++;
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

        // the save offer: only the TYPED password earns one, and only when it
        // isn't already saved — a password already on the list has nothing
        // new to offer
        if (okViaTyped > 0 && !typedAlreadySaved)
        {
            // Snapshot the password that WON, not whatever the live box
            // holds by the time this runs — they're the same value here,
            // but this is the one place that matters: SaveBannerPassword
            // must protect this snapshot, never Password itself.
            _bannerPassword = password;
            SaveBannerText = $"✓ {okViaTyped} unlocked with a new password — save it as:";
            SaveBannerVisible = true;
        }
    }

    /// <summary>Tries each candidate password against one file in order,
    /// stopping at the first success. A non-password failure (not encrypted,
    /// or a real error) stops the search too — a different password changes
    /// nothing about either outcome. <paramref name="typedCount"/> is 1 when
    /// the typed password occupies candidate 0, else 0 — it tells the caller
    /// whether the winning candidate was what was typed (for the save
    /// banner) versus a saved password.</summary>
    private (Unlock.UnlockResult Result, bool ViaTyped) TryCandidates(
        string path, List<string> candidates, int typedCount)
    {
        var last = new Unlock.UnlockResult("wrong_password", path, Message: "That password didn't work.");
        for (var i = 0; i < candidates.Count; i++)
        {
            last = _unlock(path, candidates[i]);
            if (last.Ok) return (last, i < typedCount);
            if (last.Status != "wrong_password") return (last, false);
        }
        return (last, false);
    }

    private static Unlock.UnlockResult Cancelled(string path) =>
        new("cancelled", path, Message: "Cancelled — the file was not touched.");
}
