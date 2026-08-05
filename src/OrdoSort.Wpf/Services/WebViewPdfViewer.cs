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

    public async Task<bool> InitAsync()
    {
        try
        {
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

            // ...nor start a download. Nothing in this app's workflow downloads.
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
        catch (Exception ex)
        {
            InitError = ex.ToString();
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
    /// releases the PDF file handle before the move — proven by the smoke test.</summary>
    public async Task ReleaseAsync()
    {
        if (!_ready) return;
        _expected = null;
        var tcs = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _view.CoreWebView2.NavigationCompleted -= Handler;
            tcs.TrySetResult();
        }
        _view.CoreWebView2.NavigationCompleted += Handler;
        _view.CoreWebView2.Navigate("about:blank");
        await Task.WhenAny(tcs.Task, Task.Delay(2000));
    }

    /// <summary>Current document URL — used by the smoke test to prove the
    /// real viewer rendered the real file.</summary>
    internal string CurrentUrl => _ready ? _view.CoreWebView2.Source ?? "" : "";
}
