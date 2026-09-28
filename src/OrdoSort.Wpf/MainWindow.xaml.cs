using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf;

public partial class MainWindow : Window
{
    // taskbar overlay: a red dot with a thin light ring so it reads on any
    // taskbar color. Drawn once, frozen.
    private static readonly ImageSource AlertBadge = MakeBadge();

    private static ImageSource MakeBadge()
    {
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(Brushes.White, null,
            new EllipseGeometry(new Point(16, 16), 16, 16)));
        dg.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)), null,
            new EllipseGeometry(new Point(16, 16), 13, 13)));
        var img = new DrawingImage(dg);
        img.Freeze();
        return img;
    }

    private void ApplyAlertBadge()
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = Shell.HasActiveAlert ? AlertBadge : null;
    }
    private readonly FolderWatchService _watch;
    internal ShellViewModel Shell { get; }
    internal IDialogService Dialogs { get; set; }

    /// <summary>The session's own window (spec 2026-09-26): the viewer, the
    /// Processing screen and Done. This window is the dashboard.</summary>
    internal ProcessingWindow Processing { get; }

    /// <summary>The PDF viewer, which lives in <see cref="Processing"/>.</summary>
    internal WebViewPdfViewer Pdf => Processing.PdfViewer;

    public MainWindow(Config cfg, string cfgPath) : this(cfg, cfgPath, null, null) { }

    /// <param name="initViewer">Starts the session window's viewer; tests
    /// pass their own so no real Edge starts.</param>
    /// <param name="sessionWorkArea">Where sessions open; tests pass an
    /// off-screen area. Defaults to this window's monitor.</param>
    internal MainWindow(Config cfg, string cfgPath, Func<Task<bool>>? initViewer, Func<Rect>? sessionWorkArea = null)
    {
        InitializeComponent();
        // A session opens in its own window (spec 2026-09-26), which owns the
        // viewer; this window is the dashboard and never reshapes.
        Processing = new ProcessingWindow(sessionWorkArea ?? (() => MonitorWorkArea.For(this)), initViewer);
        Dialogs = new DialogService(this);
        _watch = new FolderWatchService(pollMs: cfg.PollSeconds * 1000,
            context: SynchronizationContext.Current);
        // messages raised during a session appear over the session's window
        Shell = new ShellViewModel(cfg, cfgPath, Processing.PdfViewer,
            new DialogRelay(() => Processing.IsVisible ? Processing.Dialogs : Dialogs), _watch,
            SynchronizationContext.Current, sounds: new SoundService());
        DataContext = Shell;
        Processing.Attach(Shell);
        Shell.PrepareSessionView = Processing.OpenForSessionAsync;
        Shell.ShowSessionRequested += Processing.BringToFront;

        // taskbar overlay follows the alert state; a new alert flashes the
        // taskbar button only while neither window is in front
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.HasActiveAlert)) ApplyAlertBadge();
        };
        ShouldFlash = () => FlashFor(IsActive, Processing.IsActive);
        Shell.AlertArrived += () => { if (ShouldFlash()) TaskbarFlash.Flash(this); };
        // a filing-loop exception the view model didn't expect still reaches
        // crash.log — the user has already been warned by then. The lambda
        // (rather than a method-group `+= App.LogCrash`) is needed because
        // LogCrash now returns bool (whether the write succeeded) while this
        // event is Action<Exception>; the return value isn't needed here.
        Shell.UnexpectedError += ex => App.LogCrash(ex);
        Shell.SettingsApplied += () =>
        {
            App.ApplyFont(Application.Current, Shell.Cfg);
            Theme.ThemeManager.SetMode(Application.Current, Shell.Cfg.Theme);
        };

        WindowStartupLocation = WindowStartupLocation.Manual;
        ParkDashboard();

        // a manual user resize flips SizeToContent to Manual (WPF behavior);
        // re-assert auto-fit when the tile set changes so the dashboard keeps
        // tracking its content
        Shell.Tiles.CollectionChanged += (_, _) =>
        {
            if (SizeToContent != SizeToContent.Height) SizeToContent = SizeToContent.Height;
        };
        InputBindings.Add(new System.Windows.Input.KeyBinding(
            new Mvvm.RelayCommand(() => OnSettings(this, new RoutedEventArgs())),
            System.Windows.Input.Key.OemComma, System.Windows.Input.ModifierKeys.Control));

        Loaded += async (_, _) =>
        {
            // the session window's viewer starts now, as it always has at
            // launch, so a first Start is instant and a broken viewer is
            // reported here rather than mid-session
            var started = await Processing.WarmUpAsync();
            // closed while the viewer was starting (a quick exit at launch, or
            // a slow first Edge start): the shell is disposed, so there is
            // nothing to warn about or start
            if (_closed) return;
            if (!started)
                Dialogs.Warn(
                    "The PDF viewer (WebView2) failed to start:\n\n" + Processing.PdfViewer.InitError,
                    "OrdoSort");
            Shell.Initialize();
        };
        // X mid-session (or on the summary) ends the session instead of
        // exiting — the queue stays in the inbox, nothing is lost. The app
        // really closes from the dashboard, File > Exit, or OS shutdown.
        Closing += (_, e) =>
        {
            if (_reallyExit)
            {
                // The file moves on a thread-pool thread and the audit row is
                // written after it. Closing here disposes the shell — and the
                // History connection — out from under that, so the document
                // moves and the log entry is lost. Let the in-flight commit
                // land first; it is bounded by a single file move.
                //
                // _forceClosing is the escape hatch for FinishClosingWhenIdle's
                // OWN re-entrant Close() call: without it, a commit that is
                // still busy at the timeout would hit this same branch again,
                // cancel again, and re-arm another FinishClosingWhenIdle —
                // forever. A hung move must end in a closed app, not an
                // unclosable one; that would be worse than the bug this fixes.
                if (Shell.IsBusy && !_forceClosing)
                {
                    e.Cancel = true;
                    _ = FinishClosingWhenIdle();
                }
                return;
            }
            if (Shell.IsProcessing)
            {
                e.Cancel = true;
                Shell.StopSession();   // respects the mid-commit busy guard
            }
            else if (Shell.IsDone)
            {
                e.Cancel = true;
                Shell.RescanCommand.Execute(null);
            }
        };
        if (Application.Current is { } app)
            app.SessionEnding += (_, _) => OnWindowsSessionEnding();

        Closed += (_, _) =>
        {
            _closed = true;
            Processing.CloseForReal();
            _watch.Dispose();
            Shell.Dispose();
            // The app ends when this window closes, and that would cut off a
            // document still being moved over the network, and its history
            // row (QC-19). The window is already gone; the process stays until
            // that one filing lands, with a backstop so a dead share can't
            // keep it forever.
            if (!Shell.FilingInFlight.Wait(FilingExitWait))
                App.LogCrash(new TimeoutException(
                    $"OrdoSort closed while a document was still being filed, after waiting {FilingExitWait.TotalSeconds:0} s; "
                    + "check History and the destination folder for the last document."));
        };
    }

    private bool _reallyExit;
    private bool _closed;

    /// <summary>How long the closed app waits for a filing still in flight
    /// before exiting anyway. A seam for tests.</summary>
    internal TimeSpan FilingExitWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Windows is shutting down or signing out: nothing here may
    /// cancel that, so the session window closes for real too.</summary>
    internal void OnWindowsSessionEnding()
    {
        _reallyExit = true;
        Processing.CloseForReal();
    }

    /// <summary>Whether an alert flashes the taskbar now: only while neither
    /// window is in front. A seam for tests.</summary>
    internal Func<bool> ShouldFlash { get; set; }

    /// <summary>The rule behind <see cref="ShouldFlash"/>: flash only when the
    /// person is in neither window.</summary>
    internal static bool FlashFor(bool dashboardActive, bool sessionActive) =>
        !dashboardActive && !sessionActive;

    /// <summary>Set immediately before FinishClosingWhenIdle's own re-entrant
    /// Close() call, so THAT re-entry into the Closing handler above cannot
    /// be cancelled again — whether the wait actually went idle or hit
    /// <see cref="CloseIdleTimeout"/>. Never set anywhere else.</summary>
    private bool _forceClosing;

    /// <summary>How long FinishClosingWhenIdle waits for a mid-flight commit
    /// before forcing the close through anyway. Internal (not private) only
    /// so the timeout-path test can shrink it well below 10 seconds —
    /// production code never assigns to this. Same "settable only by tests"
    /// seam pattern as <see cref="Theme.ThemeManager.IsHighContrast"/>.</summary>
    internal TimeSpan CloseIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private void OnExit(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Close();
    }

    /// <summary>Re-issue the close once the in-flight commit has landed. The
    /// timeout is a backstop: OS shutdown will not wait forever, and a hung
    /// move must not make the app unclosable — the far worse failure. Sets
    /// _forceClosing right before its own Close() call (same synchronous
    /// call, no await between them) so that re-entry into Closing always
    /// lands — a commit that outlives the timeout gets its close forced
    /// through rather than restarting the wait forever.</summary>
    private async Task FinishClosingWhenIdle()
    {
        await Shell.WaitForIdleAsync(CloseIdleTimeout);
        _reallyExit = true;
        _forceClosing = true;
        Close();
    }

    // ------------------------------------------------------------ the dashboard
    /// <summary>Parks the dashboard in the top-right corner of the work area,
    /// sized to its content and growing downward as monitored folders appear
    /// (no scrollbars), capped at the work area so a huge folder list can't
    /// push it off-screen. It never reshapes after this: a session opens in
    /// its own window (spec 2026-09-26).</summary>
    private void ParkDashboard()
    {
        var wa = SystemParameters.WorkArea;   // DIPs, primary monitor
        MinWidth = 400;
        MinHeight = 0;
        MaxHeight = wa.Height - 24;
        SizeToContent = SizeToContent.Height;
        Width = 470;
        Left = wa.Right - Width - 12;
        Top = wa.Top + 12;
    }

    private void OnViewHistory(object sender, RoutedEventArgs e)
    {
        if (Shell.HistorySwapping)
        {
            Dialogs.Info("One moment — the history database is being switched over.", "OrdoSort");
            return;
        }
        new Windows.HistoryWindow(new ViewModels.HistoryViewModel(Shell.History, Dialogs))
        { Owner = this }.ShowDialog();
    }

    private void OnUnlock(object sender, RoutedEventArgs e) =>
        new Windows.UnlockWindow(new UnlockViewModel(Shell.Cfg, Shell.SaveSavedPasswordsNow, dialogs: Dialogs))
        { Owner = this }.ShowDialog();

    private void OnBulkRename(object sender, RoutedEventArgs e)
    {
        var vm = new BulkRenameViewModel(uiContext: SynchronizationContext.Current);
        new Windows.BulkRenameWindow(vm) { Owner = this }.ShowDialog();
        vm.Dispose();   // cancel any still-armed preview probe now the dialog is closing
    }

    private void OnStandardiseNames(object sender, RoutedEventArgs e) =>
        new Windows.StandardiseNamesWindow(new StandardiseNamesViewModel(Dialogs))
        { Owner = this }.ShowDialog();

    private void OnMatchMerge(object sender, RoutedEventArgs e) =>
        new Windows.MatchMergeWindow(new MatchMergeViewModel(
            Shell.Cfg, Shell.SaveMergeHeaders, Dialogs, Shell.SaveConfigNow))
        { Owner = this }.ShowDialog();

    // The three names are OrdoSort's, passed in rather than baked into the
    // shared window: BoxLabels.exe opens the same one and must not show the
    // name of a product its user does not have. Shell.Cfg.LabelClients is the
    // pre-split config's inline roster, migrated on first open — the
    // standalone has no config.json and passes none.
    private void OnLabelMaker(object sender, RoutedEventArgs e)
    {
        var vm = new LabelMakerViewModel(Shell.Cfg.LabelClients, Shell.BoxLabelsPath, Dialogs,
            "OrdoSort — label maker");
        vm.UnexpectedError += ex => App.LogCrash(ex);
        new Windows.LabelMakerWindow(vm, "OrdoSort — Box labels", "OrdoSort — Print preview")
        { Owner = this }.ShowDialog();
    }

    private void OnFilenameList(object sender, RoutedEventArgs e)
    {
        var vm = new FilenameListViewModel(Dialogs, uiContext: SynchronizationContext.Current);
        new Windows.FilenameListWindow(vm) { Owner = this }.ShowDialog();
        vm.Dispose();   // cancel any still-armed rebuild probe now the dialog is closing
    }

    private void OnPageCounts(object sender, RoutedEventArgs e) =>
        new Windows.PageCountsWindow(new PageCountsViewModel(Dialogs, uiContext: SynchronizationContext.Current))
        { Owner = this }.ShowDialog();

    private void OnListReformat(object sender, RoutedEventArgs e) =>
        new Windows.ListReformatWindow(new ListReformatViewModel()) { Owner = this }.ShowDialog();

    private void OnZipTools(object sender, RoutedEventArgs e) =>
        new Windows.ZipToolsWindow(new ZipExtractViewModel(Dialogs, SavedPasswordsNow(),
            uiContext: SynchronizationContext.Current))
        { Owner = this }.ShowDialog();

    private void OnMergePdfs(object sender, RoutedEventArgs e) =>
        new Windows.MergePdfsWindow(new MergePdfsViewModel(Dialogs, SavedPasswordsNow(),
            uiContext: SynchronizationContext.Current, config: Shell.Cfg, saveConfig: Shell.SaveConfigNow))
        { Owner = this }.ShowDialog();

    /// <summary>The Unlock tool's saved passwords, revealed, for the two
    /// windows that try them silently before asking anyone — read once as
    /// each window opens, through the same PasswordVault path Unlock uses.
    /// A legacy DPAPI entry this machine cannot decrypt reveals as "" and is
    /// skipped, not reported: Unlock already owns that conversation.</summary>
    private IReadOnlyList<string> SavedPasswordsNow() =>
        Shell.Cfg.SavedPasswords
            .Select(saved => PasswordVault.Reveal(saved.Password))
            .Where(password => password.Length > 0)
            .ToList();

    private bool _openingSettings;

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        if (!Shell.IsReady)
        {
            Dialogs.Info("Finish or stop the current session first (Esc stops it — nothing is lost).",
                "OrdoSort");
            return;
        }
        // The config is read off the UI thread (Q2-20); a second click while
        // it loads must not open a second Settings window.
        if (_openingSettings) return;
        _openingSettings = true;
        Config fresh;
        try { fresh = await Shell.FreshConfigForSettingsAsync(); }
        finally { _openingSettings = false; }
        if (!IsLoaded || !Shell.IsReady) return;   // closed, or a session started, while it loaded
        var vm = new SettingsViewModel(fresh, Dialogs, () => Theme.ThemeManager.Current,
            Shell.CfgPath, new SoundService(), uiContext: SynchronizationContext.Current);
        var win = new Windows.SettingsWindow(vm) { Owner = this };
        var accepted = win.ShowDialog() == true;
        vm.Dispose();   // cancel any still-armed per-field/per-row probes now the dialog is closing
        if (accepted && vm.Result is { } cfg)
            Shell.ApplySettings(cfg);
    }

    /// <summary>Lets the smoke harness swap in a recording dialog service
    /// after construction without the view model holding a stale reference.</summary>
    private sealed class DialogRelay : IDialogService
    {
        private readonly Func<IDialogService> _get;
        public DialogRelay(Func<IDialogService> get) => _get = get;
        public void Warn(string m, string t) => _get().Warn(m, t);
        public void Info(string m, string t) => _get().Info(m, t);
        public bool Confirm(string m, string t) => _get().Confirm(m, t);
        // Forwarded explicitly: without this override the relay would inherit
        // the interface's default, which drops the labels and calls the 2-arg
        // Confirm — so every verb-labelled question routed through the relay
        // would silently come back as Yes/No.
        public bool Confirm(string m, string t, string yes, string no) =>
            _get().Confirm(m, t, yes, no);
        public string? AskSaveFile(string f, string s) => _get().AskSaveFile(f, s);
        public string? AskOpenFile(string f) => _get().AskOpenFile(f);
        public string? AskFilePath(string f, string s) => _get().AskFilePath(f, s);
        public string? BrowseFolder(string? s) => _get().BrowseFolder(s);
        // The same hazard as Confirm, for every member with a default body:
        // unforwarded, AskOpenFiles came back single-select and AskPassword
        // and AskDate never asked at all (DW-24).
        public string? AskOpenFile(string f, string? dir) => _get().AskOpenFile(f, dir);
        public string[] AskOpenFiles(string f) => _get().AskOpenFiles(f);
        public string? AskPassword(PasswordRequest r) => _get().AskPassword(r);
        public string? AskDate(string d, int n) => _get().AskDate(d, n);
    }
}
