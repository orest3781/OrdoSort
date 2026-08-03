using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

file sealed class NoDialogs : IDialogService
{
    public void Warn(string message, string title) { }
    public void Info(string message, string title) { }
    public bool Confirm(string message, string title) => true;
    public string? AskSaveFile(string filter, string suggestedName) => null;
    public string? AskOpenFile(string filter) => null;
    public string? AskFilePath(string filter, string suggestedName) => null;
    public string? BrowseFolder(string? startAt) => null;
}

/// <summary>Task 9 of the 2026-08-02 audit remediation: copy and terminology.
/// Everything here is asserted against REAL production windows/views built
/// off-screen and their RESOLVED values, never against the markup text — a
/// header literal, an AutomationProperties.Name and a rendered TextBlock are
/// three different things and the audit found them disagreeing.
///
/// The three findings covered: I4 (one column said "Route" while the rest of
/// the app said "Destination"), I5 ("Dashboard" vs "Monitored folders" for one
/// feature), M4 (the app's only Title-Case button) and M3 (informational notes
/// wearing the needs-attention colour).</summary>
[Collection(HighlightContrastTests.Name)]
public class CopyAndTerminologyTests
{
    private readonly HighlightContrastFixture _fx;
    public CopyAndTerminologyTests(HighlightContrastFixture fx) => _fx = fx;

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static SettingsWindow BuildSettingsWindow(SettingsViewModel vm) =>
        new(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

    private static SettingsViewModel BuildSettingsVm(bool dark, out string cfgPath)
    {
        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_copy_" + Guid.NewGuid(), "config.json");
        return new SettingsViewModel(cfg, new NoDialogs(),
            () => dark ? ThemePalette.Dark : ThemePalette.Light, cfgPath,
            uiContext: SynchronizationContext.Current);
    }

    // ------------------------------------------- I5: one word for one feature

    /// <summary>The tab, the section header it opens with, and the Data files
    /// tab's label for the very same list must all say the SAME thing. Before
    /// this task the tab said "Dashboard" and the other two said "Monitored
    /// folders" — one feature, two names, and the loser was the name used
    /// everywhere else in the product (including
    /// <see cref="Config.MonitorTitle"/>'s own default, which is the heading a
    /// user reads on the Ready screen).
    ///
    /// Asserted on the LIVE tab (marker-stripped header + the announced
    /// automation name + the rendered section heading), because those are
    /// three separately-authored strings that have to agree.</summary>
    [Fact]
    public void MonitoredFoldersIsCalledThatOnItsTabItsHeadingAndTheDataFilesLabel() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var vm = BuildSettingsVm(dark: false, out _);
        var window = BuildSettingsWindow(vm);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var tabControl = Descendants<TabControl>(window).First();
            var tabs = tabControl.Items.Cast<TabItem>().ToList();

            var tab = tabs.FirstOrDefault(t => (t.Header?.ToString() ?? "").Replace("_", "") == "Monitored folders")
                ?? throw new InvalidOperationException(
                    "no tab reads \"Monitored folders\" (headers: " +
                    string.Join(", ", tabs.Select(t => $"\"{t.Header}\"")) + ")");

            // the word it replaced must be gone from the tab strip entirely
            Assert.DoesNotContain(tabs, t =>
                (t.Header?.ToString() ?? "").Replace("_", "").Contains("Dashboard"));
            Assert.Equal("Monitored folders", AutomationProperties.GetName(tab));

            // the heading INSIDE the tab, resolved from the live tree
            tabControl.SelectedItem = tab;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();
            var headings = Descendants<TextBlock>(window)
                .Where(t => ReferenceEquals(t.Style, window.TryFindResource("SectionText")))
                .Select(t => t.Text)
                .ToList();
            Assert.Contains("Monitored folders", headings);

            // and the Data files tab's label for the same file
            tabControl.SelectedItem = tabs.First(t => (t.Header?.ToString() ?? "").Replace("_", "") == "Data files");
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();
            var labels = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
            Assert.Contains("Monitored folders:", labels);
        }
        finally { window.Close(); }
    });

    // ----------------------------------------------------- M4: sentence case

    /// <summary>"Start Processing" was the only Title-Case button in an app
    /// that is sentence case everywhere else. Both readings are asserted: the
    /// glyph a sighted user sees and the name a screen reader announces are
    /// authored separately here (the button's content is a StackPanel, so the
    /// peer cannot fall back to it), and letting them drift would tell two
    /// users two different labels.</summary>
    [Fact]
    public void TheReadyScreensPrimaryButtonIsSentenceCase() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var view = new ReadyView();
        view.Measure(new Size(600, 800));
        view.Arrange(new Rect(0, 0, 600, 800));
        view.UpdateLayout();

        var button = Descendants<Button>(view)
                .FirstOrDefault(b => AutomationProperties.GetName(b).Contains("tart", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("no start button under ReadyView");

        Assert.Equal("Start processing", AutomationProperties.GetName(button));
        var labels = Descendants<TextBlock>(button).Select(t => t.Text).ToList();
        Assert.Contains("Start processing", labels);
        Assert.DoesNotContain("Start Processing", labels);
    });

    // ------------------------------- M3: amber is reserved for needs-attention

    /// <summary>Each case: what to put in the Inbox box, the note it produces,
    /// and whether that note is something the user has to act on.</summary>
    public static IEnumerable<object[]> NoteCases()
    {
        foreach (var dark in new[] { false, true })
        {
            // a fact about a perfectly valid setting
            yield return new object[] { dark, @"inbox", "relative — resolved beside the config file", false };
            // a setting that will fail at OK time
            yield return new object[] { dark, @"C:\definitely\not\here\ordo", "folder doesn't exist", true };
            // blank inbox: nothing will ever be processed
            yield return new object[] { dark, "", "no inbox folder set", true };
        }
    }

    /// <summary>Amber means needs-attention everywhere else in this app, so
    /// spending it on "relative — resolved beside the config file" spent it on
    /// nothing. The note now resolves to Theme.SubtleText for a fact and
    /// Theme.StatusAmber for a problem.
    ///
    /// Measured as a RESOLVED value on the real TextBlock in a real
    /// SettingsWindow, in both palettes — not by reading the Style. The note
    /// TextBlock is located by its Text BINDING PATH rather than by its text
    /// or its position, so this cannot silently start reading a different
    /// field's note.</summary>
    [Theory, MemberData(nameof(NoteCases))]
    public void AnInformationalNoteIsSubtleAndOnlyAProblemIsAmber(
        bool dark, string inbox, string expectedNote, bool expectedAmber) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var vm = BuildSettingsVm(dark, out _);
        var window = BuildSettingsWindow(vm);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            // Every note value this test uses is a synchronous fast path (blank
            // or relative resolve with no I/O; a non-existent absolute folder
            // resolves through the probe, which the fixture's inline scheduler
            // and immediate=false debounce would otherwise delay) — so pump
            // until the text arrives rather than assuming a single layout pass.
            vm.Inbox = inbox;
            var note = Descendants<TextBlock>(window).FirstOrDefault(t =>
                    BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(vm.InboxNote))
                ?? throw new InvalidOperationException("no TextBlock bound to InboxNote");

            for (var i = 0; i < 200 && !note.Text.Contains(expectedNote, StringComparison.Ordinal); i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(10);
            }
            window.UpdateLayout();
            PumpRender();

            Assert.Contains(expectedNote, note.Text);
            Assert.Equal(expectedAmber, vm.InboxNoteNeedsAttention);
            Assert.Equal(Visibility.Visible, note.Visibility);

            var palette = dark ? ThemePalette.Dark : ThemePalette.Light;
            var expected = expectedAmber ? palette.StatusAmber : palette.SubtleText;
            var actual = ToRgb(note.Foreground);
            Assert.True(expected == actual,
                $"{(dark ? "dark" : "light")} \"{note.Text}\": expected " +
                $"{(expectedAmber ? "StatusAmber" : "SubtleText")} {expected}, resolved {actual}");
        }
        finally { window.Close(); }
    });

    /// <summary>The de-emphasis this task introduces must not be illegible.
    /// Measured, not assumed, against the background the note is really drawn
    /// on (the nearest painted ancestor in the live tree), in both palettes.
    /// The threshold is WCAG AA for body text; the point of recording the
    /// number in the failure message is that a future palette tweak that
    /// drifts toward it says so.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASubtleNoteStillMeetsAaContrastOnItsOwnBackground(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var vm = BuildSettingsVm(dark, out _);
        var window = BuildSettingsWindow(vm);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            vm.Inbox = "inbox";                 // relative -> informational, resolved synchronously
            window.UpdateLayout();
            PumpRender();

            var note = Descendants<TextBlock>(window).First(t =>
                BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(vm.InboxNote));
            Assert.Contains("relative", note.Text);

            var fg = ToRgb(note.Foreground);
            var (bg, source) = NearestPaintedBackground(note);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"{(dark ? "dark" : "light")}: subtle note {fg} on {source} {bg} = {ratio:F2}");
        }
        finally { window.Close(); }
    });

    private static Rgb ToRgb(Brush? brush)
    {
        var color = (brush as SolidColorBrush)?.Color
            ?? throw new InvalidOperationException($"not a SolidColorBrush: {brush?.GetType().Name ?? "null"}");
        return new Rgb(color.R, color.G, color.B);
    }

    /// <summary>Walk up the VISUAL tree to whatever actually paints behind this
    /// element. A TextBlock has no background of its own, and the panel
    /// directly above it usually doesn't either, so "the window's Background"
    /// would be a guess — this reports what was found and where.</summary>
    private static (Rgb Color, string Source) NearestPaintedBackground(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            Brush? brush = node switch
            {
                Panel p => p.Background,
                Border b => b.Background,
                Control c => c.Background,
                _ => null,
            };
            if (brush is SolidColorBrush { Color.A: > 0 })
                return (ToRgb(brush), node.GetType().Name);
        }
        throw new InvalidOperationException("nothing paints a background above this element");
    }
}
