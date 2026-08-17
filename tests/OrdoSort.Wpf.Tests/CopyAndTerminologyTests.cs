using System.Reflection;
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

    /// <summary>Takes the resolved <see cref="ThemePalette"/> directly (not a
    /// bool/scheme key) so this one helper serves both the still-bool-dark
    /// terminology Facts below (which pass ThemePalette.Light/Dark literally)
    /// and the migrated contrast theories (which pass their own resolved
    /// scheme.Palette) without needing two near-identical overloads.</summary>
    private static SettingsViewModel BuildSettingsVm(ThemePalette palette, out string cfgPath,
        int probeDelayMs = 300)
    {
        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_copy_" + Guid.NewGuid(), "config.json");
        return new SettingsViewModel(cfg, new NoDialogs(),
            () => palette, cfgPath,
            uiContext: SynchronizationContext.Current, probeDelayMs: probeDelayMs);
    }

    /// <summary>Pump this thread's dispatcher until <paramref name="settled"/>
    /// holds, YIELDING between checks (a DispatcherTimer inside a nested
    /// DispatcherFrame) rather than sleeping on the very thread the awaited
    /// work has to be posted back to. The per-field notes resolve off-thread
    /// through <see cref="OrdoSort.Wpf.Services.DebouncedProbe{T}"/> and come
    /// back via SynchronizationContext.Post, so nothing arrives at all unless
    /// this thread keeps dispatching.</summary>
    private static void PumpUntil(Func<bool> settled, Func<string> describe)
    {
        if (settled()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10),
            DispatcherPriority.Background,
            (_, _) => { if (settled() || DateTime.UtcNow > deadline) frame.Continue = false; },
            Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(settled(), describe());
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
        var vm = BuildSettingsVm(ThemePalette.Light, out _);
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
            // section headings may carry SectionText or its spacing-canon
            // derivatives (SectionHeader / SectionHeaderFirst, BasedOn it)
            var headingStyles = new[] { "SectionText", "SectionHeader", "SectionHeaderFirst" }
                .Select(window.TryFindResource)
                .Where(s => s is not null)
                .ToList();
            var headings = Descendants<TextBlock>(window)
                .Where(t => headingStyles.Any(s => ReferenceEquals(t.Style, s)))
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
        foreach (var s in ThemePalette.Schemes)
        {
            // a fact about a perfectly valid setting
            yield return new object[] { s.Key, @"inbox", "relative — resolved beside the config file", false };
            // a setting that will fail at OK time
            yield return new object[] { s.Key, @"C:\definitely\not\here\ordo", "folder doesn't exist", true };
            // blank inbox: nothing will ever be processed
            yield return new object[] { s.Key, "", "no inbox folder set", true };
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
        string schemeKey, string inbox, string expectedNote, bool expectedAmber) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);
        var vm = BuildSettingsVm(scheme.Palette, out var cfgPath);
        // 2026-08 audit finding C2: a relative Inbox now really does resolve
        // beside config.json (Config.ResolveBeside) and the note's own
        // existence check runs against THAT location — so the "inbox" case
        // below needs a real folder there, or it would (correctly, post-fix)
        // report "folder doesn't exist" instead of the relative-info text.
        var cfgDir = Path.GetDirectoryName(cfgPath)!;
        Directory.CreateDirectory(Path.Combine(cfgDir, "inbox"));
        var window = BuildSettingsWindow(vm);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            // Blank resolves with no I/O; a relative value now goes through
            // the same debounced, off-thread existence probe an absolute
            // value does (see the class doc above) — so pump until the text
            // arrives rather than assuming a single layout pass.
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

            var palette = scheme.Palette;
            var expected = expectedAmber ? palette.StatusAmber : palette.SubtleText;
            var actual = ToRgb(note.Foreground);
            Assert.True(expected == actual,
                $"{schemeKey} \"{note.Text}\": expected " +
                $"{(expectedAmber ? "StatusAmber" : "SubtleText")} {expected}, resolved {actual}");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(cfgDir, true); } catch (IOException) { }
        }
    });

    /// <summary>The de-emphasis this task introduces must not be illegible.
    /// Measured, not assumed, against the background the note is really drawn
    /// on (the nearest painted ancestor in the live tree), in both palettes.
    /// The threshold is WCAG AA for body text; the point of recording the
    /// number in the failure message is that a future palette tweak that
    /// drifts toward it says so.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ASubtleNoteStillMeetsAaContrastOnItsOwnBackground(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);
        var vm = BuildSettingsVm(scheme.Palette, out var cfgPath);
        // 2026-08 audit finding C2: relative Inbox now resolves — and is
        // existence-checked — beside config.json, so "inbox" needs a real
        // folder there to settle on the relative-info text this test reads.
        var cfgDir = Path.GetDirectoryName(cfgPath)!;
        Directory.CreateDirectory(Path.Combine(cfgDir, "inbox"));
        var window = BuildSettingsWindow(vm);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            vm.Inbox = "inbox";                 // relative -> informational, once the debounced probe settles
            var note = Descendants<TextBlock>(window).First(t =>
                BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(vm.InboxNote));
            PumpUntil(() => note.Text.Contains("relative", StringComparison.Ordinal),
                () => $"InboxNote never settled on the relative-info text; last seen \"{note.Text}\"");
            window.UpdateLayout();
            PumpRender();
            Assert.Contains("relative", note.Text);

            var fg = ToRgb(note.Foreground);
            var (bg, source) = NearestPaintedBackground(note);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"{schemeKey}: subtle note {fg} on {source} {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(cfgDir, true); } catch (IOException) { }
        }
    });

    /// <summary>One per-field note: which tab it lives on, the view-model
    /// property carrying its text, and two states it can really be driven
    /// into through production logic — never a literal copied out of the
    /// XAML.</summary>
    private sealed record NoteCase(
        string Tab, string TextProperty,
        Action<SettingsViewModel> First, string FirstText, bool FirstNeedsAttention,
        Action<SettingsViewModel> Then, string ThenText, bool ThenNeedsAttention);

    /// <summary>The severity mechanism is an EIGHT-way coupling between XAML
    /// and C#: one shared <c>NoteText</c> style carries the amber trigger, and
    /// each of the eight note TextBlocks has to hand it its own flag through
    /// <c>Tag="{Binding …NoteNeedsAttention}"</c>. Drop one of those eight
    /// attributes in a later edit and nothing throws: the binding simply
    /// isn't there, Tag stays null, the trigger never fires, and that one note
    /// is permanently subtle — silently de-emphasising, for instance, History
    /// database's missing-folder warning or a data file's ConfigException
    /// parse error. The narrower predecessor of this test only ever exercised
    /// InboxNote, so seven of the eight were unguarded.
    ///
    /// Everything here is RESOLVED RUNTIME STATE, the same standard the tab
    /// mnemonics are held to: the TextBlocks are located in a real
    /// SettingsWindow by their Text BINDING PATH, the flag is read off the
    /// live view model by that path's name (so a note whose property was
    /// renamed or deleted fails rather than being skipped), the Tag binding's
    /// own path is checked to be the matching flag (which colour alone cannot
    /// catch when two notes are the same severity), and the colour is the
    /// brush the element actually resolved.
    ///
    /// Both states of every note are driven through production logic. Seven
    /// reach amber. <c>NamesFileNote</c> deliberately cannot: its "file
    /// doesn't exist yet" branch is Info by design (the field is optional and
    /// the file is written the first time a name is learned), and asserting
    /// that here is what stops a later edit from quietly promoting it.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void EveryFieldNoteCarriesItsOwnSeverityToItsRenderedColour(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);
        var vm = BuildSettingsVm(scheme.Palette, out var cfgPath, probeDelayMs: 0);
        var cfgDir = Path.GetDirectoryName(cfgPath)!;
        Directory.CreateDirectory(cfgDir);
        // never created: the "folder doesn't exist" branches need a real miss
        var absent = Path.Combine(Path.GetTempPath(), "ordo_absent_" + Guid.NewGuid());
        foreach (var name in new[] { "destinations", "folders", "alerts", "labels" })
            File.WriteAllText(Path.Combine(cfgDir, $"broken-{name}.json"), "{ not json");
        // 2026-08 audit finding C2: Inbox/Deferred's relative-info branches
        // below ("inbox", "set-aside") are real folders now that a relative
        // value's existence is checked beside config.json rather than never
        // checked at all — create them so those branches stay Info, not a
        // "folder doesn't exist" regression neither case intends to cover.
        Directory.CreateDirectory(Path.Combine(cfgDir, "inbox"));
        Directory.CreateDirectory(Path.Combine(cfgDir, "set-aside"));

        const string relative = "relative — resolved beside the config file";
        var cases = new[]
        {
            new NoteCase("General", nameof(vm.InboxNote),
                v => v.Inbox = "inbox", relative, false,
                v => v.Inbox = "", "no inbox folder set", true),
            new NoteCase("General", nameof(vm.DeferredNote),
                v => v.Deferred = "set-aside", relative, false,
                v => v.Deferred = absent, "folder doesn't exist", true),
            // the one note with no problem branch at all — see the class doc
            new NoteCase("General", nameof(vm.NamesFileNote),
                v => v.NamesFile = "names-list.txt", relative, false,
                v => v.NamesFile = Path.Combine(absent, "names.txt"),
                "file doesn't exist yet", false),
            new NoteCase("General", nameof(vm.HistoryDbNote),
                v => v.HistoryDb = "history-db.sqlite",
                "relative — kept beside the config file", false,
                v => v.HistoryDb = Path.Combine(absent, "history.sqlite"),
                "folder doesn't exist", true),
            new NoteCase("Data files", nameof(vm.DestinationsFileNote),
                v => v.DestinationsFile = "", "blank = the default beside config.json", false,
                v => v.DestinationsFile = "broken-destinations.json", "is not valid JSON", true),
            new NoteCase("Data files", nameof(vm.MonitoredFoldersFileNote),
                v => v.MonitoredFoldersFile = "", "blank = the default beside config.json", false,
                v => v.MonitoredFoldersFile = "broken-folders.json", "is not valid JSON", true),
            new NoteCase("Data files", nameof(vm.AlertsFileNote),
                v => v.AlertsFile = "", "blank = the default beside config.json", false,
                v => v.AlertsFile = "broken-alerts.json", "is not valid JSON", true),
            new NoteCase("Data files", nameof(vm.BoxLabelsFileNote),
                v => v.BoxLabelsFile = "", "blank = the default beside config.json", false,
                v => v.BoxLabelsFile = "broken-labels.json", "is not valid JSON", true),
        };

        var window = BuildSettingsWindow(vm);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            AssertPhase(window, vm, cases, scheme, second: false);
            AssertPhase(window, vm, cases, scheme, second: true);
        }
        finally
        {
            window.Close();
            try { Directory.Delete(cfgDir, true); } catch (IOException) { }
        }
    });

    private static string NoteText(SettingsViewModel vm, string property) =>
        (string)(typeof(SettingsViewModel).GetProperty(property)
            ?? throw new InvalidOperationException($"SettingsViewModel has no {property}"))
            .GetValue(vm)!;

    private static bool NoteFlag(SettingsViewModel vm, string property) =>
        (bool)(typeof(SettingsViewModel).GetProperty(property + "NeedsAttention")
            ?? throw new InvalidOperationException(
                $"SettingsViewModel has no {property}NeedsAttention — the severity " +
                $"flag {property}'s note binds to Tag no longer exists"))
            .GetValue(vm)!;

    /// <summary>Drive all eight notes into one of their two states at once,
    /// wait for the off-thread probes to land, then read every note's rendered
    /// colour tab by tab (a TabControl only realises the SELECTED tab's
    /// content, so the four General notes and the four Data files notes are
    /// never in the visual tree at the same moment). Every mismatch is
    /// collected before failing, so one run names all of them.</summary>
    private static void AssertPhase(Window window, SettingsViewModel vm,
        NoteCase[] cases, ThemeScheme scheme, bool second)
    {
        (string Text, bool Amber) Expected(NoteCase c) =>
            second ? (c.ThenText, c.ThenNeedsAttention) : (c.FirstText, c.FirstNeedsAttention);

        foreach (var c in cases) (second ? c.Then : c.First)(vm);

        PumpUntil(
            () => cases.All(c =>
            {
                var (text, amber) = Expected(c);
                return NoteText(vm, c.TextProperty).Contains(text, StringComparison.Ordinal)
                       && NoteFlag(vm, c.TextProperty) == amber;
            }),
            () => $"notes never settled ({(second ? "second" : "first")} state): " +
                  string.Join("; ", cases.Select(c =>
                      $"{c.TextProperty}=\"{NoteText(vm, c.TextProperty)}\"" +
                      $"/{NoteFlag(vm, c.TextProperty)}")));

        var palette = scheme.Palette;
        var tabControl = Descendants<TabControl>(window).First();
        var tabs = tabControl.Items.Cast<TabItem>().ToList();
        var problems = new List<string>();

        foreach (var tabName in cases.Select(c => c.Tab).Distinct())
        {
            tabControl.SelectedItem = tabs.First(t =>
                (t.Header?.ToString() ?? "").Replace("_", "") == tabName);
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var notes = Descendants<TextBlock>(window)
                .Select(t => (Block: t,
                    Path: BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path))
                .Where(x => x.Path is not null)
                .ToDictionary(x => x.Path!, x => x.Block, StringComparer.Ordinal);

            foreach (var c in cases.Where(c => c.Tab == tabName))
            {
                var (text, amber) = Expected(c);
                var where = $"{scheme.Key} {c.TextProperty}";
                if (!notes.TryGetValue(c.TextProperty, out var note))
                {
                    problems.Add($"{where}: no TextBlock on the \"{tabName}\" tab binds Text to it");
                    continue;
                }

                if (!note.Text.Contains(text, StringComparison.Ordinal))
                    problems.Add($"{where}: rendered \"{note.Text}\", expected to contain \"{text}\"");
                if (note.Visibility != Visibility.Visible)
                    problems.Add($"{where}: {note.Visibility}, so nothing is read at all");

                // the Tag binding is what hands this note's own severity to the
                // one shared style's trigger; a dropped attribute leaves it null
                var tagPath = BindingOperations.GetBinding(note, FrameworkElement.TagProperty)?.Path.Path;
                if (tagPath != c.TextProperty + "NeedsAttention")
                    problems.Add($"{where}: Tag is bound to \"{tagPath ?? "(nothing)"}\", " +
                                 $"expected \"{c.TextProperty}NeedsAttention\"");
                if (note.Tag is not bool flag) problems.Add($"{where}: Tag resolved to " +
                    $"{note.Tag?.ToString() ?? "null"}, so the amber trigger can never match");
                else if (flag != amber)
                    problems.Add($"{where}: Tag carries {flag}, view model says {amber}");

                var expected = amber ? palette.StatusAmber : palette.SubtleText;
                var actual = ToRgb(note.Foreground);
                if (expected != actual)
                    problems.Add($"{where}: \"{note.Text}\" resolved {actual}, expected " +
                                 $"{(amber ? "StatusAmber" : "SubtleText")} {expected}");
            }
        }

        Assert.True(problems.Count == 0,
            $"{(second ? "second" : "first")} state:\n  " + string.Join("\n  ", problems));
    }

    // ------------------------------- I6: the dialog's promise about crash.log

    /// <summary>The dialog for an unexpected filing fault ends "The technical
    /// details were written to crash.log, beside your config file" — and since
    /// this task removed the raw exception text from the dialog itself, that
    /// channel is now the ONLY way a developer ever sees what actually threw.
    /// It hangs on one line in MainWindow's constructor
    /// (<c>Shell.UnexpectedError += App.LogCrash;</c>). Delete it in a later
    /// refactor and every test still passes while the dialog keeps promising a
    /// file that is never written.
    ///
    /// Measured against a REAL MainWindow, not a re-creation of its wiring:
    /// the handler is read off the live ShellViewModel's event field and
    /// invoked, and crash.log is then read back. (Reflection is the only way
    /// in — a field-like event can only be raised by its declaring type — but
    /// what it reads is the actual subscription list the app built.) The
    /// window is never Show()n, so its Loaded handler never starts a real
    /// WebView2 environment; the same trick TriageWindowDisposalTests uses.
    /// App's own DispatcherUnhandledException handler needs no equivalent —
    /// it calls LogCrash inline rather than through an event.</summary>
    [Fact]
    public void TheShellsUnexpectedErrorChannelReallyReachesCrashLog() => _fx.Invoke(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "ordo_crashlog_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var cfg = new Config
        {
            Inbox = Path.Combine(dir, "inbox"),
            HistoryDb = Path.Combine(dir, "history.sqlite"),
        };
        Directory.CreateDirectory(cfg.Inbox);
        var previousCrashDir = App._crashDir;
        App._crashDir = dir;

        var window = new MainWindow(cfg, Path.Combine(dir, "config.json"))
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            var subscribers = (Action<Exception>?)typeof(ShellViewModel)
                .GetField(nameof(ShellViewModel.UnexpectedError),
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window.Shell);

            Assert.True(subscribers is not null,
                "nothing is subscribed to ShellViewModel.UnexpectedError — the dialog " +
                "promises crash.log, so the raw exception has nowhere left to go");

            subscribers!(new InvalidOperationException("the detail the dialog no longer shows"));

            var log = Path.Combine(dir, "crash.log");
            Assert.True(File.Exists(log), $"no crash.log was written to {dir}");
            var text = File.ReadAllText(log);
            Assert.Contains("the detail the dialog no longer shows", text);
            Assert.Contains(nameof(InvalidOperationException), text);
        }
        finally
        {
            window.Close();
            App._crashDir = previousCrashDir;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var i = 0; i < 10; i++)
            {
                try { Directory.Delete(dir, true); break; } catch (IOException) { Thread.Sleep(50); }
            }
        }
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
