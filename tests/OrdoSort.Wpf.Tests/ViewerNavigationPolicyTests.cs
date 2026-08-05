using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>The PDF pane renders this app's defining untrusted input — a
/// document that arrived from a scanner or a shared folder. Before this, the
/// viewer called EnsureCoreWebView2Async and nothing else: a link annotation
/// in a hostile PDF could navigate the pane to any http(s) or file: URL, run
/// script there, and put up a convincing fake "enter the PDF password" prompt
/// inside the app's own frame (2026-08-04 audit, finding 4.1).
///
/// The policy is deliberately NOT a scheme allowlist. The viewer only ever
/// goes two places — the file:// URL of the document being triaged, and
/// about:blank — and it knows which one it just asked for. So the rule is the
/// strictest one available: permit only what this viewer itself initiated.
/// A hostile PDF cannot forge that, because it cannot make the viewer set its
/// own expectation.</summary>
public class ViewerNavigationPolicyTests
{
    private const string Doc = "file:///C:/inbox/20240115--111111.pdf";

    [Fact]
    public void TheDocumentTheViewerAskedForIsPermitted() =>
        Assert.True(WebViewPdfViewer.IsPermittedNavigation(Doc, Doc));

    [Fact]
    public void BlankIsAlwaysPermittedBecauseReleaseDependsOnIt() =>
        Assert.True(WebViewPdfViewer.IsPermittedNavigation("about:blank", null));

    /// <summary>The attack this whole task exists to stop.</summary>
    [Theory]
    [InlineData("https://evil.example/login")]
    [InlineData("http://evil.example/login")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("file://server/share/other.pdf")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>Enter your password</h1>")]
    [InlineData("about:config")]
    public void AnythingTheViewerDidNotAskForIsRefused(string requested) =>
        Assert.False(WebViewPdfViewer.IsPermittedNavigation(requested, Doc));

    /// <summary>A different local PDF is still refused — "it's only a file
    /// URL" is not the rule; "it's the one we asked for" is.</summary>
    [Fact]
    public void ADifferentLocalPdfIsRefused() =>
        Assert.False(WebViewPdfViewer.IsPermittedNavigation(
            "file:///C:/inbox/20240115--222222.pdf", Doc));

    /// <summary>Nothing is expected yet (before the first ShowAsync), so only
    /// about:blank may pass.</summary>
    [Fact]
    public void WithNothingExpectedOnlyBlankPasses()
    {
        Assert.False(WebViewPdfViewer.IsPermittedNavigation(Doc, null));
        Assert.True(WebViewPdfViewer.IsPermittedNavigation("about:blank", null));
    }

    /// <summary>Windows paths are case-insensitive and Edge may normalise the
    /// URL it reports; the comparison must not refuse the very document the
    /// viewer just asked for over casing. A false refusal here blanks the
    /// pane on a legitimate document — the worst outcome this change can
    /// produce, and worse than the attack it prevents.</summary>
    [Fact]
    public void CasingDifferencesDoNotRefuseTheExpectedDocument() =>
        Assert.True(WebViewPdfViewer.IsPermittedNavigation(
            "file:///C:/Inbox/20240115--111111.PDF",
            "file:///c:/inbox/20240115--111111.pdf"));
}

/// <summary>Step 7's other half, rewritten for the final-review Finding 1 fix
/// (2026-08-05). The ORIGINAL version of this class drove a real WebView2 but
/// only checked, via reflection into the WebView2 SDK's private backing
/// field, that <c>InitAsync</c> had subscribed SOME handler declared on
/// <see cref="WebViewPdfViewer"/> to <c>CoreWebView2.NavigationStarting</c> —
/// never what that handler actually DOES. Proven empirically at the time
/// (see the final-review notes): deleting the leading <c>!</c> from
/// <c>e.Cancel = !IsPermittedNavigation(e.Uri, _expected)</c> — inverting the
/// guard so it refuses the document the viewer itself asked for and admits
/// everything else — left EVERY test in this file green, including that
/// reflection-based one, because reflection only sees that a delegate is
/// attached, not what it decides.
///
/// This class drives the real viewer end-to-end instead, so a flipped
/// polarity has somewhere to fail: <c>InitAsync</c>, then <c>ShowAsync</c> a
/// legitimate local document and confirm the real <c>CoreWebView2</c>
/// actually navigated there (the correct-polarity direction: the expected
/// document is admitted), then <c>Navigate</c> the SAME live
/// <c>CoreWebView2</c> directly — bypassing <c>ShowAsync</c>, exactly as a
/// hostile PDF's link annotation would — to a different, still-local
/// <c>file://</c> URL that was never asked for, and confirm the pane's
/// <c>Source</c> did NOT change (the other direction: an unexpected document
/// is refused). Either direction of an inverted polarity fails one of those
/// two assertions; see task-1-report.md for the pasted revert-proof output.
///
/// It still stops short of a full hostile-link-click simulation against
/// rendered PDF content (mouse coordinates into Edge's own PDF viewer chrome
/// aren't something this suite can address deterministically) — that
/// end-to-end path is tools/OrdoSort.Smoke's job, not a unit test's. What
/// this DOES cover is the actual decision the guard makes when fed a real,
/// unrequested local navigation, through the real API, not a proxy for it.</summary>
[Collection(HighlightContrastTests.Name)]
public class WebViewPdfViewerGuardBehaviourTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir;

    public WebViewPdfViewerGuardBehaviourTests(HighlightContrastFixture fx)
    {
        _fx = fx;
        _dir = Path.Combine(Path.GetTempPath(), "ordo_guardtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Pumps a nested Dispatcher frame on the fixture's STA thread
    /// until <paramref name="task"/> completes — the same technique
    /// TriageWindowInitRaceTests' PumpUntilComplete and
    /// CopyAndTerminologyTests' PumpUntil use, and for the same reason:
    /// InitAsync's/navigation's awaits post their continuations back to THIS
    /// thread's DispatcherSynchronizationContext, so a plain blocking wait
    /// here (<c>.GetAwaiter().GetResult()</c>) would never let them run —
    /// PushFrame keeps the message loop alive while "blocked". Bounded (unlike
    /// the original InitAsync-only version) because a navigation that never
    /// completes — e.g. a guard bug that swallows the event entirely — must
    /// fail this test, not hang the whole suite.</summary>
    private static void PumpUntilComplete(Task task, int timeoutMs = 15000)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        var timedOut = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
        timer.Tick += (_, _) => { timedOut = true; timer.Stop(); frame.Continue = false; };
        timer.Start();
        task.ContinueWith(_ => { timer.Stop(); frame.Continue = false; },
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        if (timedOut && !task.IsCompleted)
            throw new TimeoutException($"operation did not complete within {timeoutMs}ms");
    }

    private static (WebView2 view, Window window) NewView()
    {
        var window = new Window
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 200, Height = 200,
        };
        var view = new WebView2();
        window.Content = view;
        window.Show();
        return (view, window);
    }

    /// <summary>Runs <paramref name="navigate"/> and waits for the NEXT
    /// <c>NavigationCompleted</c> on <paramref name="view"/>'s CoreWebView2,
    /// whatever the outcome (success OR a guard cancellation) — the caller
    /// decides what a given result means.</summary>
    private static CoreWebView2NavigationCompletedEventArgs NavigateAndWait(WebView2 view, Action navigate)
    {
        var tcs = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult(e);
        view.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            navigate();
            PumpUntilComplete(tcs.Task);
            return tcs.Task.Result;
        }
        finally
        {
            view.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    private static bool InitReady(WebViewPdfViewer viewer)
    {
        var initTask = viewer.InitAsync();
        PumpUntilComplete(initTask);
#pragma warning disable xUnit1031
        return initTask.IsCompletedSuccessfully && initTask.Result;
#pragma warning restore xUnit1031
    }

    /// <summary>A minimal one-page PDF Edge renders fine — the same technique
    /// tools/OrdoSort.Smoke/MinimalPdf.cs uses. Duplicated here rather than
    /// referenced: Wpf.Tests has no project reference to the Smoke console
    /// app (and taking one just for this would pull in PdfSharp and a whole
    /// console entry point for four lines of PDF bytes) — MinimalPdf.cs's own
    /// doc comment already notes it is carried verbatim in more than one file
    /// for exactly this reason; this is the third copy.</summary>
    private string WritePdf(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        var stream = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }
        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        Obj("2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n");
        Obj("3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n");
        Obj($"4 0 obj<</Length {stream.Length}>>stream\n{stream}\nendstream endobj\n");
        Obj("5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n");
        var xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer<</Size 6/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void GuardAdmitsTheExpectedDocumentAndRefusesEverythingElse() => _fx.Invoke(() =>
    {
        var (view, window) = NewView();
        try
        {
            var viewer = new WebViewPdfViewer(view);
            Assert.True(InitReady(viewer), "real WebView2 init failed: " + viewer.InitError);

            var legit = WritePdf("legit.pdf", "LEGIT DOCUMENT");
            var other = WritePdf("other.pdf", "A DIFFERENT DOCUMENT — NEVER REQUESTED");
            var legitUri = new Uri(Path.GetFullPath(legit)).AbsoluteUri;
            var otherUri = new Uri(Path.GetFullPath(other)).AbsoluteUri;

            // (1) the document the viewer itself asked for is admitted. If the
            // polarity were inverted, THIS is the navigation that would get
            // cancelled, and Source would never become the legit document —
            // the pane would just go blank on every document, always.
            // ShowAsync's Task is already Task.CompletedTask by the time
            // Navigate() is called (see its own source) — GetResult() here
            // never blocks on anything still running, same as this file's
            // pre-existing InitAsync .Result read below.
#pragma warning disable xUnit1031
            var first = NavigateAndWait(view, () => viewer.ShowAsync(legit).GetAwaiter().GetResult());
#pragma warning restore xUnit1031
            Assert.True(first.IsSuccess, "the legitimate document failed to navigate: " + first.WebErrorStatus);
            Assert.Equal(legitUri, viewer.CurrentUrl, ignoreCase: true);

            // (2) a DIFFERENT local file, navigated to DIRECTLY on the live
            // CoreWebView2 (bypassing ShowAsync — exactly what a link
            // annotation inside the currently-open PDF would do) must be
            // refused. A guard-cancelled navigation still raises
            // NavigationCompleted (IsSuccess=false) — see ReleaseAsync's own
            // Finding-3 fix and test for the empirical basis of that.
            var second = NavigateAndWait(view, () => view.CoreWebView2.Navigate(otherUri));
            Assert.False(second.IsSuccess,
                "a navigation to a document this viewer never asked for was NOT refused");

            // The converse of (2): the legitimate document is STILL what the
            // pane shows. If the polarity were inverted, step (2) would have
            // succeeded (an unrequested document is "permitted" under the
            // flipped check) and this would now read the OTHER document's URI.
            Assert.Equal(legitUri, viewer.CurrentUrl, ignoreCase: true);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Final review Finding 2 (2026-08-05): the comparison in
    /// <see cref="WebViewPdfViewer.IsPermittedNavigation"/> is between .NET's
    /// <c>Uri.AbsoluteUri</c> (what <see cref="WebViewPdfViewer.ShowAsync"/>
    /// sets as <c>_expected</c>) and Chromium's OWN canonicalized URL (what
    /// <c>NavigationStarting</c> reports back as <c>e.Uri</c>) — every demo
    /// and smoke corpus filename up to this point was bare ASCII, so the two
    /// canonicalizers agreeing was never actually exercised. If they disagree
    /// on a real filename, this document gets FALSELY REFUSED and the pane
    /// goes blank — the exact failure <see cref="WebViewPdfViewer"/>'s own doc
    /// comment on <c>IsPermittedNavigation</c> calls worse than the attack the
    /// guard prevents. This drives the real comparison — both canonicalizers,
    /// for real — against a filename with the awkward properties a realistic
    /// scanned-document name can have: spaces, an en dash, and a non-ASCII
    /// numero sign.</summary>
    [Fact]
    public void AnAwkwardRealisticFilenameIsNotFalselyRefused() => _fx.Invoke(() =>
    {
        var (view, window) = NewView();
        try
        {
            var viewer = new WebViewPdfViewer(view);
            Assert.True(InitReady(viewer), "real WebView2 init failed: " + viewer.InitError);

            var awkward = WritePdf("Fall 2024 – №7 (draft).pdf", "AWKWARD FILENAME DOCUMENT");
#pragma warning disable xUnit1031
            var nav = NavigateAndWait(view, () => viewer.ShowAsync(awkward).GetAwaiter().GetResult());
#pragma warning restore xUnit1031

            Assert.True(nav.IsSuccess,
                "an awkward-but-legitimate filename failed to navigate: " + nav.WebErrorStatus);
            Assert.False(
                string.IsNullOrEmpty(viewer.CurrentUrl)
                    || string.Equals(viewer.CurrentUrl, "about:blank", StringComparison.OrdinalIgnoreCase),
                "the pane went blank instead of showing the awkward-named document — " +
                ".NET's and Chromium's URL canonicalizers disagree on this filename");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Final review Finding 3 (2026-08-05): reproduces the
    /// interleaving <see cref="WebViewPdfViewer.ReleaseAsync"/>'s own doc
    /// comment now describes — a navigation the guard is about to cancel,
    /// fired without waiting for it, immediately followed by a real
    /// <c>ReleaseAsync</c> call — and asserts the contract callers actually
    /// depend on (<see cref="ShellViewModel"/> moves the file the instant this
    /// returns): by the time <c>ReleaseAsync</c> completes, the pane is
    /// GENUINELY on <c>about:blank</c>, not merely "some navigation, cancelled
    /// or not, completed." Before the fix this was a real (if timing-
    /// dependent) race — resolving on ANY <c>NavigationCompleted</c> let the
    /// cancelled navigation's own completion satisfy the wait while Edge still
    /// held the file open.</summary>
    [Fact]
    public void ReleaseAsyncOnlyResolvesOnItsOwnBlankNavigation() => _fx.Invoke(() =>
    {
        var (view, window) = NewView();
        try
        {
            var viewer = new WebViewPdfViewer(view);
            Assert.True(InitReady(viewer), "real WebView2 init failed: " + viewer.InitError);

            var legit = WritePdf("legit-release.pdf", "LEGIT RELEASE DOCUMENT");
            var other = WritePdf("other-release.pdf", "A DIFFERENT DOCUMENT — NEVER REQUESTED");
            var otherUri = new Uri(Path.GetFullPath(other)).AbsoluteUri;

#pragma warning disable xUnit1031
            var shown = NavigateAndWait(view, () => viewer.ShowAsync(legit).GetAwaiter().GetResult());
#pragma warning restore xUnit1031
            Assert.True(shown.IsSuccess, "setup: legit document failed to navigate: " + shown.WebErrorStatus);

            // Fire-and-forget a navigation the guard is about to cancel, then
            // IMMEDIATELY race ReleaseAsync against it — the same interleaving
            // a link click during a release would produce.
            view.CoreWebView2.Navigate(otherUri);
            var release = viewer.ReleaseAsync();
            PumpUntilComplete(release);

            Assert.Equal("about:blank", viewer.CurrentUrl, ignoreCase: true);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Extends the same real-behaviour coverage to
    /// <c>DownloadStarting</c> (final review Finding 1's stretch goal). Unlike
    /// the navigation guard, this needs no hostile-navigation setup at all: a
    /// file type Edge's PDF viewer can't render inline triggers a download
    /// attempt even though the navigation ITSELF is fully permitted — it's the
    /// exact document <see cref="WebViewPdfViewer.ShowAsync"/> asked for —
    /// proving <c>DownloadStarting</c> is a genuinely separate guard from
    /// <c>NavigationStarting</c>, not a restatement of it.</summary>
    [Fact]
    public void DownloadStartingCancelsADownloadOfAPermittedDocument() => _fx.Invoke(() =>
    {
        var (view, window) = NewView();
        try
        {
            var viewer = new WebViewPdfViewer(view);
            Assert.True(InitReady(viewer), "real WebView2 init failed: " + viewer.InitError);

            var notAPdf = Path.Combine(_dir, "not-a-pdf.bin");
            File.WriteAllBytes(notAPdf, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });

            var tcs = new TaskCompletionSource<CoreWebView2DownloadStartingEventArgs>();
            void Handler(object? s, CoreWebView2DownloadStartingEventArgs e) => tcs.TrySetResult(e);
            view.CoreWebView2.DownloadStarting += Handler;
#pragma warning disable xUnit1031
            try
            {
                viewer.ShowAsync(notAPdf).GetAwaiter().GetResult();
                PumpUntilComplete(tcs.Task);
            }
            finally
            {
                view.CoreWebView2.DownloadStarting -= Handler;
            }

            Assert.True(tcs.Task.Result.Cancel,
                "DownloadStarting fired for a permitted document's download but was not cancelled");
#pragma warning restore xUnit1031
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>EXPERIMENTAL — see final report before trusting this. Extends
    /// the same coverage to <c>NewWindowRequested</c>. A real hostile trigger
    /// (a rendered link's target="_blank") needs a genuine mouse click at
    /// on-screen pixel coordinates inside Edge's own PDF viewer chrome, which
    /// this suite has no reliable, headless way to do. <c>ExecuteScriptAsync</c>
    /// is documented to run even with <c>IsScriptEnabled=false</c> ("Scripts
    /// injected with ExecuteScriptAsync(String) run even if script is
    /// disabled"), so this uses it, host-side, to call <c>window.open()</c>
    /// directly — real Chromium, real event, no reflection — and checks that
    /// <see cref="WebViewPdfViewer"/>'s handler (subscribed first, during
    /// InitAsync) already marked the request Handled by the time THIS test's
    /// own handler (subscribed after) observes it.</summary>
    [Fact]
    public void NewWindowRequestedIsMarkedHandled() => _fx.Invoke(() =>
    {
        var (view, window) = NewView();
        try
        {
            var viewer = new WebViewPdfViewer(view);
            Assert.True(InitReady(viewer), "real WebView2 init failed: " + viewer.InitError);

            var tcs = new TaskCompletionSource<CoreWebView2NewWindowRequestedEventArgs>();
            void Handler(object? s, CoreWebView2NewWindowRequestedEventArgs e) => tcs.TrySetResult(e);
            view.CoreWebView2.NewWindowRequested += Handler;
#pragma warning disable xUnit1031
            try
            {
                var script = view.CoreWebView2.ExecuteScriptAsync(
                    "window.open('https://example.invalid/popup'); 'done';");
                PumpUntilComplete(tcs.Task);
                _ = script;
            }
            finally
            {
                view.CoreWebView2.NewWindowRequested -= Handler;
            }

            Assert.True(tcs.Task.Result.Handled,
                "WebViewPdfViewer did not mark the new-window request Handled");
#pragma warning restore xUnit1031
        }
        finally
        {
            window.Close();
        }
    });
}
