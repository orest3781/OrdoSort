using System.Reflection;
using System.Windows;
using System.Windows.Threading;
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

/// <summary>Step 7's other half. Every test above calls
/// <see cref="WebViewPdfViewer.IsPermittedNavigation"/> directly — a pure
/// function — so none of them can tell whether <c>InitAsync</c> actually
/// wires it to the real <c>CoreWebView2.NavigationStarting</c> event.
/// Confirmed empirically (2026-08-05): commenting out the
/// <c>core.NavigationStarting += ...</c> line in <c>InitAsync</c> and
/// re-running every test in <see cref="ViewerNavigationPolicyTests"/> left
/// all twelve green. That is the exact "untested branch carrying the whole
/// safety argument" failure mode this task exists to avoid, so this class
/// drives a REAL WebView2/Edge startup — the one thing the rest of this
/// project's Wpf suite deliberately avoids (see
/// <see cref="TriageWindowInitRaceTests"/>'s class doc) — specifically so a
/// deleted or misplaced guard has somewhere to fail.
///
/// It still stops short of firing an actual hostile navigation and watching
/// WebView2 refuse it: that needs a loaded window pumping its own message
/// loop for as long as real Edge/network timing takes, which is what the
/// smoke harness (tools/OrdoSort.Smoke) is for, not a unit test. What this
/// asserts instead is narrower and honest about the gap: that
/// <c>InitAsync</c> itself — not just the WebView2 control's own internal
/// plumbing, which is ALWAYS subscribed here regardless of this app's code,
/// confirmed empirically — registered a handler on the live
/// <c>CoreWebView2.NavigationStarting</c> event, found via reflection on the
/// WebView2 SDK's own backing field (a plain <c>EventHandler&lt;...&gt;</c>
/// field named <c>navigationStarting</c>, confirmed present in
/// Microsoft.Web.WebView2.Core 1.0.2903.40 by inspecting the shipped DLL —
/// this is reading a third-party implementation detail, not a public
/// contract, so it can break on a WebView2 SDK upgrade; if it does, that
/// breakage itself is a prompt to re-verify the guard by hand, not something
/// to silently work around). Deleting the wiring line makes this test fail;
/// the pure-predicate tests above do not.</summary>
[Collection(HighlightContrastTests.Name)]
public class WebViewPdfViewerGuardWiringTests
{
    private readonly HighlightContrastFixture _fx;
    public WebViewPdfViewerGuardWiringTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>Pumps a nested Dispatcher frame on the fixture's STA thread
    /// until <paramref name="task"/> completes — the same technique
    /// TriageWindowInitRaceTests' PumpUntilComplete and
    /// CopyAndTerminologyTests' PumpUntil use, and for the same reason:
    /// InitAsync's awaits post their continuations back to THIS thread's
    /// DispatcherSynchronizationContext, so a plain blocking wait here
    /// (<c>.GetAwaiter().GetResult()</c>) would never let them run —
    /// PushFrame keeps the message loop alive while "blocked".</summary>
    private static void PumpUntilComplete(Task task)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void InitAsyncSubscribesANavigationStartingHandlerOnTheRealCoreWebView2() => _fx.Invoke(() =>
    {
        var window = new Window
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 200, Height = 200,
        };
        var view = new WebView2();
        window.Content = view;
        try
        {
            window.Show();

            var viewer = new WebViewPdfViewer(view);
            var initTask = viewer.InitAsync();
            PumpUntilComplete(initTask);

            // .Result here is synchronous-safe, not a deadlock risk:
            // PumpUntilComplete already spun the dispatcher until initTask
            // completed, so this only ever reads an already-set result.
#pragma warning disable xUnit1031
            Assert.True(initTask.IsCompletedSuccessfully && initTask.Result,
                "real WebView2 init failed: " + viewer.InitError);
#pragma warning restore xUnit1031

            var field = view.CoreWebView2.GetType()
                .GetField("navigationStarting", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field is not null,
                "WebView2 SDK no longer exposes a 'navigationStarting' backing field — " +
                "this test needs updating for the new SDK version, it is not proof the guard is gone.");

            // The WPF WebView2 control ALWAYS has its own internal forwarder
            // subscribed here (Microsoft.Web.WebView2.Wpf.WebView2Base's own
            // CoreWebView2_NavigationStarting, confirmed empirically — count
            // is 1 even with WebViewPdfViewer's guard deleted), so a bare
            // "is anything subscribed" check is not enough: it would pass
            // whether or not InitAsync ever ran. What distinguishes OUR guard
            // is that its backing method is declared on WebViewPdfViewer
            // itself (confirmed empirically as
            // "OrdoSort.Wpf.Services.WebViewPdfViewer.<InitAsync>b__12_0")
            // rather than on the SDK's wrapper type.
            var handler = field!.GetValue(view.CoreWebView2) as Delegate;
            var ours = handler?.GetInvocationList()
                .Any(d => d.Method.DeclaringType == typeof(WebViewPdfViewer)) ?? false;
            Assert.True(ours,
                "InitAsync did not subscribe its own NavigationStarting handler on the real " +
                "CoreWebView2 (only the WebView2 control's built-in forwarder is present) — " +
                "a hostile PDF's link annotation would navigate unchecked.");
        }
        finally
        {
            window.Close();
        }
    });
}
