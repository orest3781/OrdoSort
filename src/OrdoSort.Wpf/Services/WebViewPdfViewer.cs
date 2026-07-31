using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OrdoSort.Wpf.Services;

/// <summary>WebView2-backed viewer. Edge's built-in PDF renderer means no
/// bundled PDF library — a deliberate size-for-dependency trade.</summary>
public sealed class WebViewPdfViewer : IPdfViewer
{
    private readonly WebView2 _view;
    private bool _ready;

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
            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            InitError = ex.ToString();
            return false;
        }
    }

    public Task ShowAsync(string path)
    {
        if (_ready)
            _view.CoreWebView2.Navigate(new Uri(Path.GetFullPath(path)).AbsoluteUri);
        return Task.CompletedTask;
    }

    public void Blank()
    {
        if (_ready) _view.CoreWebView2.Navigate("about:blank");
    }

    /// <summary>Navigate to a blank page and wait for completion so Edge
    /// releases the PDF file handle before the move — verbatim contract from
    /// MainForm.ReleaseViewerAsync, proven by the smoke test.</summary>
    public async Task ReleaseAsync()
    {
        if (!_ready) return;
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
