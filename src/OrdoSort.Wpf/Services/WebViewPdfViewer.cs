using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OrdoSort.Wpf.Services;

/// <summary>WebView2-backed viewer. Edge's built-in PDF renderer means no
/// bundled PDF library — a deliberate size-for-dependency trade.</summary>
public sealed class WebViewPdfViewer : IPdfViewer
{
    private readonly WebView2 _view;
    private bool _ready;

    /// <summary>What this viewer last asked to navigate to. A hostile PDF
    /// cannot forge it: only ShowAsync/Blank/ReleaseAsync set it, immediately
    /// before calling Navigate.</summary>
    private string? _expected;

    public WebViewPdfViewer(WebView2 view) => _view = view;

    public string? InitError { get; private set; }
    public bool Ready => _ready;

    private const string InstallUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private static string MissingRuntimeMessage =>
        "Document previews need the Microsoft Edge WebView2 Runtime, which isn't installed on this " +
        "computer.\n\nInstall it from " + InstallUrl + " (the free \"Evergreen Bootstrapper\" — no " +
        "restart required) and reopen OrdoSort.\n\nEverything else keeps working without it: scanning, " +
        "renaming, filing, and history are all unaffected. Only the preview pane stays blank until " +
        "it's installed.";

    private static string UnexpectedInitFailureMessage(bool crashLogged) =>
        "Document preview couldn't start this session, even though the WebView2 Runtime is installed. " +
        "The rest of OrdoSort is unaffected — scanning, renaming, filing, and history keep working; " +
        "only the preview pane stays blank.\n\n" +
        (crashLogged
            ? "The technical details were written to crash.log, beside your config file."
            : "The technical details could not be written to crash.log — the location may not be " +
              "writable.") +
        "\n\nIf this keeps happening, try reinstalling the WebView2 Runtime from " + InstallUrl + ".";

    /// <summary>Whether the WebView2 Runtime is present, checked with the SDK's own
    /// purpose-built probe (<see cref="CoreWebView2Environment.GetAvailableBrowserVersionString"/>,
    /// documented to throw <see cref="WebView2RuntimeNotFoundException"/> — not return null —
    /// when nothing is installed) instead of waiting for <c>CreateAsync</c> to fail with a bare
    /// COM HRESULT. Internal (not private) only so a test can force "missing" and "present"
    /// without needing the runtime uninstalled on the test machine; production code never
    /// assigns to this. Same "settable only by tests" seam pattern as
    /// <see cref="OrdoSort.Wpf.App._crashDir"/> and <see cref="Theme.ThemeManager.IsHighContrast"/> —
    /// the existing WebView2 test seams in this file all drive a REAL browser on the STA fixture
    /// (by design, for the navigation-guard behaviour they prove), which is exactly what this
    /// probe must NOT depend on: whether THIS machine happens to have the runtime installed is
    /// not what "does OrdoSort report a missing runtime correctly" should hinge on.</summary>
    internal static Func<bool> RuntimeAvailable = () =>
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString(browserExecutableFolder: null);
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    };

    /// <summary>Where Edge keeps its browser profile for us. Left to itself
    /// WebView2 puts this BESIDE THE EXECUTABLE, which breaks the moment the
    /// app is run from a network share: it tries to create the profile on the
    /// share and fails, so the same build works from a local copy and errors
    /// for a colleague opening it from \\server\share. A share that IS
    /// writable does not help either — the profile has to be on local storage.
    ///
    /// The folder is named for the product rather than the assembly. It is new,
    /// so nothing depends on the old name the way config.json and the exe do.</summary>
    public static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrdoSort", "WebView2");

    /// <summary>Starts the real browser engine. Degrades, never blocks: a failure here
    /// (missing runtime or anything else) only ever sets <see cref="InitError"/> and returns
    /// false — it never throws, and callers keep running with an inert viewer (see
    /// <see cref="Ready"/>-gated no-ops below on <see cref="ShowAsync"/>/<see cref="Blank"/>/
    /// <see cref="ReleaseAsync"/>). That degrade-not-block posture is a deliberate choice, not
    /// an accident: this app's core job is filing documents — scanning, renaming, committing,
    /// history, the Label maker — none of which touches the PDF pane, so refusing to start over
    /// a missing preview engine would make the product worse, not safer. The one enforcement
    /// point for that choice lives in MainWindow.xaml.cs's Loaded handler, which calls
    /// Shell.Initialize() unconditionally after this returns, whatever it returns — that call is
    /// NOT gated on this method's result, and must stay that way.
    ///
    /// Before this, a missing runtime surfaced only as
    /// <c>System.Runtime.InteropServices.COMException (0x8007139F): Class not registered</c> (or
    /// similar), caught below and dumped verbatim into <see cref="InitError"/> via
    /// <c>ex.ToString()</c> — a raw stack trace, not a sentence a user could act on, and it never
    /// reached crash.log (the catch swallowed it, so neither DispatcherUnhandledException nor
    /// AppDomain.UnhandledException ever saw it). <see cref="RuntimeAvailable"/> now checks for
    /// that specific, common cause up front and reports it in plain language with what to
    /// install; a genuinely unexpected failure (runtime present, something else wrong) still
    /// gets a plain-language message AND now reaches crash.log via <see cref="App.LogCrash"/>,
    /// matching how every other unanticipated exception in this app is handled.</summary>
    public async Task<bool> InitAsync()
    {
        try
        {
            if (!RuntimeAvailable())
            {
                InitError = MissingRuntimeMessage;
                return false;
            }

            Directory.CreateDirectory(UserDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: UserDataFolder);
            await _view.EnsureCoreWebView2Async(env);

            var core = _view.CoreWebView2;

            // A link annotation in a hostile PDF must not be able to steer this
            // pane anywhere — the fake in-app password prompt is the attack.
            core.NavigationStarting += (_, e) =>
                e.Cancel = !IsPermittedNavigation(e.Uri, _expected);

            // ...nor open a second window to do it in.
            core.NewWindowRequested += (_, e) => e.Handled = true;

            // ...nor start a download. Nothing in this app's workflow downloads
            // a file through the pane — filing/setting aside is done through
            // the app's own toolbar/hotkeys against the ORIGINAL file on disk,
            // never through the browser. This also means the PDF toolbar's
            // Save / "Save as" now silently no-ops (final review, Finding 4a,
            // 2026-08-05): that is the intended effect of this line, not a
            // missed case — nothing downstream in this app expects a copy to
            // land in Downloads.
            core.DownloadStarting += (_, e) => e.Cancel = true;

            var s = core.Settings;
            s.AreHostObjectsAllowed = false;        // no bridge into the process
            s.IsWebMessageEnabled = false;          // ditto; nothing uses it
            s.AreDefaultScriptDialogsEnabled = false; // no alert()/prompt() as UI
            s.AreDevToolsEnabled = false;
            s.IsPasswordAutosaveEnabled = false;    // this app handles documents,
            s.IsGeneralAutofillEnabled = false;     // never web forms
            s.IsStatusBarEnabled = false;
            // DELIBERATELY LEFT ENABLED (user's decision, security-only change):
            // AreDefaultContextMenusEnabled and AreBrowserAcceleratorKeysEnabled —
            // right-click and Ctrl+P in the pane keep working as today.

            // Measured 2026-08-05: Edge's built-in PDF renderer keeps working with
            // script off — the toolbar (page nav, zoom, print) and the page content
            // both rendered correctly against the demo-full workbench (267 PDFs) with
            // this set to false, confirmed by looking at the running app, not just at
            // CurrentUrl. See task-1-report.md for the screenshots and method.
            s.IsScriptEnabled = false;

            _ready = true;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Reached deeper than the RuntimeAvailable() pre-check (e.g. the runtime was
            // removed in the gap between the check and CreateAsync) — same friendly message
            // either way; the user does not care where in the call stack this was noticed.
            InitError = MissingRuntimeMessage;
            return false;
        }
        catch (Exception ex)
        {
            var logged = App.LogCrash(ex);
            InitError = UnexpectedInitFailureMessage(logged);
            return false;
        }
    }

    /// <summary>The navigation policy, as a pure function so it can be tested
    /// without a browser. Permits about:blank (ReleaseAsync depends on it) and
    /// the exact document the viewer just asked for — nothing else. Compared
    /// case-insensitively because Windows paths are case-insensitive and Edge
    /// may normalise the URL it reports back; refusing the expected document
    /// over casing would blank the pane on a legitimate file, which is worse
    /// than the attack this prevents.</summary>
    internal static bool IsPermittedNavigation(string requested, string? expected)
    {
        if (string.Equals(requested, "about:blank", StringComparison.OrdinalIgnoreCase))
            return true;
        return expected is not null
            && string.Equals(requested, expected, StringComparison.OrdinalIgnoreCase);
    }

    public Task ShowAsync(string path)
    {
        if (_ready)
        {
            _expected = new Uri(Path.GetFullPath(path)).AbsoluteUri;
            _view.CoreWebView2.Navigate(_expected);
        }
        return Task.CompletedTask;
    }

    public void Blank()
    {
        if (_ready)
        {
            _expected = null;
            _view.CoreWebView2.Navigate("about:blank");
        }
    }

    /// <summary>Navigate to a blank page and wait for completion so Edge
    /// releases the PDF file handle before the move — proven by the smoke test.
    ///
    /// A navigation the guard just CANCELLED (e.g. a link click in the
    /// document that's about to be released) also raises NavigationCompleted,
    /// with IsSuccess=false — it is not only about:blank's own completion that
    /// can land on this handler (final review, Finding 3, 2026-08-05). If that
    /// cancelled navigation's completion arrives between subscribing below and
    /// the about:blank navigation's OWN completion, resolving on "any"
    /// NavigationCompleted lets the commit proceed while Edge still holds the
    /// file handle — the move then fails against a perfectly good document.
    /// NavigationId is what WebView2 uses to correlate a given Navigate() call
    /// with its own completion event, so it's what this uses too: capture the
    /// id of the about:blank navigation THIS call started (via NavigationStarting,
    /// the only place the id is paired with the URI) and resolve only when
    /// NavigationCompleted reports that same id.</summary>
    public async Task ReleaseAsync()
    {
        if (!_ready) return;
        _expected = null;
        var core = _view.CoreWebView2;
        var tcs = new TaskCompletionSource();
        ulong? blankNavigationId = null;

        void OnStarting(object? s, CoreWebView2NavigationStartingEventArgs e)
        {
            if (blankNavigationId is null
                && string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
                blankNavigationId = e.NavigationId;
        }
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (blankNavigationId is null || e.NavigationId != blankNavigationId) return;
            tcs.TrySetResult();
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate("about:blank");
            await Task.WhenAny(tcs.Task, Task.Delay(2000));
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>Current document URL — used by the smoke test to prove the
    /// real viewer rendered the real file.</summary>
    internal string CurrentUrl => _ready ? _view.CoreWebView2.Source ?? "" : "";
}
