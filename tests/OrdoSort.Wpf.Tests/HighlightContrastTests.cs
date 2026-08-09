using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>A no-op IDialogService for constructing real production view
/// models (SettingsViewModel, LabelMakerViewModel) off-screen: none of the
/// ListBox-selection tests below ever exercise a dialog, so every member is
/// a harmless no-op/null rather than a mock that would need maintaining.</summary>
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

/// <summary>Declares the shared collection every consumer of
/// <see cref="HighlightContrastFixture"/> must join via
/// <c>[Collection(HighlightContrastTests.Name)]</c> instead of its own
/// <c>IClassFixture&lt;HighlightContrastFixture&gt;</c>: xunit constructs an
/// <c>ICollectionFixture</c> exactly ONCE per collection (shared by every
/// class in it) and never runs two classes in the SAME collection in
/// parallel with each other — both properties this fixture's single
/// dedicated STA thread and process-wide <see cref="Application"/> depend on.
/// See <see cref="HighlightContrastFixture"/>'s own class doc for the two
/// distinct crashes reproduced without this.</summary>
[CollectionDefinition(HighlightContrastTests.Name)]
public sealed class HighlightContrastCollection : ICollectionFixture<HighlightContrastFixture>
{
}

/// <summary>Hosts the one WPF <see cref="Application"/> a process may create,
/// with the real <c>Theme/Styles.xaml</c> merged in exactly as <c>App.xaml</c>
/// does — so every test in this class resolves the SAME DynamicResource
/// brushes (Theme.Text, Theme.Accent, …) the shipped app does. Deliberately
/// does NOT instantiate <see cref="App"/> itself: that subclass's
/// <c>OnStartup</c> override fires the moment anything pumps this thread's
/// dispatcher (confirmed in the 2026-08-02 Task 1 investigation) and tries to
/// load a config + open a real MainWindow/SQLite history — none of which this
/// suite needs just to read a resolved brush, and all of which would turn one
/// contrast assertion into a fragile, slow integration test forever. A bare
/// <see cref="Application"/> only raises the (unhandled, so harmless) base
/// <c>Startup</c> event.
///
/// Everything runs on one dedicated, persistent STA thread: merely
/// constructing a <see cref="System.Windows.Controls.Control"/> touches
/// <c>InputManager</c>/<c>KeyboardNavigation</c>, which throws
/// "The calling thread must be STA" on an ordinary xunit worker thread even
/// with no Window ever shown — confirmed empirically here (not assumed).
/// One thread for the whole fixture (not one per test) because
/// <see cref="Application"/> may only be constructed once per process — and,
/// just as importantly, because it's a <c>DispatcherObject</c> pinned to
/// whichever thread creates it: every consumer must reuse this SAME instance
/// (and therefore this same thread's <see cref="Dispatcher"/>), never spin up
/// a second one. <see cref="HighlightContrastCollection"/> (above) is what
/// enforces that: two test classes each independently declaring
/// <c>IClassFixture&lt;HighlightContrastFixture&gt;</c> get TWO separate
/// instances on TWO separate threads — the second thread's
/// <c>Application.Current ?? new Application(…)</c> check either races the
/// first (crashing with "Cannot create more than one
/// System.Windows.Application instance") or, if it loses that race and reuses
/// the first thread's `Application.Current`, then throws "The calling thread
/// cannot access this object because a different thread owns it" the moment
/// it touches any DispatcherObject member (e.g. <c>Application.Windows</c>)
/// from its OWN thread. Both reproduced empirically while adding
/// <see cref="DataGridStarColumnTests"/> as a second consumer (Task 1,
/// 2026-08-02) — fixed by switching every consumer from
/// <c>IClassFixture&lt;&gt;</c> to the shared collection, which guarantees
/// xunit constructs this fixture exactly once for however many test classes
/// use it.</summary>
public sealed class HighlightContrastFixture : IDisposable
{
    private readonly Thread _thread;
    public Application App { get; }
    public Dispatcher Dispatcher { get; }

    public HighlightContrastFixture()
    {
        Application? app = null;
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!app.Resources.MergedDictionaries.Any(d =>
                    d.Source is { } src && src.OriginalString.Contains("Theme/Styles.xaml")))
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    // AssemblyName is "OrdoSort" (OrdoSort.Wpf.csproj), not "OrdoSort.Wpf".
                    Source = new Uri("pack://application:,,,/OrdoSort;component/Theme/Styles.xaml"),
                });
            }
            // Same reasoning as the Styles.xaml merge above — App.xaml also
            // merges Theme/Illustrations.xaml (Phase-2 restyle, D2), and this
            // fixture deliberately never constructs the real App, so a real
            // production Window that resolves an Illustration.* StaticResource
            // (ReadyView, DoneView, LabelMakerWindow, UnlockWindow) throws
            // "resource not found" unless it's merged here too.
            if (!app.Resources.MergedDictionaries.Any(d =>
                    d.Source is { } src && src.OriginalString.Contains("Theme/Illustrations.xaml")))
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/OrdoSort;component/Theme/Illustrations.xaml"),
                });
            }
            // App.xaml declares these converters (and a couple of plain
            // values) as loose Application.Resources entries — not a
            // separate merge-able ResourceDictionary — and this fixture
            // deliberately never constructs the real App (see the class doc
            // above), so a real production Window that resolves one via
            // StaticResource throws "resource not found" unless it's wired
            // here too. This mirrors App.xaml's own <Application.Resources>
            // block verbatim: the SAME converter TYPES App.xaml uses (not a
            // reimplementation of their logic), under the same keys, added
            // only if not already present (so this stays a no-op the moment
            // a real App does get constructed on this Resources instance
            // first). Grown from just BoolToVis/ZeroToVis/FileName (enough
            // for UnlockWindow/PrintPreviewWindow) to the full App.xaml set
            // once ManageSavedWindow (PasswordStatus) and SettingsWindow
            // (InvertBool, ColorStringToBrush, SwatchCheck, the font
            // converters, …) needed constructing too.
            void AddIfMissing(string key, object value)
            {
                if (!app.Resources.Contains(key)) app.Resources[key] = value;
            }
            AddIfMissing("BoolToVis", new System.Windows.Controls.BooleanToVisibilityConverter());
            AddIfMissing("RgbToBrush", new OrdoSort.Wpf.Views.RgbToBrushConverter());
            AddIfMissing("InvertBool", new OrdoSort.Wpf.Views.InvertBoolConverter());
            AddIfMissing("ColorStringToBrush", new OrdoSort.Wpf.Views.ColorStringToBrushConverter());
            AddIfMissing("ColorStringToForeBrush", new OrdoSort.Wpf.Views.ColorStringToForeBrushConverter());
            AddIfMissing("SwatchCheck", new OrdoSort.Wpf.Views.SwatchCheckConverter());
            AddIfMissing("ZeroToVis", new OrdoSort.Wpf.Views.ZeroToVisibilityConverter());
            AddIfMissing("FileName", new OrdoSort.Wpf.Views.FileNameConverter());
            AddIfMissing("PasswordStatus", new OrdoSort.Wpf.Views.PasswordStatusConverter());
            AddIfMissing("FontFamilyString", new OrdoSort.Wpf.Views.FontFamilyStringConverter());
            AddIfMissing("FontSizeText", new OrdoSort.Wpf.Views.FontSizeTextConverter());
            AddIfMissing("NoneSentinel", new OrdoSort.Wpf.Views.NoneSentinelConverter());
            AddIfMissing("AppFontFamily", new FontFamily("Segoe UI Variable Text, Segoe UI"));
            AddIfMissing("AppFontSize", 14.0);
            dispatcher = Dispatcher.CurrentDispatcher;
            // ready must be set from ON this thread, after Dispatcher.CurrentDispatcher
            // exists, but BEFORE Dispatcher.Run() blocks it.
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        App = app!;
        Dispatcher = dispatcher!;
    }

    /// <summary>Marshal a test body onto the fixture's STA thread and rethrow
    /// there with the original type/stack intact, so a failing xunit
    /// Assert.True surfaces as this test's own failure, not a wrapped one.</summary>
    public void Invoke(Action body)
    {
        ExceptionDispatchInfo? captured = null;
        Dispatcher.Invoke(() =>
        {
            try { body(); }
            catch (Exception ex) { captured = ExceptionDispatchInfo.Capture(ex); }
        });
        captured?.Throw();
    }

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

/// <summary>The highlighted-row text-contrast bug, reproduced directly against
/// the real <c>Theme/Styles.xaml</c> (no scratch harness, no screenshots): a
/// container's trigger correctly flips the CONTAINER's own Foreground DP on
/// IsHighlighted/IsSelected (e.g. ComboBoxItem -&gt; Theme.AccentText), but
/// WPF's plain-string Content auto-wrap builds its TextBlock via an internal,
/// special-cased template that consults the APPLICATION-level implicit
/// TextBlock style (Styles.xaml, Foreground=Theme.Text) instead of
/// inheriting the container's flipped value: a Style Setter always outranks
/// property-value inheritance. Measured in Task 1 (2026-08-02): ComboBoxItem
/// 1.35 light / 1.27 dark — both well under the WCAG AA floor of 4.5. See
/// .superpowers/sdd/2026-08-02-highlight-text-contrast/task-1-report.md.
///
/// Two independently-broken shapes exist (Task 1, Step 3): a plain-string
/// ComboBoxItem (fixable centrally, in Styles.xaml's ComboBoxItem style) and
/// an ItemTemplate-based one (NOT reachable from Styles.xaml at all —
/// ItemsControl assigns ItemTemplate to the generated container's
/// ContentTemplate as a LOCAL value, which outranks any Style Setter, so the
/// call site's own template needs the same fix). Both are covered below —
/// and for the ItemTemplate shape, by resolving the REAL production
/// templates (<c>KvpValueTemplate</c>/<c>FontChoiceTemplate</c>, both moved
/// into Theme/Styles.xaml specifically so this suite can load them by key)
/// rather than a hand-authored stand-in, so a future accidental revert of
/// either template's Foreground binding fails this suite, not just a
/// hand-copied duplicate of it.</summary>
[Collection(Name)]
public class HighlightContrastTests
{
    /// <summary>Shared with every other test class that needs the same
    /// <see cref="HighlightContrastFixture"/> (currently also
    /// <see cref="DataGridStarColumnTests"/>) via <c>[Collection(Name)]</c> —
    /// see <see cref="HighlightContrastCollection"/> and the fixture's own
    /// class doc for why a second, independent instance is unsafe.</summary>
    public const string Name = "HighlightContrastFixture collection";

    private readonly HighlightContrastFixture _fx;
    public HighlightContrastTests(HighlightContrastFixture fx) => _fx = fx;

    public static IEnumerable<object[]> ComboBoxShapes()
    {
        foreach (var s in ThemePalette.Schemes)
        {
            yield return new object[] { "plain-string", s.Key };
            yield return new object[] { "KvpValueTemplate", s.Key };
            yield return new object[] { "FontChoiceTemplate", s.Key };
        }
    }

    /// <summary>Covers all shapes Task 1 found broken. "plain-string" mirrors
    /// MainWindow.xaml's/BulkRenameWindow.xaml's `&lt;ComboBoxItem
    /// Content="…"/&gt;` (no ItemTemplate — the fix lives in Styles.xaml's
    /// ComboBoxItem style, so this case exercises the REAL Styles.xaml,
    /// merged above, unmodified by this test file). "KvpValueTemplate" and
    /// "FontChoiceTemplate" are resolved BY KEY straight out of that same
    /// loaded <c>Theme/Styles.xaml</c> — the actual `DataTemplate` resources
    /// SettingsWindow.xaml's naming-mode/process-order pickers and font
    /// picker use via `ItemTemplate="{StaticResource …}"` — and applied as a
    /// standalone ComboBoxItem's local `ContentTemplate` (the exact same WPF
    /// property-value precedence `ItemsControl.PrepareContainerForItemOverride`
    /// creates internally) rather than via a real ComboBox+Popup, since the
    /// bug is a pure DependencyProperty-precedence conflict that doesn't care
    /// who set the local value. Because these are the PRODUCTION template
    /// objects (not a copy), stripping either one's Foreground binding in
    /// SettingsWindow.xaml/Styles.xaml — Task 1's most important finding,
    /// the call-site shape a Styles.xaml-only fix cannot reach — fails this
    /// test directly; proven by temporarily doing exactly that (see the
    /// task-2 follow-up report for the pasted failing output).</summary>
    [Theory, MemberData(nameof(ComboBoxShapes))]
    public void HighlightedComboBoxItemTextMeetsWcagAa(string shape, string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var container = new ComboBoxItem();
        if (shape == "plain-string")
        {
            container.Content = "Failed queues";
        }
        else
        {
            // shape is a resource key ("KvpValueTemplate"/"FontChoiceTemplate")
            // in the real, loaded Theme/Styles.xaml -- not a copy.
            container.Content = new KeyValuePair<string, string>("k", "Failed queues");
            container.ContentTemplate = (DataTemplate)_fx.App.Resources[shape];
        }

        Realize(container);
        ForceHighlighted(container);
        Realize(container);

        var text = FindTextElement(container)
            ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under ComboBoxItem");
        var bd = FindDescendant<Border>(container)
            ?? throw new InvalidOperationException("no Border ('Bd') descendant under ComboBoxItem");

        var fg = ToRgb(ForegroundOf(text));
        var bg = ToRgb(bd.Background);
        var ratio = ThemePalette.ContrastRatio(fg, bg);
        Assert.True(ratio >= 4.5,
            $"ComboBoxItem {shape} ({schemeKey}): {fg} on {bg} = {ratio:F2}");
    });

    /// <summary>UNRESOLVED-CONFLICT investigation (Task 2 brief), later
    /// CORRECTED after independent re-verification: re-measure the MenuItem
    /// submenu Header in a REAL menu, declared the way the app declares it
    /// (MainWindow.xaml: an access-keyed `Header="_Tools"` TopLevelHeader
    /// containing a `Header="_…"` SubmenuItem), rather than trusting Task 1's
    /// synthetic harness or the 2026-08-01 QC round.
    ///
    /// Establishing which element actually paints the glyphs (per the brief)
    /// took an extra step, and an earlier version of this file got it wrong
    /// in a way worth recording: an access-keyed Header resolves to
    /// <see cref="System.Windows.Controls.AccessText"/>, which does NOT
    /// derive from TextBlock. A naive "first TextBlock descendant" walk
    /// silently drills PAST the real AccessText into a private,
    /// always-empty child TextBlock it builds internally, reading that
    /// decoy's unrelated (Theme.Text-pinned) Foreground instead of the real
    /// one — which is what the ORIGINAL version of this test did, producing
    /// a false "1.35/1.27, FAILS" reading that looked identical to the real
    /// ComboBoxItem bug and led to an unnecessary `HeaderTemplate` Setter
    /// being added to Styles.xaml's MenuItem style. Independent re-verification
    /// against a build with zero occurrences of "HeaderTemplate" in the
    /// compiled OrdoSort.dll (i.e. provably pre-fix) — using the CORRECTED
    /// <see cref="FindTextElement"/> below, which stops at EITHER a TextBlock
    /// or an AccessText instead of always drilling to a TextBlock leaf —
    /// found the auto-generated AccessText was ALREADY correct:
    /// 12.89:1 light / 11.48:1 dark, PASSING. Root cause of why it was never
    /// broken: Styles.xaml has no AccessText-specific implicit style, and
    /// implicit-style lookup only walks an element's own .NET base-type
    /// chain — AccessText's doesn't include TextBlock — so the "Setter
    /// outranks inheritance" trap that breaks ComboBoxItem's plain-string
    /// Content has no mechanism to reach AccessText at all; its Foreground
    /// simply inherits from the MenuItem, which the MenuSubmenuItem
    /// template's IsHighlighted trigger already flips correctly. The
    /// `HeaderTemplate` Setter was reverted from Styles.xaml as a no-op; this
    /// test case is kept as a regression guard and passes with or without
    /// it (verified both ways).</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void HighlightedMenuSubmenuHeaderTextMeetsWcagAa(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var menu = new Menu();
        var topLevel = new MenuItem { Header = "_Tools" };
        var child = new MenuItem { Header = "_Unlock PDFs…" };
        topLevel.Items.Add(child);
        menu.Items.Add(topLevel);

        // A real off-screen Window + a genuinely OPENED submenu Popup (not a
        // "loose" element, unlike the ComboBoxItem cases above): MenuItem's
        // Header ContentPresenter only finishes generating/binding its
        // content once the submenu is actually open in a live PresentationSource.
        var window = new Window
        {
            Content = menu, Width = 300, Height = 200,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            topLevel.IsSubmenuOpen = true;
            PumpRender();
            window.UpdateLayout();
            ForceHighlighted(child);
            PumpRender();
            window.UpdateLayout();

            // Sanity: same Role MainWindow.xaml's own nested MenuItems resolve to
            // (a TopLevelHeader's non-header child), so this is really exercising
            // the MenuSubmenuItem ControlTemplate — not, say, TopLevelItem.
            Assert.Same(_fx.App.Resources["MenuSubmenuItem"], child.Template);

            var text = FindTextElement(child)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under submenu MenuItem");
            var bd = FindDescendant<Border>(child)
                ?? throw new InvalidOperationException("no Border ('Bd') descendant under submenu MenuItem");

            var fg = ToRgb(ForegroundOf(text));
            var bg = ToRgb(bd.Background);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"MenuItem submenu Header ({schemeKey}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);


    // ---------------------------------------------------------------- ListBox

    /// <summary>Task 2 (theme-coverage audit, 2026-08-02): Styles.xaml has
    /// ZERO ListBoxItem style, so selection currently renders through stock
    /// WPF's default (Aero2) ListBoxItem template — the audit measured that
    /// at 8.50-17.44:1, i.e. it ALREADY PASSES WCAG AA. A contrast-only
    /// assertion therefore cannot fail here and would prove nothing; the
    /// actual defect is BRAND, not contrast: the selected background is
    /// stock Aero blue, not Theme.Accent, disagreeing with DataGridCell
    /// (below in Styles.xaml) which already selects correctly. This test
    /// asserts the resolved colours EQUAL the palette's Accent/AccentText
    /// outright, which the stock blue fails regardless of its contrast
    /// ratio.
    ///
    /// Uses a loose ListBoxItem (no owning ListBox needed — unlike
    /// ComboBoxItem/MenuItem's read-only IsHighlighted above,
    /// Selector.IsSelectedProperty is a plain public read/write DP, so no
    /// reflection hack is needed to force it), the same
    /// Realize-then-flip-then-Realize shape as
    /// HighlightedComboBoxItemTextMeetsWcagAa.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void SelectedListBoxItemUsesTheAccentPalette(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var container = new ListBoxItem { Content = "Invoices" };
        Realize(container);
        container.IsSelected = true;
        Realize(container);

        var text = FindTextElement(container)
            ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under ListBoxItem");
        var bd = FindDescendant<Border>(container)
            ?? throw new InvalidOperationException("no Border descendant under ListBoxItem");

        var fg = ToRgb(ForegroundOf(text));
        var bg = ToRgb(bd.Background);
        Assert.Equal(p.Accent, bg);
        Assert.Equal(p.AccentText, fg);
        var ratio = ThemePalette.ContrastRatio(fg, bg);
        Assert.True(ratio >= 4.5,
            $"ListBoxItem selected ({schemeKey}): {fg} on {bg} = {ratio:F2}");
    });

    /// <summary>Closes a gap the synthetic case above can't: every real
    /// ListBox in this app (SettingsWindow's route/watch lists, LabelMaker's
    /// client list, ManageSavedWindow, UnlockWindow's file list) supplies its
    /// OWN ItemTemplate with hand-authored label TextBlocks, declared in
    /// each window's own XAML — not a resource this suite can pull by key
    /// the way ComboBoxItem's KvpValueTemplate/FontChoiceTemplate can (those
    /// live in Styles.xaml specifically so they could be). An ItemTemplate's
    /// TextBlock is exactly as vulnerable to the "Style Setter outranks
    /// inheritance" trap documented throughout this file: without a LOCAL
    /// Foreground binding back to its ListBoxItem, it resolves the
    /// application-level implicit TextBlock style (Theme.Text) no matter
    /// what the container's own Foreground is. Before this fix that
    /// coincidentally read fine against stock Aero blue (near-black Text in
    /// light mode, near-white Text in dark mode, both contrast acceptably
    /// against a mid-tone blue) — but this app's dark-mode Accent is itself
    /// a near-white light grey, so an unfixed label would go from
    /// "accidentally passing" to genuinely illegible the moment the
    /// selected background becomes Theme.Accent. UnlockWindow's FileList is
    /// the cheapest real window to build (a bare <see cref="Config"/>, no
    /// on-disk fixture needed), so this constructs the REAL, compiled window
    /// and resolves its REAL FileList.ItemTemplate TextBlock via the visual
    /// tree (never a named field — UnlockWindow's x:Name fields are
    /// internal to OrdoSort.Wpf, which this test project has no
    /// InternalsVisibleTo into), proving the local-Foreground-binding fix
    /// added to UnlockWindow.xaml reaches what actually ships.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void SelectedUnlockFileListRowUsesTheAccentPalette(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new UnlockViewModel(new Config(), () => true);
        vm.Files.Add(new UnlockFileRow(@"C:\inbox\20240101--1111111111.pdf"));
        var window = new UnlockWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under UnlockWindow");
            listBox.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("FileList row 0 never realized a container");

            var text = FindTextElement(container)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under FileList's ListBoxItem");
            var bd = FindDescendant<Border>(container)
                ?? throw new InvalidOperationException("no Border descendant under FileList's ListBoxItem");

            var fg = ToRgb(ForegroundOf(text));
            var bg = ToRgb(bd.Background);
            Assert.Equal(p.Accent, bg);
            Assert.Equal(p.AccentText, fg);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"UnlockWindow FileList selected row ({schemeKey}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Shared body for all four non-Pending/NotEncrypted readiness
    /// suffixes (status-colour-vocabulary plan, 2026-08-08, Task 1 Step 4).
    /// Updated, not replaced: this used to be
    /// AssertSelectedUnlockFileListRowContrast, reading a SINGLE bound
    /// TextBlock (DisplayText) via FindTextElement. Task 1 Step 3 split
    /// FileList's ItemTemplate into two TextBlocks — FileName (unchanged
    /// ancestor Foreground binding) and Note (the vocabulary colour, unless
    /// selected) — so this now reads BOTH via FindAllDescendants&lt;TextBlock&gt;
    /// (the same technique ManageSavedPasswordStatusStaysSubtleUnlessSelected
    /// below already uses to tell two same-typed elements apart) and takes a
    /// `selected` parameter the four original tests never had.
    ///
    /// Selected re-asserts exactly what the four ORIGINAL tests already
    /// asserted (both elements clear 4.5:1 against the Accent background) —
    /// proving the split didn't regress the selected-row contract the
    /// ancestor binding exists for. Only a RATIO check is made for the
    /// note here, deliberately no colour-equality check: Step 5's teeth
    /// proof breaks the "let selection win" trigger and the failure needs to
    /// read as a dropped ratio (1.28-2.51:1 measured across all four
    /// statuses, both palettes — matches UnlockWindow.xaml:90's own record
    /// of the same proof), not a value mismatch, per the plan's own
    /// instruction not to let a teeth proof fail for the wrong reason.
    /// This range used to be printed here as "1.28-3.58:1", disagreeing
    /// with UnlockWindow.xaml:90 (status-colour-vocabulary plan, 2026-08-08,
    /// Task 3 Part C investigated the mismatch rather than just picking one
    /// number): 3.58 was real, not a typo — independent WCAG math against
    /// this file's own RGBs reproduces it exactly as Theme.Danger
    /// ((192,57,43), identical in both palettes) against Dark
    /// Theme.Accent ((205,210,218)) = 3.581:1. That pairing was live for
    /// this exact teeth proof for one intermediate stretch of Task 1: the
    /// Unreadable note's trigger was first written pointing at Theme.Danger
    /// (the obvious first choice for "couldn't be read"), and ONLY THEN,
    /// separately, was Theme.Danger found to fail 4.5:1 as foreground text
    /// against Theme.Surface too (2.69:1 — see ThemePalette.cs's StatusRed
    /// comment and UnlockWindow.xaml's own "found while building this note"
    /// remark), which is what StatusRed was added to fix. Once Unreadable's
    /// trigger was switched to Theme.StatusRed, re-running this SAME proof
    /// against the now-current code drops the dark-mode Unreadable value
    /// from 3.58 (Danger vs Accent) to 1.97 (StatusRed vs Accent), so the
    /// four-status/two-palette range's true maximum is light-mode
    /// StatusGreen vs Accent at 2.51 — the number this comment now carries.
    /// 3.58 does not describe anything shipping today; it is corrected here,
    /// not just harmonised with the other comment, because the two
    /// disagreeing numbers were each accurate for a different snapshot of
    /// the same trigger and only one of those snapshots survived.
    ///
    /// Unselected is new: the note must render the exact vocabulary colour
    /// for its status (StatusGreen/StatusAmber/Danger, palette-resolved) at
    /// >=4.5:1 against the ListBox's REAL background — Theme.Surface, which
    /// is what Styles.xaml's ListBox style actually paints behind an
    /// unselected, transparent-background ListBoxItem — while the filename
    /// stays untouched. This is the assertion the pre-split tests had no way
    /// to make at all: DisplayText was one string, so a fixed colour on it
    /// would have painted the filename too.</summary>
    private void AssertUnlockFileListNoteContrast(string schemeKey, bool selected, ReadinessStatus status,
        string message, string expectedNoteFragment, Func<ThemePalette, Rgb> expectedNoteColorWhenUnselected)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new UnlockViewModel(new Config(), () => true);
        var row = new UnlockFileRow(@"C:\inbox\20240101--1111111111.pdf");
        row.SetProbeResult(status, message);
        vm.Files.Add(row);
        Assert.Contains(expectedNoteFragment, row.Note);   // sanity: real suffix text is in play (was row.DisplayText pre-split)
        var window = new UnlockWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under UnlockWindow");
            listBox.SelectedIndex = selected ? 0 : -1;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("FileList row 0 never realized a container");

            var textBlocks = FindAllDescendants<TextBlock>(container);
            Assert.True(textBlocks.Count >= 2,
                $"expected FileName + Note TextBlocks, found {textBlocks.Count}");
            var fileNameFg = ToRgb(textBlocks[0].Foreground);
            var noteFg = ToRgb(textBlocks[1].Foreground);

            if (selected)
            {
                var bd = FindDescendant<Border>(container)
                    ?? throw new InvalidOperationException("no Border descendant under FileList's ListBoxItem");
                var bg = ToRgb(bd.Background);
                Assert.Equal(p.Accent, bg);
                Assert.Equal(p.AccentText, fileNameFg);
                var fileNameRatio = ThemePalette.ContrastRatio(fileNameFg, bg);
                Assert.True(fileNameRatio >= 4.5,
                    $"UnlockWindow FileList selected filename, {status} ({schemeKey}): {fileNameFg} on {bg} = {fileNameRatio:F2}");
                // Ratio only, deliberately (see class doc above): this is the
                // assertion Step 5's teeth proof breaks.
                var noteRatio = ThemePalette.ContrastRatio(noteFg, bg);
                Assert.True(noteRatio >= 4.5,
                    $"UnlockWindow FileList selected note, {status} ({schemeKey}): {noteFg} on {bg} = {noteRatio:F2}");
            }
            else
            {
                var expectedNote = expectedNoteColorWhenUnselected(p);
                Assert.Equal(expectedNote, noteFg);
                var noteRatio = ThemePalette.ContrastRatio(noteFg, p.Surface);
                Assert.True(noteRatio >= 4.5,
                    $"UnlockWindow FileList unselected note, {status} ({schemeKey}): {noteFg} on {p.Surface} = {noteRatio:F2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void UnlockFileListReadyNoteIsGreenUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertUnlockFileListNoteContrast(schemeKey, selected, ReadinessStatus.Ready,
            "A saved password opens this.", "a saved password opens this", p => p.StatusGreen));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void UnlockFileListNeedsPasswordNoteIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertUnlockFileListNoteContrast(schemeKey, selected, ReadinessStatus.NeedsPassword,
            "This PDF needs a password none of the saved ones supply.", "needs a password", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void UnlockFileListInUseNoteIsAmberUnlessSelected(string schemeKey, bool selected) =>
        _fx.Invoke(() => AssertUnlockFileListNoteContrast(schemeKey, selected, ReadinessStatus.InUse,
            "It's open in another program — close it there and try again.", "in use, couldn't check", p => p.StatusAmber));

    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void UnlockFileListUnreadableNoteIsDangerUnlessSelected(string schemeKey, bool selected) =>
        // p.StatusRed, NOT p.Danger: Danger AS FOREGROUND TEXT fails 4.5:1
        // against Dark.Surface (2.69:1, measured while writing this test) --
        // see ThemePalette.cs's StatusRed field comment and the Task 1 report.
        _fx.Invoke(() => AssertUnlockFileListNoteContrast(schemeKey, selected, ReadinessStatus.Unreadable,
            "Couldn't read it: The file is not a valid PDF document.", "couldn't be read", p => p.StatusRed));

    // -------------------------------------------------------- Result lines

    /// <summary>Task 3 Step 3 (status-colour-vocabulary plan, 2026-08-08):
    /// the Unlock window's results list (<see cref="UnlockResultLine"/>/
    /// <see cref="UnlockResultKind"/>) is a plain ItemsControl, not a
    /// Selector — no selection concept exists for it at all (see the
    /// comment on ResultList's enclosing Border in UnlockWindow.xaml, and
    /// x:Name="ResultList" added there so this test can resolve the REAL
    /// ItemsControl by name instead of the first-match
    /// <see cref="FindDescendant{T}"/> walk, which would find the FileList
    /// ListBox first — ListBox derives from ItemsControl too). Asserts the
    /// RESOLVED brush of the generated row's TextBlock against the
    /// ItemsControl's real background (Theme.Surface, painted by the
    /// enclosing Border) — the same resolved-not-XAML standard the FileList
    /// tests above already hold to.</summary>
    private void AssertUnlockResultLineContrast(string schemeKey, UnlockResultKind kind, string text,
        Func<ThemePalette, Rgb> expectedColor)
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new UnlockViewModel(new Config(), () => true);
        vm.ResultLines.Add(new UnlockResultLine(text, kind));
        var window = new UnlockWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var itemsControl = window.FindName("ResultList") as ItemsControl
                ?? throw new InvalidOperationException("no ItemsControl named ResultList in UnlockWindow");
            var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(0)
                ?? throw new InvalidOperationException("ResultList row 0 never realized a container");

            var textEl = FindTextElement(container)
                ?? throw new InvalidOperationException(
                    "no TextBlock/AccessText descendant under the result line's container");
            var fg = ToRgb(ForegroundOf(textEl));
            Assert.Equal(expectedColor(p), fg);
            var ratio = ThemePalette.ContrastRatio(fg, p.Surface);
            Assert.True(ratio >= 4.5,
                $"UnlockWindow ResultList {kind} ({schemeKey}): {fg} on {p.Surface} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    }

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void UnlockResultLineOkIsGreen(string schemeKey) =>
        _fx.Invoke(() => AssertUnlockResultLineContrast(schemeKey, UnlockResultKind.Ok,
            "✓  a.pdf  →  b.pdf", p => p.StatusGreen));

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void UnlockResultLineFailIsAmber(string schemeKey) =>
        _fx.Invoke(() => AssertUnlockResultLineContrast(schemeKey, UnlockResultKind.Fail,
            "✗  a.pdf — wrong password", p => p.StatusAmber));

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void UnlockResultLineSkipIsSubtle(string schemeKey) =>
        _fx.Invoke(() => AssertUnlockResultLineContrast(schemeKey, UnlockResultKind.Skip,
            "•  a.pdf — already unlocked", p => p.SubtleText));

    /// <summary>Same gap-closing purpose as the UnlockWindow test above, for
    /// LabelMakerWindow's client list. Its ItemTemplate is a DockPanel with
    /// TWO TextBlocks (NextNumberText docked right, Id filling the rest);
    /// <see cref="FindTextElement"/> returns the FIRST one found in visual-
    /// tree order, which is NextNumberText here (declared first in the
    /// DockPanel, regardless of its Dock side) — still a real, production
    /// label exercising the exact same local-Foreground-binding fix as Id,
    /// so it's an equally valid proof the row's LABEL_TEXT (not just its
    /// background) reaches Theme.AccentText when selected.
    /// LabelMakerViewModel's constructor reads/writes a box-labels JSON file
    /// by path; pointing it at a fresh temp path that's never created keeps
    /// this hermetic (BoxLabelStore.Read treats "missing file" as "no
    /// clients yet" — see its own doc comment), and the one client added
    /// here goes straight onto the public Clients collection rather than
    /// through the Add-command's Hook() dirty-tracking, so this window's
    /// Closing handler (which persists only if something's dirty) writes
    /// nothing back to that path either.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void SelectedLabelMakerClientRowUsesTheAccentPalette(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        var vm = new LabelMakerViewModel(new Config(), boxLabelsPath, new NoDialogs());
        vm.Clients.Add(new LabelClientVm { Id = "TEST" });
        var window = new LabelMakerWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under LabelMakerWindow");
            listBox.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Clients row 0 never realized a container");

            var text = FindTextElement(container)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the Clients row");
            var bd = FindDescendant<Border>(container)
                ?? throw new InvalidOperationException("no Border descendant under the Clients row");

            var fg = ToRgb(ForegroundOf(text));
            var bg = ToRgb(bd.Background);
            Assert.Equal(p.Accent, bg);
            Assert.Equal(p.AccentText, fg);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"LabelMakerWindow Clients selected row ({schemeKey}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Same gap-closing purpose again, for ManageSavedWindow's
    /// saved-password list. Its ItemTemplate is a horizontal StackPanel with
    /// TWO TextBlocks (Label, then a SubtleText-styled password-status
    /// annotation); <see cref="FindTextElement"/> returns the first —
    /// Label — here.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void SelectedManageSavedRowUsesTheAccentPalette(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new UnlockViewModel(new Config(), () => true);
        vm.Saved.Add(new SavedPassword { Label = "Test client", Password = "hunter2" });
        var window = new ManageSavedWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under ManageSavedWindow");
            listBox.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Saved row 0 never realized a container");

            var text = FindTextElement(container)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the Saved row");
            var bd = FindDescendant<Border>(container)
                ?? throw new InvalidOperationException("no Border descendant under the Saved row");

            var fg = ToRgb(ForegroundOf(text));
            var bg = ToRgb(bd.Background);
            Assert.Equal(p.Accent, bg);
            Assert.Equal(p.AccentText, fg);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"ManageSavedWindow Saved selected row ({schemeKey}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The riskiest of the five real call sites: SettingsWindow's
    /// RouteList is the ONE ListBox that sets ItemContainerStyle directly
    /// (see Styles.xaml's ListBoxItem comment) — an explicitly-assigned
    /// ItemContainerStyle replaces implicit-by-type lookup entirely for that
    /// ListBox's containers, so without BasedOn="{StaticResource {x:Type
    /// ListBoxItem}}" on that inline Style, this row would silently keep
    /// using the plain WPF default no matter what Styles.xaml says — the
    /// central style being correct would prove nothing for this specific
    /// list. This test's palette-equality assertions fail exactly that way
    /// if the BasedOn wiring is ever dropped (proven directly in the task
    /// report: temporarily removing it reproduces the stock-grey failure
    /// this whole task started from).
    /// SettingsViewModel's Routes collection is seeded straight from the
    /// Config it's given (RouteEditVm.From), so a single in-memory Route on
    /// a fresh Config is enough — no config.json on disk needed. cfgPath is
    /// a syntactically-valid but nonexistent temp path (not null): several
    /// of SettingsViewModel's OTHER tab properties dereference it with `!`,
    /// and while this test only visits the Destinations tab, a real,
    /// resolvable-but-missing path costs nothing and removes that as a
    /// future footgun.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void SelectedSettingsRouteListRowUsesTheAccentPalette(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_settings_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(),
            () => scheme.Palette, cfgPath,
            uiContext: System.Threading.SynchronizationContext.Current);
        var window = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var tabControl = FindDescendant<TabControl>(window)
                ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
            var destinationsTab = tabControl.Items.Cast<TabItem>()
                    // Task 8 gave this header an access-key marker
                    // ("_Destinations"); TabItem.Header itself is the raw
                    // literal string (RecognizesAccessKey only affects how
                    // the ContentPresenter renders it), so this comparison
                    // needs the underscore too.
                    .FirstOrDefault(ti => ti.Header?.ToString() == "_Destinations")
                ?? throw new InvalidOperationException("no \"Destinations\" TabItem found");
            tabControl.SelectedItem = destinationsTab;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            // Search the WHOLE window, not destinationsTab: TabControl hosts
            // the selected tab's content through its own ContentPresenter
            // (commonly "PART_SelectedContentHost"), which is never a visual
            // descendant of the TabItem header object itself — RouteList
            // lives under the TabControl, not under destinationsTab.
            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under SettingsWindow");
            listBox.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("RouteList row 0 never realized a container");

            var text = FindTextElement(container)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the RouteList row");
            var bd = FindDescendant<Border>(container)
                ?? throw new InvalidOperationException("no Border descendant under the RouteList row");

            var fg = ToRgb(ForegroundOf(text));
            var bg = ToRgb(bd.Background);
            Assert.Equal(p.Accent, bg);
            Assert.Equal(p.AccentText, fg);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"SettingsWindow RouteList selected row ({schemeKey}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    // ----------------------------------------------- SubtleText de-emphasis

    public static IEnumerable<object[]> PalettesAndSelection()
    {
        foreach (var s in ThemePalette.Schemes)
        foreach (var selected in new[] { false, true })
            yield return new object[] { s.Key, selected };
    }

    /// <summary>Theme-coverage final review (2026-08-02), Finding 1:
    /// NextNumberText carries Style="{StaticResource SubtleText}" AND
    /// (before this fix) the SAME blanket LOCAL Foreground binding as its
    /// sibling Id label — a LOCAL value always outranks a named style's
    /// Setter, so the SubtleText de-emphasis was silently destroyed
    /// whenever the row is unselected. The three tests in this section
    /// escaped the SelectedXxxUsesTheAccentPalette tests above because
    /// those only ever select the row first — where the bug is invisible,
    /// since both the flat binding and the correct fix resolve to
    /// Theme.AccentText once selected. This test covers BOTH states
    /// directly: unselected must equal ThemePalette.SubtleText (proving the
    /// de-emphasis actually survives), selected must equal
    /// ThemePalette.AccentText (the same contract the flat binding already
    /// gave selected rows, preserved by the DataTrigger-based fix).</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void LabelMakerNextNumberTextStaysSubtleUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        var vm = new LabelMakerViewModel(new Config(), boxLabelsPath, new NoDialogs());
        vm.Clients.Add(new LabelClientVm { Id = "TEST" });
        var window = new LabelMakerWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under LabelMakerWindow");
            listBox.SelectedIndex = selected ? 0 : -1;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Clients row 0 never realized a container");

            // NextNumberText is declared first in the DockPanel (regardless
            // of its Dock="Right" side), so it's the FIRST Text/AccessText
            // FindTextElement finds — same element the existing selected-only
            // test above already resolves.
            var text = FindTextElement(container)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the Clients row");

            var fg = ToRgb(ForegroundOf(text));
            var expected = selected ? p.AccentText : p.SubtleText;
            Assert.Equal(expected, fg);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Same Finding-1 gap-closing purpose as the LabelMakerWindow
    /// test above, for ManageSavedWindow's password-status annotation.
    /// Unlike NextNumberText/GestureText (both declared FIRST in their
    /// DockPanel), this annotation is declared SECOND in its StackPanel
    /// (after Label) — <see cref="FindTextElement"/> alone would resolve
    /// Label instead, which was never broken (it never carried
    /// Style="SubtleText"). <see cref="FindAllDescendants{T}"/> collects
    /// every TextBlock in visual-tree order so this test can specifically
    /// check the SECOND one.</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void ManageSavedPasswordStatusStaysSubtleUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new UnlockViewModel(new Config(), () => true);
        vm.Saved.Add(new SavedPassword { Label = "Test client", Password = "hunter2" });
        var window = new ManageSavedWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under ManageSavedWindow");
            listBox.SelectedIndex = selected ? 0 : -1;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Saved row 0 never realized a container");

            var textBlocks = FindAllDescendants<TextBlock>(container);
            Assert.True(textBlocks.Count >= 2,
                $"expected Label + password-status TextBlocks, found {textBlocks.Count}");
            var passwordStatus = textBlocks[1];

            var fg = ToRgb(passwordStatus.Foreground);
            var expected = selected ? p.AccentText : p.SubtleText;
            Assert.Equal(expected, fg);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Same Finding-1 gap-closing purpose again, for
    /// SettingsWindow's RouteList GestureText annotation — the riskiest of
    /// the three, since RouteList is also the one ListBox with an explicit
    /// ItemContainerStyle (see the RouteList test above).</summary>
    [Theory, MemberData(nameof(PalettesAndSelection))]
    public void SettingsRouteListGestureTextStaysSubtleUnlessSelected(string schemeKey, bool selected) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_settings_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(),
            () => scheme.Palette, cfgPath,
            uiContext: System.Threading.SynchronizationContext.Current);
        var window = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var tabControl = FindDescendant<TabControl>(window)
                ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
            var destinationsTab = tabControl.Items.Cast<TabItem>()
                    // Task 8 gave this header an access-key marker
                    // ("_Destinations"); TabItem.Header itself is the raw
                    // literal string (RecognizesAccessKey only affects how
                    // the ContentPresenter renders it), so this comparison
                    // needs the underscore too.
                    .FirstOrDefault(ti => ti.Header?.ToString() == "_Destinations")
                ?? throw new InvalidOperationException("no \"Destinations\" TabItem found");
            tabControl.SelectedItem = destinationsTab;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under SettingsWindow");
            listBox.SelectedIndex = selected ? 0 : -1;
            PumpRender();
            window.UpdateLayout();

            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("RouteList row 0 never realized a container");

            // GestureText (Dock="Right", declared FIRST in the DockPanel,
            // after the non-Text Rectangle swatch) is the FIRST
            // Text/AccessText FindTextElement finds — same element the
            // existing selected-only test above already resolves.
            var text = FindTextElement(container)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the RouteList row");

            var fg = ToRgb(ForegroundOf(text));
            var expected = selected ? p.AccentText : p.SubtleText;
            Assert.Equal(expected, fg);
        }
        finally
        {
            window.Close();
        }
    });

    // ------------------------------------------------------- DocumentViewer

    /// <summary>DocumentViewer's toolbar chrome (PrintPreviewWindow), Task 2:
    /// Styles.xaml had NO ToolBar style at all, so its "MainPanelBorder"
    /// chrome rendered stock Aero2's light-blue background (#FFEEF5FD)
    /// regardless of theme — a stark white bar across the top of an
    /// otherwise-dark window (rendered proof: printpreview-fixed-dark.png,
    /// scratchpad root). A plain implicit ToolBar Style with Background/
    /// Foreground Setters reaches it — confirmed via a visual-tree dump that
    /// MainPanelBorder's own Background is TemplateBound to ToolBar.
    /// Background — no retemplate needed here, unlike ComboBoxItem/TabItem/
    /// MenuItem elsewhere in this file.
    ///
    /// This is a PARTIAL fix, honestly short of "no stock-white region
    /// remains" — two pieces of the same chrome were investigated and found
    /// unreachable via ordinary Styles.xaml declarations, not silently
    /// dropped:
    /// (1) The Find toolbar (Ctrl+F inside the preview) is
    /// <c>MS.Internal.Documents.FindToolBar</c> — internal to the
    /// PresentationUI assembly (its Type.Assembly, confirmed by reflection,
    /// is PresentationUI, not PresentationFramework, even though it derives
    /// from the public System.Windows.Controls.ToolBar in
    /// PresentationFramework), so no XAML in this app can name its exact
    /// type to key an implicit style to it, and — confirmed by the same
    /// dump — it does NOT fall back to a plain {x:Type ToolBar} style
    /// despite deriving from ToolBar (this fix reached the real ToolBar's
    /// MainPanelBorder but left FindToolBar's identically-named
    /// MainPanelBorder at the stock colour). A reflection-based workaround
    /// (resolve FindToolBar's System.Type at runtime and register a Style
    /// under that exact key) is technically possible but wasn't taken: it
    /// would pin this app's chrome to an undocumented internal type name
    /// that could vanish on any .NET update, for a bar that only appears
    /// behind an opt-in Ctrl+F, not the first-glance defect the main
    /// toolbar was.
    /// (2) The page-layout button group (ActualSize/PageWidth/WholePage/
    /// TwoPages) keeps a stock light "chip" background. A matching
    /// {x:Static ToolBar.ButtonStyleKey} Style (the mechanism that DID fix
    /// the group separators, below) was tried and verified INERT — the same
    /// dump showed every toolbar button's Background resolving to a
    /// DrawingBrush byte-for-byte identically with or without that Setter,
    /// meaning something baked into ToolBar's own stock per-button
    /// ControlTemplate (almost certainly a template trigger) sets it
    /// directly, outranking a plain Style Setter — reaching it would need a
    /// full custom retemplate of the native per-button chrome, which risks
    /// breaking the print/zoom/layout buttons' actual glyph rendering for a
    /// cosmetic residual "chip" that reads far less jarring than the
    /// full-width bar this fix already removes.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void PrintPreviewToolBarUsesThemeChrome(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var doc = OrdoSort.Wpf.Views.LabelPrinting.BuildDocument(
            BoxLabels.Batch("ABCD", 1, 12, new DateTime(2026, 7, 25), 30));
        var window = new PrintPreviewWindow(doc, "test", _ => { })
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var toolBar = FindDescendant<ToolBar>(window)
                ?? throw new InvalidOperationException("no ToolBar descendant under PrintPreviewWindow");
            var separator = FindDescendant<Separator>(toolBar)
                ?? throw new InvalidOperationException("no Separator descendant under the toolbar");

            Assert.Equal(p.WindowBg, ToRgb(toolBar.Background));
            Assert.Equal(p.Border, ToRgb(separator.Background));
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------- Calendar

    /// <summary>The DatePicker's drop-down Calendar, reproduced directly
    /// against the real <c>Theme/Styles.xaml</c> (Task 1, 2026-08-02): before
    /// this fix, that file had ZERO styles for Calendar/CalendarItem/
    /// CalendarDayButton/CalendarButton, so the stock Aero2 theme templates
    /// were in play. Those hardcode the whole popup face — a literal light
    /// <c>LinearGradientBrush</c> background and "#FF333333" text colours,
    /// none of it resource-bound. Day numbers are worse: CalendarDayButton
    /// paints its content through a bare <see cref="ContentPresenter"/> with
    /// no ContentTemplate (the day number is a plain string), so WPF
    /// auto-wraps it into an internal TextBlock that resolves THIS app's
    /// global implicit TextBlock style (Foreground=Theme.Text) instead of
    /// the stock template's own hardcoded colour — the exact same "Style
    /// Setter outranks inheritance" trap ComboBoxItem hit above. Measured
    /// before this fix: the popup's background never left its hardcoded
    /// light gradient while Theme.Text flipped to near-white in dark mode,
    /// so day numbers rendered near-white on a near-white face: 1.12-1.95:1,
    /// both failing WCAG AA by a wide margin.
    ///
    /// Because the day button's own Background is never set anywhere (the
    /// stock style only sets MinWidth/FontSize/Template, not Background, so
    /// its Border's TemplateBinding resolves to nothing) and the ancestor
    /// Calendar's Background is a gradient (not a flat SolidColorBrush
    /// <c>ToRgb</c> can read), a DP-based "read the Border's Background"
    /// check — the ComboBoxItem test's approach above — cannot resolve a
    /// meaningful colour here. Instead this renders the real Calendar to a
    /// bitmap (RenderTargetBitmap itself works fine on a disconnected Visual
    /// with no Window/Show() — it's Calendar's own style resolution that
    /// needs one, see the second doc paragraph below) and scans ACTUAL
    /// PIXELS within the day button's bounds: the most common
    /// colour is the background (it dominates by area over a 1-2 digit
    /// glyph), and the pixel with the highest WCAG contrast against that
    /// background is the glyph's fully-opaque core — never a hand-picked
    /// coordinate, which would silently start measuring the wrong thing the
    /// moment padding or font size changes. This also correctly captures the
    /// "disabled" state's element-level Opacity dimming, which a plain
    /// Foreground DP read would miss entirely (Opacity is a render-time
    /// composite, not something the Foreground getter reflects).
    ///
    /// One more wrinkle found empirically while writing this: unlike
    /// ComboBoxItem/Button/TabItem (which this app already gives an app-level
    /// implicit style, so a "loose" ApplyTemplate+Measure+Arrange element
    /// with no Window — see <see cref="Realize"/> — is enough), Calendar
    /// currently has NO app-level style at all (that's this task), so it
    /// falls through to the SYSTEM theme's default style — and THAT lookup
    /// resolves to nothing (Style and Template both stay null, DesiredSize
    /// stays 0x0) unless the element is part of a real, live
    /// PresentationSource. Confirmed by direct comparison: identical
    /// ApplyTemplate+Measure+Arrange calls left a loose Calendar's Template
    /// null with zero visual children, while wrapping the exact same
    /// instance in a real (offscreen) Window + Show() — the MenuItem test's
    /// own technique above, for the same underlying reason — populated the
    /// full month/year grids. So this test uses that Window technique too.</summary>
    public static IEnumerable<object[]> CalendarDayStates()
    {
        foreach (var s in ThemePalette.Schemes)
        foreach (var state in new[] { "default", "today", "selected", "inactive", "disabled" })
            yield return new object[] { state, s.Key };
    }

    [Theory, MemberData(nameof(CalendarDayStates))]
    public void CalendarDayNumbersMeetWcagAa(string state, string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var calendar = new Calendar { DisplayDate = new DateTime(2024, 6, 1) };
        if (state == "today") calendar.DisplayDate = DateTime.Today;
        if (state == "selected") calendar.SelectedDate = new DateTime(2024, 6, 15);
        if (state == "disabled") calendar.IsEnabled = false;

        var window = new Window
        {
            Content = calendar, SizeToContent = SizeToContent.WidthAndHeight,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            CalendarDayButton day = (state switch
            {
                "today" => FindDayButton(calendar, b => b.IsToday),
                "selected" => FindDayButton(calendar, b => b.IsSelected),
                "inactive" => FindDayButton(calendar, b => b.IsInactive),
                "disabled" => FindDayButton(calendar, _ => true),
                _ => FindDayButton(calendar, b => !b.IsToday && !b.IsSelected && !b.IsInactive && b.IsEnabled),
            }) ?? throw new InvalidOperationException($"no CalendarDayButton found for state '{state}'");

            // Sanity: a real text element paints the day number (the
            // ComboBoxItem-style "resolve a text element" check) — the
            // actual ratio below comes from rendered pixels (see class
            // doc), which is what makes this correct for every state,
            // including "disabled"'s Opacity dimming.
            _ = FindTextElement(day)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under CalendarDayButton");

            var (fg, bg) = SampleRenderedMaxContrast(calendar, day);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"calendar day {state} ({schemeKey}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    private static CalendarDayButton? FindDayButton(DependencyObject root, Func<CalendarDayButton, bool> predicate)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CalendarDayButton day && predicate(day)) return day;
            if (FindDayButton(child, predicate) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Render <paramref name="root"/> to a bitmap and scan the
    /// pixels under <paramref name="target"/>'s bounds: the most common
    /// colour is the background (it dominates by area over a 1-2 digit
    /// glyph), and the pixel with the highest WCAG contrast against that
    /// background is the glyph's fully-opaque core — i.e. the actual
    /// rendered foreground, whatever compositing (Opacity, gradients,
    /// DynamicResource brushes) produced it.</summary>
    private static (Rgb fg, Rgb bg) SampleRenderedMaxContrast(FrameworkElement root, FrameworkElement target)
    {
        var rootW = (int)Math.Ceiling(root.ActualWidth);
        var rootH = (int)Math.Ceiling(root.ActualHeight);
        var bmp = new RenderTargetBitmap(rootW, rootH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);

        var topLeft = target.TranslatePoint(new Point(0, 0), root);
        var x0 = Math.Max(0, (int)topLeft.X);
        var y0 = Math.Max(0, (int)topLeft.Y);
        var w = Math.Min((int)Math.Ceiling(target.ActualWidth), rootW - x0);
        var h = Math.Min((int)Math.Ceiling(target.ActualHeight), rootH - y0);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"CalendarDayButton has no on-screen bounds ({w}x{h})");

        var stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(new Int32Rect(x0, y0, w, h), pixels, stride, 0);

        var counts = new Dictionary<Rgb, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var rgb = new Rgb(pixels[i + 2], pixels[i + 1], pixels[i]);   // Pbgra32: B,G,R,A
            counts[rgb] = counts.GetValueOrDefault(rgb) + 1;
        }
        var bg = counts.OrderByDescending(kv => kv.Value).First().Key;

        var bestFg = bg;
        var bestRatio = 1.0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var rgb = new Rgb(pixels[i + 2], pixels[i + 1], pixels[i]);
            var ratio = ThemePalette.ContrastRatio(rgb, bg);
            if (ratio > bestRatio) { bestRatio = ratio; bestFg = rgb; }
        }
        return (bestFg, bg);
    }

    // ------------------------------------------- ReadyView / ProcessingView

    /// <summary>Minimal duck-typed stand-in for the ShellViewModel bindings
    /// ReadyView actually reads (status-colour-vocabulary plan, 2026-08-08,
    /// Task 3 Part B). WPF's Binding engine resolves a source property by
    /// reflection on whatever object DataContext holds, not by a declared
    /// interface/base type — this test needs only the ONE render pass a
    /// fresh DataContext gets on attach, not live updates, so no
    /// INotifyPropertyChanged and no dependency on ShellViewModel's real
    /// constructor (Config/IPdfViewer/IDialogService/FolderWatchService/
    /// History — none of which this test needs just to read a resolved
    /// brush). Bindings ReadyView has that this stub does not supply
    /// (DashboardVisible, TileGroups, AllQuiet, CountCaption, DetailLine,
    /// StartCommand, OpenInboxCommand) fail to resolve harmlessly — WPF logs
    /// a binding error and leaves the target property at its default; it
    /// does not throw.</summary>
    private sealed class ReadyViewStub
    {
        public string BigCount { get; init; } = "";
        public bool CountAlertOn { get; init; }
    }

    /// <summary>Status-colour-vocabulary plan, 2026-08-08, Task 3 Part B: the
    /// alert-red inbox count switched from Theme.Danger to Theme.StatusRed.
    /// Danger as foreground measured only 3.14:1 against this element's real
    /// background — Theme.WindowBg, what MainWindow.xaml's ScrollViewer
    /// paints behind ReadyView/ProcessingView/DoneView — in dark mode, a
    /// WCAG AA failure shipping today (found while building Task 1).
    /// Rendered STANDALONE (no Window, no Show()) the same way
    /// CopyAndTerminologyTests.TheReadyScreensPrimaryButtonIsSentenceCase
    /// already proves is enough to resolve DynamicResource brushes — see
    /// Realize's own doc comment below for why.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ReadyViewCountAlertIsStatusRed(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var view = new ReadyView { DataContext = new ReadyViewStub { BigCount = "42", CountAlertOn = true } };
        Realize(view);

        var bigCount = FindAllDescendants<TextBlock>(view).FirstOrDefault(t => t.Text == "42")
            ?? throw new InvalidOperationException("no TextBlock showing BigCount=42 under ReadyView");
        var fg = ToRgb(ForegroundOf(bigCount));
        Assert.Equal(p.StatusRed, fg);
        var ratio = ThemePalette.ContrastRatio(fg, p.WindowBg);
        Assert.True(ratio >= 4.5,
            $"ReadyView BigCount alert ({schemeKey}): {fg} on {p.WindowBg} = {ratio:F2}");
    });

    /// <summary>Phase 3, Task 3.4 (approved mockup): pins
    /// WidthToColumnsConverter's breakpoint against a REAL ShellViewModel
    /// with four tiles in one group — the bare <see cref="ReadyViewStub"/>
    /// above (no Tiles at all) can't exercise the UniformGrid this test
    /// needs. Built the same way DashboardTests' WithWatchFolder helper
    /// builds a fixture (real temp folders/files, no config.json write
    /// needed), four watch folders sharing the default (blank) Section so
    /// they land in a SINGLE TileGroupViewModel.Tiles collection — the
    /// thing the UniformGrid below actually wraps.
    ///
    /// Measured/Arranged at two explicit ReadyView widths rather than via a
    /// real MainWindow: the compact/normal window geometry itself is pinned
    /// separately by reasoning, and confirmed empirically (pixel-measured
    /// off a real MainWindow-ready-graphite.png smoke screenshot at the
    /// compact 470-wide window — see WidthToColumnsConverter.cs's own doc
    /// comment for the exact pixel math, 422px), by the OrdoSort.Smoke
    /// screenshot this task's own verification step already requires; this
    /// test only needs to prove the CONVERTER'S breakpoint actually reaches
    /// the UniformGrid it's bound to, at widths straddling it (422 is the
    /// measured real compact panel width, 620 stands in for the mockup's
    /// "user widens" case).</summary>
    [Fact]
    public void ReadyViewTileGridIsTwoColumnsCompactThreeColumnsWide() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, ThemePalette.FindScheme("paper")!);

        using var fx = new ShellFixture(cfg =>
        {
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(cfg.Inbox, "..", $"watch{i}");
                Directory.CreateDirectory(path);
                cfg.WatchFolders.Add(new WatchFolder { Label = $"Folder {i}", Path = path, Filetypes = "pdf" });
                File.WriteAllText(Path.Combine(path, "a.pdf"), "x");
            }
        });
        fx.Shell.Initialize();
        var group = Assert.Single(fx.Shell.TileGroups);
        Assert.Equal(4, group.Tiles.Count);

        var view = new ReadyView { DataContext = fx.Shell };

        // narrower than WidthToColumnsConverter.Breakpoint (560) — 422 is
        // the real compact-parked ActualWidth, pixel-measured off a real
        // MainWindow-ready-graphite.png smoke screenshot (see
        // WidthToColumnsConverter.cs's own doc comment)
        view.Measure(new Size(422, 2000));
        view.Arrange(new Rect(0, 0, 422, 2000));
        view.UpdateLayout();
        var grid = FindDescendant<UniformGrid>(view)
            ?? throw new InvalidOperationException("no UniformGrid under ReadyView's tile group");
        Assert.Equal(2, grid.Columns);

        // wider than the breakpoint — the "user widens" case
        view.Measure(new Size(620, 2000));
        view.Arrange(new Rect(0, 0, 620, 2000));
        view.UpdateLayout();
        Assert.Equal(3, grid.Columns);
    });

    /// <summary>Phase 3, Task 3.4: the skeleton restructure (StackPanel-of-
    /// StackPanel -> a single Grid with one row per section) moved
    /// DashboardVisible/AllQuiet's Visibility bindings off two WRAPPING
    /// StackPanels onto each of their two children individually (tiles +
    /// divider; quiet-line + divider) — the exact seam a reparent like that
    /// could silently break. The tile-grid test above only ever exercises
    /// the DashboardVisible=true branch; this covers the other one:
    /// AllQuiet=true (watch folders configured, all empty — WithWatchFolder-
    /// style, no files ever written) must show the "quiet" line and hide
    /// the tiles ItemsControl, proving each row still collapses
    /// independently now that nothing wraps them.</summary>
    [Fact]
    public void ReadyViewShowsTheQuietLineAndHidesTilesWhenAllQuiet() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, ThemePalette.FindScheme("paper")!);

        using var fx = new ShellFixture(cfg =>
        {
            var path = Path.Combine(cfg.Inbox, "..", "watched");
            Directory.CreateDirectory(path);
            cfg.WatchFolders.Add(new WatchFolder { Label = "Failed faxes", Path = path, Filetypes = "pdf" });
        });
        fx.Shell.Initialize();
        Assert.True(fx.Shell.AllQuiet);
        Assert.False(fx.Shell.DashboardVisible);

        var view = new ReadyView { DataContext = fx.Shell };
        Realize(view);

        var quietLine = FindAllDescendants<TextBlock>(view)
            .FirstOrDefault(t => t.Text == "All monitored folders are quiet");
        Assert.NotNull(quietLine);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)quietLine!.Parent).Visibility);

        var tilesItemsControl = FindDescendant<ItemsControl>(view)
            ?? throw new InvalidOperationException("no ItemsControl under ReadyView");
        Assert.Equal(Visibility.Collapsed, tilesItemsControl.Visibility);
    });

    /// <summary>Same purpose as <see cref="ReadyViewStub"/> above, for
    /// ProcessingView's illegal-name Preview warning.</summary>
    private sealed class ProcessingViewStub
    {
        public string Preview { get; init; } = "";
        public bool PreviewIsWarning { get; init; }
    }

    /// <summary>Status-colour-vocabulary plan, 2026-08-08, Task 3 Part B: the
    /// illegal-filename Preview warning switched from Theme.Danger to
    /// Theme.StatusRed — same defect, same fix, same real background
    /// (Theme.WindowBg) as ReadyViewCountAlertIsStatusRed above.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ProcessingViewWarningPreviewIsStatusRed(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var view = new ProcessingView
        {
            DataContext = new ProcessingViewStub { Preview = "⚠ bad name", PreviewIsWarning = true },
        };
        Realize(view);

        var preview = FindAllDescendants<TextBlock>(view).FirstOrDefault(t => t.Text == "⚠ bad name")
            ?? throw new InvalidOperationException("no TextBlock showing the warning Preview under ProcessingView");
        var fg = ToRgb(ForegroundOf(preview));
        Assert.Equal(p.StatusRed, fg);
        var ratio = ThemePalette.ContrastRatio(fg, p.WindowBg);
        Assert.True(ratio >= 4.5,
            $"ProcessingView warning preview ({schemeKey}): {fg} on {p.WindowBg} = {ratio:F2}");
    });

    // ------------------------------------------------- MainWindow notice rail

    /// <summary>MainWindow's alert icon used to live on a standalone floating
    /// toast Button; Phase 3 Task 3.3 (notification-rail unification,
    /// 2026-08-09) folded that toast — plus the old set-aside/history-backup
    /// banners — into one <c>NoticeItemTemplate</c>, a keyed resource in
    /// MainWindow.Resources that <c>NoticeRail</c> (the rail's ItemsControl)
    /// applies as its <c>ItemTemplate</c>. This still constructs the REAL
    /// MainWindow, so the template resolved below is the exact production
    /// resource, not a hand-copied stand-in — but it stops short of
    /// realizing it through NoticeRail's own generated container: that would
    /// need a connected PresentationSource (ItemContainerGenerator only
    /// produces containers once the ItemsControl is laid out inside a
    /// Show()n Window — see the ListBox-based tests elsewhere in this file,
    /// which all call Show()), and Show()ing MainWindow fires Loaded, which
    /// starts a REAL WebView2/Edge process via _pdf.InitAsync — the exact
    /// cost this test always went out of its way to avoid (previously by
    /// resolving the (then-inline) toast Button by x:Name off the Window's
    /// own NameScope, never by Show()ing it).
    ///
    /// The fix: resolve NoticeItemTemplate BY KEY straight off the real,
    /// already-InitializeComponent()'d Window.Resources (populated by the
    /// constructor alone, no Show() required — the same technique
    /// SettingsThemeCardRadioButtonShowsTheBronzeFocusRing already uses for
    /// SettingsWindow's keyed "ThemeCard" style), and apply it as a loose
    /// ContentPresenter's ContentTemplate with a real, Error-kind NoticeVm
    /// as Content — the identical "resolve the production DataTemplate by
    /// key, instantiate it on a loose container" technique
    /// HighlightedComboBoxItemTextMeetsWcagAa already established for
    /// KvpValueTemplate/FontChoiceTemplate above. FindTextElement below
    /// walks the SAME depth-first order it always has; NoticeItemTemplate's
    /// Grid declares the icon TextBlock (x:Name="KindIcon") as its first
    /// child, so this still resolves the icon, not the message/detail text.
    /// cfg.HistoryDb points at a fresh temp path (dir pre-created) because
    /// ShellViewModel's constructor opens a History connection synchronously.
    ///
    /// Status-colour-vocabulary plan, 2026-08-08, Task 3 Part B: the icon
    /// glyph switched from Theme.Danger to Theme.StatusRed, which sits on
    /// Theme.SurfaceRaised — one step LIGHTER than Surface in dark mode —
    /// a background StatusRed was never tuned against. That left three of
    /// seven schemes (graphite 4.11:1, ledger 4.34:1, microfilm 4.38:1)
    /// short of this app's 4.5 floor, a known, open gap this test used to
    /// pin instead of assert away.
    ///
    /// GAP CLOSED 2026-08-09: the glyph now binds Theme.StatusRedRaised — a
    /// per-scheme red tuned specifically against SurfaceRaised (see
    /// ThemePalette.cs's StatusRedRaised field comment for the search and
    /// the exact values/ratios). This test is now a clean >=4.5 assertion
    /// for every scheme, no floor-lowering and no per-scheme pin — the
    /// notice-rail move above changes WHERE the real icon is found, not
    /// this assertion.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void MainWindowToastIconContrast(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var dir = Path.Combine(Path.GetTempPath(), "ordo_test_toast_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var cfg = new Config { HistoryDb = Path.Combine(dir, "history.sqlite") };
        var cfgPath = Path.Combine(dir, "config.json");

        var window = new MainWindow(cfg, cfgPath)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            var template = window.Resources["NoticeItemTemplate"] as DataTemplate
                ?? throw new InvalidOperationException(
                    "no DataTemplate keyed \"NoticeItemTemplate\" in MainWindow.Resources");
            var notice = new NoticeVm("alert", NoticeKind.Error, "Alert",
                "URGENT-callback.pdf — in Invoices", "Open folder", () => { }, () => { });
            var presenter = new ContentPresenter { Content = notice, ContentTemplate = template };
            Realize(presenter);

            var card = FindDescendant<Border>(presenter)
                ?? throw new InvalidOperationException("no Border ('Card') descendant under the notice item");
            var icon = FindTextElement(presenter)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the notice item");

            var fg = ToRgb(ForegroundOf(icon));
            var bg = ToRgb(card.Background);
            Assert.Equal(p.StatusRedRaised, fg);
            var ratio = ThemePalette.ContrastRatio(fg, bg);
            Assert.True(ratio >= 4.5,
                $"MainWindow notice rail icon ({schemeKey}): {fg} on {bg} = {ratio:F2}, want >= 4.5");
        }
        finally
        {
            window.Close();
        }
    });

    // --------------------------------------------- Hover/pressed tint strength

    /// <summary>Hover-tint strength review, ROUND 2 (2026-08-08). A parallel
    /// audit of round 1 found two problems: (1) Match & Merge/Bulk rename/
    /// History/Triage — "where the owner spends their time" — had NO
    /// DataGridRow hover at all (Styles.xaml had zero style for it), so
    /// round 1's stronger SurfaceHover never reached the grids the complaint
    /// was actually about; (2) a single shared Mix(Surface, Text, amount)
    /// formula can't both grow the surround-delta AND protect every
    /// foreground colour that can sit on it — two status colours (StatusGreen
    /// light, StatusRed dark) were already below 4.5:1 at round 1's OLD 0.08,
    /// which round 1 raised further without knowing it, and could only
    /// "fix" by staying so weak the delta barely moved.
    ///
    /// This round replaces the single shared token with TWO tiers, hand-
    /// tuned per palette on ThemePalette itself (see that file's own
    /// comment at SurfaceHover/SurfacePressed/RowHover for the full
    /// reasoning and safe-zone boundaries):
    ///   CHROME (Theme.SurfaceHover/SurfacePressed) — MenuItem, TabItem,
    ///   ChipButton, MainWindow's Rescan button. Only Theme.Text ever
    ///   renders on these, so they're free to be strong.
    ///   ROW (Theme.RowHover) — DataGridRow (newly added to all four grids
    ///   this round), ListBoxItem, the Calendar family, ReadyView's inbox
    ///   button. StatusAmber/StatusGreen/StatusRed/SubtleText can all
    ///   render on these, so it's deliberately modest — but, unlike round
    ///   1, EVERY one of those five foregrounds is now guaranteed >=4.5:1
    ///   against it in both palettes (verified by brute-force byte search),
    ///   closing the two pre-existing gaps round 1 could only document.
    ///
    /// Every test below reads a REAL rendered brush from a REAL control —
    /// never the ThemeManager resource value directly — so a future edit
    /// that stops wiring a trigger to the right tier fails these tests even
    /// if the resource itself still computed a strong number (the "verify
    /// it reaches the pixel" caution this review was given all three rounds,
    /// after one XAML highlight was found declared but dead the first
    /// time, and a second — HistoryWindow's Reverted trigger — found dead
    /// the same way by round 2's own audit).
    ///
    /// ROUND 3 correction: round 2's ROW tier reasoning above ("hold
    /// luminance near Surface to protect the five foregrounds") was real
    /// WCAG math that led somewhere wrong — ContrastRatio is a pure
    /// luminance ratio, so it can't distinguish "held near Surface on
    /// purpose" from "barely visible", and round 2's neutral-grey RowHover
    /// measured PERCEPTUALLY fainter (CIE76) than round 1's shared value,
    /// in light mode by almost 5x. RowHover is now a CHROMATIC tint (warm,
    /// this app's own AccentBronze family) instead of a neutral grey: hue
    /// shifts at near-constant lightness cost almost nothing in WCAG terms
    /// but read as plainly visible — see ThemeManager.cs's own comment at
    /// the Theme.RowHover assignment for the full round 1/2/3 CIE76 table,
    /// and ThemePalette.cs's RowHover field comments for the exact
    /// per-colour contrast numbers. CHROME is UNCHANGED this round (still
    /// neutral, still luminance-tuned — it never had ROW's problem, since
    /// only Theme.Text ever sits on it).</summary>

    /// <summary>Renders a real TabItem (Theme.SurfaceHover's CHROME tier —
    /// only Theme.Text ever sits on it) with IsMouseOver forced via
    /// <see cref="ForceMouseOver"/>, the same Realize-then-force-then-
    /// Realize shape <see cref="SelectedListBoxItemUsesTheAccentPalette"/>
    /// already uses for IsSelected. The floor (1.5 light / 1.5 dark) sits
    /// strictly between round 1's shared 0.10 mix (1.228:1 light, 1.318:1
    /// dark — what a lazy "just reuse round 1's number" fix would still
    /// measure) and round 2's actual CHROME values (1.729:1 light, 1.780:1
    /// dark), so reverting to round 1's shared token fails THIS assertion
    /// specifically, not a missing-render error.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ChromeHoverSurroundDeltaClearsTheStrengthenedFloor(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var tab = new TabItem { Header = "Destinations" };
        Realize(tab);
        ForceMouseOver(tab, true);
        Realize(tab);

        var bd = FindDescendant<Border>(tab)
            ?? throw new InvalidOperationException("no Border ('Bd') descendant under TabItem");
        var hoverRgb = ToRgb(bd.Background);
        var surfaceRgb = ToRgb((Brush)_fx.App.Resources["Theme.Surface"]);
        var delta = ThemePalette.ContrastRatio(hoverRgb, surfaceRgb);

        var floor = 1.5;
        Assert.True(delta >= floor,
            $"Chrome hover surround-delta ({schemeKey}): {hoverRgb} vs surface {surfaceRgb} = {delta:F3}:1, want >= {floor}");
    });

    /// <summary>Same purpose as the Hover test above, for
    /// Theme.SurfacePressed — rendered through its one real production call
    /// site (MenuTopLevelHeader's IsSubmenuOpen trigger, Styles.xaml), a
    /// real, genuinely-opened submenu in a live PresentationSource (the same
    /// technique <see cref="HighlightedMenuSubmenuHeaderTextMeetsWcagAa"/>
    /// above already establishes works for this exact control template).
    /// IsSubmenuOpen is a plain public read/write property (unlike
    /// IsHighlighted), so no reflection trick is needed here.
    ///
    /// The floor (2.0 light / 2.0 dark) sits strictly between round 1's
    /// shared 0.20 mix (1.529:1 light, 1.780:1 dark) and round 2's actual
    /// CHROME pressed values (2.649:1 light, 2.502:1 dark).</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ChromePressedSurroundDeltaClearsTheStrengthenedFloor(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var menu = new Menu();
        var topLevel = new MenuItem { Header = "_Tools" };
        topLevel.Items.Add(new MenuItem { Header = "_Unlock PDFs…" });
        menu.Items.Add(topLevel);
        var window = new Window
        {
            Content = menu, Width = 300, Height = 200,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            topLevel.IsSubmenuOpen = true;
            PumpRender();
            window.UpdateLayout();

            // Sanity: really the TopLevelHeader template (the one that owns
            // the IsSubmenuOpen -> Theme.SurfacePressed trigger), not some
            // other role.
            Assert.Same(_fx.App.Resources["MenuTopLevelHeader"], topLevel.Template);

            var bd = FindDescendant<Border>(topLevel)
                ?? throw new InvalidOperationException("no Border ('Bd') descendant under the top-level MenuItem");
            var pressedRgb = ToRgb(bd.Background);
            var surfaceRgb = ToRgb((Brush)_fx.App.Resources["Theme.Surface"]);
            var delta = ThemePalette.ContrastRatio(pressedRgb, surfaceRgb);

            var floor = 2.0;
            Assert.True(delta >= floor,
                $"Chrome pressed surround-delta ({schemeKey}): {pressedRgb} vs surface {surfaceRgb} = {delta:F3}:1, want >= {floor}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Step 2's own instruction: "keep pressed visibly stronger than
    /// hover" — a structural invariant on the two REAL rendered CHROME-tier
    /// brushes the tests above already resolve independently, re-derived
    /// here rather than hardcoding two numbers that could drift apart from
    /// each other without either individual floor test failing. (The ROW
    /// tier has no pressed variant at all — see ThemePalette.cs's RowHover
    /// comment for why none of its consumers need one — so this comparison
    /// is CHROME-only.)</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void ChromePressedSurroundDeltaExceedsChromeHoverSurroundDelta(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);
        var surfaceRgb = ToRgb((Brush)_fx.App.Resources["Theme.Surface"]);

        var tab = new TabItem { Header = "Destinations" };
        Realize(tab);
        ForceMouseOver(tab, true);
        Realize(tab);
        var hoverRgb = ToRgb((FindDescendant<Border>(tab)
            ?? throw new InvalidOperationException("no Border ('Bd') descendant under TabItem")).Background);
        var hoverDelta = ThemePalette.ContrastRatio(hoverRgb, surfaceRgb);

        var menu = new Menu();
        var topLevel = new MenuItem { Header = "_Tools" };
        topLevel.Items.Add(new MenuItem { Header = "_Unlock PDFs…" });
        menu.Items.Add(topLevel);
        var window = new Window
        {
            Content = menu, Width = 300, Height = 200,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            topLevel.IsSubmenuOpen = true;
            PumpRender();
            window.UpdateLayout();
            var pressedRgb = ToRgb((FindDescendant<Border>(topLevel)
                ?? throw new InvalidOperationException("no Border ('Bd') descendant under the top-level MenuItem")).Background);
            var pressedDelta = ThemePalette.ContrastRatio(pressedRgb, surfaceRgb);

            Assert.True(pressedDelta > hoverDelta,
                $"Chrome Pressed ({pressedDelta:F3}:1) should read stronger than Chrome Hover ({hoverDelta:F3}:1) in {schemeKey}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Hover-tint strength review, round 3 (2026-08-08): a
    /// luminance-ratio floor is the WRONG guard for Theme.RowHover, and
    /// this test (round 2's RowHoverSurroundDeltaIsNonTrivial, which this
    /// replaces) proved it by turning red the moment RowHover became a
    /// chromatic tint — not because the tint got weaker, but because
    /// holding L* close to Surface (by design, to keep StatusGreen/
    /// StatusRed legible without a luminance floor to fall back on) makes
    /// ContrastRatio measure almost exactly 1:1 regardless of how far the
    /// hue has shifted (dark measured 1.001:1 the first time this ran
    /// against the new value — reproduced deliberately, not a guess).
    /// ContrastRatio cannot see hue at all; ThemePalette.DeltaE76 (CIE76,
    /// Lab space) can, and is what this app's chromatic tint actually
    /// spends. Renders a real ListBoxItem the identical way the old test
    /// did (IsMouseOver forced, real "Bd" Border.Background read).
    ///
    /// The floor (10.0, both palettes) sits strictly between round 2's
    /// grey (dE76 1.84 light / 7.37 dark — the regression this round
    /// fixes) and round 3's actual chromatic value (15.07 / 15.59 — see
    /// ThemeManager.cs's own comment at the Theme.RowHover assignment for
    /// the full round 1/2/3 table), and comfortably above the ~1-2 "just
    /// noticeable difference" CIE76 threshold — the target is "obviously
    /// there", not "technically different".</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void RowHoverDeltaEIsComfortablyVisible(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var item = new ListBoxItem { Content = "Invoices" };
        Realize(item);
        ForceMouseOver(item, true);
        Realize(item);

        var bd = FindDescendant<Border>(item)
            ?? throw new InvalidOperationException("no Border descendant under ListBoxItem");
        var hoverRgb = ToRgb(bd.Background);
        var surfaceRgb = ToRgb((Brush)_fx.App.Resources["Theme.Surface"]);
        var dE = ThemePalette.DeltaE76(hoverRgb, surfaceRgb);

        var floor = 10.0;
        Assert.True(dE >= floor,
            $"Row hover CIE76 ({schemeKey}): {hoverRgb} vs surface {surfaceRgb} = dE76 {dE:F2}, want >= {floor}");
    });

    /// <summary>The live "SubtleText and StatusAmber on Hover" pairing this
    /// review's own brief named directly: BulkRenameWindow.xaml's
    /// DataGridRow RowStyle paints a NeedsName row's Background with
    /// Theme.RowHover (a persistent state highlight, not literal mouse
    /// hover, but the identical resource and the identical legibility
    /// question — and, this round, ALSO the same token IsMouseOver itself
    /// now uses on that same RowStyle, see BulkRenameWindow.xaml), and
    /// BulkRenameViewModel.ApplyPlans makes NeedsName=true ALWAYS imply
    /// NoteIsProblem=true (needsName is defined as `!pr.Changed &&
    /// pr.Note.Length > 0`, and noteIsProblem is `pr.Note.Length > 0` — the
    /// first condition is strictly narrower) — so a NeedsName row's Note
    /// column is ALWAYS StatusAmber (Manual/Changed's SubtleText triggers
    /// are declared before it and always lose), and its Changed=false
    /// always holds too, so "New name" is ALWAYS SubtleText.
    /// DataGridNoteColourTests' own BulkRename coverage checks these same
    /// colours against Theme.Surface (the DataGrid's OWN Background,
    /// correct for a Transparent-background row) — not what a NeedsName
    /// row's REAL background is. This test reads the ROW's actual rendered
    /// Background instead, closing exactly that gap, and — the "reaches the
    /// pixel" check — asserts it against the SAME resolved Theme.RowHover
    /// resource rather than a hardcoded RGB triple.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void BulkRenameNeedsNameRowStaysLegibleOnItsOwnHoverTint(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new BulkRenameViewModel();
        vm.Preview.Add(new RenameRow(@"C:\inbox\c.pdf", "c.pdf", "c.pdf",
            "doesn't match the review-file layout — skipped",
            changed: false, manual: false, needsName: true, editSeed: "c.pdf", noteIsProblem: true));
        var window = new BulkRenameWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under BulkRenameWindow");
            var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                ?? throw new InvalidOperationException("row 0 never realized a container");
            row.ApplyTemplate();
            row.UpdateLayout();

            var rowBg = ToRgb(row.Background);
            Assert.Equal(ToRgb((Brush)_fx.App.Resources["Theme.RowHover"]), rowBg);

            var newNameColumn = grid.Columns.FirstOrDefault(c => (c.Header as string)?.StartsWith("New name") == true)
                ?? throw new InvalidOperationException("no 'New name' column found");
            var noteColumn = grid.Columns.FirstOrDefault(c => (c.Header as string) == "Note")
                ?? throw new InvalidOperationException("no 'Note' column found");
            var cells = FindAllDescendants<DataGridCell>(row);
            var newNameCell = cells.FirstOrDefault(c => c.Column == newNameColumn)
                ?? throw new InvalidOperationException("'New name' cell never realized");
            var noteCell = cells.FirstOrDefault(c => c.Column == noteColumn)
                ?? throw new InvalidOperationException("'Note' cell never realized");
            newNameCell.ApplyTemplate(); newNameCell.UpdateLayout();
            noteCell.ApplyTemplate(); noteCell.UpdateLayout();
            var newNameText = FindDescendant<TextBlock>(newNameCell)
                ?? throw new InvalidOperationException("'New name' cell TextBlock never realized");
            var noteText = FindDescendant<TextBlock>(noteCell)
                ?? throw new InvalidOperationException("'Note' cell TextBlock never realized");

            var newNameFg = ToRgb(newNameText.Foreground);
            var noteFg = ToRgb(noteText.Foreground);
            Assert.Equal(p.SubtleText, newNameFg);
            Assert.Equal(p.StatusAmber, noteFg);

            var newNameRatio = ThemePalette.ContrastRatio(newNameFg, rowBg);
            var noteRatio = ThemePalette.ContrastRatio(noteFg, rowBg);
            Assert.True(newNameRatio >= 4.5,
                $"BulkRename NeedsName row 'New name' (SubtleText) on its own Hover tint ({schemeKey}): {newNameFg} on {rowBg} = {newNameRatio:F3}");
            Assert.True(noteRatio >= 4.5,
                $"BulkRename NeedsName row 'Note' (StatusAmber) on its own Hover tint ({schemeKey}): {noteFg} on {rowBg} = {noteRatio:F3}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The other live "SubtleText on Hover" pairing — the one round
    /// 1's brief predicted as "the most likely casualty": RouteList's
    /// GestureText caption (Theme.SubtleText, via its CaptionText-based
    /// inline Style) on an unselected, hovered ListBoxItem row. Same shape
    /// LabelMakerWindow's NextNumberText and ManageSavedWindow's
    /// password-status annotation share — all three are declared FIRST in
    /// their DockPanel/StackPanel and stay Theme.SubtleText until IsSelected
    /// (see SettingsRouteListGestureTextStaysSubtleUnlessSelected above,
    /// which covers selected/unselected-but-not-hovered; this test is the
    /// hovered-but-unselected state that was never previously exercised).
    /// IsMouseOver forced the same way RowHoverDeltaEIsComfortablyVisible
    /// does above.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void RouteListGestureTextStaysLegibleWhenHoveredButUnselected(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_settings_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(),
            () => scheme.Palette, cfgPath,
            uiContext: System.Threading.SynchronizationContext.Current);
        var window = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var tabControl = FindDescendant<TabControl>(window)
                ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
            var destinationsTab = tabControl.Items.Cast<TabItem>()
                    .FirstOrDefault(ti => ti.Header?.ToString() == "_Destinations")
                ?? throw new InvalidOperationException("no \"Destinations\" TabItem found");
            tabControl.SelectedItem = destinationsTab;
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under SettingsWindow");
            // SettingsViewModel's constructor auto-selects the first route
            // (SelectedRoute = Routes.FirstOrDefault()) — deliberately
            // deselect it here so this exercises IsMouseOver ALONE, the
            // exact state this review's Step 2/3 tension is about, not
            // "selected AND hovered" (which Styles.xaml's ListBoxItem style
            // already resolves to Theme.Accent regardless, since IsSelected
            // is declared after IsMouseOver in that Style's triggers).
            listBox.SelectedIndex = -1;
            window.UpdateLayout();
            PumpRender();
            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("RouteList row 0 never realized a container");
            ForceMouseOver(container, true);
            container.UpdateLayout();

            var bd = FindDescendant<Border>(container)
                ?? throw new InvalidOperationException("no Border descendant under the RouteList row");
            var rowBg = ToRgb(bd.Background);
            Assert.Equal(ToRgb((Brush)_fx.App.Resources["Theme.RowHover"]), rowBg);

            // GestureText is declared FIRST in the DockPanel (Dock="Right",
            // after the non-Text Rectangle swatch) — same element
            // SettingsRouteListGestureTextStaysSubtleUnlessSelected already
            // resolves via FindTextElement.
            var gestureFg = ToRgb(FindAllDescendants<TextBlock>(container)[0].Foreground);
            Assert.Equal(p.SubtleText, gestureFg);
            var ratio = ThemePalette.ContrastRatio(gestureFg, rowBg);
            Assert.True(ratio >= 4.5,
                $"RouteList GestureText (SubtleText) hovered-unselected ({schemeKey}): {gestureFg} on {rowBg} = {ratio:F3}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>UnlockWindow's FileList Note column, hovered but NOT
    /// selected — the pairing that surfaced round 1's most important
    /// finding, and round 2's proof that the fix actually landed: ALL FOUR
    /// statuses now clear 4.5:1 here in BOTH palettes, closing the two
    /// pre-existing gaps round 1 could only document as known/open
    /// (StatusGreen light was 4.343:1, StatusRed dark was 3.945:1 — both
    /// already below the floor at the ORIGINAL 0.08 mix, before either
    /// round touched anything) plus the one round 1's own strengthening
    /// pushed under the floor for the first time (StatusRed light, 4.606:1
    /// -> 4.430:1 at round 1's 0.10). Theme.RowHover's per-palette,
    /// all-five-protected design (ThemePalette.cs's own comment) is what
    /// makes this a straight `>= 4.5` assertion instead of the "known,
    /// open gap" range round 1 had to pin here — this test IS the proof
    /// that "fix the contrast failures rather than pinning them" was
    /// actually achieved, not just claimed.</summary>
    public static IEnumerable<object[]> HoverUnselectedNoteCases()
    {
        foreach (var s in ThemePalette.Schemes)
        foreach (var status in new[] { "Ready", "NeedsPassword", "Unreadable" })
            yield return new object[] { s.Key, status };
    }

    [Theory, MemberData(nameof(HoverUnselectedNoteCases))]
    public void UnlockFileListHoverUnselectedNoteContrast(string schemeKey, string statusName) => _fx.Invoke(() =>
    {
        var status = statusName switch
        {
            "Ready" => ReadinessStatus.Ready,
            "NeedsPassword" => ReadinessStatus.NeedsPassword,
            "Unreadable" => ReadinessStatus.Unreadable,
            _ => throw new ArgumentOutOfRangeException(nameof(statusName)),
        };
        Func<ThemePalette, Rgb> expectedColor = statusName switch
        {
            "Ready" => p => p.StatusGreen,
            "NeedsPassword" => p => p.StatusAmber,
            "Unreadable" => p => p.StatusRed,
            _ => throw new ArgumentOutOfRangeException(nameof(statusName)),
        };
        var message = statusName switch
        {
            "Ready" => "A saved password opens this.",
            "NeedsPassword" => "This PDF needs a password none of the saved ones supply.",
            "Unreadable" => "Couldn't read it: The file is not a valid PDF document.",
            _ => throw new ArgumentOutOfRangeException(nameof(statusName)),
        };

        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new UnlockViewModel(new Config(), () => true);
        var row = new UnlockFileRow(@"C:\inbox\20240101--1111111111.pdf");
        row.SetProbeResult(status, message);
        vm.Files.Add(row);
        var window = new UnlockWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();

            var listBox = FindDescendant<ListBox>(window)
                ?? throw new InvalidOperationException("no ListBox descendant under UnlockWindow");
            var container = listBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("FileList row 0 never realized a container");
            ForceMouseOver(container, true);
            container.UpdateLayout();

            var bd = FindDescendant<Border>(container)
                ?? throw new InvalidOperationException("no Border descendant under FileList's ListBoxItem");
            var rowBg = ToRgb(bd.Background);
            Assert.Equal(ToRgb((Brush)_fx.App.Resources["Theme.RowHover"]), rowBg);

            var textBlocks = FindAllDescendants<TextBlock>(container);
            Assert.True(textBlocks.Count >= 2,
                $"expected FileName + Note TextBlocks, found {textBlocks.Count}");
            var noteFg = ToRgb(textBlocks[1].Foreground);
            Assert.Equal(expectedColor(p), noteFg);
            var ratio = ThemePalette.ContrastRatio(noteFg, rowBg);

            Assert.True(ratio >= 4.5,
                $"UnlockWindow FileList hovered-unselected {statusName} ({schemeKey}): {noteFg} on {rowBg} = {ratio:F3}");
        }
        finally
        {
            window.Close();
        }
    });

    // ------------------------------------------- DataGridRow hover coverage

    /// <summary>The complaint round 1 missed entirely: Styles.xaml had ZERO
    /// style for DataGridRow before round 2 — no IsMouseOver trigger on
    /// DataGridRow OR DataGridCell anywhere in this app, confirmed by grep
    /// — so Match & Merge, one of the four grids "where the owner spends
    /// their time" (round 2's own brief), showed no hover feedback
    /// whatsoever no matter how strong any Theme.* token was. Renders a
    /// REAL MatchMergeWindow with one "ambiguous" (StatusAmber Note) row,
    /// forces IsMouseOver on the real DataGridRow container (MatchGrid's
    /// OWN local RowStyle now carries the same IsMouseOver trigger this
    /// review added — see MatchMergeWindow.xaml), and asserts BOTH that the
    /// row's actual Background resolves to Theme.RowHover (the "reaches the
    /// pixel" check) and that the Note text stays legible on it.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void MatchMergeGridRowHoverRendersAndStaysLegible(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var vm = new MatchMergeViewModel(new Config(), _ => { }, new FakeDialogs());
        vm.Rows.Add(new MatchRow(@"C:\inbox\a.pdf", "a.pdf", "", "some note text here", "ambiguous"));
        var window = new MatchMergeWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            var grid = FindDescendant<DataGrid>(window)
                ?? throw new InvalidOperationException("no DataGrid descendant under MatchMergeWindow");
            var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                ?? throw new InvalidOperationException("row 0 never realized a container");
            row.ApplyTemplate();
            row.UpdateLayout();
            ForceMouseOver(row, true);
            row.UpdateLayout();

            var rowBg = ToRgb(row.Background);
            Assert.Equal(ToRgb((Brush)_fx.App.Resources["Theme.RowHover"]), rowBg);

            var noteColumn = grid.Columns.FirstOrDefault(c => (c.Header as string) == "Note")
                ?? throw new InvalidOperationException("no 'Note' column found");
            var noteCell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == noteColumn)
                ?? throw new InvalidOperationException("'Note' cell never realized");
            noteCell.ApplyTemplate();
            noteCell.UpdateLayout();
            var noteText = FindDescendant<TextBlock>(noteCell)
                ?? throw new InvalidOperationException("'Note' cell TextBlock never realized");
            var noteFg = ToRgb(noteText.Foreground);
            Assert.Equal(p.StatusAmber, noteFg);
            var ratio = ThemePalette.ContrastRatio(noteFg, rowBg);
            Assert.True(ratio >= 4.5,
                $"Match & Merge row Note (StatusAmber) on its own Hover tint ({schemeKey}): {noteFg} on {rowBg} = {ratio:F3}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>TriageWindow's Candidates grid sets NO local RowStyle at all
    /// (unlike BulkRename/MatchMerge/History) — confirmed by reading
    /// TriageWindow.xaml — so it's the ONE grid that resolves the new
    /// implicit `&lt;Style TargetType="DataGridRow"&gt;` Styles.xaml gained
    /// this round, rather than a window-local copy of the same trigger.
    /// Proves that implicit style actually reaches a real grid (the same
    /// "local value outranks style" trap this file documents repeatedly
    /// elsewhere means it CANNOT be assumed from the other three windows'
    /// tests passing). Built the same way DataGridNoteColourTests'
    /// AssertTriageWhyColour does — ShowCurrentAsync() directly rather than
    /// Show()ing the window, since Show() would start a real WebView2 init
    /// this sandbox can't complete.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void TriageGridRowHoverRendersViaTheImplicitStyle(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        ThemeManager.Apply(_fx.App, scheme);

        var item = new MatchMerge.MatchResult(@"C:\inbox\doc.pdf", "suggested", "SMITH", "JOHN",
            Suggestions: new List<MatchMerge.Suggestion>
            {
                new(new MatchMerge.Candidate("1", new Dictionary<string, string> { ["A"] = "x" }),
                    "token match on last name"),
            });
        var window = new TriageWindow(new List<MatchMerge.MatchResult> { item }, new[] { "A" })
        {
            Dialogs = new FakeDialogs(),
        };
        try
        {
#pragma warning disable xUnit1031
            window.ShowCurrentAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

            var grid = window.Candidates;
            grid.ApplyTemplate();
            grid.Measure(new Size(440, 500));
            grid.Arrange(new Rect(0, 0, 440, 500));
            grid.UpdateLayout();

            var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                ?? throw new InvalidOperationException("Candidates row 0 never realized a container");
            row.ApplyTemplate();
            row.UpdateLayout();
            // Sanity: really resolved through the NEW implicit
            // {x:Type DataGridRow} style (an implicit style's resource key
            // is the CLR Type, not a string), not some other local value —
            // this is the one grid with no local RowStyle of its own to
            // compete with it.
            Assert.Same(_fx.App.Resources[typeof(DataGridRow)], row.Style);
            ForceMouseOver(row, true);
            row.UpdateLayout();

            var rowBg = ToRgb(row.Background);
            Assert.Equal(ToRgb((Brush)_fx.App.Resources["Theme.RowHover"]), rowBg);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>HistoryWindow's Reverted DataTrigger — round 2's second
    /// finding, "same trap as Match & Merge": a Foreground Setter declared
    /// on DataGridRow's own RowStyle, which Styles.xaml's DataGridCell
    /// style's own unconditional Foreground Setter always outranks (the
    /// cell inherits nothing from the row that carries any weight), so it
    /// never painted anything, ever — the row's OTHER Setter on that same
    /// trigger (FontStyle="Italic") DID render, which is exactly why nobody
    /// noticed the Foreground half was dead. Fixed the identical way Task 2
    /// fixed Match & Merge: a per-column ElementStyle DataTrigger on each of
    /// the five text columns (When/Original/Filed as/Name/Destination).
    /// This test builds a REAL reverted history row, reads the When
    /// column's actual rendered TextBlock, and proves both halves now work:
    /// unselected shows Theme.SubtleText (the fix), selected still shows
    /// Theme.AccentText (the "let selection win" override, same shape as
    /// every other per-column vocabulary fix in this file) — plus that
    /// hovering the row (a state nothing here exercised before either)
    /// renders Theme.RowHover on the row itself.</summary>
    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void HistoryRevertedRowIsNowLegibleAndHoverRenders(string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var p = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var dbPath = Path.Combine(Path.GetTempPath(), "ordo_test_history_" + Guid.NewGuid() + ".sqlite");
        var history = new History(dbPath);
        try
        {
            var id = history.LogCommit(@"c:\in\x.pdf", "x.pdf", "Y.pdf", "Y",
                "insert", "", "Invoices", @"c:\out", tagged: false, "");
            history.MarkReverted(id);
            var vm = new HistoryViewModel(history, new FakeDialogs(), new InlineWorkScheduler());
            var window = new HistoryWindow(vm)
            {
                Left = -20000, Top = 0, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var grid = FindDescendant<DataGrid>(window)
                    ?? throw new InvalidOperationException("no DataGrid descendant under HistoryWindow");
                var row = grid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow
                    ?? throw new InvalidOperationException("row 0 never realized a container");
                row.ApplyTemplate();
                row.UpdateLayout();
                Assert.Equal(FontStyles.Italic, row.FontStyle);   // the half that always worked

                ForceMouseOver(row, true);
                row.UpdateLayout();
                var rowBg = ToRgb(row.Background);
                Assert.Equal(ToRgb((Brush)_fx.App.Resources["Theme.RowHover"]), rowBg);
                ForceMouseOver(row, false);
                row.UpdateLayout();

                var whenColumn = grid.Columns.FirstOrDefault(c => (c.Header as string) == "When")
                    ?? throw new InvalidOperationException("no 'When' column found");
                var whenCell = FindAllDescendants<DataGridCell>(row).FirstOrDefault(c => c.Column == whenColumn)
                    ?? throw new InvalidOperationException("'When' cell never realized");
                whenCell.ApplyTemplate();
                whenCell.UpdateLayout();
                var whenText = FindDescendant<TextBlock>(whenCell)
                    ?? throw new InvalidOperationException("'When' cell TextBlock never realized");

                var unselectedFg = ToRgb(whenText.Foreground);
                Assert.Equal(p.SubtleText, unselectedFg);   // the half that was DEAD before this fix
                var unselectedRatio = ThemePalette.ContrastRatio(unselectedFg, p.Surface);
                Assert.True(unselectedRatio >= 4.5,
                    $"History Reverted row 'When' unselected ({schemeKey}): {unselectedFg} on {p.Surface} = {unselectedRatio:F3}");

                grid.SelectedIndex = 0;
                grid.UpdateLayout();
                var selectedFg = ToRgb(whenText.Foreground);
                Assert.Equal(p.AccentText, selectedFg);   // let selection win
                var selectedRatio = ThemePalette.ContrastRatio(selectedFg, p.Accent);
                Assert.True(selectedRatio >= 4.5,
                    $"History Reverted row 'When' selected ({schemeKey}): {selectedFg} on {p.Accent} = {selectedRatio:F3}");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            history.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    });

    // -------------------------------------------------------------- plumbing

    /// <summary>Force full template/content generation on a "loose" element —
    /// no Window, no Show(), no STA thread needed: ApplyTemplate + Measure +
    /// Arrange is exactly what materializes a ControlTemplate's named parts
    /// and a ContentPresenter's chosen DataTemplate/auto-wrap, and none of
    /// that requires a live PresentationSource. DynamicResource/implicit-style
    /// lookups still resolve because they fall back to Application.Current's
    /// resources regardless of how disconnected the element's own tree is.</summary>
    private static void Realize(FrameworkElement el)
    {
        el.ApplyTemplate();
        el.Measure(new Size(400, 200));
        el.Arrange(new Rect(0, 0, 400, 200));
        el.UpdateLayout();
    }

    /// <summary>IsHighlighted is a read-only DP on both ComboBoxItem and
    /// MenuItem, normally flipped only by real mouse/keyboard interaction.
    /// Task 1 proved forcing it via reflection on the private
    /// `IsHighlightedPropertyKey` + the public
    /// `DependencyObject.SetValue(DependencyPropertyKey,object)` overload
    /// reproduces the identical state WPF's own hover/keyboard-navigation
    /// logic sets naturally (cross-checked there against a live dropdown) —
    /// deterministic and repeatable where real input isn't.</summary>
    private static void ForceHighlighted(DependencyObject item)
    {
        var field = item.GetType().GetField("IsHighlightedPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{item.GetType()} has no private static IsHighlightedPropertyKey field");
        var key = (DependencyPropertyKey)field.GetValue(null)!;
        item.SetValue(key, true);
    }

    /// <summary>Same trick as <see cref="ForceHighlighted"/> above, for
    /// <c>UIElement.IsMouseOver</c> — also read-only, via the private static
    /// <c>IsMouseOverPropertyKey</c> field declared on <see cref="UIElement"/>
    /// itself (not on the concrete control type, unlike IsHighlighted/
    /// IsSelected above). Used by the hover-tint strength review's own new
    /// coverage below to reach a real ControlTemplate's IsMouseOver trigger
    /// deterministically, without real mouse input.</summary>
    private static void ForceMouseOver(UIElement el, bool value)
    {
        var field = typeof(UIElement).GetField("IsMouseOverPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "UIElement has no private static IsMouseOverPropertyKey field");
        var key = (DependencyPropertyKey)field.GetValue(null)!;
        el.SetValue(key, value);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Like <see cref="FindDescendant{T}"/>, but collects EVERY
    /// matching descendant in visual-tree (depth-first) order instead of
    /// stopping at the first — needed where a single container has more
    /// than one same-typed element to distinguish, e.g. ManageSavedWindow's
    /// Label + password-status TextBlocks, which <see cref="FindTextElement"/>
    /// alone can't tell apart.</summary>
    private static List<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) results.Add(match);
            results.AddRange(FindAllDescendants<T>(child));
        }
        return results;
    }

    /// <summary>Like <see cref="FindDescendant{T}"/>, but stops at EITHER a
    /// TextBlock or an AccessText — whichever is found first — instead of
    /// only a TextBlock. AccessText does not derive from TextBlock and
    /// internally builds its own private (always-empty) child TextBlock, so
    /// a TextBlock-only search silently drills past the real element that
    /// paints an access-keyed Header into that decoy (see the class-level
    /// comment on the MenuItem test for how this was found).</summary>
    private static DependencyObject? FindTextElement(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is AccessText or TextBlock) return child;
            if (FindTextElement(child) is { } nested) return nested;
        }
        return null;
    }

    private static Brush? ForegroundOf(DependencyObject el) => el switch
    {
        AccessText at => at.Foreground,
        TextBlock tb => tb.Foreground,
        _ => throw new InvalidOperationException($"not a text element: {el.GetType()}"),
    };

    private static Rgb ToRgb(Brush? brush) => brush switch
    {
        SolidColorBrush s => new Rgb(s.Color.R, s.Color.G, s.Color.B),
        _ => throw new InvalidOperationException(
            $"expected a resolved SolidColorBrush, got {brush?.GetType().Name ?? "null"}"),
    };
}
