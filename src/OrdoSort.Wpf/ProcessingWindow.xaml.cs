using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf;

/// <summary>A filing session in its own window (spec 2026-09-26). Created once
/// with the dashboard and hidden between sessions; it owns the one PDF viewer
/// the view model is given. The viewer starts at launch (<see cref="WarmUpAsync"/>).
/// Closing the window stops the session, or leaves Done, and hides it; only
/// <see cref="CloseForReal"/> (the app closing, or Windows ending the
/// session) destroys it.</summary>
public partial class ProcessingWindow : Window
{
    private readonly WebViewPdfViewer _pdf;
    private readonly Func<Rect> _dashboardWorkArea;
    private readonly Func<Task<bool>> _initViewer;
    private readonly Func<Rect?> _panZone;
    private readonly List<KeyBinding> _routeBindings = new();
    private Task<bool>? _viewerStart;
    private bool _reallyClosing;
    private ShellViewModel? _shell;

    /// <param name="dashboardWorkArea">The work area of the monitor the
    /// dashboard is on; every session opens there.</param>
    public ProcessingWindow(Func<Rect> dashboardWorkArea) : this(dashboardWorkArea, null) { }

    /// <param name="initViewer">Starts the viewer; tests pass their own so no
    /// real Edge starts.</param>
    internal ProcessingWindow(Func<Rect> dashboardWorkArea, Func<Task<bool>>? initViewer)
    {
        InitializeComponent();
        _dashboardWorkArea = dashboardWorkArea;
        Viewer.CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
        {
            AdditionalBrowserArguments = "--disable-smooth-scrolling",
        };
        _pdf = new WebViewPdfViewer(Viewer);
        _initViewer = initViewer ?? _pdf.InitAsync;
        Dialogs = new DialogService(this);
        _panZone = ViewerPanZone;
        Loaded += (_, _) => ViewerInputEnhancer.Register(_panZone);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            ViewerInputEnhancer.Unregister(_panZone);
            // Disposing WebView2 while it is still starting blocks the UI
            // thread (the app closed within a moment of launch): wait for the
            // start to settle, then dispose.
            if (_viewerStart is { IsCompleted: false } starting)
                starting.ContinueWith(_ => Viewer.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
            else
                Viewer.Dispose();
        };
    }

    /// <summary>The viewer the view model files through.</summary>
    internal WebViewPdfViewer PdfViewer => _pdf;

    /// <summary>Messages raised while this window is up appear over it.
    /// Settable so the smoke harness can swap in a recording service.</summary>
    internal IDialogService Dialogs { get; set; }

    public void Attach(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;
        shell.RoutesRebuilt += RebindRouteHotkeys;
        shell.FitViewerToPage += FitToPage;
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Screen) && shell.IsReady) Hide();
        };
    }

    /// <summary>Starts the viewer at launch: shown off-screen and without
    /// activating, just long enough for WebView2 to start, then hidden. Runs
    /// once; later calls wait for the same start. False when the viewer could
    /// not start (<see cref="WebViewPdfViewer.InitError"/> says why): the
    /// dashboard reports it, and sessions still run with an empty pane.</summary>
    public Task<bool> WarmUpAsync() => _viewerStart ??= StartViewerAsync();

    private async Task<bool> StartViewerAsync()
    {
        var (left, top, taskbar) = (Left, Top, ShowInTaskbar);
        var showing = IsVisible;
        if (!showing)
        {
            Left = -32000;
            Top = -32000;
            ShowInTaskbar = false;
            ShowActivated = false;
            Show();
        }
        var ok = await _initViewer();
        if (!showing)
        {
            Hide();
            (Left, Top, ShowInTaskbar, ShowActivated) = (left, top, taskbar, true);
        }
        return ok;
    }

    /// <summary>Opens the window for a session on the dashboard's screen and
    /// waits for the viewer's launch-time start (never a second one) before
    /// the first document loads. The page fit follows (<see cref="FitToPage"/>).</summary>
    public async Task OpenForSessionAsync()
    {
        if (!IsVisible) PlaceOnDashboardScreen();
        BringToFront();
        await WarmUpAsync();
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Destroys the window: the dashboard is closing, or Windows is
    /// ending the session. Never cancelled.</summary>
    internal void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }

    private void PlaceOnDashboardScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var area = _dashboardWorkArea();
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClosing || _shell is null) return;
        e.Cancel = true;
        // StopSession refuses mid-commit; the window then stays, showing it.
        // Otherwise the Screen change to Ready hides it (Attach).
        if (_shell.IsProcessing) _shell.StopSession();
        else if (_shell.IsDone) _shell.RescanCommand.Execute(null);
    }

    /// <summary>Fits the whole first page (spec 2026-09-26), once per session,
    /// on the dashboard's screen. A maximized window is left alone.</summary>
    internal void FitToPage(double aspect)
    {
        if (WindowState != WindowState.Normal) return;
        UpdateLayout();
        if (Viewer.ActualHeight <= 0) return;
        if (FitMath.SessionBounds(_dashboardWorkArea(), ActualWidth - Viewer.ActualWidth,
                ActualHeight - Viewer.ActualHeight, aspect, MinWidth, MinHeight) is { } r)
        {
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
            // measured now, so the first page is shown at the zoom for this
            // size rather than re-opened once layout catches up
            UpdateLayout();
        }
    }

    /// <summary>Where left-drag pans and Shift+scroll zooms: the viewer's
    /// document area while a session is running, in device pixels.</summary>
    private Rect? ViewerPanZone()
    {
        if (_shell is null || !_shell.IsProcessing || !IsActive || !Viewer.IsVisible) return null;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Viewer);
        var topLeft = Viewer.PointToScreen(new Point(0, 0));
        var device = new Rect(topLeft.X, topLeft.Y,
            Viewer.ActualWidth * dpi.DpiScaleX, Viewer.ActualHeight * dpi.DpiScaleY);
        return PanMath.PanZone(device, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>Config-driven route hotkeys: rebuilt with the route buttons at
    /// every session start (and after Settings changes). Digit hotkeys bind
    /// the numpad twin too — a gesture matches exact keys, and nobody thinks
    /// of Ctrl+NumPad1 as a different keystroke from Ctrl+1.</summary>
    private void RebindRouteHotkeys()
    {
        if (_shell is null) return;
        foreach (var b in _routeBindings) InputBindings.Remove(b);
        _routeBindings.Clear();

        void Bind(Key key, ModifierKeys mods, int index)
        {
            var binding = new KeyBinding(_shell.RouteCommand, key, mods) { CommandParameter = index };
            _routeBindings.Add(binding);
            InputBindings.Add(binding);
        }

        foreach (var route in _shell.Routes)
        {
            if (route.Gesture is null || !route.Enabled) continue;
            Bind(route.Gesture.Key, route.Gesture.Modifiers, route.Index);
            if (HotkeyParser.DigitTwin(route.Gesture.Key) is not Key.None and var twin)
                Bind(twin, route.Gesture.Modifiers, route.Index);
        }
    }
}
