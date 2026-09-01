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
    private readonly WebViewPdfViewer _pdf;
    private readonly FolderWatchService _watch;
    internal ShellViewModel Shell { get; }
    internal IDialogService Dialogs { get; set; }
    internal WebViewPdfViewer Pdf => _pdf;

    private readonly Func<System.Windows.Rect?> _panZone;

    public MainWindow(Config cfg, string cfgPath)
    {
        InitializeComponent();
        // immediate scrolls instead of animated ones — left-drag panning
        // converts to wheel messages and must track the hand directly
        Viewer.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            AdditionalBrowserArguments = "--disable-smooth-scrolling",
        };
        _pdf = new WebViewPdfViewer(Viewer);
        Dialogs = new DialogService(this);
        _watch = new FolderWatchService(pollMs: cfg.PollSeconds * 1000,
            context: SynchronizationContext.Current);
        Shell = new ShellViewModel(cfg, cfgPath, _pdf,
            new DialogRelay(() => Dialogs), _watch, SynchronizationContext.Current,
            sounds: new SoundService());
        DataContext = Shell;

        Shell.RoutesRebuilt += RebindRouteHotkeys;
        // taskbar overlay follows the alert state; a new alert flashes the
        // taskbar button when we're not the foreground window
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.HasActiveAlert)) ApplyAlertBadge();
        };
        Shell.AlertArrived += () => { if (!IsActive) TaskbarFlash.Flash(this); };
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

        // Window lifecycle: the Ready dashboard is a compact
        // window parked in the top-right corner; the window only grows to the
        // full viewer layout while a session runs. Both geometries remembered.
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Screen)) ApplyWindowMode();
        };
        Shell.FitViewerToPage += FitViewerTo;
        WindowStartupLocation = WindowStartupLocation.Manual;
        EnterCompact(initial: true);

        // a manual user resize flips SizeToContent to Manual (WPF behavior);
        // re-assert auto-fit when the tile set changes so the dashboard keeps
        // tracking its content
        Shell.Tiles.CollectionChanged += (_, _) =>
        {
            if (_compact && SizeToContent != SizeToContent.Height)
                SizeToContent = SizeToContent.Height;
        };
        InputBindings.Add(new System.Windows.Input.KeyBinding(
            new Mvvm.RelayCommand(() => OnSettings(this, new RoutedEventArgs())),
            System.Windows.Input.Key.OemComma, System.Windows.Input.ModifierKeys.Control));

        _panZone = ViewerPanZone;
        Loaded += async (_, _) =>
        {
            ViewerInputEnhancer.Register(_panZone);
            if (!await _pdf.InitAsync())
                Dialogs.Warn(
                    "The PDF viewer (WebView2) failed to start:\n\n" + _pdf.InitError,
                    "OrdoSort");
            Shell.Initialize();
        };
        // X while filing (or on the summary) returns to the dashboard instead
        // of exiting — the queue stays in the inbox, nothing is lost. The app
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
            app.SessionEnding += (_, _) => _reallyExit = true;

        Closed += (_, _) =>
        {
            ViewerInputEnhancer.Unregister(_panZone);
            _watch.Dispose();
            Shell.Dispose();
        };
    }

    private bool _reallyExit;

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

    /// <summary>Where left-drag pans and Shift+scroll zooms: the viewer's
    /// document area while a session is running, in device pixels.</summary>
    private System.Windows.Rect? ViewerPanZone()
    {
        if (_compact || !Shell.IsProcessing || !IsActive || !Viewer.IsVisible) return null;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Viewer);
        var topLeft = Viewer.PointToScreen(new System.Windows.Point(0, 0));
        var device = new System.Windows.Rect(topLeft.X, topLeft.Y,
            Viewer.ActualWidth * dpi.DpiScaleX, Viewer.ActualHeight * dpi.DpiScaleY);
        return PanMath.PanZone(device, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    // ------------------------------------------------ compact/normal modes
    private Rect? _normalBounds;
    private Rect? _compactBounds;
    private bool _compact;

    private void ApplyWindowMode()
    {
        if (Shell.IsReady) EnterCompact(initial: false);
        else EnterNormal();
    }

    private void EnterCompact(bool initial)
    {
        if (!initial)
        {
            if (_compact) return;
            _normalBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        }
        _compact = true;

        Viewer.Visibility = Visibility.Collapsed;
        Split.Visibility = Visibility.Collapsed;
        ViewerCol.MinWidth = 0;
        ViewerCol.Width = new GridLength(0);
        SplitterCol.Width = new GridLength(0);
        PanelCol.MinWidth = 0;
        PanelCol.Width = new GridLength(1, GridUnitType.Star);
        MinWidth = 400;
        MinHeight = 0;

        // auto-fit: the dashboard sizes itself to its content and grows
        // downward as monitored folders appear — no scrollbars. Capped at the
        // work area so a huge folder list can't push it off-screen (the
        // ScrollViewer only kicks in past that cap).
        var wa = SystemParameters.WorkArea;   // DIPs, primary monitor
        MaxHeight = wa.Height - 24;
        SizeToContent = SizeToContent.Height;

        if (_compactBounds is { } b)
        {
            Left = b.Left; Top = b.Top; Width = b.Width;
        }
        else
        {
            Width = 470;
            Left = wa.Right - Width - 12;
            Top = wa.Top + 12;
        }
    }

    /// <summary>Size the window so the viewer's document area matches the
    /// shape of the page about to appear in it, once, as a session starts.
    /// Only the width moves: the height is whatever the user last chose, and
    /// fitting a page means fitting it to that height.
    ///
    /// Runs after <see cref="EnterNormal"/> has already applied the session
    /// geometry — the Screen change that calls one is what eventually raises
    /// the other — so it adjusts a real, current layout rather than
    /// predicting one.</summary>
    private void FitViewerTo(double aspect)
    {
        // The compact dashboard has no viewer to fit, and a maximized window
        // cannot be resized without unmaximizing it first, which is a bigger
        // surprise than a pane that does not match the page.
        if (_compact || WindowState != WindowState.Normal) return;

        // EnterNormal assigned Width moments ago; ActualWidth only catches up
        // once a layout pass has run over that assignment, and the fit is
        // measured from the pane as it actually is.
        UpdateLayout();
        if (Viewer.ActualHeight <= 0) return;

        // The monitor this window is actually on, NOT the primary. This code
        // moves a window the user has already placed, so measuring it against
        // SystemParameters.WorkArea (always the primary's) relocated a window
        // sitting on a secondary monitor onto the primary at every session
        // start — see MonitorWorkArea for the numbers.
        var workArea = MonitorWorkArea.For(this);
        var width = FitMath.WindowWidthFor(ActualWidth, Viewer.ActualWidth, Viewer.ActualHeight,
            aspect, MinWidth, workArea.Width);
        Width = width;
        Left = FitMath.LeftFor(Left, width, workArea);
    }

    private void EnterNormal()
    {
        if (!_compact) return;
        // capture BEFORE touching MinWidth — raising it resizes the window
        // immediately and would corrupt the geometry math below
        var compact = new Rect(Left, Top, ActualWidth, ActualHeight);
        _compactBounds = compact;
        _compact = false;

        // back to explicit sizing BEFORE the bounds are set, or the Height
        // assignments below would be ignored
        SizeToContent = SizeToContent.Manual;
        MaxHeight = double.PositiveInfinity;

        Viewer.Visibility = Visibility.Visible;
        Split.Visibility = Visibility.Visible;
        ViewerCol.MinWidth = 320;
        ViewerCol.Width = new GridLength(1, GridUnitType.Star);
        SplitterCol.Width = new GridLength(5);
        PanelCol.MinWidth = 370;
        PanelCol.Width = new GridLength(430);
        MinWidth = 900;
        MinHeight = 600;

        if (_normalBounds is { } b)
        {
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        }
        else
        {
            // grow leftward from the parked corner so the big window opens on
            // the same monitor the user parked the dashboard on
            Width = 1280;
            Height = 860;
            Left = Math.Max(SystemParameters.VirtualScreenLeft, compact.Right - Width);
            Top = compact.Top;
        }
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

    private void OnLabelMaker(object sender, RoutedEventArgs e) =>
        new Windows.LabelMakerWindow(new LabelMakerViewModel(
            Shell.Cfg, Shell.BoxLabelsPath, Dialogs)) { Owner = this }.ShowDialog();

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

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (!Shell.IsReady)
        {
            Dialogs.Info("Finish or stop the current session first (Esc stops it — nothing is lost).",
                "OrdoSort");
            return;
        }
        var vm = new SettingsViewModel(Shell.FreshConfigForSettings(), Dialogs, () => Theme.ThemeManager.Current,
            Shell.CfgPath, new SoundService(), uiContext: SynchronizationContext.Current);
        var win = new Windows.SettingsWindow(vm) { Owner = this };
        var accepted = win.ShowDialog() == true;
        vm.Dispose();   // cancel any still-armed per-field/per-row probes now the dialog is closing
        if (accepted && vm.Result is { } cfg)
            Shell.ApplySettings(cfg);
    }

    private readonly List<System.Windows.Input.KeyBinding> _routeBindings = new();

    /// <summary>Config-driven route hotkeys: rebuilt with the route buttons at
    /// every session start (and after Settings changes). Digit hotkeys bind
    /// the numpad twin too — a gesture matches exact keys, and nobody thinks
    /// of Ctrl+NumPad1 as a different keystroke from Ctrl+1.</summary>
    private void RebindRouteHotkeys()
    {
        foreach (var b in _routeBindings) InputBindings.Remove(b);
        _routeBindings.Clear();

        void Bind(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods, int index)
        {
            var binding = new System.Windows.Input.KeyBinding(Shell.RouteCommand, key, mods)
            {
                CommandParameter = index,
            };
            _routeBindings.Add(binding);
            InputBindings.Add(binding);
        }

        foreach (var route in Shell.Routes)
        {
            if (route.Gesture is null || !route.Enabled) continue;
            Bind(route.Gesture.Key, route.Gesture.Modifiers, route.Index);
            if (HotkeyParser.DigitTwin(route.Gesture.Key) is not System.Windows.Input.Key.None and var twin)
                Bind(twin, route.Gesture.Modifiers, route.Index);
        }
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
    }
}
