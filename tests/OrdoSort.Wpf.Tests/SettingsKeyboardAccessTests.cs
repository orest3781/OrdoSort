using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
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

/// <summary>Task 8 (audit remediation, 2026-08-02), Steps 2 and 4, against a
/// REAL <see cref="SettingsWindow"/> built off-screen.
///
/// Step 4 gave the six tab headers access keys and named the four ↑/↓ reorder
/// buttons. The access-key assertions below deliberately do NOT check that
/// the Header string contains an underscore — that would pass on markup that
/// never reaches a live AccessKeyManager registration (e.g. a template whose
/// header ContentPresenter lacks RecognizesAccessKey, which is exactly the
/// precondition the brief flagged as needing checking). They assert the two
/// things that actually make Alt+letter work: WPF really registered the key
/// in this window's scope, and the rendered header really produced an
/// <see cref="AccessText"/> carrying that key.
///
/// Step 2 let Escape out of the hotkey-capture box. That is covered here
/// rather than in a separate file because the only way to exercise it is
/// through this same real window.</summary>
[Collection(HighlightContrastTests.Name)]
public class SettingsKeyboardAccessTests
{
    private readonly HighlightContrastFixture _fx;
    public SettingsKeyboardAccessTests(HighlightContrastFixture fx) => _fx = fx;

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    /// <summary>Header literal -> the access key WPF derives from it. WPF
    /// takes the character after the first single underscore, which is why
    /// three of these are NOT the initial letter: General/Filing/
    /// Destinations/Dashboard/Appearance/Data files would otherwise be
    /// G/F/D/D/A/D — a three-way collision on D.</summary>
    public static IEnumerable<object[]> TabHeaders()
    {
        yield return new object[] { "_General", "G" };
        yield return new object[] { "_Filing", "F" };
        yield return new object[] { "_Destinations", "D" };
        yield return new object[] { "Dash_board", "B" };
        yield return new object[] { "_Appearance", "A" };
        yield return new object[] { "Da_ta files", "T" };
    }

    private static SettingsWindow BuildSettingsWindow()
    {
        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_a11y_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(),
            () => ThemePalette.Light, cfgPath,
            uiContext: SynchronizationContext.Current);
        return new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // ------------------------------------------------------- Step 4: mnemonics

    /// <summary>The registration proof: after the window is up, WPF's own
    /// <see cref="AccessKeyManager"/> reports each of G/F/D/B/A/T as a live
    /// access key for THIS window's scope. Nothing about the markup is
    /// inspected — if the header ContentPresenter ever loses
    /// RecognizesAccessKey, or a header is retyped without its underscore,
    /// this goes red.</summary>
    [Theory, MemberData(nameof(TabHeaders))]
    public void EachSettingsTabHeaderRegistersItsAccessKey(string header, string key) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = BuildSettingsWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var scope = PresentationSource.FromVisual(window)
                ?? throw new InvalidOperationException("no PresentationSource for the off-screen SettingsWindow");
            Assert.True(AccessKeyManager.IsKeyRegistered(scope, key),
                $"Alt+{key} (from header \"{header}\") is not a registered access key in " +
                "SettingsWindow's scope");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The rendering proof, and the half that pins which header owns
    /// which key: the header ContentPresenter must have produced an
    /// <see cref="AccessText"/> whose <c>AccessKey</c> is the expected
    /// character. (AccessText is searched for directly — it does NOT derive
    /// from TextBlock and it carries a private, always-empty child TextBlock
    /// that has already produced one false positive in this suite, so a
    /// TextBlock-based walk would be wrong here.)</summary>
    [Theory, MemberData(nameof(TabHeaders))]
    public void EachSettingsTabHeaderRendersAnAccessTextWithThatKey(string header, string key) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = BuildSettingsWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var tabControl = Descendants<TabControl>(window).FirstOrDefault()
                ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
            var tab = tabControl.Items.Cast<TabItem>()
                    .FirstOrDefault(ti => ti.Header?.ToString() == header)
                ?? throw new InvalidOperationException($"no TabItem with Header \"{header}\"");

            var accessTexts = Descendants<AccessText>(tab).ToList();
            var texts = accessTexts.Select(a => $"\"{a.Text}\"->'{a.AccessKey}'").ToList();
            Assert.True(
                accessTexts.Any(a => char.ToUpperInvariant(a.AccessKey) == char.ToUpperInvariant(key[0])),
                $"header \"{header}\" rendered no AccessText with AccessKey '{key}' " +
                $"(found: {(texts.Count == 0 ? "none at all" : string.Join(", ", texts))})");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>All six are distinct — the whole reason Dashboard and Data
    /// files use non-initial letters. Asserted against the LIVE registrations
    /// rather than the literals, so a future header edit that quietly
    /// reintroduces the D/D/D collision fails here.</summary>
    [Fact]
    public void TheSixSettingsTabAccessKeysAreAllDistinct() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = BuildSettingsWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var tabControl = Descendants<TabControl>(window).FirstOrDefault()
                ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
            var keys = tabControl.Items.Cast<TabItem>()
                .Select(ti => Descendants<AccessText>(ti)
                    .Select(a => a.AccessKey)
                    .FirstOrDefault(k => k != '\0'))
                .ToList();

            Assert.Equal(6, keys.Count);
            Assert.All(keys, k => Assert.NotEqual('\0', k));
            Assert.Equal(6, keys.Select(char.ToUpperInvariant).Distinct().Count());
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Why MainWindow's own File/Tools menu mnemonics cannot collide
    /// with these, asserted rather than reasoned about: access keys are
    /// scoped per <see cref="PresentationSource"/>, i.e. per top-level
    /// window. SettingsWindow's six are invisible from another window's
    /// scope — MainWindow uses F (_File) and T (_Tools), which SettingsWindow
    /// also uses (_Filing, Da_ta files), and this is the property that keeps
    /// that harmless.</summary>
    [Fact]
    public void SettingsAccessKeysAreScopedToItsOwnWindow() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var settings = BuildSettingsWindow();
        var other = new Window
        {
            Content = new Border { Width = 40, Height = 20 },
            Width = 120, Height = 90,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            settings.Show();
            settings.UpdateLayout();
            other.Show();
            other.UpdateLayout();
            PumpRender();

            var settingsScope = PresentationSource.FromVisual(settings)!;
            var otherScope = PresentationSource.FromVisual(other)!;
            Assert.NotSame(settingsScope, otherScope);

            foreach (var key in new[] { "G", "F", "D", "B", "A", "T" })
            {
                Assert.True(AccessKeyManager.IsKeyRegistered(settingsScope, key),
                    $"Alt+{key} missing from SettingsWindow's scope");
                Assert.False(AccessKeyManager.IsKeyRegistered(otherScope, key),
                    $"Alt+{key} leaked out of SettingsWindow's scope into an unrelated window — " +
                    "the per-window scoping that keeps MainWindow's menu mnemonics collision-free " +
                    "no longer holds");
            }
        }
        finally
        {
            other.Close();
            settings.Close();
        }
    });

    // --------------------------------------------------- Step 4: control names

    public static IEnumerable<object[]> ReorderButtons()
    {
        // header of the tab that hosts them, glyph, expected screen-reader name
        yield return new object[] { "_Destinations", "↑", "Move destination up" };
        yield return new object[] { "_Destinations", "↓", "Move destination down" };
        yield return new object[] { "Dash_board", "↑", "Move folder up" };
        yield return new object[] { "Dash_board", "↓", "Move folder down" };
    }

    /// <summary>The four ↑/↓ reorder buttons announced nothing but their
    /// arrow glyph. Checks BOTH the attached property (which is what makes
    /// the name explicit rather than inferred) and the value the automation
    /// peer actually hands a screen reader — the peer alone would be a weak
    /// assertion, since an unnamed Button's peer happily falls back to its
    /// own content and would report the bare "↑".</summary>
    [Theory, MemberData(nameof(ReorderButtons))]
    public void ReorderButtonsAnnounceWhatTheyMove(string tabHeader, string glyph, string expected) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = BuildSettingsWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var tabControl = Descendants<TabControl>(window).FirstOrDefault()
                ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
            tabControl.SelectedItem = tabControl.Items.Cast<TabItem>()
                    .FirstOrDefault(ti => ti.Header?.ToString() == tabHeader)
                ?? throw new InvalidOperationException($"no TabItem with Header \"{tabHeader}\"");
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            // Search the whole window: TabControl hosts the selected tab's
            // content through its own PART_SelectedContentHost, never under
            // the TabItem object itself.
            var button = Descendants<Button>(window)
                    .FirstOrDefault(b => b.Content as string == glyph)
                ?? throw new InvalidOperationException(
                    $"no \"{glyph}\" Button found on the {tabHeader} tab");

            Assert.Equal(expected, AutomationProperties.GetName(button));
            var peer = UIElementAutomationPeer.CreatePeerForElement(button)
                ?? throw new InvalidOperationException("no automation peer for the reorder button");
            Assert.Equal(expected, peer.GetName());
        }
        finally
        {
            window.Close();
        }
    });

    // ----------------------------------------------------- Step 2: Esc capture

    /// <summary>The hotkey box records whatever you press, and used to set
    /// <c>e.Handled = true</c> for EVERY key except Tab — so Escape was
    /// swallowed and recorded as the literal hotkey "Escape" instead of
    /// closing the dialog. Decision taken in Step 2: Escape closes the dialog
    /// (there is no separate draft state for a "cancel capture" to unwind),
    /// so the handler must leave it unhandled for the Cancel button's
    /// IsCancel="True" to see it.
    ///
    /// The Ctrl+1 case is the load-bearing control: without it, an Escape-only
    /// assertion would also pass against a handler that was never wired up at
    /// all, or one that had stopped capturing entirely.</summary>
    [Fact]
    public void EscapeIsLeftUnhandledByTheHotkeyBoxButRealKeysAreCaptured() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = BuildSettingsWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var tabControl = Descendants<TabControl>(window).FirstOrDefault()!;
            tabControl.SelectedItem = tabControl.Items.Cast<TabItem>()
                .First(ti => ti.Header?.ToString() == "_Destinations");
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var listBox = Descendants<ListBox>(window).FirstOrDefault()
                ?? throw new InvalidOperationException("no route ListBox under SettingsWindow");
            listBox.SelectedIndex = 0;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var hotkeyBox = Descendants<TextBox>(window)
                    .FirstOrDefault(t => AutomationProperties.GetName(t) == "Hotkey — press keys to set")
                ?? throw new InvalidOperationException("no hotkey TextBox under SettingsWindow");
            var source = PresentationSource.FromVisual(window)!;

            KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            var before = hotkeyBox.Text;

            var esc = Press(Key.Escape);
            hotkeyBox.RaiseEvent(esc);
            Assert.False(esc.Handled,
                "Escape was swallowed by the hotkey-capture handler — it can never reach the " +
                "dialog's Cancel button");
            Assert.Equal(before, hotkeyBox.Text);
            Assert.DoesNotContain("Escape", hotkeyBox.Text);

            // The Cancel button IS the thing an unhandled Escape reaches;
            // without it, "not handled" would be a hollow guarantee.
            Assert.Contains(Descendants<Button>(window), b => b.IsCancel);

            var digit = Press(Key.D1);
            hotkeyBox.RaiseEvent(digit);
            Assert.True(digit.Handled, "the hotkey box stopped capturing ordinary keys");
            Assert.Equal("1", hotkeyBox.Text);
        }
        finally
        {
            window.Close();
        }
    });
}
