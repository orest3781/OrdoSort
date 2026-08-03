using OrdoSort.Core;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 5 (audit remediation, 2026-08-02): TriageWindow builds a
/// fresh WebView2 for every "Review matches" pass (MatchMergeWindow.OnReview)
/// — deliberately not reused, so nothing leaks across FILES the way the
/// predecessor did — but nothing ever disposed the old one either, so its
/// underlying browser process outlived the window and accumulated across
/// repeated review sessions instead.
///
/// Proof standard here (per the task brief): assert the disposal headlessly
/// if reachable at all, and say plainly if not. It IS reachable, without ever
/// starting a real Edge/CoreWebView2 environment: <c>WebView2.Dispose()</c>
/// (confirmed empirically against the actual installed package,
/// Microsoft.Web.WebView2 1.0.2903.40, via a throwaway net8.0-windows probe —
/// not assumed) makes the SAME entry point
/// <see cref="OrdoSort.Wpf.Services.WebViewPdfViewer.InitAsync"/> calls
/// (<c>EnsureCoreWebView2Async</c>) throw <see cref="ObjectDisposedException"/>
/// afterward, and — separately confirmed in that same probe —
/// <c>Window.Closed</c> fires even for a window that was never <c>Show()</c>n,
/// so this test never needs a live Edge runtime, a shown window, or any
/// pumping: constructing TriageWindow and calling <c>Close()</c> is enough to
/// exercise its Closed handler exactly the way a real "Review matches" pass
/// would, just without ever paying for a real browser process to prove it
/// disposed one.
///
/// <c>Viewer</c> is the real, compiled TriageWindow.xaml's x:Name field
/// (generated `internal`, confirmed in TriageWindow.g.cs) — reachable
/// directly, no reflection needed, via this project's InternalsVisibleTo.
///
/// Shares HighlightContrastFixture's single STA thread/Application (only one
/// WPF Application may exist per process, and TriageWindow.xaml resolves
/// StaticResources — HeadlineText, PrimaryButton, … — from the same
/// Theme/Styles.xaml that fixture merges in), same as every other
/// real-window test in HighlightContrastTests.</summary>
[Collection(HighlightContrastTests.Name)]
public class TriageWindowDisposalTests
{
    private readonly HighlightContrastFixture _fx;
    public TriageWindowDisposalTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void ClosingTheWindowDisposesItsWebView2() => _fx.Invoke(() =>
    {
        var win = new TriageWindow(new List<MatchMerge.MatchResult>(), new[] { "A", "B" });
        var viewer = win.Viewer;

        // Never Show()n: Loaded (which would call _pdf.InitAsync and start a
        // real Edge environment) never fires, so this stays fast and
        // hermetic — see the class doc for why Closed still fires anyway.
        win.Close();

        // GetResult() here is synchronous-safe, not a deadlock risk: Dispose
        // already ran, so this throws ObjectDisposedException immediately —
        // there is no real async continuation pending to block on.
#pragma warning disable xUnit1031
        Assert.Throws<ObjectDisposedException>(() =>
            viewer.EnsureCoreWebView2Async().GetAwaiter().GetResult());
#pragma warning restore xUnit1031
    });
}
