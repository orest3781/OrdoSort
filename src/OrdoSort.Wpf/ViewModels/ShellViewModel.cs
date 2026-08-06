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
        HistoryBackup.BackupDaily(dbPath,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backups"),
            DateTime.Now);
        _history = new History(dbPath);
        _session = new Session(cfg, _history);

        StartCommand = new RelayCommand(StartProcessing, () => StartEnabled);
        RescanCommand = new RelayCommand(Rescan);
        OpenDeferredCommand = new RelayCommand(() => OpenFolder(_cfg.Deferred));
        OpenInboxCommand = new RelayCommand(() => OpenFolder(_cfg.Inbox));
        OpenToastCommand = new RelayCommand(() => { OpenFolder(_toastFolder); HideToast(); });
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

    /// <summary>Tell the user a document moved but went unrecorded, and keep
    /// going — the queue has already advanced past it.</summary>
    private void ReportAuditFailure(AuditError ex, string title)
    {
        _auditFailedThisSession = true;
        if (_cfg.Sounds.Enabled) _sounds.Play(SoundEvent.Error, _cfg.Sounds.Error);
        UnexpectedError?.Invoke(ex);
        _dialogs.Warn(ex.Message, title);
        StatusLine = $"{Path.GetFileName(ex.NewPath)} moved, but the history " +
                     "database didn't record it — see the warning.";
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

    public RelayCommand StartCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand OpenDeferredCommand { get; }
    public RelayCommand OpenInboxCommand { get; }
    public RelayCommand OpenToastCommand { get; }

    /// <summary>Called once by the window after the viewer init attempt:
    /// start watching and take the first scan.</summary>
    public void Initialize() => _ = InitializeAsync();

    internal async Task InitializeAsync()
    {
        var cfg = _cfg;
        // Directory.Exists + watcher registration are network round trips on
        // an SMB share — never on the UI thread
        await _scheduler.Run(() => _watch.SetFolders(cfg.Inbox, cfg.Deferred));
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
                var snap = await _scheduler.Run(() => new FolderSnapshot(
                    Scanner.Scan(cfg.Inbox, cfg.Sort, cfg.NamingMode),
                    Scanner.DeferredSummary(cfg.Deferred),
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
                StatusLine = $"{added} new file{(added == 1 ? "" : "s")} arrived — added to this session.";
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
                StatusLine = $"{snap.Scan.Count} file{(snap.Scan.Count == 1 ? "" : "s")} waiting in the inbox.";
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

    private void ApplyDeferred(Scanner.DeferredInfo info)
    {
        if (info.Count == 0) { DeferredAlert = ""; return; }
        var age = info.OldestAgeDays switch
        {
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
    }

    private async Task RefreshDeferredAsync()
    {
        var deferred = _cfg.Deferred;
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
                current.Add(new AlertItem($"inbox\0{nm}", "the inbox", nm, _cfg.Inbox));
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

    private void ShowToast(string text, string detail, string folder)
    {
        ToastText = text;
        ToastDetail = detail;
        _toastFolder = folder;
        ToastVisible = true;
        _toastTimer.Change(ToastMs, Timeout.Infinite);
    }

    internal void HideToast()
    {
        ToastVisible = false;
        _toastTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

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

    private string _statusLine = "";
    public string StatusLine { get => _statusLine; internal set => Set(ref _statusLine, value); }

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

    internal void StartProcessing() => _ = StartProcessingAsync();

    internal async Task StartProcessingAsync()
    {
        if (_busy) return;
        var cfg = _cfg;
        // the scan AND the destination probes (ProbeWritable touches every
        // route folder — a network round trip each) run off the UI thread
        var (scan, problems) = await _scheduler.Run(() =>
            (Scanner.Scan(cfg.Inbox, cfg.Sort, cfg.NamingMode),
             cfg.Routes.Select(Config.ValidateRoute).ToList()));
        if (Screen == Screen.Processing) return;   // a double Start raced us
        if (scan.Count == 0) { Rescan(); return; }
        BuildRoutes(problems);
        _session.Start(scan.Matching);
        _lastRoute = null;
        MarkRouteState();   // Enter always has a target now — mark it before the first document
        _auditFailedThisSession = false;
        Screen = Screen.Processing;
        StatusLine = "";
        HideLastAction();
        // hide the dashboard while filing
        DashboardVisible = false;
        AllQuiet = false;
        StopFlash();
        ApplyFlashAll();
        await RefreshCompleterAsync();
        await LoadCurrentAsync();
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
        StatusLine = "";   // mid-session notes don't belong under the summary
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
                    StatusLine = "That file disappeared from the inbox — logged and moved on.";
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
                    StatusLine = "That file disappeared from the inbox — logged and moved on.";
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
        if (!_session.CanUndo) { StatusLine = "Nothing to undo."; return; }
        _busy = true;
        try
        {
            try
            {
                var (filed, original) = await _scheduler.Run(() => _session.UndoLast());
                StatusLine = $"Undid {Path.GetFileName(filed)} → {Path.GetFileName(original)}";
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
        if (!Config.TrySave(_cfg, _cfgPath, out var error))
            _dialogs.Warn(error, "OrdoSort — settings not saved");
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
    internal void ApplySettings(Config cfg) => _ = ApplySettingsAsync(cfg);

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
                    "OrdoSort — settings conflict");
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
            try
            {
                var fresh = await _scheduler.Run(() =>
                {
                    HistoryBackup.BackupDaily(newDb,
                        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(newDb))!, "backups"),
                        DateTime.Now);
                    return new History(newDb);
                });
                _history.Dispose();
                _history = fresh;
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
        _cfg = cfg;
        if (!Config.TrySave(cfg, _cfgPath, out var error))
            _dialogs.Warn(error, "OrdoSort — settings not saved");
        _session = new Session(cfg, _history);
        await _scheduler.Run(() => _watch.SetFolders(cfg.Inbox, cfg.Deferred));
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
        StatusLine = "";
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
    internal static string ResolvePath(string value, string cfgPath) =>
        Path.IsPathRooted(value)
            ? value
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfgPath))!, value);

    internal static void OpenFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            { UseShellExecute = true });
    }

    public void Dispose()
    {
        _watch.Activity -= OnFolderActivity;
        _flash.Dispose();
        _lastActionTimer.Dispose();
        _toastTimer.Dispose();
        _history.Dispose();
    }
}
