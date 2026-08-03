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
        foreach (var dark in new[] { false, true })
        {
            yield return new object[] { "plain-string", dark };
            yield return new object[] { "KvpValueTemplate", dark };
            yield return new object[] { "FontChoiceTemplate", dark };
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
    public void HighlightedComboBoxItemTextMeetsWcagAa(string shape, bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

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
            $"ComboBoxItem {shape} ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
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
    [Theory, MemberData(nameof(Palettes))]
    public void HighlightedMenuSubmenuHeaderTextMeetsWcagAa(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

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
                $"MenuItem submenu Header ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    public static IEnumerable<object[]> Palettes()
    {
        yield return new object[] { false };
        yield return new object[] { true };
    }

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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedListBoxItemUsesTheAccentPalette(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
            $"ListBoxItem selected ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
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
    [Theory, MemberData(nameof(Palettes))]
    public void SelectedUnlockFileListRowUsesTheAccentPalette(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var vm = new UnlockViewModel(new Config(), () => { });
        vm.Files.Add(@"C:\inbox\20240101--1111111111.pdf");
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
                $"UnlockWindow FileList selected row ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

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
    [Theory, MemberData(nameof(Palettes))]
    public void SelectedLabelMakerClientRowUsesTheAccentPalette(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
                $"LabelMakerWindow Clients selected row ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
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
    [Theory, MemberData(nameof(Palettes))]
    public void SelectedManageSavedRowUsesTheAccentPalette(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var vm = new UnlockViewModel(new Config(), () => { });
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
                $"ManageSavedWindow Saved selected row ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
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
    [Theory, MemberData(nameof(Palettes))]
    public void SelectedSettingsRouteListRowUsesTheAccentPalette(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_settings_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(),
            () => dark ? ThemePalette.Dark : ThemePalette.Light, cfgPath);
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
                    .FirstOrDefault(ti => ti.Header?.ToString() == "Destinations")
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
                $"SettingsWindow RouteList selected row ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            window.Close();
        }
    });

    // ----------------------------------------------- SubtleText de-emphasis

    public static IEnumerable<object[]> PalettesAndSelection()
    {
        foreach (var dark in new[] { false, true })
        foreach (var selected in new[] { false, true })
            yield return new object[] { dark, selected };
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
    public void LabelMakerNextNumberTextStaysSubtleUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
    public void ManageSavedPasswordStatusStaysSubtleUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var vm = new UnlockViewModel(new Config(), () => { });
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
    public void SettingsRouteListGestureTextStaysSubtleUnlessSelected(bool dark, bool selected) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var cfg = new Config();
        cfg.Routes.Add(new Route { Label = "Invoices", Path = @"C:\dest", Hotkey = "Ctrl+1" });
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_settings_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(),
            () => dark ? ThemePalette.Dark : ThemePalette.Light, cfgPath);
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
                    .FirstOrDefault(ti => ti.Header?.ToString() == "Destinations")
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
    [Theory, MemberData(nameof(Palettes))]
    public void PrintPreviewToolBarUsesThemeChrome(bool dark) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

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
        foreach (var dark in new[] { false, true })
        foreach (var state in new[] { "default", "today", "selected", "inactive", "disabled" })
            yield return new object[] { state, dark };
    }

    [Theory, MemberData(nameof(CalendarDayStates))]
    public void CalendarDayNumbersMeetWcagAa(string state, bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);

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
                $"calendar day {state} ({(dark ? "dark" : "light")}): {fg} on {bg} = {ratio:F2}");
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
