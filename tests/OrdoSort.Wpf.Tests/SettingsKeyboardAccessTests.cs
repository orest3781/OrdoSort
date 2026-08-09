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
    /// takes the character after the first single underscore, which is why one
    /// of these is NOT the initial letter: General/Filing/Destinations/
    /// Monitored folders/Appearance/Data files would otherwise be
    /// G/F/D/M/A/D — a collision on D, so Data files takes "T" from "Da_ta".
    ///
    /// Task 9 (copy and terminology) renamed the fourth tab from "Dashboard"
    /// to "Monitored folders" — the name its own section header, the Data
    /// files label and Config.MonitorTitle's default already used. That
    /// retired the old "B" (from "Dash_board", which existed only because
    /// "Dashboard" made a third D) and freed the mnemonic back to an initial
    /// letter. M was unclaimed, so the set was G/F/D/M/A/T.
    ///
    /// Phase 3 task 3.2 split "Monitored folders" — the folder master/detail
    /// stayed, and its Alerts + Polling regions moved to a new seventh tab
    /// inserted right after it. "Alerts & polling" can't take A (Appearance's
    /// already); P (from "Polling") was free, so the header reads on its
    /// second word instead of its first — the same shape "Data files"
    /// already uses on "T" from Da_ta. The set is now G/F/D/M/A/T/P; the
    /// distinctness assertion below is what actually guards that, against
    /// live registrations rather than these literals.</summary>
    public static IEnumerable<object[]> TabHeaders()
    {
        yield return new object[] { "_General", "G" };
        yield return new object[] { "_Filing", "F" };
        yield return new object[] { "_Destinations", "D" };
        yield return new object[] { "_Monitored folders", "M" };
        yield return new object[] { "Alerts & _polling", "P" };
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
    /// <see cref="AccessKeyManager"/> reports each of G/F/D/M/A/T as a live
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

    /// <summary>Header literal -> what a screen reader must ANNOUNCE for that
    /// tab: the plain label, with no access-key marker in it.
    ///
    /// This is the companion the mnemonic assertions above cannot make: giving
    /// the six headers access keys put a literal "_" into the header literal,
    /// and nothing above notices if that leaks into what gets announced.
    ///
    /// A TabItem has TWO peers and they did NOT agree. Measured on this WPF,
    /// one run, one window, before any AutomationProperties.Name existed:
    /// <code>
    /// header="_General"    TabItemAutomationPeer="General"     (UIA tree)
    ///                      TabItemWrapperAutomationPeer="_General"
    /// header="Da_ta files" TabItemAutomationPeer="Data files"  (UIA tree)
    ///                      TabItemWrapperAutomationPeer="Da_ta files"
    /// MenuItem "_File"  -> MenuItemAutomationPeer="File"
    /// Button   "_Save"  -> ButtonAutomationPeer="Save"
    /// </code>
    /// So the peer a screen reader really walks — the
    /// <see cref="TabItemAutomationPeer"/> that
    /// <c>TabControlAutomationPeer.GetChildren()</c> publishes — was ALREADY
    /// clean: its GetNameCore runs the result through
    /// <c>AccessText.RemoveAccessKeyMarker</c>, exactly like MenuItem and
    /// ButtonBase. What carried the raw "_General" was the TabItem's own
    /// wrapper peer, which <c>UIElementAutomationPeer.CreatePeerForElement</c>
    /// hands back but which WPF never publishes as a UIA element (it exists to
    /// serve the item peer, and its EventsSource points back at it).
    ///
    /// The fix is an explicit <c>AutomationProperties.Name</c> per tab —
    /// preferred by <c>FrameworkElementAutomationPeer.GetNameCore</c> over
    /// every fallback, the same mechanism
    /// <see cref="ReorderButtonsAnnounceWhatTheyMove"/> already relies on — so
    /// both peers report the plain label and the announced name no longer
    /// rides on an undocumented strip inside one peer class. All three
    /// readings are asserted below; only two of them were ever red.</summary>
    public static IEnumerable<object[]> TabAutomationNames()
    {
        yield return new object[] { "_General", "General" };
        yield return new object[] { "_Filing", "Filing" };
        yield return new object[] { "_Destinations", "Destinations" };
        yield return new object[] { "_Monitored folders", "Monitored folders" };
        yield return new object[] { "Alerts & _polling", "Alerts & polling" };
        yield return new object[] { "_Appearance", "Appearance" };
        yield return new object[] { "Da_ta files", "Data files" };
    }

    /// <summary>Three readings, because no one of them is sufficient:
    /// <list type="number">
    /// <item>the <see cref="TabItemAutomationPeer"/> published in the UIA tree
    /// — the outcome that actually matters, and the one that was already
    /// green (it stays here so a future header edit that drops the label, or
    /// a WPF that stops stripping, is still caught);</item>
    /// <item>the wrapper peer <c>CreatePeerForElement</c> hands back — the
    /// reading that carried the raw "_General";</item>
    /// <item>the attached property itself — what makes the name explicit
    /// rather than inferred.</item>
    /// </list></summary>
    [Theory, MemberData(nameof(TabAutomationNames))]
    public void EachSettingsTabAnnouncesItsNameWithoutTheAccessKeyMarker(string header, string expected) =>
        _fx.Invoke(() =>
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

            var tabControlPeer = UIElementAutomationPeer.CreatePeerForElement(tabControl)
                ?? throw new InvalidOperationException("no automation peer for the TabControl");
            var itemPeers = (tabControlPeer.GetChildren() ?? new List<AutomationPeer>())
                .OfType<TabItemAutomationPeer>()
                .ToList();
            var peer = itemPeers.FirstOrDefault(p => ReferenceEquals(p.Item, tab))
                ?? throw new InvalidOperationException(
                    $"the TabControl's peer published no TabItemAutomationPeer for \"{header}\" " +
                    $"(found: {(itemPeers.Count == 0 ? "none at all" : string.Join(", ", itemPeers.Select(p => $"\"{p.GetName()}\"")))})");

            // (1) what the screen reader is handed
            Assert.Equal(expected, peer.GetName());

            // (2) the TabItem's own wrapper peer — this is the reading that
            // reported "_General" verbatim
            var wrapperPeer = UIElementAutomationPeer.CreatePeerForElement(tab)
                ?? throw new InvalidOperationException($"no automation peer for the \"{header}\" TabItem");
            Assert.Equal(expected, wrapperPeer.GetName());

            // (3) the mechanism that guarantees both
            Assert.Equal(expected, AutomationProperties.GetName(tab));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>All seven are distinct — the whole reason Data files and
    /// (since Phase 3 task 3.2) Alerts & polling use a non-initial letter.
    /// Asserted against the LIVE registrations rather than the literals, so
    /// a future header edit that quietly reintroduces a collision (D between
    /// Destinations/Data files, and "Dashboard" before Task 9 renamed it; A
    /// between Appearance/Alerts) fails here rather than in a user's
    /// hands.</summary>
    [Fact]
    public void TheSevenSettingsTabAccessKeysAreAllDistinct() => _fx.Invoke(() =>
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

            Assert.Equal(7, keys.Count);
            Assert.All(keys, k => Assert.NotEqual('\0', k));
            Assert.Equal(7, keys.Select(char.ToUpperInvariant).Distinct().Count());
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Why MainWindow's own File/Tools menu mnemonics cannot collide
    /// with these, asserted rather than reasoned about: access keys are
    /// scoped per <see cref="PresentationSource"/>, i.e. per top-level
    /// window. SettingsWindow's seven are invisible from another window's
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

            foreach (var key in new[] { "G", "F", "D", "M", "A", "T", "P" })
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
        yield return new object[] { "_Monitored folders", "↑", "Move folder up" };
        yield return new object[] { "_Monitored folders", "↓", "Move folder down" };
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

    /// <summary>The last hop the handler-level test above deliberately stops
    /// short of: Escape pressed with focus really inside the hotkey-capture
    /// box CLOSES the dialog. Both tests are kept — this one proves the
    /// outcome, the one above pins WHY (and keeps its positive control that
    /// ordinary keys are still captured, which this one cannot express).
    ///
    /// The report for this task claimed such a test was impossible because
    /// <c>ShowDialog()</c> would deadlock the fixture's single STA thread.
    /// That was wrong: ShowDialog pumps a NESTED DISPATCHER FRAME, so work
    /// posted to this same dispatcher before the call runs INSIDE that frame,
    /// on this very thread, while the dialog is up. That is also what makes
    /// <c>Button.IsCancel</c> reachable at all — IsCancel closes a dialog by
    /// assigning <c>Window.DialogResult</c>, which throws on a <c>Show()</c>n
    /// window, so no Show()-based test can ever exercise it.
    ///
    /// Escape must go through <see cref="InputManager"/>, not
    /// <c>RaiseEvent</c>: <c>IsCancel</c> registers "\x1B" with
    /// <see cref="AccessKeyManager"/> (exactly as <c>IsDefault</c> registers
    /// "\r" — see <see cref="UnlockEnterKeyTests"/>), and AccessKeyManager
    /// only ever sees a key in InputManager's POST-process stage, which a
    /// bare tree bubble never reaches. It is also why the hotkey handler's
    /// <c>e.Handled = false</c> is load-bearing: a handled PreviewKeyDown is
    /// never promoted to KeyDown, so AccessKeyManager never sees the key.
    ///
    /// The watchdog is what makes this a test rather than a hang: against a
    /// build where Escape is swallowed the dialog simply stays up forever, so
    /// the timer force-closes it and the run FAILS on <c>timedOut</c> instead
    /// of blocking the suite.</summary>
    [Fact]
    public void EscapeInTheHotkeyBoxClosesTheSettingsDialog() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var window = BuildSettingsWindow();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var reached = false;
        var focusedHotkeyBox = false;
        var visibleAfterEscape = true;
        var timedOut = false;
        Exception? failure = null;

        var watchdog = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Send,
            (_, _) => { timedOut = true; window.Close(); }, dispatcher);

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var tabControl = Descendants<TabControl>(window).FirstOrDefault()
                    ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
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

                hotkeyBox.Focus();
                Keyboard.Focus(hotkeyBox);
                focusedHotkeyBox = hotkeyBox.IsKeyboardFocused;

                var source = PresentationSource.FromVisual(window)
                    ?? throw new InvalidOperationException("no PresentationSource for the SettingsWindow");
                InputManager.Current.ProcessInput(
                    new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    });

                visibleAfterEscape = window.IsVisible;
                reached = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                window.Close();
            }
        }), DispatcherPriority.ApplicationIdle);

        bool? dialogResult;
        try
        {
            dialogResult = window.ShowDialog();
        }
        finally
        {
            watchdog.Stop();
        }

        if (failure is not null)
            throw new InvalidOperationException(
                "the posted Escape never got as far as pressing the key", failure);

        // `reached` FIRST, deliberately: a watchdog timeout has two possible
        // causes and only one of them is the defect. If the posted body never
        // ran at all — ApplicationIdle starvation, or the arrangement throwing
        // before ProcessInput — then `timedOut` is true for a reason that has
        // nothing to do with Escape, and asserting it first would report the
        // swallowed-Escape message for a harness problem. Not a flakiness fix:
        // the green path takes ~218ms against a 20s watchdog, ~90x margin.
        Assert.True(reached,
            "the Escape press never ran — the posted arrangement did not reach ProcessInput, so this " +
            "run says nothing about Escape either way (a harness problem, not the swallowed-Escape defect)");
        Assert.False(timedOut,
            "Escape with focus in the hotkey-capture box did NOT close the Settings dialog — " +
            "it stayed up until the watchdog force-closed it, which is exactly the swallowed-Escape " +
            "defect (a handled PreviewKeyDown is never promoted to KeyDown, so the Cancel button's " +
            "IsCancel registration in AccessKeyManager never sees the key)");
        Assert.True(focusedHotkeyBox,
            "the hotkey-capture box never took keyboard focus, so this proves nothing about it");
        Assert.False(visibleAfterEscape,
            "the dialog was still visible immediately after Escape reached the input pipeline");
        Assert.False(dialogResult);   // IsCancel closes by setting DialogResult = false
    });
}
