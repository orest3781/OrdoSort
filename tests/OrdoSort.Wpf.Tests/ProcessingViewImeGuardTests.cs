using System.Windows;
using System.Windows.Input;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 5 (audit remediation, 2026-08-02): ProcessingView's
/// NameBox_PreviewKeyDown treated every Enter as "file this document" with no
/// guard for an in-progress IME composition. WPF reports the key that
/// resolves a composition (often Enter, confirming a CJK candidate) as
/// <see cref="Key.ImeProcessed"/> rather than the literal key — so a user
/// typing a composed name and pressing Enter to accept the candidate would
/// file the document mid-keystroke instead of just confirming the name.
///
/// The Enter-files contract itself (last-used-or-first per enter_commits,
/// EnterTargetIndex shared by MarkRouteState/UpdatePreview — see
/// ShellViewModel and FilingLoopTests/RouteTrailTests) is untouched: this
/// suite proves a genuine (non-IME) Enter still fires it end to end, and
/// only an IME-owned keystroke is turned away.
///
/// Constructs the REAL, compiled ProcessingView (not a stand-in) with a real
/// ShellViewModel/Session/temp-folder filing loop (ShellFixture — same
/// fixture FilingLoopTests uses, fake viewer/dialogs only) inside a real,
/// off-screen Window so a genuine <see cref="KeyEventArgs"/> can be built
/// against a real <see cref="PresentationSource"/> and handed straight to
/// NameBox_PreviewKeyDown (internal for exactly this reason — see its own
/// doc comment). Shares HighlightContrastFixture's single STA thread/
/// Application (Theme/Styles.xaml + BoolToVis/RgbToBrush already merged
/// there), same as every other real-window test in HighlightContrastTests.</summary>
[Collection(HighlightContrastTests.Name)]
public class ProcessingViewImeGuardTests
{
    private readonly HighlightContrastFixture _fx;
    public ProcessingViewImeGuardTests(HighlightContrastFixture fx) => _fx = fx;

    private (ShellFixture shellFx, ProcessingView view, Window window) Build()
    {
        // ProcessingView.xaml resolves several Theme.* DynamicResources
        // (AccentBronze, SurfaceRaised, Border, Danger, …) that only exist on
        // the fixture's Application once a theme has actually been applied —
        // same requirement every other real-window test in this collection
        // has (see HighlightContrastTests).
        ThemeManager.Apply(_fx.App, dark: false);

        var shellFx = new ShellFixture();
        shellFx.AddInboxFile("20240115--111111.pdf");
        shellFx.Shell.Initialize();

        var view = new ProcessingView { DataContext = shellFx.Shell };
        var window = new Window
        {
            Content = view, Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        window.UpdateLayout();

        shellFx.Shell.StartProcessing();
        window.UpdateLayout();
        return (shellFx, view, window);
    }

    private static KeyEventArgs BuildKeyEventArgs(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("no PresentationSource for the offscreen window");
        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
    }

    [Fact]
    public void GenuineEnterStillFilesTheDocument() => _fx.Invoke(() =>
    {
        var (shellFx, view, window) = Build();
        try
        {
            shellFx.Shell.TypedName = "SMITH JOHN";

            var e = BuildKeyEventArgs(window, Key.Enter);
            view.NameBox_PreviewKeyDown(view, e);

            Assert.True(e.Handled);
            Assert.True(File.Exists(
                Path.Combine(shellFx.RouteDir, "20240115-SMITH JOHN-111111.pdf")));
        }
        finally { window.Close(); shellFx.Dispose(); }
    });

    [Fact]
    public void ImeProcessedEnterDoesNotFileTheDocument() => _fx.Invoke(() =>
    {
        var (shellFx, view, window) = Build();
        try
        {
            shellFx.Shell.TypedName = "SMITH JOHN";

            // The composition-owned keystroke: WPF reports Key.ImeProcessed
            // here, not Key.Enter, while an IME candidate window is
            // confirming a composed name.
            var e = BuildKeyEventArgs(window, Key.ImeProcessed);
            view.NameBox_PreviewKeyDown(view, e);

            // Left unhandled too: swallowing it here would stop the
            // TextBox/IME from finishing its own, unrelated job with this
            // keystroke.
            Assert.False(e.Handled);
            Assert.False(File.Exists(
                Path.Combine(shellFx.RouteDir, "20240115-SMITH JOHN-111111.pdf")));
            Assert.Equal("1 / 1", shellFx.Shell.ProgressLine);   // still on the same document
            Assert.Equal("SMITH JOHN", shellFx.Shell.TypedName);  // untouched
        }
        finally { window.Close(); shellFx.Dispose(); }
    });

    [Fact]
    public void IsImeComposingRecognizesOnlyTheImeProcessedKey() => _fx.Invoke(() =>
    {
        // The pure predicate itself, isolated from the view/session plumbing
        // above: exercises every Key the real handler's switch cases react
        // to, so a future case added to that switch doesn't silently escape
        // this guard.
        var window = new Window
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            Assert.True(ProcessingView.IsImeComposing(BuildKeyEventArgs(window, Key.ImeProcessed)));

            foreach (var key in new[] { Key.Enter, Key.Tab, Key.Down, Key.Up, Key.Escape, Key.S })
                Assert.False(ProcessingView.IsImeComposing(BuildKeyEventArgs(window, key)));
        }
        finally { window.Close(); }
    });
}
