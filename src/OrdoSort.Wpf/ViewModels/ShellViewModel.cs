using System.Collections.ObjectModel;
using System.Security.Cryptography;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.ViewModels;

public enum Screen { Ready, Processing, Done }

/// <summary>Per-file conflict marker: a content hash, not file metadata.
/// SaveConfigNow (tile-visibility toggle, merge headers, saved passwords —
/// none of them Settings edits) rewrites all three shared section files on
/// every call via Config.TrySave, whether or not their content changed, so
/// last-write-time and length both change on a save that made no actual
/// edit to a given section. Hashing the bytes is the only comparison that
/// tells "changed" from "rewritten unchanged" apart, and these files are
/// small enough that the cost is a non-issue.</summary>
internal readonly record struct FileStamp(string Hash);

/// <summary>Fingerprint of the three shared side files the Settings window
/// can change (destinations, monitored folders, alerts) at one instant. A
/// file that doesn't exist fingerprints as null rather than some sentinel —
/// first-run creation, or a section repointed at a file nobody has written
/// yet, must not read as a conflict. Two null fingerprints compare equal;
/// null against a real stamp does not.</summary>
internal sealed record SectionFingerprint(
    FileStamp? Destinations,
    FileStamp? MonitoredFolders,
    FileStamp? Alerts);

/// <summary>The app's state machine: Ready (dashboard) → Processing (filing
/// loop) → Done (summary), plus live folder monitoring. Owns Config, History,
/// Session. No WPF types — the whole lifecycle is unit-tested headless.</summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private Config _cfg;               // replaced by Settings
    private readonly string _cfgPath;
    // Fingerprint of the shared section files taken the moment the Settings
    // window opened (FreshConfigForSettings) — compared again in
    // ApplySettingsAsync just before saving, so a peer station's edit that
    // landed while this station's dialog was open is caught instead of
    // silently overwritten. Null when no Settings editing session is in
    // flight (e.g. ApplySettings invoked without opening Settings first).
    private SectionFingerprint? _settingsSnapshot;
    private History _history;          // re-opened if history_db changes
    private Session _session;
    private readonly IPdfViewer _viewer;
    private readonly IDialogService _dialogs;
    private readonly FolderWatchService _watch;
    private readonly SynchronizationContext? _uiContext;
    private readonly Func<ThemePalette> _palette;
    private readonly IWorkScheduler _scheduler;
    private readonly ISoundService _sounds;
    private readonly System.Threading.Timer _flash;

    // Side-file keys ("destinations_file", etc.) already named in a "not
    // saved" warning this run — see WarnSaveFailure's doc comment for why a
    // confinement-refused key is only ever worth saying once per session for
    // an UNRELATED background save (SaveConfigNow), never once per save —
    // and for why ApplySettingsAsync's own save bypasses that suppression
    // instead of consulting this set.
    private readonly HashSet<string> _warnedRefusedSideFileKeys = new(StringComparer.Ordinal);

    public ShellViewModel(Config cfg, string cfgPath, IPdfViewer viewer,
        IDialogService dialogs, FolderWatchService watch,
        SynchronizationContext? uiContext = null, Func<ThemePalette>? palette = null,
        IWorkScheduler? scheduler = null, ISoundService? sounds = null)
    {
        _cfg = cfg;
        _cfgPath = cfgPath;
        _viewer = viewer;
        _dialogs = dialogs;
        _watch = watch;
        _uiContext = uiContext;
        _palette = palette ?? (() => ThemeManager.Current);
        _scheduler = scheduler ?? new TaskWorkScheduler();
        _sounds = sounds ?? new NullSoundService();
        _flash = new System.Threading.Timer(_ =>
        {
            if (_uiContext is null) FlashTick();
            else _uiContext.Post(_ => FlashTick(), null);
        });
        _lastActionTimer = new System.Threading.Timer(_ =>
        {
            if (_uiContext is null) HideLastAction();
            else _uiContext.Post(_ => HideLastAction(), null);
        });
        _toastTimer = new System.Threading.Timer(_ =>
        {
            if (_uiContext is null) HideToast();
            else _uiContext.Post(_ => HideToast(), null);
        });
        _statusTimer = new System.Threading.Timer(_ =>
        {
            if (_uiContext is null) ExpireStatusNote();
            else _uiContext.Post(_ => ExpireStatusNote(), null);
        });

        // history_db is resolved with the SAME unconfined rule as the four
        // Config side files (ResolvePath === Config.ResolveBeside) — an
        // absolute history_db is NOT refused the way an absolute
        // destinations_file/monitored_folders_file/alerts_file/box_labels_file
        // now is (2026-08 audit 4.2[A]). This is deliberate, not an
        // oversight: unlike those four, which are "designed to sit beside
        // the config" (task-1-brief.md), History.cs's own class doc says
        // this database is DESIGNED to live on its own SMB share, shared
        // across stations independently of where config.json lives —
        // confining it to the config directory would break that supported
        // deployment.
        //
        // The residual exposure from leaving it unconfined is real but much
        // narrower than the JSON side-file case, and was verified rather
        // than assumed:
        //   - History's constructor never writes attacker-chosen CONTENT —
        //     it only ever runs its own fixed schema (CREATE TABLE IF NOT
        //     EXISTS ...). A malicious shared config.json can point
        //     history_db at a path with no file there yet and get an
        //     empty, app-schema'd SQLite file (plus whatever parent
        //     directories don't exist — Directory.CreateDirectory) planted
        //     on the victim's disk; it cannot inject chosen bytes into it.
        //   - Pointing history_db at an EXISTING non-SQLite file does NOT
        //     overwrite it: SQLite validates the file header before
        //     touching page content and raises SQLITE_NOTADB ("file is not
        //     a database") instead, leaving every byte untouched — verified
        //     empirically (ScratchSqliteProbe, since removed): opening a
        //     57-byte non-DB file threw SqliteException "SQLite Error 26:
        //     'file is not a database'" with the file byte-for-byte
        //     identical afterward.
        //   - A failed open here does not corrupt this call path either:
        //     ApplySettingsAsync (below) already treats "the new History
        //     failed to construct" as non-fatal — it keeps the OLD, still-
        //     open database live for the rest of the session and warns the
        //     user, rather than propagating the failure destructively.
        //
        // Known gap this does NOT close, recorded rather than chased: a
        // symlink/junction inside the config directory would pass the four
        // keys' new containment check too (Path.GetFullPath is lexical and
        // never resolves reparse points) — exploiting that needs local
        // write access to the config directory already, which is a much
        // higher bar than editing a shared config.json, so it's out of
        // scope here.
        var dbPath = ResolvePath(cfg.HistoryDb, cfgPath);
        // Daily point-in-time backup, taken while the file is at rest — BEFORE
        // we open the connection. The audit DB is the only link between a
        // filed document and its original id, so it must have redundancy.
        //
        // This — plus the SQLite open + migration on the next line — runs
        // synchronously here, before MainWindow's constructor returns and
        // before Show() (2026-08-07 audit 5.3). Measured rather than assumed:
        // a 100k-row history db (raw-SQL-seeded, one transaction — LogCommit
        // per row is thousands of fsyncs and would measure the disk, not the
        // code), backups pruned to the daily-skip case cleared first so the
        // copy actually runs. Five local-SSD runs: BackupDaily's File.Copy
        // 6.5-8.1ms, new History(dbPath) open+migrate (schema already
        // current — the common case) 0.9-1.0ms, the whole constructor
        // 7.6-9.8ms. Tens of milliseconds, not a perceptible hang — moving
        // this off the UI thread would trade a real (if small) local cost
        // for the complexity of a visible "starting" state and, if the
        // backup moved past the connection open, a torn live-file copy
        // (finding 1.3) — the wrong direction. Decision: measure and record,
        // change nothing. The one gap this measurement does NOT cover: the
        // documented deployment is an SMB share, where a whole-file copy of
        // a database that just grew ~39% (the history index, 5.1) is not
        // the same proposition as a local SSD — re-measure there before
        // assuming this conclusion travels.
        var backupOk = RunHistoryBackup(dbPath, out _historyBackupDir);
        SetHistoryBackupWarning(backupOk);
        _history = new History(dbPath);
        _session = new Session(cfg, _history, _cfgPath);

        StartCommand = new RelayCommand(StartProcessing, () => StartEnabled);
        RescanCommand = new RelayCommand(Rescan);
        // Inbox/Deferred may be a relative, config-relative value (Settings
        // says so, and now it's true) — resolve at the moment the folder is
        // opened, not once at startup, so a Settings edit applied later is
        // reflected here without a matching edit to this closure.
        OpenDeferredCommand = new RelayCommand(() => OpenFolder(ResolvePath(_cfg.Deferred, _cfgPath)));
        OpenInboxCommand = new RelayCommand(() => OpenFolder(ResolvePath(_cfg.Inbox, _cfgPath)));
        OpenToastCommand = new RelayCommand(() => { OpenFolder(_toastFolder); HideToast(); });
        OpenHistoryBackupFolderCommand = new RelayCommand(() => OpenFolder(_historyBackupDir));
        // Points at config.json's own folder, not a Settings window this
        // class has no way to open (its own class doc: "No WPF types" --
        // Settings is opened by MainWindow's code-behind). That folder is
        // also where the colliding side files themselves live, so it is
        // still the fastest path to actually seeing the problem.
        OpenConfigFolderCommand = new RelayCommand(() =>
            OpenFolder(Path.GetDirectoryName(Path.GetFullPath(_cfgPath)) ?? ""));
        RouteCommand = new AsyncRelayCommand<int>(OnRouteAsync);
        SkipCommand = new AsyncRelayCommand(OnSkipAsync);
        UndoCommand = new AsyncRelayCommand(OnUndoAsync, () => _session.CanUndo);
        StopCommand = new RelayCommand(StopSession);
        ExportHistoryCommand = new RelayCommand(ExportHistory);

        // Without these the async commands swallow every unexpected exception:
        // a failed filing would look like a button that simply did nothing.
        // Each says WHICH action stopped: "the last action" is all the shared
        // handler could ever have said, and a user who pressed a destination
        // button and one who pressed Undo need different reassurance.
        RouteCommand.OnError += ex => ReportUnexpected(ex, "Filing that document");
        SkipCommand.OnError += ex => ReportUnexpected(ex, "Setting that document aside");
        // Undo is the one action whose two places are the OTHER way round, so
        // it cannot share the default sentence — see ReportUnexpected.
        UndoCommand.OnError += ex => ReportUnexpected(ex, "Undoing the last filing",
            "either back in the inbox or still in the folder it was filed to");

        _watch.Activity += OnFolderActivity;
    }

    /// <summary>Raised for an exception no handler anticipated, so the window can
    /// put it in crash.log. The user is warned either way.</summary>
    public event Action<Exception>? UnexpectedError;

    /// <summary>2026-08-02 audit finding I6: this used to open "Something went
    /// wrong" and then paste the raw exception message into the dialog —
    /// wording that tells a user neither what happened nor what to do, and
    /// text like "Object reference not set to an instance of an object" that
    /// only a developer can read. The rest of the app (Session's audit-failure
    /// copy, Commit's CommitError messages) names the document, says where it
    /// ended up and stops; this now does the same.
    ///
    /// The claim about the document is deliberately conservative rather than
    /// precise, because this handler cannot know which side of the move the
    /// fault landed on. What IS guaranteed, and is what the user needs: the
    /// app only ever MOVES files (see Commit's class doc — never deleted,
    /// never overwritten), so the document is in exactly one of two places and
    /// looking will settle it. The raw text is not lost, it just goes where it
    /// is useful: <see cref="UnexpectedError"/> is wired to App.LogCrash, which
    /// appends the full exception to crash.log beside the config.</summary>
    /// <param name="action">What the user was doing, sentence-initial —
    /// "Filing that document", etc.</param>
    /// <param name="whereabouts">The two places the document can be, as the
    /// user would name them. The default reads correctly for filing and for
    /// setting aside — both start in the inbox and end somewhere else — but
    /// NOT for Undo, which runs the move backwards: after pressing Undo,
    /// "where it started" is the destination folder and "where it was going"
    /// is the inbox, which is the exact opposite of how a user reads those
    /// words. Undo therefore passes its own sentence. The guarantee itself is
    /// unchanged in every case: the document is in one of exactly two places,
    /// because the app only ever moves files.</param>
    private void ReportUnexpected(Exception ex, string action,
        string whereabouts = "either where it started or where it was going")
    {
        UnexpectedError?.Invoke(ex);
        _dialogs.Warn(
            $"{action} didn't finish.\n\n" +
            "Nothing was deleted — OrdoSort only ever moves files, so the document " +
            $"is {whereabouts}. Check both before " +
            "trying again.\n\n" +
            "The technical details were written to crash.log, beside your config file.",
            "OrdoSort — that didn't finish");
    }

    /// <summary>Await a fire-and-forget task and report anything it throws.
    ///
    /// Initialize, StartProcessing and ApplySettings are all entry points the
    /// window calls as <c>_ = SomethingAsync()</c>. An exception thrown inside
    /// an async method — including before its first await — is captured into
    /// the returned Task rather than thrown to the caller, and .NET does not
    /// tear the process down for an unobserved faulted Task, so discarding
    /// that Task discarded the failure with it: no dialog, no crash.log line,
    /// nothing. The worst case was Initialize, where a throw from
    /// <c>_watch.SetFolders</c> (a locked-down share can list a folder while
    /// denying change-notification rights, and the share can drop between the
    /// Directory.Exists check and EnableRaisingEvents) skipped the first scan
    /// AND left the session with no live folder watcher — for the whole
    /// session, since nothing re-arms it — while the dashboard looked merely
    /// idle. This is the same net RouteCommand/SkipCommand/UndoCommand get
    /// from AsyncRelayCommand; these three had no equivalent.
    ///
    /// The wording is deliberately not ReportUnexpected's: that one promises
    /// "the document is in one of two places", which is the right sentence for
    /// a failed file move and the wrong one for a failed startup or a failed
    /// settings save, where no document was in flight at all.</summary>
    private async Task RunGuarded(Task work, string action, string consequence)
    {
        try { await work; }
        catch (Exception ex)
        {
            UnexpectedError?.Invoke(ex);
            _dialogs.Warn(
                $"{action} didn't finish.\n\n{consequence}\n\n" +
                "The technical details were written to crash.log, beside your config file.",
                "OrdoSort — that didn't finish");
        }
    }

    /// <summary>Tell the user a document moved but went unrecorded, and keep
    /// going — the queue has already advanced past it.</summary>
    private void ReportAuditFailure(AuditError ex, string title)
    {
        _auditFailedThisSession = true;
        if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Error, _cfg.Sounds.Error);
        UnexpectedError?.Invoke(ex);
        _dialogs.Warn(ex.Message, title);
        ShowStatusNote($"{Path.GetFileName(ex.NewPath)} moved, but the history " +
                       "database didn't record it — see the warning.");
    }

    internal Config Cfg => _cfg;
    internal string CfgPath => _cfgPath;
    internal Session Session => _session;
    internal History History => _history;

    /// <summary>box-labels.json resolved beside the config (or absolute).</summary>
    internal string BoxLabelsPath => ResolvePath(_cfg.BoxLabelsFile, _cfgPath);

    /// <summary>Fresh config for the Settings window: shared side files may
    /// have changed on disk. A load failure (e.g. a half-edited side file)
    /// warns and falls back to the in-memory config rather than blocking.
    /// This is also the moment the user's editing session begins, so it's
    /// where the conflict-detection snapshot is taken (see
    /// <see cref="_settingsSnapshot"/> and <see cref="ApplySettingsAsync"/>).</summary>
    internal Config FreshConfigForSettings()
    {
        _settingsSnapshot = SnapshotSections();
        try { return Config.Load(_cfgPath); }
        catch (ConfigException ex)
        {
            _dialogs.Warn(ex.Message + "\n\nShowing the settings the app is currently running with.",
                "OrdoSort — settings");
            return _cfg;
        }
    }

    /// <summary>Fingerprint the three shared side files as they stand right
    /// now, keyed off _cfg's current file names. Internal so tests can
    /// assert on it directly (e.g. that a missing file fingerprints as
    /// null, not as "changed").</summary>
    internal SectionFingerprint SnapshotSections() => new(
        StampSection(_cfg.DestinationsFile),
        StampSection(_cfg.MonitoredFoldersFile),
        StampSection(_cfg.AlertsFile));

    private FileStamp? StampSection(string sectionFile)
    {
        var path = ResolvePath(sectionFile, _cfgPath);
        if (!File.Exists(path)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return new FileStamp(hash);
    }

    /// <summary>Human-readable names of the sections that differ between two
    /// fingerprints, in a fixed order, for the conflict prompt.</summary>
    private static List<string> ChangedSectionNames(SectionFingerprint before, SectionFingerprint after)
    {
        var changed = new List<string>();
        if (before.Destinations != after.Destinations) changed.Add("destinations");
        if (before.MonitoredFolders != after.MonitoredFolders) changed.Add("monitored folders");
        if (before.Alerts != after.Alerts) changed.Add("alerts");
        return changed;
    }

    /// <summary>Joins section names the way a sentence needs, not just
    /// "a and b and c": "x", "x and y", or "x, y, and z".</summary>
    private static string JoinNaturally(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
    };

    /// <summary>True for the moment ApplySettings is re-opening the history
    /// DB at a new path — _history is unusable until the swap lands.</summary>
    internal bool HistorySwapping { get; private set; }

    /// <summary>The view uses this to turn on TextBox CharacterCasing so
    /// uppercase typing keeps the caret steady.</summary>
    public bool UppercaseNames => _cfg.UppercaseNames;

    // ------------------------------------------------------------- screen
    private Screen _screen = Screen.Ready;
    public Screen Screen
    {
        get => _screen;
        private set
        {
            if (Set(ref _screen, value))
            {
                Raise(nameof(IsReady));
                Raise(nameof(IsProcessing));
                Raise(nameof(IsDone));
                Raise(nameof(TileControlsVisible));
            }
        }
    }

    public bool IsReady => Screen == Screen.Ready;
    public bool IsProcessing => Screen == Screen.Processing;
    public bool IsDone => Screen == Screen.Done;

    // -------------------------------------------------------- ready state
    private string _countLine = "";
    public string CountLine { get => _countLine; private set => Set(ref _countLine, value); }

    /// <summary>The big number over the Start button: inbox PDF count, or ⚠
    /// when the inbox can't be read (DetailLine carries the reason).</summary>
    private string _bigCount = "";
    public string BigCount { get => _bigCount; private set => Set(ref _bigCount, value); }

    private string _countCaption = "";
    public string CountCaption { get => _countCaption; private set => Set(ref _countCaption, value); }

    private string _detailLine = "";
    public string DetailLine { get => _detailLine; private set => Set(ref _detailLine, value); }

    /// <summary>The one place the app says the trail out loud. A session that
    /// moved at least one document ends by vouching for the record; a session
    /// that moved nothing says nothing, because there is nothing to vouch for.
    /// Deliberately carries no count — a count would have to decide whether a
    /// vanished file is a move, and would be wrong in exactly the session where
    /// the sentence most needs to be trusted.</summary>
    private string _logLine = "";
    public string LogLine
    {
        get => _logLine;
        private set { if (Set(ref _logLine, value)) Raise(nameof(HasLogLine)); }
    }
    public bool HasLogLine => _logLine.Length > 0;

    private bool _startEnabled;
    public bool StartEnabled
    {
        get => _startEnabled;
        private set { if (Set(ref _startEnabled, value)) StartCommand.RaiseCanExecuteChanged(); }
    }

    private string _deferredAlert = "";
    public string DeferredAlert { get => _deferredAlert; private set { if (Set(ref _deferredAlert, value)) Raise(nameof(HasDeferred)); } }
    public bool HasDeferred => DeferredAlert.Length > 0;

    /// <summary>Non-empty when the most recent daily history backup — at
    /// startup or at a Settings history-db swap — ran against a database
    /// that existed and still came back without a copy (2026-08-07 QC sweep
    /// R1: both call sites used to discard <see
    /// cref="HistoryBackup.BackupDaily"/>'s return, so a backup failing every
    /// day — permissions, a full disk, a disconnected share — failed silently
    /// forever). A modal here would either block startup (before Show()) or
    /// nag on every single launch for as long as the underlying problem
    /// persists — both worse than the silence being fixed. This instead
    /// stays visible on every screen for the rest of the session, the same
    /// way <see cref="DeferredAlert"/> already does, and clears the moment a
    /// backup succeeds again.</summary>
    private string _historyBackupWarning = "";
    public string HistoryBackupWarning
    {
        get => _historyBackupWarning;
        private set { if (Set(ref _historyBackupWarning, value)) Raise(nameof(HasHistoryBackupWarning)); }
    }
    public bool HasHistoryBackupWarning => HistoryBackupWarning.Length > 0;

    private const string BackupFailureMessage =
        "⚠ Today's history backup didn't complete — the audit log has no fresh " +
        "safety copy today. Click to check the backups folder; ask about disk " +
        "space or permissions if this keeps happening.";

    // Set by RunHistoryBackup, so OpenHistoryBackupFolderCommand always
    // points at the backups folder for the database currently in use.
    private string _historyBackupDir = "";

    /// <summary>True until the user dismisses the history-backup rail
    /// notice. Cleared — the notice re-raises — on a false-&gt;true
    /// transition of <see cref="HasHistoryBackupWarning"/> itself: unlike
    /// the set-aside notice, this warning is only ever (re)computed at two
    /// discrete moments (construction, a Settings history-db swap), never on
    /// a poll, so there is no "count went up while already showing" case to
    /// widen this for.</summary>
    private bool _historyBackupDismissed;

    /// <summary>True until the user dismisses the config-side-file-collision
    /// rail notice (app-qc-2026-08-21 finding 1). Simpler than
    /// <see cref="_historyBackupDismissed"/>'s own re-raise rule: unlike
    /// HistoryBackupWarning, <see cref="Config.SideFileCollisionWarning"/> is
    /// computed exactly once, by Config.Load, before this object even
    /// exists, and nothing in this class ever recomputes or clears it for
    /// the rest of the session — so there is no false-&gt;true transition
    /// that would ever need to un-dismiss this.</summary>
    private bool _configCollisionDismissed;

    /// <summary>The only writer of <see cref="HistoryBackupWarning"/> — both
    /// call sites (the constructor, ApplySettingsAsync's db swap) route
    /// through here so the dismiss re-raise rule and the rail refresh can't
    /// drift out of sync with a THIRD direct assignment somewhere new.</summary>
    private void SetHistoryBackupWarning(bool backupOk)
    {
        var wasWarning = HasHistoryBackupWarning;
        HistoryBackupWarning = backupOk ? "" : BackupFailureMessage;
        if (!wasWarning && HasHistoryBackupWarning) _historyBackupDismissed = false;
        RefreshNotices();
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand OpenDeferredCommand { get; }
    public RelayCommand OpenInboxCommand { get; }
    public RelayCommand OpenToastCommand { get; }
    public RelayCommand OpenHistoryBackupFolderCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }

    /// <summary>Called once by the window after the viewer init attempt:
    /// start watching and take the first scan.</summary>
    public void Initialize() => _ = RunGuarded(InitializeAsync(), "Starting up",
        "Folder watching may not be live for this session, so new arrivals might " +
        "not appear on their own. Use Rescan to refresh, or restart OrdoSort.");

    internal async Task InitializeAsync()
    {
        var cfg = _cfg;
        var cfgPath = _cfgPath;
        // Directory.Exists + watcher registration are network round trips on
        // an SMB share — never on the UI thread
        await _scheduler.Run(() => _watch.SetFolders(
            ResolvePath(cfg.Inbox, cfgPath), ResolvePath(cfg.Deferred, cfgPath)));
        Rescan();
    }

    public void Rescan()
    {
        Screen = Screen.Ready;
        _viewer.Blank();
        _ = RefreshFoldersAsync(showErrors: true);
    }

    // ---------------------------------------------------- folder snapshots
    // Every folder read happens OFF the UI thread: over SMB a single
    // enumeration can take seconds, and a blocked UI thread here doesn't just
    // freeze the app — it starves the global mouse hook.
    private sealed record FolderSnapshot(
        Scanner.ScanResult Scan, Scanner.DeferredInfo Deferred,
        List<FolderMonitor.FolderStatus>? Statuses);

    private bool _refreshBusy;
    private bool _refreshPending;

    /// <summary>Gather (thread pool) → apply (UI). Overlapping requests
    /// coalesce: one runs, the latest waiter reruns after.</summary>
    internal async Task RefreshFoldersAsync(bool showErrors = false)
    {
        if (_refreshBusy) { _refreshPending = true; return; }
        _refreshBusy = true;
        try
        {
            do
            {
                _refreshPending = false;
                // while filing the dashboard is hidden, and in Hidden mode the
                // user asked for silence — either way skip the watch-folder
                // sweep (one network enumeration per watched folder, per
                // debounce, is pure churn on SMB)
                var mode = TileMode;
                var wantStatuses = Screen != Screen.Processing && mode != "hidden";
                var cfg = _cfg;
                var cfgPath = _cfgPath;
                var snap = await _scheduler.Run(() => new FolderSnapshot(
                    Scanner.Scan(ResolvePath(cfg.Inbox, cfgPath), cfg.Sort, cfg.NamingMode),
                    Scanner.DeferredSummary(ResolveDeferredPath(cfg.Deferred, cfgPath)),
                    wantStatuses
                        ? FolderMonitor.All(cfg.WatchFolders, cfg.AlertTexts)
                            .Where(s => mode == "all" || s.HasFiles || s.Error.Length > 0)
                            .ToList()
                        : null));
                ApplySnapshot(snap, showErrors);
            } while (_refreshPending);
        }
        finally { _refreshBusy = false; }
    }

    private void ApplySnapshot(FolderSnapshot snap, bool showErrors)
    {
        ApplyDeferred(snap.Deferred);
        if (snap.Scan.Error.Length > 0 && !showErrors && Screen != Screen.Ready)
            return;   // a transient share hiccup must not wipe the screen

        if (Screen == Screen.Processing && !_session.Done)
        {
            if (snap.Scan.Error.Length > 0) return;
            var added = _session.Extend(snap.Scan.Matching);
            if (added > 0)
            {
                RaiseProgress();
                ShowStatusNote($"{added} new file{(added == 1 ? "" : "s")} arrived — added to this session.");
            }
        }
        else if (Screen == Screen.Ready)
        {
            ShowReady(snap);
        }
        else
        {
            // Done: notify, don't yank — the session summary stays put
            if (snap.Scan.Error.Length == 0 && snap.Scan.Count > 0)
                SetStatus($"{snap.Scan.Count} file{(snap.Scan.Count == 1 ? "" : "s")} waiting in the inbox.");
        }
    }

    private void ShowReady(FolderSnapshot snap)
    {
        var scan = snap.Scan;
        _viewer.Blank();
        CountLine = scan.Error.Length > 0
            ? "Inbox problem"
            : $"{scan.Count} file{(scan.Count == 1 ? "" : "s")} ready";
        BigCount = scan.Error.Length > 0 ? "⚠" : scan.Count.ToString();
        CountCaption = scan.Error.Length > 0
            ? "inbox problem"
            : $"PDF{(scan.Count == 1 ? "" : "s")} in the inbox";
        DetailLine = scan.Error.Length > 0
            ? scan.Error
            : (scan.IgnoredCount > 0
                ? $"{scan.IgnoredCount} other file{(scan.IgnoredCount == 1 ? "" : "s")} ignored"
                : "");
        StartEnabled = scan.Count > 0;
        RefreshDashboard(scan, snap.Statuses);
    }

    /// <summary>True until the user dismisses the set-aside rail notice; the
    /// underlying warning itself does not clear just because it's dismissed
    /// (see <see cref="DeferredAlert"/>/<see cref="HasDeferred"/>, unchanged).
    /// Cleared — the notice re-raises — on either edge ApplyDeferred can see
    /// cheaply from <see cref="Scanner.DeferredInfo"/> alone, no extra
    /// file-set diffing needed: the count going 0 -&gt; positive (the alert
    /// reappearing after the set-aside folder was cleared out) or, while
    /// already positive, going UP further (more files landed while this was
    /// dismissed) — <see cref="_deferredLastCount"/> is the cheapest signal
    /// that tells both apart, since <see cref="Scanner.DeferredSummary"/>
    /// already computes it on every scan this method is called from.</summary>
    private bool _deferredDismissed;
    private int _deferredLastCount;
    private string _deferredMessage = "";
    private string _deferredDetail = "";

    // internal, not private: QC-13's "unknown" rendering needs OldestAgeDays
    // null with Count > 0, which only happens when every set-aside file
    // vanishes between Directory.GetFiles and its mtime read (Scanner) — not
    // reproducible as a real race on this machine. Constructing the
    // DeferredInfo directly and calling this pins the rendering seam
    // instead. See ShellReadyTests.
    internal void ApplyDeferred(Scanner.DeferredInfo info)
    {
        if (info.Count == 0)
        {
            DeferredAlert = "";
            _deferredMessage = "";
            _deferredDetail = "";
            _deferredDismissed = false;
            _deferredLastCount = 0;
            RefreshNotices();
            return;
        }
        if (_deferredLastCount == 0 || info.Count > _deferredLastCount)
            _deferredDismissed = false;
        _deferredLastCount = info.Count;

        // QC-13: OldestAgeDays is null when every set-aside file's mtime read
        // failed (Scanner.SafeMtime, e.g. a file gone by read time) -- render
        // "unknown" rather than let that read as "0 days old" or, pre-fix, a
        // ~155,000-day sentinel date.
        var age = info.OldestAgeDays switch
        {
            null => "   ·   oldest unknown",
            0 => "",
            1 => "   ·   oldest 1 day",
            var d => $"   ·   oldest {d} days",
        };
        // "click to open" (non-breaking spaces): measured at the
        // MainWindow's compact-mode default/minimum widths (2026-08-03,
        // Task 7), the banner's TextWrapping="Wrap" TextBlock
        // (MainWindow.xaml:114) splits this three-word phrase mid-clause —
        // "click to" / "open" at 470px (the compact mode's own default
        // width), "click" / "to open" at 440px — reproducing the 2026-08-02
        // audit's M1 finding. The em dash before it is a fine, intentional
        // break point and is left as regular spaces.
        DeferredAlert = $"⚠ {info.Count} set-aside file{(info.Count == 1 ? "" : "s")} waiting"
            + age + "   —   click to open";
        // _deferredMessage/_deferredDetail: a SEPARATE, rail-facing split
        // built alongside DeferredAlert above (Task 3.3, the notification
        // rail) -- not a re-parse of that composed sentence, which stays
        // exactly as it was for the tests/consumers that still read it.
        _deferredMessage = $"{info.Count} set-aside file{(info.Count == 1 ? "" : "s")} waiting";
        _deferredDetail = info.OldestAgeDays switch
        {
            null => "oldest unknown",
            0 => "",
            1 => "oldest 1 day",
            var d => $"oldest {d} days",
        };
        RefreshNotices();
    }

    private async Task RefreshDeferredAsync()
    {
        var deferred = ResolveDeferredPath(_cfg.Deferred, _cfgPath);
        ApplyDeferred(await _scheduler.Run(() => Scanner.DeferredSummary(deferred)));
    }

    // ----------------------------------------------------------- dashboard
    public ObservableCollection<TileViewModel> Tiles { get; } = new();

    /// <summary>The same TileViewModel instances as <see cref="Tiles"/>,
    /// projected into named sections (Config's per-folder Section, or the
    /// dashboard's default title for blank sections) for grouped rendering.
    /// Rebuilt in lockstep with Tiles — never independently.</summary>
    public ObservableCollection<TileGroupViewModel> TileGroups { get; } = new();

    private string _monitorTitle = "";
    public string MonitorTitle { get => _monitorTitle; private set => Set(ref _monitorTitle, value); }

    private bool _dashboardVisible;
    public bool DashboardVisible { get => _dashboardVisible; private set => Set(ref _dashboardVisible, value); }

    /// <summary>True when monitoring is on (Active-only mode, folders
    /// configured) and nothing needs attention — the dashboard says so
    /// instead of silently vanishing the whole section.</summary>
    private bool _allQuiet;
    public bool AllQuiet { get => _allQuiet; private set => Set(ref _allQuiet, value); }

    /// <summary>The config's tile_visibility normalized: unknown values (hand
    /// edits, future keys) read as the default so the dashboard never
    /// silently disappears on a typo.</summary>
    private string TileMode => _cfg.TileVisibility switch
    {
        "all" or "hidden" => _cfg.TileVisibility,
        _ => "active",
    };

    /// <summary>Header-bar dropdown: 0 = Active only, 1 = All (even empty),
    /// 2 = Hidden. Persisted, and refreshes the dashboard live; Hidden also
    /// skips the watch-folder sweep entirely — a real saving on SMB.</summary>
    public int TileVisibilityIndex
    {
        get => TileMode switch { "all" => 1, "hidden" => 2, _ => 0 };
        set
        {
            var mode = value switch { 1 => "all", 2 => "hidden", _ => "active" };
            if (TileMode == mode) return;
            _cfg.TileVisibility = mode;
            Raise();
            SaveConfigNow();
            _ = RefreshFoldersAsync();
        }
    }

    /// <summary>The dropdown only appears on Ready, and only when there are
    /// monitored folders to control.</summary>
    public bool TileControlsVisible => IsReady && _cfg.WatchFolders.Count > 0;

    private bool _inboxAlerting;
    public bool InboxAlerting { get => _inboxAlerting; private set => Set(ref _inboxAlerting, value); }

    private bool _flashOn;
    internal bool FlashRunning { get; private set; }

    /// <summary>The Ready count goes alert-red while an inbox filename trips an
    /// alert term (flashing, or steady when flash_alerts is off).</summary>
    public bool CountAlertOn => InboxAlerting && (_flashOn || !_cfg.FlashAlerts);

    private List<string> _tileSignature = new();
    private ThemePalette? _tilePalette;

    /// <summary>Rebuild the monitored-folder tiles (shown on Ready only, and
    /// only for folders holding files or in error), the inbox alert state, and
    /// (re)start the 600 ms flash if anything is alerting. Statuses arrive
    /// pre-gathered off-thread. Tiles are only REBUILT when something actually
    /// changed — the backstop poll must not replay the fade-in on identical
    /// tiles (the "blink").</summary>
    private void RefreshDashboard(Scanner.ScanResult inboxScan,
        List<FolderMonitor.FolderStatus>? swept)
    {
        var statuses = swept ?? new List<FolderMonitor.FolderStatus>();
        var p = _palette();
        var defaultTitle = string.IsNullOrWhiteSpace(_cfg.MonitorTitle)
            ? "Monitored folders" : _cfg.MonitorTitle;
        var signature = statuses.Select(s =>
            $"{s.Label}|{s.Path}|{s.Color}|{s.Count}|{s.Error}|{s.Alerting}"
            + $"|{string.Join(",", s.AlertFolders)}|{s.Section}").ToList();
        signature.Add(defaultTitle);
        if (!ReferenceEquals(p, _tilePalette) || !signature.SequenceEqual(_tileSignature))
        {
            _tileSignature = signature;
            _tilePalette = p;
            Tiles.Clear();
            TileGroups.Clear();
            var byTitle = new Dictionary<string, TileGroupViewModel>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var s in statuses)
            {
                var tile = new TileViewModel(s, p);
                Tiles.Add(tile);
                var title = string.IsNullOrWhiteSpace(s.Section) ? defaultTitle : s.Section.Trim();
                if (!byTitle.TryGetValue(title, out var group))
                {
                    group = new TileGroupViewModel(title);
                    byTitle[title] = group;
                    TileGroups.Add(group);
                }
                group.Tiles.Add(tile);
            }
        }

        MonitorTitle = _cfg.MonitorTitle;
        DashboardVisible = Screen == Screen.Ready && statuses.Count > 0;
        AllQuiet = Screen == Screen.Ready && statuses.Count == 0
            && _cfg.WatchFolders.Count > 0 && TileMode == "active";

        InboxAlerting = inboxScan.Matching
            .Any(f => FolderMonitor.IsAlerting(Path.GetFileName(f), _cfg.AlertTexts));

        var anyAlert = InboxAlerting || statuses.Any(s => s.Alerting);
        HasActiveAlert = anyAlert;
        DetectNewAlerts(inboxScan, swept);

        if (anyAlert && _cfg.FlashAlerts)
        {
            // don't reset a running flash — every poll would otherwise
            // visibly stutter the blink phase
            if (!FlashRunning)
            {
                FlashRunning = true;
                _flash.Change(600, 600);
            }
        }
        else
        {
            StopFlash();
        }
        ApplyFlashAll();
    }

    // ------------------------------------------------------------ new alerts
    // A per-file memory: which alerting files we've already reacted to. The
    // sound, the toast, and the window flash all fire ONCE per newly-arrived
    // alerting file — never on a later poll re-seeing the same one.
    //
    // The inbox and the watch folders are remembered SEPARATELY because they
    // are not always scanned together: Hidden mode skips the watch-folder
    // sweep. A skipped sweep is not evidence the alert went away, so it must
    // leave that half of the memory alone — otherwise re-showing the tiles
    // would replay every standing alert as if it had just landed.
    private readonly HashSet<string> _seenInboxAlerts = new();
    private readonly HashSet<string> _seenFolderAlerts = new();
    private bool _alertsSeeded;

    /// <summary>True while any alerting file exists — drives the taskbar badge.</summary>
    private bool _hasActiveAlert;
    public bool HasActiveAlert { get => _hasActiveAlert; private set => Set(ref _hasActiveAlert, value); }

    /// <summary>Raised when a genuinely new alerting file appears (not on
    /// startup, not on re-poll) — the window flashes its taskbar button.</summary>
    public event Action? AlertArrived;

    private sealed record AlertItem(string Key, string Label, string File, string Folder);

    /// <param name="swept">Watch-folder statuses, or null when the sweep was
    /// skipped — in which case the folder half of the memory is left intact.</param>
    private void DetectNewAlerts(Scanner.ScanResult inboxScan,
        List<FolderMonitor.FolderStatus>? swept)
    {
        // watch folders first, so the toast names one of those over an inbox
        // hit when several land at once
        var current = new List<AlertItem>();
        if (swept is not null)
            foreach (var s in swept.Where(s => s.Alerting))
                foreach (var m in s.Matches)
                    current.Add(new AlertItem($"{s.Path}\0{m}", s.Label,
                        Path.GetFileName(m), s.Path));
        var folderCount = current.Count;

        foreach (var f in inboxScan.Matching)
        {
            var nm = Path.GetFileName(f);
            if (FolderMonitor.IsAlerting(nm, _cfg.AlertTexts))
                current.Add(new AlertItem($"inbox\0{nm}", "the inbox", nm,
                    ResolvePath(_cfg.Inbox, _cfgPath)));
        }

        // first sweep after launch/rescan: adopt what's already there silently —
        // the red tiles are visible; the chime is for things that arrive later
        if (_alertsSeeded)
        {
            var fresh = current
                .Where((a, i) => !(i < folderCount ? _seenFolderAlerts : _seenInboxAlerts)
                    .Contains(a.Key))
                .ToList();
            if (fresh.Count > 0) RaiseNewAlerts(fresh);
        }
        _alertsSeeded = true;

        Remember(_seenInboxAlerts, current.Skip(folderCount));
        if (swept is not null) Remember(_seenFolderAlerts, current.Take(folderCount));
    }

    private static void Remember(HashSet<string> memory, IEnumerable<AlertItem> live)
    {
        memory.Clear();
        foreach (var a in live) memory.Add(a.Key);
    }

    private void RaiseNewAlerts(List<AlertItem> fresh)
    {
        if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.NewAlert, _cfg.Sounds.NewAlert);

        var first = fresh[0];
        ShowToast(
            fresh.Count == 1 ? "Alert" : $"{fresh.Count} new alerts",
            fresh.Count == 1 ? $"{first.File} — in {first.Label}" : $"newest: {first.File}",
            first.Folder);
        AlertArrived?.Invoke();
    }

    // ---------------------------------------------------------------- toast
    // A transient bottom-corner card when a new alert arrives — reaches you
    // even if the dashboard tile is scrolled off or the window is unfocused.
    internal const int ToastMs = 7000;
    private readonly System.Threading.Timer _toastTimer;
    private string _toastFolder = "";

    private bool _toastVisible;
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

    private string _toastText = "";
    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }

    private string _toastDetail = "";
    public string ToastDetail { get => _toastDetail; private set => Set(ref _toastDetail, value); }

    /// <summary>Internal (not private) purely as a test seam — same
    /// "settable/callable only by tests" pattern as
    /// <see cref="Theme.ThemeManager.IsHighContrast"/> — so
    /// HighlightContrastTests.MainWindowToastIconContrast can drive a real
    /// error-class notice through the exact production path instead of
    /// reflecting a private method; production code only ever reaches this
    /// via <see cref="RaiseNewAlerts"/>.</summary>
    internal void ShowToast(string text, string detail, string folder)
    {
        ToastText = text;
        ToastDetail = detail;
        _toastFolder = folder;
        ToastVisible = true;
        _toastTimer.Change(ToastMs, Timeout.Infinite);
        RefreshNotices();
    }

    internal void HideToast()
    {
        ToastVisible = false;
        _toastTimer.Change(Timeout.Infinite, Timeout.Infinite);
        RefreshNotices();
    }

    // ------------------------------------------------------ notification rail
    // MainWindow's unified bottom rail (Phase 3, Task 3.3): one vertical
    // stack of notice items replacing the old set-aside banner, history-
    // backup banner, and floating toast, all three of which used to render
    // (and animate, and gate hit-testing) completely independently of one
    // another. Notices is derived fresh from the three sources above every
    // time any of them changes — never a second source of truth — and the
    // reconciliation below is deliberately NOT a Clear()+rebuild (unlike
    // Tiles' simpler signature-guarded rebuild): a Clear() raises a single
    // CollectionChanged Reset, which regenerates EVERY container regardless
    // of whether that container's underlying item actually changed, and
    // every container's Loaded-triggered entrance animation would replay on
    // every poll tick right along with it — the exact "blink" bug
    // RefreshDashboard's own tile-signature guard exists to avoid, just one
    // layer further down (a per-item Insert/Move/Update mutation set, not a
    // do-we-rebuild-at-all flag). A genuinely NEW notice (a key that wasn't
    // present a moment ago) gets Insert()ed as a new NoticeVm and therefore
    // plays the entrance; an EXISTING notice whose text merely changed (the
    // deferred count ticking up, a fresh alert's toast text) is updated in
    // place via NoticeVm.Update, which raises ordinary property-changed
    // notifications the bound TextBlocks pick up without WPF ever tearing
    // down and rebuilding that item's container.
    public ObservableCollection<NoticeVm> Notices { get; } = new();

    /// <summary>True while the rail has anything to show. Notices.Count
    /// isn't itself an INotifyPropertyChanged member an ObservableCollection
    /// raises on its own — RefreshNotices raises this explicitly, the same
    /// way HasDeferred/HasHistoryBackupWarning wrap their own source.</summary>
    public bool HasNotices => Notices.Count > 0;

    private void RefreshNotices()
    {
        var wanted = new List<(string Key, NoticeKind Kind, string Message, string Detail)>();
        if (HasDeferred && !_deferredDismissed)
            wanted.Add(("deferred", NoticeKind.Warning, _deferredMessage, _deferredDetail));
        if (HasHistoryBackupWarning && !_historyBackupDismissed)
            wanted.Add(("history-backup", NoticeKind.Warning,
                "Today's history backup didn't complete",
                "The audit log has no fresh safety copy today — ask about disk space " +
                "or permissions if this keeps happening."));
        if (ToastVisible)
            wanted.Add(("alert", NoticeKind.Error, ToastText, ToastDetail));
        // app-qc-2026-08-21 finding 1: Config.SideFileCollisionWarning was
        // computed by Load and then read by nothing in src/ -- a config.json
        // that already had a collision (a hand edit, or a save made before
        // that check existed) started the app with no indication at all,
        // discoverable only via a refused Save or a trip into Settings. This
        // is the fix: the same non-blocking rail every other startup-time
        // problem already uses. No Detail split -- the message Config builds
        // already names both colliding keys and the save-refusal consequence
        // in one sentence, and re-deriving that split here would just be a
        // second copy of Config's own wording to keep in sync.
        if (_cfg.SideFileCollisionWarning is { } collision && !_configCollisionDismissed)
            wanted.Add(("config-collision", NoticeKind.Warning, collision, ""));

        for (var i = Notices.Count - 1; i >= 0; i--)
            if (!wanted.Any(w => w.Key == Notices[i].Key))
                Notices.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            var existing = -1;
            for (var j = 0; j < Notices.Count; j++)
                if (Notices[j].Key == w.Key) { existing = j; break; }

            if (existing < 0)
                Notices.Insert(i, new NoticeVm(w.Key, w.Kind, w.Message, w.Detail, "Open folder",
                    NoticeActionFor(w.Key), NoticeDismissFor(w.Key)));
            else
            {
                if (existing != i) Notices.Move(existing, i);
                Notices[i].Update(w.Message, w.Detail);
            }
        }

        Raise(nameof(HasNotices));
    }

    /// <summary>Reuses the SAME commands the rail's action link stands in
    /// for — OpenDeferredCommand/OpenHistoryBackupFolderCommand/
    /// OpenToastCommand already implement exactly the click behaviour each
    /// old banner/toast had, so the rail item and a future direct caller of
    /// those commands can never drift apart.</summary>
    private Action NoticeActionFor(string key) => key switch
    {
        "deferred" => () => OpenDeferredCommand.Execute(null),
        "history-backup" => () => OpenHistoryBackupFolderCommand.Execute(null),
        "alert" => () => OpenToastCommand.Execute(null),
        "config-collision" => () => OpenConfigFolderCommand.Execute(null),
        _ => () => { },
    };

    private Action NoticeDismissFor(string key) => key switch
    {
        "deferred" => () => { _deferredDismissed = true; RefreshNotices(); },
        "history-backup" => () => { _historyBackupDismissed = true; RefreshNotices(); },
        // The toast's dismiss IS HideToast -- there is no separate dismissed
        // flag to track: the same auto-hide/re-show lifecycle that already
        // governed the floating toast (ToastVisible false, then true again
        // on the next RaiseNewAlerts) is exactly the re-raise behaviour a
        // dismissed flag would otherwise exist to reproduce.
        "alert" => () => HideToast(),
        "config-collision" => () => { _configCollisionDismissed = true; RefreshNotices(); },
        _ => () => { },
    };

    internal void FlashTick()
    {
        _flashOn = !_flashOn;
        ApplyFlashAll();
    }

    private void StopFlash()
    {
        FlashRunning = false;
        _flash.Change(Timeout.Infinite, Timeout.Infinite);
        _flashOn = false;
    }

    private void ApplyFlashAll()
    {
        var p = _palette();
        foreach (var tile in Tiles) tile.ApplyFlash(_flashOn, _cfg.FlashAlerts, p);
        Raise(nameof(CountAlertOn));
    }

    // ------------------------------------------------------------ watching
    /// <summary>Debounced watcher/poll tick: refresh the set-aside alert, and
    /// either update the Ready count or feed new arrivals into the queue.
    /// All disk reads happen off-thread inside RefreshFoldersAsync.</summary>
    internal void OnFolderActivity() => _ = RefreshFoldersAsync();

    // ------------------------------------------------------ processing state
    private string _progressLine = "";
    public string ProgressLine { get => _progressLine; private set => Set(ref _progressLine, value); }

    private string _currentFilename = "";
    public string CurrentFilename { get => _currentFilename; private set => Set(ref _currentFilename, value); }

    // ---------------------------------------------------------- status line
    // The single line under the buttons carries two kinds of text, and they
    // are not interchangeable. A NOTE reports something that just happened
    // ("Undid ...", "Nothing to undo.") and goes stale the moment the next
    // document is on screen, so it expires by itself the way the last-action
    // card and the toast below it do. A STANDING line states a condition that
    // is still true ("3 files waiting in the inbox" on the Done screen, which
    // the watcher only re-states when the folder next changes) and has to
    // stay until something replaces it.
    //
    // Until 2026-08-27 every one of them was a bare assignment: the line was
    // blanked only by starting, stopping, or finishing a session, so an undo
    // note sat there for the rest of the session describing an action several
    // documents ago. Every write now goes through ShowStatusNote or
    // SetStatus -- which is why the setter is private -- so no write can
    // leave the countdown in a state that disagrees with the text.
    internal const int DefaultStatusNoteMs = 4000;

    /// <summary>How long a note stays up. Settable only by tests -- the same
    /// seam <see cref="ShowToast"/> documents -- so the expiry can be proven
    /// to fire without a test waiting four real seconds. Production never
    /// assigns it.</summary>
    internal int StatusNoteMs { get; set; } = DefaultStatusNoteMs;

    private readonly System.Threading.Timer _statusTimer;

    private string _statusLine = "";
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

    /// <summary>True while the line holds a note whose expiry is armed.
    /// Not a mirror of the timer but the thing the timer obeys: a countdown
    /// that outlives its note finds this false and does nothing. That is what
    /// makes a standing line safe from an older note's timer even in the
    /// window between the two, and it lets a test tell the two kinds apart
    /// without waiting; <c>TheUndoNoteDisappearsWithNoFurtherInput</c> is what
    /// proves an armed countdown actually fires.</summary>
    internal bool StatusNoteExpires { get; private set; }

    /// <summary>Report something that just happened, and start its expiry.</summary>
    private void ShowStatusNote(string text)
    {
        StatusLine = text;
        StatusNoteExpires = true;
        _statusTimer.Change(StatusNoteMs, Timeout.Infinite);
    }

    /// <summary>State a standing condition, or blank the line. Either way the
    /// countdown is cancelled, so an older note can never blank a line
    /// written after it.</summary>
    private void SetStatus(string text)
    {
        StatusLine = text;
        StatusNoteExpires = false;
        _statusTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The expiry timer's own callback: blanks the line only if the
    /// note it was armed for is still the one on screen. Internal so tests can
    /// cause it directly, as they do with <see cref="HideLastAction"/> and
    /// <see cref="HideToast"/>.</summary>
    internal void ExpireStatusNote()
    {
        if (StatusNoteExpires) SetStatus("");
    }

    private void ClearStatus() => SetStatus("");

    // -------------------------------------------------- last-action card
    // A transient confirmation after every commit/set-aside, colored like the
    // route that was pressed — the at-a-glance "yes, it went where I meant".
    internal const int LastActionMs = 4000;
    private readonly System.Threading.Timer _lastActionTimer;

    private bool _lastActionVisible;
    public bool LastActionVisible { get => _lastActionVisible; private set => Set(ref _lastActionVisible, value); }

    private string _lastActionText = "";
    public string LastActionText { get => _lastActionText; private set => Set(ref _lastActionText, value); }

    private string _lastActionDetail = "";
    public string LastActionDetail { get => _lastActionDetail; private set => Set(ref _lastActionDetail, value); }

    private Rgb _lastActionBack;
    public Rgb LastActionBack { get => _lastActionBack; private set => Set(ref _lastActionBack, value); }

    private Rgb _lastActionFore;
    public Rgb LastActionFore { get => _lastActionFore; private set => Set(ref _lastActionFore, value); }

    private void ShowLastAction(string text, string detail, Rgb back)
    {
        LastActionText = text;
        LastActionDetail = detail;
        LastActionBack = back;
        LastActionFore = ThemePalette.IdealForeground(back);
        LastActionVisible = true;
        _lastActionTimer.Change(LastActionMs, Timeout.Infinite);
    }

    internal void HideLastAction()
    {
        LastActionVisible = false;
        _lastActionTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public ObservableCollection<RouteButtonViewModel> Routes { get; } = new();

    /// <summary>Raised whenever the route set is rebuilt so the window can
    /// re-register the hotkey bindings.</summary>
    public event Action? RoutesRebuilt;

    /// <summary>Raised when the name box should take focus (new document).</summary>
    public event Action? RequestNameFocus;

    /// <summary>Raised once when a session starts, carrying the width-to-height
    /// ratio of the first document's page, so the window can size the viewer
    /// pane to what it is about to show. Not raised per document: the pane
    /// would then move under the user's hands every time they filed
    /// something, and a batch of scans is usually one shape anyway.</summary>
    public event Action<double>? FitViewerToPage;

    public AsyncRelayCommand<int> RouteCommand { get; }
    public AsyncRelayCommand SkipCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public RelayCommand StopCommand { get; }

    private int? _lastRoute;
    private bool _busy;   // cross-command reentrancy guard (commit/skip/undo)

    /// <summary>True while a commit/skip/undo is mid-flight. Exposed so the
    /// window can refuse to finish closing underneath one — see MainWindow's
    /// Closing handler (2026-08-04 audit 1.1).</summary>
    internal bool IsBusy => _busy;

    /// <summary>Wait for the in-flight commit to finish, up to
    /// <paramref name="timeout"/>. Returns false if it did not. Polls on the
    /// UI thread because _busy is only ever written there.</summary>
    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_busy && sw.Elapsed < timeout)
            await Task.Delay(25);
        return !_busy;
    }

    /// <summary>True once any commit/skip/undo this session has hit an
    /// AuditError. The log line must never claim "every move is in the log"
    /// in the same session where one demonstrably was not — see
    /// <see cref="ReportAuditFailure"/> and <see cref="ShowDone"/>.</summary>
    private bool _auditFailedThisSession;

    /// <summary>The same polishing the name box applies to typing — used on
    /// completer names too, so suggestions match and complete in their FINAL
    /// form (with the word separator, uppercased).</summary>
    private string Polish(string s)
    {
        if (_cfg.UppercaseNames) s = s.ToUpperInvariant();
        if (_cfg.WordSeparator.Length > 0) s = s.Replace(" ", _cfg.WordSeparator);
        return s;
    }

    /// <summary>What separates "words" in the name box: the configured word
    /// separator when set, else a space.</summary>
    private string WordBoundary =>
        _cfg.WordSeparator.Length > 0 ? _cfg.WordSeparator : " ";

    private string _typedName = "";
    public string TypedName
    {
        get => _typedName;
        set
        {
            var polished = Polish(value);
            if (Set(ref _typedName, polished))
            {
                UpdatePreview();
                // A cycling step keeps the frozen candidate list on screen.
                // Re-deriving it here is what used to collapse the popup after
                // one ↓ — see CycleSuggestion. Anything else is real typing and
                // starts the list over.
                if (!_cycling)
                {
                    ResetCycle();
                    RefreshSuggestions();
                }
            }
            else if (polished != value)
            {
                Raise();   // the view's text differs from the polished value
            }
        }
    }

    private string _preview = "";
    public string Preview { get => _preview; private set => Set(ref _preview, value); }

    private bool _previewIsWarning;
    public bool PreviewIsWarning { get => _previewIsWarning; private set => Set(ref _previewIsWarning, value); }

    public bool CanUndo => _session.CanUndo;

    private void RaiseProgress() => ProgressLine = $"{_session.Pos + 1} / {_session.Total}";

    internal void StartProcessing() => _ = RunGuarded(StartProcessingAsync(),
        "Starting that session",
        "No document has been renamed or moved. Go back to the dashboard and try again.");

    internal async Task StartProcessingAsync()
    {
        if (_busy) return;
        var cfg = _cfg;
        var cfgPath = _cfgPath;
        // the scan AND the destination probes (ProbeWritable touches every
        // route folder — a network round trip each) run off the UI thread
        var (scan, problems) = await _scheduler.Run(() =>
            (Scanner.Scan(ResolvePath(cfg.Inbox, cfgPath), cfg.Sort, cfg.NamingMode),
             cfg.Routes.Select(Config.ValidateRoute).ToList()));
        if (Screen == Screen.Processing) return;   // a double Start raced us
        if (scan.Count == 0) { Rescan(); return; }
        BuildRoutes(problems);
        _session.Start(scan.Matching);
        _lastRoute = null;
        MarkRouteState();   // Enter always has a target now — mark it before the first document
        _auditFailedThisSession = false;
        Screen = Screen.Processing;
        ClearStatus();
        HideLastAction();
        // hide the dashboard while filing
        DashboardVisible = false;
        AllQuiet = false;
        StopFlash();
        ApplyFlashAll();
        await RefreshCompleterAsync();
        await LoadCurrentAsync();
        await FitViewerToCurrentAsync();
    }

    /// <summary>Measure the document now on screen and ask the window to fit
    /// the viewer pane to it. Silent about everything that can go wrong: an
    /// empty session, a file that will not open, a page with no size. The
    /// pane keeping the size it already had is a non-event, and an inbox is
    /// exactly where unreadable files turn up.</summary>
    private async Task FitViewerToCurrentAsync()
    {
        var path = _session.Current;
        if (path is null) return;
        // a PDF header read off an SMB inbox is a network round trip
        var aspect = await _scheduler.Run(() => PageShape.AspectOf(path));
        if (aspect is > 0) FitViewerToPage?.Invoke(aspect.Value);
    }

    private void BuildRoutes(IReadOnlyList<string> problems)
    {
        Routes.Clear();
        var p = _palette();
        for (var i = 0; i < _cfg.Routes.Count; i++)
            Routes.Add(new RouteButtonViewModel(i, _cfg.Routes[i], p,
                i < problems.Count ? problems[i] : ""));
        RoutesRebuilt?.Invoke();
    }

    internal async Task LoadCurrentAsync()
    {
        var path = _session.Current;
        if (path is null) { ShowDone(); return; }
        RaiseProgress();
        CurrentFilename = Path.GetFileName(path);
        // the FIELD, not the property — so the setter's bookkeeping is skipped
        // and the frozen suggestion walk has to be cleared by hand, or the
        // next ↓ would offer this document the previous one's names
        _typedName = "";
        ResetCycle();
        Raise(nameof(TypedName));
        RefreshSuggestions();
        UpdatePreview();
        RaiseUndoState();
        await _viewer.ShowAsync(path);
        RequestNameFocus?.Invoke();
    }

    private void ShowDone()
    {
        Screen = Screen.Done;
        _viewer.Blank();
        CountLine = "Session complete";
        DetailLine = $"{_session.Filed} filed, {_session.Skipped} set aside"
            + (_session.Vanished > 0 ? $", {_session.Vanished} vanished" : "");
        LogLine = !_auditFailedThisSession && _session.Filed + _session.Skipped > 0
            ? "Every move is in the log."
            : "";
        ClearStatus();   // mid-session notes don't belong under the summary
        RaiseUndoState();
        if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Filed, _cfg.Sounds.Filed);
    }

    private void RaiseUndoState()
    {
        Raise(nameof(CanUndo));
        UndoCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------- actions
    internal async Task OnRouteAsync(int index)
    {
        // _busy is set BEFORE the first await: without it, a fast second
        // Enter/Ctrl+1 would start a second commit during ReleaseAsync's
        // yield, capturing the same textbox text and mislabeling the next doc.
        if (_busy || Screen != Screen.Processing || _session.Current is null
            || index >= _cfg.Routes.Count) return;
        if (Routes.Count > index && !Routes[index].Enabled) return;
        _busy = true;
        try
        {
            var typed = TypedName;
            await _viewer.ReleaseAsync();
            try
            {
                var route = _cfg.Routes[index];
                // the move itself can be a copy+delete across SMB shares —
                // never on the UI thread
                var outcome = await _scheduler.Run(() => _session.CommitCurrent(typed, route));
                _lastRoute = index;
                MarkRouteState();
                if (outcome.Vanished)
                {
                    ShowStatusNote("That file disappeared from the inbox — logged and moved on.");
                }
                else
                {
                    var back = ThemePalette.ParseColor(route.Color) ?? _palette().Success;
                    ShowLastAction($"✓  Filed to {route.Label}",
                        Path.GetFileName(outcome.NewPath!), back);
                }
            }
            catch (AuditError ex)
            {
                // the move already happened and the queue moved with it — report
                // and carry on rather than reloading a document that isn't there
                _lastRoute = index;
                MarkRouteState();
                HideLastAction();
                ReportAuditFailure(ex, "OrdoSort — filed, but not recorded");
            }
            catch (CommitError ex)
            {
                if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Error, _cfg.Sounds.Error);
                _dialogs.Warn(ex.Message, "OrdoSort — couldn't file it");
                await LoadCurrentAsync();   // reload the same doc; nothing moved
                return;
            }
            await RefreshDeferredAsync();
            await RefreshCompleterAsync();   // the just-used name is now suggestable
            await LoadCurrentAsync();
        }
        finally { _busy = false; }
    }

    internal async Task OnSkipAsync()
    {
        if (_busy || Screen != Screen.Processing || _session.Current is null) return;
        _busy = true;
        try
        {
            await _viewer.ReleaseAsync();
            try
            {
                var outcome = await _scheduler.Run(() => _session.SkipCurrent());
                if (outcome.Vanished)
                    ShowStatusNote("That file disappeared from the inbox — logged and moved on.");
                else
                {
                    ShowLastAction("✓  Set aside for later",
                        Path.GetFileName(outcome.NewPath!), _palette().Warning);
                    if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.SetAside, _cfg.Sounds.SetAside);
                }
            }
            catch (AuditError ex)
            {
                HideLastAction();
                ReportAuditFailure(ex, "OrdoSort — set aside, but not recorded");
            }
            catch (CommitError ex)
            {
                if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Error, _cfg.Sounds.Error);
                _dialogs.Warn(ex.Message, "OrdoSort — set-aside failed");
            }
            await RefreshDeferredAsync();
            await LoadCurrentAsync();
        }
        finally { _busy = false; }
    }

    internal void OnUndo() => _ = OnUndoAsync();

    internal async Task OnUndoAsync()
    {
        if (_busy) return;
        if (!_session.CanUndo) { ShowStatusNote("Nothing to undo."); return; }
        _busy = true;
        try
        {
            try
            {
                var (filed, original) = await _scheduler.Run(() => _session.UndoLast());
                ShowStatusNote($"Undid {Path.GetFileName(filed)} → {Path.GetFileName(original)}");
                HideLastAction();   // the card must never claim an undone filing
            }
            catch (AuditError ex)
            {
                // the file is back; only the history row is stale
                HideLastAction();
                ReportAuditFailure(ex, "OrdoSort — undone, but still logged as filed");
            }
            catch (CommitError ex)
            {
                _dialogs.Warn(ex.Message, "OrdoSort — undo failed");
                return;
            }
            if (Screen == Screen.Done)   // undo from Done re-enters the session
                Screen = Screen.Processing;
            await RefreshDeferredAsync();
            await RefreshCompleterAsync();   // a reverted name may drop out
            await LoadCurrentAsync();
        }
        finally { _busy = false; }
    }

    /// <summary>Index of the route Enter would press right now: null while
    /// there are no routes to press; otherwise the last-used route (falling
    /// back to the first before anything's been filed) when enter_commits is
    /// on, or always the first when it's off. The single source both
    /// MarkRouteState's ⏎ badge and UpdatePreview's route override read, so
    /// the badge, the live preview, and what Enter actually files can never
    /// drift apart from one another.</summary>
    private int? EnterTargetIndex() =>
        Routes.Count == 0 ? null : (_cfg.EnterCommits ? (_lastRoute ?? 0) : 0);

    /// <summary>Refresh both per-route marks in one pass, so they can never
    /// drift apart: the ⏎ badge on the one button Enter would press right now,
    /// and the lit trail rule on the route this session filed to last. Enter's
    /// target depends on enter_commits; the trail rule does not.</summary>
    private void MarkRouteState()
    {
        var enterTarget = EnterTargetIndex();
        for (var i = 0; i < Routes.Count; i++)
        {
            Routes[i].IsEnterTarget = i == enterTarget;
            Routes[i].IsLastUsed = i == _lastRoute;
        }
    }

    /// <summary>Enter always files: the last-used route — starting at the
    /// first — or always the first, per enter_commits.</summary>
    public void OnEnter() => _ = OnEnterAsync();

    /// <summary>Fires the resolved route through RouteCommand — never
    /// OnRouteAsync directly — so Enter gets exactly the same OnError
    /// reporting and reentrancy guard a button press gets. A button click is
    /// routed through AsyncRelayCommand already; Enter is the app's primary
    /// filing gesture and must not be the one path where an exception can go
    /// unobserved.</summary>
    internal Task OnEnterAsync()
    {
        if (Screen != Screen.Processing) return Task.CompletedTask;
        var target = EnterTargetIndex();
        if (target is not { } i) return Task.CompletedTask;
        RouteCommand.Execute(i);
        return RouteCommand.Completion;
    }

    public RelayCommand ExportHistoryCommand { get; }

    /// <summary>Raised after new settings are adopted (window re-applies the
    /// font resources and hotkey bindings).</summary>
    public event Action? SettingsApplied;

    /// <summary>Persist the live config now (tool windows saving their own
    /// state: merge headers, remembered passwords).</summary>
    internal void SaveConfigNow()
    {
        RefreshSharedSectionsFromDisk();
        if (!Config.TrySave(_cfg, _cfgPath, out var error, out var refusedKeys))
            WarnSaveFailure(error, refusedKeys);
    }

    /// <summary>Surface a <see cref="Config.TrySave"/> failure — but only
    /// once per running session for a failure that is ENTIRELY side-file
    /// confinement refusals (<paramref name="refusedKeys"/> non-empty; see
    /// the 4-arg <see cref="Config.TrySave"/> overload's doc comment for
    /// exactly when that is). Such a failure is a structural property of
    /// the CONFIGURED PATH — it recurs unchanged on every future save until
    /// someone edits the offending key in Settings — so a caller of this
    /// method that saves nothing but an unrelated main-section field (the
    /// tile-visibility dropdown, merge headers) would otherwise show
    /// "settings not saved" forever, with no new information and no
    /// actionable next step, on every single one of those unrelated saves:
    /// exactly the bricking bug this method exists to stop (2026-08-07
    /// audit, Task 1b). Any OTHER failure — a real, potentially transient
    /// I/O problem (locked file, full disk, permissions) — is never
    /// suppressed: <paramref name="refusedKeys"/> is empty whenever one of
    /// those is mixed into the same failure, by the 4-arg overload's own
    /// contract, so the "warn every time" branch below still fires for it
    /// even alongside an already-known refusal.
    ///
    /// <paramref name="alwaysWarn"/> (final review, Important 1, 2026-08-07)
    /// bypasses the once-per-session suppression entirely. It exists for
    /// exactly one caller — <see cref="ApplySettingsAsync"/> — whose whole
    /// job on the path that reaches this method is persisting the Settings
    /// window's own edits to Routes/WatchFolders/AlertTexts. The
    /// suppression above is correct for <see cref="SaveConfigNow"/>: a
    /// background save of an unrelated field (tile-visibility, merge
    /// headers) shouldn't re-nag about a side-file path the user already
    /// heard about. It is wrong for an explicit Settings OK: if a save had
    /// already warned once earlier in the session (a background save, or an
    /// earlier Settings attempt), the suppression would silently eat the
    /// dialog for THIS save too — and this save is the one whose entire
    /// purpose was writing the user's just-made edits to those side files.
    /// Without <paramref name="alwaysWarn"/>, those edits would live only in
    /// <c>_cfg</c>, vanish on restart, and never reach a peer station, with
    /// no dialog telling the user any of that happened. The refused keys are
    /// still added to <c>_warnedRefusedSideFileKeys</c> either way, so a
    /// later background save at this same station still gets the
    /// once-per-session treatment rather than re-nagging on every unrelated
    /// field edit.</summary>
    private void WarnSaveFailure(string error, IReadOnlyList<string> refusedKeys, bool alwaysWarn = false)
    {
        if (refusedKeys.Count == 0)
        {
            _dialogs.Warn(error, "OrdoSort — settings not saved");
            return;
        }
        var newlyRefused = refusedKeys.Where(k => _warnedRefusedSideFileKeys.Add(k)).ToList();
        if (alwaysWarn || newlyRefused.Count > 0)
            _dialogs.Warn(error, "OrdoSort — settings not saved");
    }

    /// <summary>Persist ONLY <c>SavedPasswords</c> — what every Unlock-tool
    /// persist path actually changes, including the load-time sweep (2026-08
    /// audit 4.3[A]) that now runs every time the Unlock window opens, not
    /// just on a deliberate password edit.
    ///
    /// <see cref="SaveConfigNow"/> writes the WHOLE live <c>_cfg</c>, and
    /// <see cref="RefreshSharedSectionsFromDisk"/> only refreshes the four
    /// side files first — Theme, TileVisibility, MergeHeaders, LabelClients,
    /// Sounds and the rest of the main config.json section still come from
    /// this station's own in-memory copy, which can be stale. That gap in
    /// SaveConfigNow is pre-existing and shared infrastructure other tools
    /// (Match &amp; merge, box labels) also rely on — deliberately NOT widened
    /// here, since it used to take a deliberate edit in one of those tools to
    /// reach it and still does. Opening Unlock now reaches a save
    /// PASSIVELY, so this save gets its own, narrower contract: read the
    /// entire config fresh from disk right before writing, overlay just
    /// this station's <c>SavedPasswords</c> (the one field this call is
    /// entitled to change) onto that fresh copy, and write THAT — so a
    /// peer station's concurrent edit to any other field survives a
    /// station that merely opened the Unlock window.
    ///
    /// A full <see cref="Config.Load"/> rather than hand-copying each
    /// scalar Config owns is deliberate: enumerating fields invites missing
    /// one the next time Config grows a field, where re-reading everything
    /// and overlaying the one field this call owns is correct by
    /// construction. Does not reassign the live <c>_cfg</c> field — this
    /// station's in-memory copy of Theme/TileVisibility/etc. is left exactly
    /// as stale (or fresh) as it already was; only what reaches disk
    /// changes. A broken/mid-write shared config.json falls back to saving
    /// this station's own in-memory <c>_cfg</c> — main file only, same as
    /// the ordinary path below — rather than losing the sweep's result.
    ///
    /// The write itself goes through <see cref="Config.TrySaveMain"/>, not
    /// <see cref="Config.TrySave"/> (final review, Important 3, 2026-08-06):
    /// this call is entitled to change SavedPasswords, a MAIN-section
    /// field, and nothing else, but TrySave also rewrites all three side
    /// files plus the box-labels bootstrap on every call — which broke this
    /// method's own contract two ways. A station with a legitimately-
    /// Browsed ABSOLUTE side-file path (a shipped Settings capability) has
    /// that path refused on every WRITE (see
    /// <see cref="Config.ResolveBesideForWrite"/>), so TrySave's side-file
    /// attempt for it always failed and the overall call returned false —
    /// even though the password change had already reached disk via
    /// TrySave's main-file write, which runs first. UnlockViewModel's
    /// constructor gates its "passwords protected" notice on this method's
    /// return value, so that notice went silently missing on exactly the
    /// stations where it mattered, right beside a "settings not saved"
    /// warning about a save that had actually partly landed. Separately,
    /// rewriting all three side files on every Unlock-window open — not
    /// just on a deliberate Settings edit — re-serialized a hand-edited
    /// side file byte-for-byte-unchanged, which trips a peer's hash-based
    /// Settings-conflict prompt (<see cref="SnapshotSections"/>) over a
    /// file this call was never entitled to touch. Writing only the main
    /// file removes both failure surfaces, and makes "persist ONLY
    /// SavedPasswords" (this method's own doc, above) true at the file
    /// level too, not just the field level.
    ///
    /// This TrySaveMain swap is only correct HERE, precisely because this
    /// method's own contract (above) is "persist ONLY SavedPasswords" — it
    /// has nothing of its own to say about Routes/WatchFolders/AlertTexts,
    /// so skipping the three side files entirely costs it nothing.
    /// <see cref="SaveConfigNow"/> and <see cref="ApplySettingsAsync"/> sit
    /// on the exact same absolute-side-file-path hazard described above
    /// (2026-08-07 audit, Task 1b: this was originally fixed for this
    /// method ONLY, leaving those two still calling <c>TrySave</c>
    /// unguarded — a comment here claiming the hazard handled would have
    /// been actively misleading about them) but CANNOT take this same
    /// fix: SaveConfigNow persists tile-visibility/merge-header edits
    /// alongside whichever Routes/WatchFolders/AlertTexts edits a peer's
    /// concurrent Settings save landed since this station last read them,
    /// and ApplySettingsAsync's entire job is persisting a Settings
    /// session's edits to exactly those three side files — TrySaveMain
    /// would silently stop saving a user's destination and monitored-
    /// folder edits at both call sites, a materially worse bug than the
    /// one being fixed. They keep <c>TrySave</c> and instead: (1) the
    /// Settings "Data files" Browse... buttons now refuse to hand back an
    /// absolute path outside the config directory in the first place
    /// (SettingsViewModel's four <c>Browse*FileCommand</c>s), so the
    /// hazard can no longer be freshly introduced through the UI; and (2)
    /// for a config that already carries such a path from before this fix
    /// (or from a hand edit), <see cref="WarnSaveFailure"/> still lands
    /// every save that CAN succeed — the main config and any side file
    /// whose own path is fine — and warns about a purely-confinement
    /// failure once per running session instead of on every unrelated
    /// save, rather than bricking the station's whole Settings experience
    /// the way this method's own bug once did.
    ///
    /// An ABSENT config.json is deliberately NOT treated as first run and
    /// gets no fallback write at all (fix round 2, Gap B): this method only
    /// ever runs on a station that already holds a loaded <c>_cfg</c> in
    /// memory, which proves the file existed at least once already — a
    /// missing file AT THIS MOMENT means a transient share hiccup, an
    /// external delete, or a peer's own atomic write caught mid-rename, not
    /// a genuine new install. The default <see cref="Config.Load(string)"/>
    /// doesn't know that: its first-run path treats ANY missing file as
    /// "create and save a fresh all-defaults Config" — and it does that
    /// SAVE itself, silently, before this method would even get to overlay
    /// <c>SavedPasswords</c> onto the result. Calling it unchecked here
    /// would wipe every peer's Theme/TileVisibility/MergeHeaders/Sounds/etc.
    /// with factory defaults on nothing more than a momentarily-missing
    /// file — exactly the peer-clobber class this method exists to
    /// prevent, arriving through Config.Load's own write instead of this
    /// method's.
    ///
    /// Final review (2026-08-06): this now calls
    /// <see cref="Config.Load(string,bool)"/> with
    /// <c>createIfMissing: false</c> instead of pairing a separate
    /// <c>File.Exists</c> pre-check with the create-on-missing overload —
    /// the previous shape narrowed the gap between "checked" and "loaded"
    /// to milliseconds rather than closing it: a file that vanished in that
    /// exact window still hit Load's first-run path and wrote fresh
    /// defaults over a peer's config, the same bug one level down. With no
    /// separate check, there is no window left to race.</summary>
    /// <returns>True if the write reached disk.</returns>
    internal bool SaveSavedPasswordsNow()
    {
        Config fresh;
        try
        {
            fresh = Config.Load(_cfgPath, createIfMissing: false);
        }
        catch (ConfigMissingException)
        {
            _dialogs.Warn(
                "Couldn't save the saved-password change — the shared config file is " +
                "missing right now (a share hiccup, or a peer saving at this exact " +
                "moment). Nothing was overwritten; try again in a moment.",
                "OrdoSort — settings not saved");
            return false;
        }
        catch (ConfigException)
        {
            // Exists but is broken/mid-write: fall back to this station's
            // own in-memory copy — main file only, same narrower write as
            // the ordinary path below — rather than losing the sweep's
            // result entirely.
            if (!Config.TrySaveMain(_cfg, _cfgPath, out var fallbackError))
            {
                _dialogs.Warn(fallbackError, "OrdoSort — settings not saved");
                return false;
            }
            return true;
        }

        fresh.SavedPasswords = _cfg.SavedPasswords;
        if (!Config.TrySaveMain(fresh, _cfgPath, out var error))
        {
            _dialogs.Warn(error, "OrdoSort — settings not saved");
            return false;
        }
        return true;
    }

    /// <summary>Before a tool-state save (tile-visibility toggle, merge
    /// headers, remembered passwords) rewrites all three Settings-owned side
    /// files, pull each one fresh from disk first. _cfg is whatever this run
    /// started with — without this, a full TrySave would silently revert an
    /// admin's intervening edit to a shared file the moment any tool saves
    /// its own unrelated state. box-labels.json is untouched here: it's
    /// already protected as a bootstrap-only write with its own exclusive
    /// writer (BoxLabelStore). A section whose file doesn't exist yet keeps
    /// its in-memory values so TrySave can still create it (first-run
    /// migration); a section whose file exists but fails to parse also keeps
    /// the in-memory copy — a broken shared file must not block a password
    /// save.</summary>
    private void RefreshSharedSectionsFromDisk()
    {
        try
        {
            if (Config.ReadDoc<DestinationsDoc>(_cfgPath, _cfg.DestinationsFile) is { } dd)
            {
                _cfg.Routes = dd.Routes ?? new();
                _cfg.DestinationsFileExtras = dd.Extras ?? new();
            }
        }
        catch (ConfigException) { /* broken shared file: keep the in-memory copy */ }

        try
        {
            if (Config.ReadDoc<MonitoredFoldersDoc>(_cfgPath, _cfg.MonitoredFoldersFile) is { } md)
            {
                _cfg.WatchFolders = md.WatchFolders ?? new();
                _cfg.MonitoredFoldersFileExtras = md.Extras ?? new();
            }
        }
        catch (ConfigException) { /* broken shared file: keep the in-memory copy */ }

        try
        {
            if (Config.ReadDoc<AlertsDoc>(_cfgPath, _cfg.AlertsFile) is { } ad)
            {
                _cfg.AlertTexts = ad.AlertTexts ?? new();
                _cfg.AlertsFileExtras = ad.Extras ?? new();
            }
        }
        catch (ConfigException) { /* broken shared file: keep the in-memory copy */ }
    }

    internal void SaveMergeHeaders(Dictionary<string, string> headers)
    {
        _cfg.MergeHeaders = headers;
        SaveConfigNow();
    }

    /// <summary>Adopt a new config: re-open the DB if its path changed (with a
    /// fresh daily backup for the NEW db), save (warning, not crashing, on a
    /// read-only file), rebuild watchers, refresh Ready. Settings is only
    /// reachable from Ready, so no live session.</summary>
    internal void ApplySettings(Config cfg) => _ = RunGuarded(ApplySettingsAsync(cfg),
        "Saving your settings",
        "Some of what you changed may not have been applied. Reopen Settings to check.");

    internal async Task ApplySettingsAsync(Config cfg)
    {
        // Audit 2.1: two stations editing Settings concurrently — the second
        // to press OK would otherwise silently overwrite the first. If a
        // snapshot was taken when this station's Settings window opened,
        // re-snapshot now and compare; a difference means a peer station
        // saved a shared section while this dialog was open. The decision
        // to proceed anyway is the user's, not the app's. Consumed (set to
        // null) either way — this editing session is over the moment this
        // method runs, whether it goes on to save or bails out below.
        if (_settingsSnapshot is { } openedAt)
        {
            _settingsSnapshot = null;
            var changed = ChangedSectionNames(openedAt, SnapshotSections());
            if (changed.Count > 0)
            {
                var proceed = _dialogs.Confirm(
                    $"Another station changed the {JoinNaturally(changed)} while you had " +
                    "Settings open. Saving now will replace their changes with yours. Save anyway, " +
                    "or cancel and reopen Settings to see theirs?",
                    "OrdoSort — settings conflict", "Save anyway", "Cancel");
                if (!proceed) return;   // abandon: _cfg, history, watchers all left untouched
            }
        }

        // history_db stays deliberately unconfined here too — see the
        // constructor's comment on its own ResolvePath(cfg.HistoryDb, ...)
        // call above for why, and for the verified residual exposure.
        var oldDbSetting = _cfg.HistoryDb;
        var oldDb = ResolvePath(oldDbSetting, _cfgPath);
        var newDb = ResolvePath(cfg.HistoryDb, _cfgPath);
        if (!string.Equals(oldDb, newDb, StringComparison.OrdinalIgnoreCase))
        {
            // the backup copies the whole DB file — off the UI thread, it can
            // live on a share. While the swap runs, _history still points at
            // the OLD (live) instance: HistorySwapping gates the two UI entry
            // points (History window, CSV export) that could touch it mid-swap.
            //
            // The new History is built FIRST and the old one is disposed only
            // once the new one exists and is open. If construction throws,
            // _history must still be the live old instance — autocomplete,
            // CSV export and the History window must not go dark for the
            // rest of the session just because the new path didn't pan out.
            HistorySwapping = true;
            // Set inside the background lambda (before the History that can
            // throw), read back here only once the swap actually succeeds —
            // an abandoned newDb attempt must not overwrite the warning that
            // describes the database still actually in use. See
            // RunHistoryBackup for why BackupDaily's own null return can't
            // tell "nothing to back up" from "genuine failure" by itself.
            var backupOk = true;
            var backupDir = _historyBackupDir;
            try
            {
                var fresh = await _scheduler.Run(() =>
                {
                    backupOk = RunHistoryBackup(newDb, out backupDir);
                    return new History(newDb);
                });
                _history.Dispose();
                _history = fresh;
                _historyBackupDir = backupDir;
                SetHistoryBackupWarning(backupOk);
            }
            catch (Exception ex)
            {
                // Keep the old, still-open database live for the rest of the
                // session. Don't let the broken path ride along into the
                // config we're about to persist below — otherwise the next
                // save (or the next launch) would silently retry, and fail
                // against, the same unreachable path.
                cfg.HistoryDb = oldDbSetting;
                _dialogs.Warn(
                    $"Couldn't open the new history database at \"{newDb}\":\n\n{ex.Message}" +
                    "\n\nKeeping the previous database for this session.",
                    "OrdoSort — history database");
            }
            finally { HistorySwapping = false; }
        }

        // Final review, Important 1 (2026-08-06): cfg.SavedPasswords is
        // whatever this station's Settings window loaded when it OPENED
        // (FreshConfigForSettings) — Settings itself never edits that field
        // (SettingsViewModel.TryBuildResult carries the JSON-cloned original
        // through untouched; the Unlock window's "Manage saved…" dialog is
        // the only editor, persisting straight to config.json through
        // SaveSavedPasswordsNow). Between then and this OK, a peer's Unlock
        // window can have swept legacy plaintext into DPAPI ciphertext and
        // already persisted that (the load-time sweep this branch made
        // passive — UnlockViewModel's constructor). Saving cfg verbatim
        // would silently overwrite that protection: exactly the bug this
        // guards against.
        //
        // The narrow fix — read the CURRENT on-disk value and keep that
        // instead of the stale snapshot — was chosen over widening
        // SnapshotSections/SectionFingerprint (the peer-edit conflict
        // prompt above) to also fingerprint config.json. The broader
        // fingerprint would only ever ASK — "another station changed the
        // settings, save anyway?" — and a user who answers "save anyway"
        // (the same choice every existing conflict prompt offers) would
        // still resurrect the plaintext; it also touches conflict-detection
        // code shared by SnapshotSections' three other callers and would
        // need a new, necessarily vague section name (config.json's main
        // section has no single field granularity the way the three side
        // files do). This overlay makes the resurrection structurally
        // impossible instead of merely asking about it, and is safe by
        // construction: Settings has nothing of its own to lose here, since
        // it never had an edit to this field in the first place. Mirrors
        // SaveSavedPasswordsNow's identical overlay for the same field.
        //
        // A missing/unreadable config.json at this exact instant (a share
        // hiccup, or a peer's own atomic replace caught mid-swap) leaves
        // nothing fresher to read — cfg simply keeps what Settings opened
        // with, and the ordinary TrySave below still carries the rest of
        // this station's edits through; this overlay is best-effort, not
        // the primary save operation. Config.Load(path, createIfMissing:
        // false) — not the create-on-missing default overload — so a
        // momentarily-missing file throws instead of silently creating and
        // SAVING a fresh all-defaults config right here (the exact
        // peer-clobber Gap B closed elsewhere; see
        // SaveSavedPasswordsNow's doc comment). ConfigMissingException IS a
        // ConfigException, so this one catch covers both "missing" and
        // "exists but corrupt" identically — either way there is nothing
        // fresher to overlay, and both are equally safe to just skip.
        try { cfg.SavedPasswords = Config.Load(_cfgPath, createIfMissing: false).SavedPasswords; }
        catch (ConfigException) { /* keep what Settings opened with */ }

        _cfg = cfg;
        // alwaysWarn: true — final review, Important 1. This save IS the
        // Settings window's persist step; a suppression meant for
        // background saves of unrelated fields must never eat the one
        // dialog that would tell the user their just-made Routes/
        // WatchFolders/AlertTexts edits didn't reach disk. See
        // WarnSaveFailure's doc comment.
        if (!Config.TrySave(cfg, _cfgPath, out var error, out var refusedKeys))
            WarnSaveFailure(error, refusedKeys, alwaysWarn: true);
        _session = new Session(cfg, _history, _cfgPath);
        await _scheduler.Run(() => _watch.SetFolders(
            ResolvePath(cfg.Inbox, _cfgPath), ResolvePath(cfg.Deferred, _cfgPath)));
        _watch.SetPollInterval(cfg.PollSeconds * 1000);   // adopt a changed cadence live
        Raise(nameof(UppercaseNames));
        Raise(nameof(TileVisibilityIndex));
        Raise(nameof(TileControlsVisible));
        SettingsApplied?.Invoke();
        Rescan();
    }

    /// <summary>File → Export history: the whole audit table as a spreadsheet
    /// (with the formula-injection guard History applies).</summary>
    internal void ExportHistory() => _ = ExportHistoryAsync();

    internal async Task ExportHistoryAsync()
    {
        if (HistorySwapping) return;   // mid-swap, the handle is being replaced
        var dest = _dialogs.AskSaveFile("Spreadsheet files (*.csv)|*.csv", "ordosort_history.csv");
        if (dest is null) return;
        try
        {
            var history = _history;
            var count = await _scheduler.Run(() => history.ExportCsv(dest));
            _dialogs.Info($"Exported {count} rows to {dest}", "OrdoSort");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Warn("Couldn't save it: " + ex.Message, "OrdoSort");
        }
    }

    /// <summary>Esc: back to Ready. Nothing is lost — the remaining queue
    /// stays in the inbox.</summary>
    internal void StopSession()
    {
        if (Screen != Screen.Processing || _busy) return;
        ClearStatus();
        Rescan();
    }

    /// <summary>Live "will be filed as" preview — the same BuildTarget the
    /// commit uses, so an illegal name (colon…) warns before the button. Keyed
    /// off the SAME route Enter would press right now (EnterTargetIndex) —
    /// not just _lastRoute — so a fresh session (or enter_commits off) still
    /// previews the route override Enter is actually about to use, rather
    /// than silently ignoring it.</summary>
    private void UpdatePreview()
    {
        var current = _session.Current;
        if (Screen != Screen.Processing || current is null)
        {
            Preview = "";
            PreviewIsWarning = false;
            return;
        }
        try
        {
            var target = EnterTargetIndex();
            var route = target is { } i && i < _cfg.Routes.Count ? _cfg.Routes[i] : null;
            var result = Naming.BuildTarget(
                Path.GetFileName(current), TypedName,
                route?.NamingMode, _session.SessionMode,
                route?.Suffix ?? "", route?.AppendSuffix ?? false,
                _ => false);
            Preview = result.Filename;
            PreviewIsWarning = false;
        }
        catch (ArgumentException ex)
        {
            Preview = "⚠ " + ex.Message;
            PreviewIsWarning = true;
        }
    }

    // -------------------------------------------------------- autocomplete
    private List<string> _allNames = new();

    public ObservableCollection<string> Suggestions { get; } = new();

    private async Task RefreshCompleterAsync()
    {
        var namesFile = string.IsNullOrWhiteSpace(_cfg.NamesFile)
            ? null : ResolvePath(_cfg.NamesFile, _cfgPath);
        var history = _history;
        // seed-file read + SQLite query off-thread; polished once here so a
        // seed list with spaces still suggests correctly with a separator set
        try
        {
            _allNames = await _scheduler.Run(() =>
                Completer.Names(history, Completer.LoadSeedNames(namesFile))
                    .Select(Polish).Distinct().ToList());
        }
        catch (Exception)
        {
            // Suggestions are a convenience, and this runs after EVERY commit.
            // An unreadable history (locked share, a DB that just died) must
            // not abort the filing loop on its way to the next document — keep
            // the names we already have and move on.
            return;
        }
        RefreshSuggestions();
    }

    private bool _hasSuggestions;
    public bool HasSuggestions { get => _hasSuggestions; private set => Set(ref _hasSuggestions, value); }

    /// <summary>Top prefix matches for the popup; empty box suggests nothing
    /// (the ranked list would just be noise).</summary>
    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        if (Screen == Screen.Processing && TypedName.Length > 0)
        {
            foreach (var name in _allNames
                         .Where(n => n.StartsWith(TypedName, StringComparison.OrdinalIgnoreCase)
                                     && !n.Equals(TypedName, StringComparison.OrdinalIgnoreCase))
                         .Take(8))
                Suggestions.Add(name);
        }
        HasSuggestions = Suggestions.Count > 0;
    }

    /// <summary>Close the popup (Esc / focus loss); it reopens on typing.</summary>
    internal void DismissSuggestions()
    {
        Suggestions.Clear();
        HasSuggestions = false;
        ResetCycle();
    }

    // ------------------------------------------------- suggestion cycling
    // Taking a suggestion sets TypedName, and that setter re-derives the list
    // from the new, longer text — which drops the exact match and empties the
    // popup. So walking the list cannot read from the live list: the
    // candidates AND the text the user actually typed are frozen for the
    // duration of a walk, and only fresh typing rebuilds them.
    private List<string> _cycle = new();
    private string _cycleStem = "";
    private int _cycleSlot;    // 0 = the text as typed, 1..n = candidates
    private bool _cycling;

    private void ResetCycle()
    {
        if (_cycle.Count > 0) _cycle = new List<string>();
        _cycleStem = "";
        _cycleSlot = 0;
    }

    /// <summary>Walk the suggestions: +1 for ↓, -1 for ↑, wrapping. The walk
    /// includes the text as typed, so it always comes back round to where it
    /// started and a wrong guess never traps you. The first ↓ still lands on
    /// the top match, so the one-press habit is unchanged — and Enter is not
    /// involved at any point, so it stays free to commit.</summary>
    internal bool CycleSuggestion(int direction)
    {
        if (_cycle.Count == 0)
        {
            if (Suggestions.Count == 0) return false;
            _cycle = Suggestions.ToList();
            _cycleStem = TypedName;
            _cycleSlot = 0;
        }
        var slots = _cycle.Count + 1;
        _cycleSlot = ((_cycleSlot + direction) % slots + slots) % slots;
        _cycling = true;
        try { TypedName = _cycleSlot == 0 ? _cycleStem : _cycle[_cycleSlot - 1]; }
        finally { _cycling = false; }
        return true;
    }


    /// <summary>Tab: complete the top suggestion one word at a time, honoring
    /// the configured word separator as the boundary (Enter stays free to
    /// commit).</summary>
    internal bool CompleteNextWord()
    {
        if (Suggestions.Count == 0) return false;
        var full = Suggestions[0];
        var sep = WordBoundary;
        var len = TypedName.Length;
        if (len >= full.Length) return false;
        var start = len;
        if (start + sep.Length <= full.Length
            && string.CompareOrdinal(full, start, sep, 0, sep.Length) == 0)
            start += sep.Length;
        var idx = start < full.Length
            ? full.IndexOf(sep, start, StringComparison.Ordinal)
            : -1;
        TypedName = idx < 0 ? full : full[..idx];
        return true;
    }

    /// <summary>Shift+Tab: drop the last completed word (same boundary).
    /// False when the box is already empty — the keystroke then traverses
    /// focus backward normally instead of being swallowed.</summary>
    internal bool DropLastWord()
    {
        if (TypedName.Length == 0) return false;
        var sep = WordBoundary;
        var t = TypedName;
        if (t.EndsWith(sep, StringComparison.Ordinal)) t = t[..^sep.Length];
        var i = t.LastIndexOf(sep, StringComparison.Ordinal);
        TypedName = i < 0 ? "" : t[..i];
        return true;
    }

    // ------------------------------------------------------------- helpers
    // Thin wrapper over Config.ResolveBeside — kept as its own name/call
    // shape (every existing call site here already reads `ResolvePath(x,
    // _cfgPath)`) rather than rewriting them, but no longer a second copy of
    // the resolution logic itself: an absolute value stays; a relative one
    // lands beside config.json. Unconfined, deliberately — see
    // Config.ResolveBeside's own doc comment for why the four side-file
    // keys get a confined variant and general config-relative paths
    // (Inbox/Deferred, the section-file "where would this point?" previews)
    // do not.
    internal static string ResolvePath(string value, string cfgPath) =>
        Config.ResolveBeside(cfgPath, value);

    // Scanner.DeferredSummary's own blank-folder guard (Scanner.cs) treats
    // "" as "nothing set aside" — but only if it is still blank by the time
    // DeferredSummary sees it. Path.Combine(dir, "") == dir, so feeding a
    // blank cfg.Deferred through ResolvePath first flattens it to
    // config.json's own directory, which is non-blank and always exists:
    // DeferredSummary's guard never fires, and it counts whatever already
    // lives beside config.json (history.sqlite, at minimum) as "set-aside
    // files waiting" (2026-08-21 QC-02 audit). Only the two
    // Scanner.DeferredSummary call sites need this — they're the ones
    // whose blank-check depends on the value staying blank.
    internal static string ResolveDeferredPath(string value, string cfgPath) =>
        string.IsNullOrWhiteSpace(value) ? value : ResolvePath(value, cfgPath);

    internal static void OpenFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            { UseShellExecute = true });
    }

    /// <summary>Run the daily history-DB backup and report whether it
    /// actually succeeded. <see cref="HistoryBackup.BackupDaily"/> returns
    /// null for two different reasons — nothing to back up yet (no DB file,
    /// e.g. first run) and a genuine failure (permissions, a full disk, a
    /// disconnected share) — and never throws either way, so the null alone
    /// can't tell them apart (2026-08-07 QC sweep R1). This resolves that
    /// ambiguity the same way BackupDaily itself does internally — checking
    /// File.Exists(dbPath) — so only a DB that existed and still came back
    /// null counts as a failure worth telling the user about.</summary>
    private static bool RunHistoryBackup(string dbPath, out string backupDir)
    {
        backupDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backups");
        var dbExisted = File.Exists(dbPath);
        return !dbExisted || HistoryBackup.BackupDaily(dbPath, backupDir, DateTime.Now) is not null;
    }

    public void Dispose()
    {
        _watch.Activity -= OnFolderActivity;
        _flash.Dispose();
        _lastActionTimer.Dispose();
        _toastTimer.Dispose();
        _statusTimer.Dispose();
        _history.Dispose();
    }
}
