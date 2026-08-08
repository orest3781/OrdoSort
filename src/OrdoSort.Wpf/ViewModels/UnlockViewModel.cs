using System.Collections.ObjectModel;
using System.Security.Cryptography;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.ViewModels;

public enum UnlockResultKind { Ok, Skip, Fail }

public sealed record UnlockResultLine(string Text, UnlockResultKind Kind);

/// <summary>What Unlock.ProbeReadiness found for one row, mirrored 1:1 onto
/// its Status strings (not_encrypted/ready/needs_password/in_use/unreadable)
/// plus Pending for "hasn't been probed yet" — a state Core has no need for
/// since it never sits idle mid-answer the way a row in this list does.</summary>
public enum ReadinessStatus { Pending, NotEncrypted, Ready, NeedsPassword, InUse, Unreadable }

/// <summary>One row in the Unlock file list: a dropped/added path plus
/// whatever the readiness probe has found out about it so far. Files used to
/// be a plain ObservableCollection&lt;string&gt; — there was nowhere to hang
/// per-file probe state, and every consumer (UnlockAsync's indexing,
/// ClearCommand, the dedupe set in AddFilesAsync, UnlockCommand's
/// Files.Count guard, RemoveFiles, and every test that touched Files) had to
/// be walked and updated for this row type to replace it (2026-08-08
/// readiness-probe plan, Task 2 Step 1).</summary>
public sealed class UnlockFileRow : ObservableObject
{
    public string Path { get; }

    public UnlockFileRow(string path) => Path = path;

    public ReadinessStatus Status { get; private set; } = ReadinessStatus.Pending;

    /// <summary>The exact wording from ProbeResult.Message for the four
    /// states worth telling the user about — "" for Pending and
    /// NotEncrypted, which stay deliberately quiet (Task 2 Step 3: neither
    /// is a problem and neither should read like one).</summary>
    public string Message { get; private set; } = "";

    /// <summary>Bumped every time a probe is (re-)requested for this row.
    /// See UnlockViewModel.ProbeRowsAsync's doc comment: this is what lets
    /// the most recently REQUESTED probe win over one that merely finishes
    /// later, without needing to cancel the loser outright.</summary>
    internal long ProbeGeneration;

    internal void SetProbeResult(ReadinessStatus status, string message)
    {
        Status = status;
        Message = message;
        Raise(nameof(Status));
        Raise(nameof(Message));
        Raise(nameof(DisplayText));
        Raise(nameof(ToolTipText));
    }

    /// <summary>Back to the same quiet default a never-yet-probed row has.
    /// Used when a saved-password change means the CURRENT verdict no
    /// longer reflects reality (risk 4) — cleared synchronously, before the
    /// re-probe that will replace it even starts, so a stale claim is never
    /// shown for even the length of that re-probe.</summary>
    internal void ResetToPending() => SetProbeResult(ReadinessStatus.Pending, "");

    /// <summary>Filename plus a short, deliberately narrow claim about what
    /// the probe actually tested — never "this will unlock": the probe runs
    /// with no typed password, but the real unlock tries the typed password
    /// FIRST (UnlockAsync below), so the only honest claim is that a SAVED
    /// password already opens the file (risk 2). System.IO.Path is
    /// qualified because this type's own Path property would otherwise
    /// shadow it.</summary>
    public string DisplayText => System.IO.Path.GetFileName(Path) + (Status switch
    {
        ReadinessStatus.Ready => "  —  a saved password opens this",
        ReadinessStatus.NeedsPassword => "  —  needs a password",
        ReadinessStatus.InUse => "  —  in use, couldn't check",
        ReadinessStatus.Unreadable => "  —  couldn't be read",
        _ => "",
    });

    /// <summary>Full path always; the probe's own message appended when
    /// there is one worth showing (hover detail for the four non-quiet
    /// states — the DisplayText suffix is deliberately short, this is where
    /// the fuller wording lives).</summary>
    public string ToolTipText => Message.Length == 0 ? Path : $"{Path}\n{Message}";
}

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
    // The constructor's load-time migration (below) gates the SAVE itself on
    // this, but no longer gates any dialog on its return value: a clean
    // conversion is silent either way now (portable-saved-passwords plan,
    // Task 1), unlike the old plaintext -> protected direction, whose
    // success notice specifically needed to know the write had reached
    // disk before claiming "protected."
    private readonly Func<bool> _trySaveCfg;
    // Null in the handful of tests that don't care about the load-time
    // notice (Step 4) — every call site is a null-conditional ?.Info/Warn, so
    // a missing dialog service just means the notice is silently skipped,
    // never a crash.
    private readonly IDialogService? _dialogs;

    // Test seams, following the fixture pattern the shell uses: the real app
    // passes neither, tests inject a scripted unlocker to make cancellation
    // and the runs-alone bound deterministic instead of timing-hopeful.
    private readonly Func<string, string, Unlock.UnlockResult> _unlock;
    private readonly Func<string, long> _fileSize;
    // Same pattern: the real app never passes this (defaults to
    // PasswordVault.TryReveal), but real DPAPI essentially never fails in
    // the "unexpected exception, not just an undecryptable entry" sense, so
    // a test proving MigrateProtectedToPlaintext's failure handling (fix
    // round 1, Important 1 — carried over from the sweep this replaced)
    // needs a way to force that failure deterministically instead of hoping
    // to provoke a real one. Returns null for "could not decrypt this
    // entry" (the normal case for a password protected on a different
    // machine/account) rather than throwing — throwing is reserved for the
    // "something unexpected went wrong" case the outer catch in
    // MigrateProtectedToPlaintext exists for.
    private readonly Func<string, string?> _tryReveal;

    // Test seam for the readiness probe, same shape as _unlock/_fileSize
    // above: the real app never passes this (defaults to the real, read-only
    // Unlock.ProbeReadiness), but tests need deterministic control over what
    // a probe reports without touching real PDF fixtures for every case, and
    // Step 8's teeth test needs a way to prove the wiring between a probe
    // result and a row's Status is real.
    private readonly Func<string, IReadOnlyList<string>, Unlock.ProbeResult> _probe;

    /// <summary>Bounds how many readiness probes run at once, shared across
    /// every call to ProbeRowsAsync (on-add AND the saved-password-change
    /// re-probe) — the same MaxConcurrentUnlocks figure and the same reason
    /// UnlockAsync bounds itself: PdfSharp's Import mode materialises the
    /// whole document, and a probe is a real disk/share read (risk 1: a
    /// 50-file drop against 5 saved passwords is already up to 250 opens).
    /// One shared instance rather than UnlockAsync's per-run local gate,
    /// because unlike UnlockAsync (reentrancy-guarded by AsyncRelayCommand),
    /// AddFilesAsync has no such guard and a fast run of drops can genuinely
    /// call ProbeRowsAsync concurrently.</summary>
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentUnlocks);

    /// <summary>Cancelled (and replaced with a fresh one) whenever the list
    /// is cleared, and cancelled for good when the window closes
    /// (CancelProbes) — a probe must never keep running for rows nobody can
    /// see anymore, the same reasoning CancelUnlock's own doc comment gives
    /// for the unlock batch itself.</summary>
    private CancellationTokenSource _probeCts = new();

    /// <summary>The most recently kicked-off probe run — exposed only so
    /// tests can await deterministic completion instead of polling; nothing
    /// in production needs this (both call sites, AddFilesAsync and the
    /// saved-password-change re-probe, are legitimately fire-and-forget from
    /// the UI's point of view, same as AddFilesAsync's own production call
    /// site in UnlockWindow.xaml.cs).</summary>
    internal Task ProbeCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Set while a batch is being added, so the command re-queries
    /// once for the batch instead of once per file.</summary>
    private bool _bulkAdding;

    private CancellationTokenSource? _cts;

    public ObservableCollection<UnlockFileRow> Files { get; } = new();
    public ObservableCollection<SavedPassword> Saved { get; }
    public ObservableCollection<UnlockResultLine> ResultLines { get; } = new();

    public UnlockViewModel(Config cfg, Func<bool> trySaveCfg,
        Func<string, string, Unlock.UnlockResult>? unlocker = null,
        Func<string, long>? fileSize = null,
        IDialogService? dialogs = null,
        Func<string, string?>? tryReveal = null,
        Func<string, IReadOnlyList<string>, Unlock.ProbeResult>? probe = null)
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
        _tryReveal = tryReveal ?? (stored => PasswordVault.TryReveal(stored, out var plain) ? plain : null);
        _probe = probe ?? Unlock.ProbeReadiness;
        Saved = new ObservableCollection<SavedPassword>(cfg.SavedPasswords);
        UnlockCommand = new AsyncRelayCommand(UnlockAsync, () => Files.Count > 0);
        CancelCommand = new RelayCommand(CancelUnlock, () => IsUnlocking);
        ClearCommand = new RelayCommand(() =>
        {
            Files.Clear();
            ResultLines.Clear();
            Summary = "";
            AddNote = "";
            // A probe still in flight for the rows just cleared must not go
            // on to update a row nobody can see anymore (Task 2 Step 2) —
            // cancel it, then hand out a FRESH token so the next drop still
            // gets probed normally rather than staying cancelled forever.
            var oldProbeCts = _probeCts;
            _probeCts = new CancellationTokenSource();
            oldProbeCts.Cancel();
            oldProbeCts.Dispose();
        });
        SaveBannerCommand = new RelayCommand(SaveBannerPassword, () => SaveBannerName.Trim().Length > 0);
        RemoveSavedCommand = new RelayCommand(RemoveSelectedSaved, () => SelectedSavedEntry is not null);
        Files.CollectionChanged += (_, _) =>
        {
            if (!_bulkAdding) UnlockCommand.RaiseCanExecuteChanged();
        };

        // Load-time migration (2026-08-08 portable-saved-passwords plan):
        // saved passwords are plaintext now, by the owner's deliberate
        // choice — a shared config.json on an SMB share, read by several
        // stations, is what DPAPI CurrentUser (bound to one account) and
        // LocalMachine (bound to one PC, and readable by every account on
        // it) both fail to serve. MigrateProtectedToPlaintext converts any
        // DPAPI-protected leftover from BEFORE that decision into plaintext
        // in place. It used to run the opposite direction (plaintext ->
        // protected, audit finding 4.3[A] — see that finding's update in
        // the same plan) on the three save paths only; this surface owns
        // the saved-password list, so its own construction — which runs
        // every time the Unlock window opens, i.e. effectively "on load" —
        // is where the migration belongs too, same as the sweep it
        // replaces.
        //
        // Gated on the migration's own Converted flag: a config with
        // nothing to convert must not be re-saved. Saving is not a
        // by-the-way action on a shared config.json — it is a real,
        // conflict-prone write on an SMB share, and this constructor runs
        // on every single open, not once. An unconditional save here would
        // rewrite (and risk write-conflicting) that shared file every time
        // anyone so much as opened the Unlock window, for zero content
        // change (see
        // UnlockViewModelTests.LoadDoesNotRewriteAnAlreadyPortableConfig).
        //
        // A clean, successful conversion is SILENT — no dialog. That used
        // to be backwards: the old direction (plaintext -> protected) fired
        // an Info every time, because protecting silently broke a shared
        // password for a colleague on another machine. This direction only
        // ever makes a saved password MORE usable (portable instead of
        // pinned to one machine+account), so a successful conversion needs
        // no notice at all — piling one on top of an ordinary, working open
        // would be exactly the unwanted dialog the owner reported in the
        // first place. The one case that DOES need telling, once: an entry
        // protected on a DIFFERENT machine or account can't be decrypted
        // here at all (PasswordVault.TryReveal reports that distinctly, see
        // its own doc comment) — it's left completely untouched rather than
        // silently blanked, but the user has to know it needs re-entering.
        //
        // MigrateProtectedToPlaintext itself never lets an UNEXPECTED
        // exception escape (fix round 1, Important 1, carried over from the
        // sweep this replaces) — see its own doc comment — so nothing here
        // needs its own try/catch to keep the window openable.
        var sweep = MigrateProtectedToPlaintext();
        if (sweep.Failed)
        {
            _dialogs?.Warn(
                "One or more saved passwords could not be converted just now — a " +
                "Windows security error, not a problem with the passwords themselves. " +
                "They were left exactly as they were and will still work here; this " +
                "will be retried the next time the Unlock window opens.",
                "OrdoSort — some saved passwords could not be converted");
        }
        else
        {
            if (sweep.Converted) _trySaveCfg();
            if (sweep.Undecryptable)
            {
                _dialogs?.Info(
                    "One or more saved passwords were protected on a different computer " +
                    "or account and couldn't be read here, so they were left exactly as " +
                    "they were — nothing was lost, but they'll need to be re-entered and " +
                    "saved again on this station to work here too.",
                    "OrdoSort — some saved passwords need re-entering");
            }
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
    /// save-and-persist flow the old "remember this password" row used,
    /// just triggered by the banner instead of a standing field.</summary>
    private void SaveBannerPassword()
    {
        var label = SaveBannerName.Trim();
        if (label.Length == 0) return;
        // Plaintext, deliberately — saved passwords are stored plaintext in
        // the shared config.json now (Task 1: no more Protect-on-save); see
        // PasswordVault's own doc comment for why.
        var entry = new SavedPassword { Label = label, Password = _bannerPassword };
        _cfg.SavedPasswords.Add(entry);
        Saved.Add(entry);
        MigrateProtectedToPlaintext();
        _trySaveCfg();
        SaveBannerVisible = false;
        SaveBannerName = "";
        _bannerPassword = "";
        // A new saved password can turn a needs-a-password row into ready —
        // see RequeueAllFilesForProbing's doc comment (risk 4/staleness).
        RequeueAllFilesForProbing();
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
            MigrateProtectedToPlaintext();
            _trySaveCfg();
            // A removed saved password can turn a ready row back into
            // needs-a-password (risk 4/staleness) — see
            // RequeueAllFilesForProbing's doc comment.
            RequeueAllFilesForProbing();
        }
        SelectedSavedEntry = Saved.FirstOrDefault();
    }

    /// <summary>False when either field is blank — the dialog shows a nudge
    /// instead of silently doing nothing.</summary>
    public bool AddSavedPassword(string label, string plain)
    {
        if (label.Trim().Length == 0 || plain.Length == 0) return false;
        // Plaintext, deliberately — see SaveBannerPassword's comment above.
        var entry = new SavedPassword { Label = label.Trim(), Password = plain };
        _cfg.SavedPasswords.Add(entry);
        Saved.Add(entry);
        MigrateProtectedToPlaintext();
        _trySaveCfg();
        // See RequeueAllFilesForProbing's doc comment (risk 4/staleness).
        RequeueAllFilesForProbing();
        return true;
    }

    /// <summary>Opportunistic migration: any saved password still
    /// DPAPI-protected (from before saved passwords became portable, or a
    /// hand-rolled <c>dpapi:</c> value) is converted to plaintext in place —
    /// plaintext IS the stored form now, by the owner's deliberate choice
    /// (see PasswordVault's own doc comment). Called both right before this
    /// surface persists (the three save-path callers above) and once from
    /// the constructor as a load-time migration, so a protected entry
    /// cannot outlive being noticed just because the saved-password list is
    /// never otherwise touched — the same opportunistic-upgrade-on-touch
    /// shape the sweep this replaces used, just reversed. Mutates the SAME
    /// <see cref="SavedPassword"/> instances that <c>Saved</c> holds (Saved
    /// is seeded from <c>_cfg.SavedPasswords</c> by reference, not by
    /// copy), so the in-memory list is automatically in sync — no separate
    /// pass over <c>Saved</c> is needed.
    ///
    /// An entry protected on a DIFFERENT machine or account cannot be
    /// decrypted here at all — that is normal and expected now that
    /// passwords are meant to travel, not a bug, and it is the one case
    /// this method must get exactly right: <see cref="PasswordVault.Reveal"/>
    /// collapses that failure into "", which would silently turn an
    /// irreplaceable password into an empty one if written back. This uses
    /// <see cref="_tryReveal"/> (defaults to
    /// <see cref="PasswordVault.TryReveal"/>) instead, which reports the
    /// failure distinctly; an entry it can't decrypt is left completely
    /// untouched — not added to <c>touched</c>, never mutated — and merely
    /// counted, so the constructor can tell the user once (fix round 1
    /// Important 1 didn't have this case at all, since the old direction
    /// — plaintext -&gt; protected — could never fail to read its own input).
    ///
    /// A TRULY unexpected failure — any exception <see cref="_tryReveal"/>
    /// throws rather than reports via its return value — must still never
    /// escape this method and brick the Unlock window: before the
    /// constructor called this, a failure here could only ever happen
    /// inside a deliberate save action; now it can happen every time the
    /// window opens, and an unhandled throw would leave it unable to open,
    /// deterministically, forever (fix round 1, Important 1; fix round 2,
    /// Gap A — catches every exception type, not just the documented one,
    /// carried over unchanged from the sweep this replaces). On that
    /// failure this rolls back every entry THIS call touched, restoring
    /// their original protected value — cheap and exactly right, since
    /// nothing needs re-encrypting: the original <c>dpapi:</c> string was
    /// saved before any mutation, so "roll back" is just restoring
    /// it.</summary>
    /// <returns><c>Converted</c> is true if at least one entry was migrated
    /// to plaintext and nothing failed unexpectedly — callers that persist
    /// unconditionally (the three above) can ignore this; the constructor's
    /// load-time migration uses it to skip saving a config that had nothing
    /// to convert. <c>Undecryptable</c> is true if at least one protected
    /// entry could not be decrypted at all (left untouched) — the
    /// constructor tells the user once, since those passwords need
    /// re-entering here. <c>Failed</c> is true only for the unexpected-
    /// exception case, in which case <c>Converted</c> is always false (a
    /// failure rolls back the whole attempt) — the constructor warns
    /// instead of claiming success.</returns>
    private (bool Converted, bool Undecryptable, bool Failed) MigrateProtectedToPlaintext()
    {
        var touched = new List<(SavedPassword Entry, string Original)>();
        var undecryptable = false;
        try
        {
            foreach (var sp in _cfg.SavedPasswords)
            {
                if (!PasswordVault.IsProtected(sp.Password)) continue;
                var plain = _tryReveal(sp.Password);
                if (plain is null) { undecryptable = true; continue; }
                var original = sp.Password;
                sp.Password = plain;
                touched.Add((sp, original));
            }
            return (touched.Count > 0, undecryptable, false);
        }
        catch (Exception)
        {
            foreach (var (entry, original) in touched)
                entry.Password = original;
            return (false, false, true);
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
        var already = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

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
        var live = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var newRows = new List<UnlockFileRow>();
        // one CanExecute re-query for the whole batch instead of one per file
        _bulkAdding = true;
        try
        {
            foreach (var p in keep)
                if (live.Add(p))
                {
                    var row = new UnlockFileRow(p);
                    Files.Add(row);
                    newRows.Add(row);
                    added++;
                }
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

        // Probe just the new arrivals — never the whole list on every drop
        // (risk 1: see ProbeRowsAsync's own doc comment). Awaited here (not
        // fire-and-forget) so AddFilesAsync's own Task represents "the add
        // AND the probe are both done" — this app's real call sites
        // (UnlockWindow.xaml.cs's drop/Add-files handlers) already
        // fire-and-forget the whole method, so awaiting internally costs the
        // UI thread nothing: it stays responsive, and every row's Status
        // still updates live, one at a time, as its own probe lands.
        ProbeCompletion = ProbeRowsAsync(newRows, _probeCts.Token);
        await ProbeCompletion;
    }

    public void RemoveFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths.ToList())
        {
            var row = Files.FirstOrDefault(r => r.Path == p);
            if (row is not null) Files.Remove(row);
        }
        AddNote = "";
    }

    /// <summary>Runs the readiness probe (Unlock.ProbeReadiness, or the
    /// injected test seam) for each given row, off the UI thread and bounded
    /// by _probeGate — never more than MaxConcurrentUnlocks at once, for the
    /// same reason UnlockAsync bounds its own concurrency (risk 1).
    ///
    /// Corruption guard (Task 2 Step 2 — "adding files while a probe is in
    /// flight must not corrupt either result"): each row's ProbeGeneration is
    /// bumped the instant a probe is REQUESTED for it — synchronously, before
    /// any await, in the loop below (LINQ's Select over an async lambda runs
    /// each lambda body up to its first await immediately when Task.WhenAll
    /// enumerates it, so every row in "rows" gets its generation bumped
    /// up-front, in order, before any of them actually starts waiting on the
    /// gate). A result is only ever applied if its captured generation still
    /// matches the row's CURRENT generation when it comes back. This matters
    /// because a row can end up with two outstanding probe requests — its
    /// original on-add probe, still in flight, and a saved-password-change
    /// re-probe (RequeueAllFilesForProbing) for the same row — and without
    /// this guard, whichever happened to FINISH last would win even if it
    /// started first and is now answering a stale question. With it, the
    /// most RECENTLY REQUESTED probe always wins regardless of finish order,
    /// and the loser's result is silently discarded: cheaper than cancelling
    /// the loser outright, and exactly as correct, since only the row's
    /// eventual state matters here.
    ///
    /// Cancellation (closing the window / clearing the list, Task 2 Step 2):
    /// a probe already running synchronously inside _probe cannot be
    /// aborted mid-call (same as UnlockAsync's own files-in-flight), so
    /// "must not leave a probe running" means what it means for CancelUnlock
    /// too — nothing NEW starts, and a result that does come back for a
    /// cancelled token is never applied.</summary>
    private async Task ProbeRowsAsync(IReadOnlyList<UnlockFileRow> rows, CancellationToken token)
    {
        if (rows.Count == 0) return;

        // Saved passwords ONLY — never the typed box: there is no typed
        // password at add time, and even when one exists later, the real
        // unlock tries it FIRST (TryCandidates below), so a probe that used
        // it could label a file ready on a password that was never actually
        // saved (risk 2). Snapshotted here, on the calling thread: Saved is
        // an ObservableCollection and the probing below runs on pool threads
        // via Task.Run, so reading it from there would race a concurrent
        // AddSavedPassword / RemoveSelectedSaved / save-banner edit.
        var probeCandidates = Saved.Select(sp => PasswordVault.Reveal(sp.Password)).ToList();

        await Task.WhenAll(rows.Select(async row =>
        {
            var myGeneration = ++row.ProbeGeneration;
            await _probeGate.WaitAsync();
            try
            {
                if (token.IsCancellationRequested) return;
                var result = await Task.Run(() => _probe(row.Path, probeCandidates));
                if (!token.IsCancellationRequested && myGeneration == row.ProbeGeneration)
                    ApplyProbeResult(row, result);
            }
            finally
            {
                _probeGate.Release();
            }
        }));
    }

    private static void ApplyProbeResult(UnlockFileRow row, Unlock.ProbeResult result)
    {
        var status = result.Status switch
        {
            "not_encrypted" => ReadinessStatus.NotEncrypted,
            "ready" => ReadinessStatus.Ready,
            "needs_password" => ReadinessStatus.NeedsPassword,
            "in_use" => ReadinessStatus.InUse,
            _ => ReadinessStatus.Unreadable,   // "unreadable", or anything unrecognized
        };
        // NotEncrypted stays quiet even in the tooltip (Task 2 Step 3) — the
        // other four keep ProbeResult's own wording verbatim.
        row.SetProbeResult(status, status == ReadinessStatus.NotEncrypted ? "" : result.Message);
    }

    /// <summary>Staleness (risk 4): a verdict must not outlive the saved-
    /// password list it was computed against. Saved passwords change in
    /// exactly three places — AddSavedPassword, RemoveSelectedSaved, and the
    /// save banner (SaveBannerPassword) — found by walking every mutator of
    /// Saved/_cfg.SavedPasswords, not by trusting a list of call sites.
    /// MigrateProtectedToPlaintext is deliberately NOT one of them: it
    /// changes how a password is STORED (unwraps a legacy DPAPI value to
    /// plaintext), never what it REVEALS to, so a probe run before and
    /// after migrating the same entry gets the identical candidate string
    /// either way.
    ///
    /// CHOICE: re-probe, not just clear-and-wait. A cleared verdict with no
    /// replacement would leave the user staring at blank rows until they did
    /// something else to re-trigger a check — actively worse than the
    /// feature not existing, since the whole point is showing readiness
    /// continuously. Re-probing the ENTIRE list here (not just recently-
    /// changed rows) is deliberately more expensive than the on-add probe
    /// ever is, but risk 1 (a 50-file drop costing up to 250 opens) is about
    /// FREQUENT, automatic events — a drop. A saved-password edit through
    /// Manage saved… or the save banner is a rare, deliberate action, and its
    /// cost is bounded by how many files happen to be queued right now, not
    /// by drop size. Every row's Status is reset to Pending FIRST,
    /// synchronously, so the stale claim is never shown for even the length
    /// of the re-probe — Pending (no suffix) is the same quiet default an
    /// unprobed row already has.</summary>
    private void RequeueAllFilesForProbing()
    {
        if (Files.Count == 0)
        {
            ProbeCompletion = Task.CompletedTask;
            return;
        }
        var rows = Files.ToList();
        foreach (var row in rows) row.ResetToPending();
        ProbeCompletion = ProbeRowsAsync(rows, _probeCts.Token);
    }

    /// <summary>Mirrors CancelUnlock for the readiness probe — called from
    /// UnlockWindow.OnClosed alongside CancelUnlock/ResetBanner. A closed
    /// window must not keep a probe running for rows nobody can see anymore
    /// (same reasoning as CancelUnlock's own doc comment).</summary>
    internal void CancelProbes() => _probeCts.Cancel();

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
        var rows = Files.ToList();
        var results = new Unlock.UnlockResult[rows.Count];
        var viaTyped = new bool[rows.Count];

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
            for (var i = 0; i < rows.Count; i++)
                (_fileSize(rows[i].Path) >= Unlock.LargeFileThresholdBytes ? large : small).Add(i);

            using var gate = new SemaphoreSlim(MaxConcurrentUnlocks);
            await Task.WhenAll(small.Select(async i =>
            {
                await gate.WaitAsync();
                try
                {
                    // the check sits AFTER the gate: a cancelled batch drains
                    // its queue as "cancelled" instead of starting more work
                    if (ct.IsCancellationRequested) { results[i] = Cancelled(rows[i].Path); return; }
                    // always in place, under the original name: the locked one
                    // is moved to a dated archive folder, never overwritten
                    (results[i], viaTyped[i]) =
                        await Task.Run(() => TryCandidates(rows[i].Path, candidates, typedCount));
                }
                finally
                {
                    gate.Release();
                }
                Summary = $"Unlocking {Interlocked.Increment(ref finished)} of {rows.Count}…";
            }));

            foreach (var i in large)
            {
                if (ct.IsCancellationRequested) { results[i] = Cancelled(rows[i].Path); continue; }
                Summary = $"Unlocking {finished + 1} of {rows.Count} (large file — running alone)…";
                (results[i], viaTyped[i]) =
                    await Task.Run(() => TryCandidates(rows[i].Path, candidates, typedCount));
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
        for (var i = 0; i < rows.Count; i++)
        {
            var r = results[i];
            var name = Path.GetFileName(rows[i].Path);
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
