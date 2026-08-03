using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>A no-op IDialogService for constructing a real SettingsViewModel
/// off-screen — none of the tests below opens a dialog. (File-scoped, so it
/// does not collide with HighlightContrastTests' identically-named one.)</summary>
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

/// <summary>Task 8b (audit remediation, 2026-08-03): the Settings window's
/// Dashboard tab rendered every row as a raw type name
/// ("OrdoSort.Wpf.ViewModels.WatchSectionVm" / "…WatchEditVm") — six identical
/// unreadable rows, plus the horizontal scrollbar those wide type names
/// induce. Confirmed on-screen against a real, interactively-driven
/// OrdoSort.exe (UI Automation clicks + PrintWindow captures), not only
/// headlessly. Pre-existing since f185da8 ("fix(theme): list selection uses
/// the app palette", 2026-08-02), i.e. it predates the audit that missed it.
///
/// MECHANISM — a FOURTH face of this project's WPF precedence trap, and the
/// one that is easiest to miss because it is about the ABSENCE of a value.
/// Styles.xaml's implicit ListBoxItem style set <c>ContentTemplate</c> as a
/// plain Style Setter (a themed TextBlock for plain-string rows). A
/// ContentPresenter only performs implicit, <c>DataType</c>-keyed DataTemplate
/// lookup when its ContentTemplate is NULL — ContentPresenter.ChooseTemplate
/// tries ContentTemplate first, then ContentTemplateSelector, and only then
/// its built-in default selector, which is what does the DataType lookup. Any
/// non-null ContentTemplate, from any source, suppresses that lookup outright.
/// WatchList declares its two row shapes as implicit <c>DataType</c>
/// DataTemplates inside its own <c>&lt;ListBox.Resources&gt;</c> and sets
/// neither ItemTemplate nor ItemContainerStyle, so nothing gave its containers
/// a local ContentTemplate — the Style Setter reached them, and every
/// DataType template in WatchList's own resources became dead markup.
///
/// Every OTHER ListBox in the app escaped because it sets ItemTemplate, which
/// ItemsControl assigns to each container's ContentTemplate as a LOCAL value
/// (precedence 3) — that outranks a Style Setter (precedence 8), so those
/// lists never saw the Setter at all. That asymmetry is exactly what the false
/// comments in Styles.xaml and SettingsWindow.xaml asserted was universal
/// ("a LOCAL value there always outranks this Style's ContentTemplate
/// Setter"), and asserting it of WatchList — whose templates are NOT a local
/// ContentTemplate — is what hid this defect through an entire audit.
///
/// The remedy narrows the blanket Setter to a ContentTemplateSelector that
/// returns the themed template ONLY for plain-string content and null for
/// everything else, restoring ChooseTemplate's fallthrough for every other
/// content shape. <see cref="PlainStringSuggestionRowStillGetsTheThemedTemplate"/>
/// is the guard on the other side of that trade: the plain-string list the
/// Setter was written for (ProcessingView's SuggestList) must keep its themed
/// template, so a future author cannot fix one of these by breaking the
/// other.
///
/// Every test here drives the REAL, compiled SettingsWindow with the REAL
/// Theme/Styles.xaml merged (HighlightContrastFixture), never a hand-authored
/// stand-in — a copy of the templates would keep passing while the shipped
/// ones stayed dead.</summary>
[Collection(HighlightContrastTests.Name)]
public class WatchListRowTemplateTests
{
    private readonly HighlightContrastFixture _fx;
    public WatchListRowTemplateTests(HighlightContrastFixture fx) => _fx = fx;

    private const string SectionA = "Failed queues";
    private const string FolderA = "Failed transfers";
    private const string ColorA = "#C0392B";

    /// <summary>A real SettingsWindow, shown off-screen, parked on the
    /// Dashboard tab with two named sections holding one folder each.</summary>
    private (SettingsViewModel Vm, SettingsWindow Window, ListBox List) OpenDashboard(bool dark = false)
    {
        ThemeManager.Apply(_fx.App, dark);

        var cfg = new Config();
        cfg.WatchFolders.Add(new WatchFolder
        {
            Label = FolderA, Path = @"C:\queues\failed", Section = SectionA, Color = ColorA,
        });
        cfg.WatchFolders.Add(new WatchFolder
        {
            Label = "Rejected", Path = @"C:\queues\rejected", Section = "Scanner queues", Color = "#E67E22",
        });
        // Resolvable but nonexistent (see SelectedSettingsRouteListRowUsesTheAccentPalette's
        // own note): several SettingsViewModel properties dereference cfgPath with `!`.
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_settings_" + Guid.NewGuid(), "config.json");
        var vm = new SettingsViewModel(cfg, new NoDialogs(), () => ThemePalette.Light, cfgPath,
            uiContext: SynchronizationContext.Current);

        var window = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        window.UpdateLayout();
        PumpRender();

        var tabControl = FindDescendant<TabControl>(window)
            ?? throw new InvalidOperationException("no TabControl descendant under SettingsWindow");
        // The header literal carries an access-key marker ("Dash_board") and
        // Task 9 may re-word it; comparing on the marker-stripped text keeps
        // this test tied to the tab a user sees, not to one spelling of it.
        var dashboard = tabControl.Items.Cast<TabItem>()
                .FirstOrDefault(ti => (ti.Header?.ToString() ?? "").Replace("_", "") == "Dashboard")
            ?? throw new InvalidOperationException("no \"Dashboard\" TabItem found");
        tabControl.SelectedItem = dashboard;
        window.UpdateLayout();
        PumpRender();
        window.UpdateLayout();

        // Identify WatchList by what it is BOUND to rather than by being the
        // first ListBox found: x:Name fields on SettingsWindow are internal to
        // OrdoSort.Wpf, and "first ListBox in the window" would silently start
        // resolving a different list if the tab layout ever changes.
        var list = FindAllDescendants<ListBox>(window)
                .FirstOrDefault(lb => ReferenceEquals(lb.ItemsSource, vm.WatchRows))
            ?? throw new InvalidOperationException("no ListBox bound to WatchRows under SettingsWindow");
        list.UpdateLayout();
        return (vm, window, list);
    }

    private static ListBoxItem Row(ListBox list, int index)
    {
        list.UpdateLayout();
        PumpRender();
        return list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem
            ?? throw new InvalidOperationException($"WatchList row {index} never realized a container");
    }

    private static int IndexOfSection(SettingsViewModel vm, string header) =>
        vm.WatchRows.ToList().FindIndex(r => r is WatchSectionVm h && h.Header == header);

    private static int IndexOfFolder(SettingsViewModel vm, string label) =>
        vm.WatchRows.ToList().FindIndex(r => r is WatchEditVm w && w.Label == label);

    private static List<string> TextsIn(DependencyObject root) =>
        FindAllDescendants<TextBlock>(root).Select(t => t.Text).ToList();

    /// <summary>The headline symptom: a section header row rendered
    /// "OrdoSort.Wpf.ViewModels.WatchSectionVm" instead of the section
    /// name.</summary>
    [Fact]
    public void SectionHeaderRowShowsItsSectionName() => _fx.Invoke(() =>
    {
        var (vm, window, list) = OpenDashboard();
        try
        {
            var container = Row(list, IndexOfSection(vm, SectionA));
            var texts = TextsIn(container);

            Assert.Contains(SectionA, texts);
            Assert.DoesNotContain(texts, t => t.Contains("WatchSectionVm", StringComparison.Ordinal));
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>The same for a folder row, plus its colour chip — the chip is
    /// a Rectangle, so no amount of text-only checking would have caught its
    /// disappearance.</summary>
    [Fact]
    public void FolderRowShowsItsLabelAndColourChip() => _fx.Invoke(() =>
    {
        var (vm, window, list) = OpenDashboard();
        try
        {
            var container = Row(list, IndexOfFolder(vm, FolderA));
            var texts = TextsIn(container);

            Assert.Contains(FolderA, texts);
            Assert.DoesNotContain(texts, t => t.Contains("WatchEditVm", StringComparison.Ordinal));

            var chip = FindDescendant<Rectangle>(container)
                ?? throw new InvalidOperationException("no colour-chip Rectangle in the folder row");
            var fill = Assert.IsAssignableFrom<SolidColorBrush>(chip.Fill);
            var expected = ThemePalette.ParseColor(ColorA)!.Value;
            Assert.Equal((expected.R, expected.G, expected.B), (fill.Color.R, fill.Color.G, fill.Color.B));
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>The two per-section buttons (✎ rename, ＋ add folder). Both
    /// vanished with the template, taking two of the Dashboard tab's five
    /// affordances with them — and neither has any other entry point.</summary>
    [Fact]
    public void SectionHeaderRowHasItsRenameAndAddFolderButtons() => _fx.Invoke(() =>
    {
        var (vm, window, list) = OpenDashboard();
        try
        {
            var container = Row(list, IndexOfSection(vm, SectionA));
            var buttons = FindAllDescendants<Button>(container);
            var names = buttons
                .Select(b => (string?)b.GetValue(AutomationProperties.NameProperty) ?? "")
                .ToList();

            Assert.Equal(2, buttons.Count);
            Assert.Contains($"Rename section {SectionA}", names);
            Assert.Contains($"Add folder to section {SectionA}", names);

            var texts = TextsIn(container);
            Assert.Contains("✎", texts);
            Assert.Contains("＋", texts);
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>The inline rename TextBox must EXIST in the header row (it is
    /// collapsed until IsEditing flips). SettingsWindow.OnAddSectionClick
    /// reaches for it with FindDescendant&lt;TextBox&gt;(item); with the row
    /// template dead there was no TextBox to find, so the model change landed
    /// but focus stayed on the button and typed characters went nowhere.</summary>
    [Fact]
    public void SectionHeaderRowCarriesTheInlineRenameTextBox() => _fx.Invoke(() =>
    {
        var (vm, window, list) = OpenDashboard();
        try
        {
            var container = Row(list, IndexOfSection(vm, SectionA));
            var box = FindDescendant<TextBox>(container);

            Assert.NotNull(box);
            Assert.False(box!.IsVisible);   // collapsed until the row is being renamed
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>End to end, the way a user meets this: click "Add section" and
    /// the new header's rename box must take focus so the next keystrokes name
    /// it. This is the behavioural half of
    /// <see cref="SectionHeaderRowCarriesTheInlineRenameTextBox"/> — it fails
    /// for exactly the same reason (OnAddSectionClick's
    /// FindDescendant&lt;TextBox&gt; finds nothing), but proves the
    /// consequence rather than the structure.</summary>
    [Fact]
    public void AddSectionFocusesTheNewSectionsRenameBox() => _fx.Invoke(() =>
    {
        var (vm, window, list) = OpenDashboard();
        try
        {
            var addSection = FindAllDescendants<Button>(window)
                    .FirstOrDefault(b => (b.Content as string) == "Add section")
                ?? throw new InvalidOperationException("no \"Add section\" button on the Dashboard tab");

            addSection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // OnAddSectionClick defers the focus step to DispatcherPriority.Loaded
            // (container generation is async); draining that queue runs it.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();

            var newHeader = vm.WatchRows.OfType<WatchSectionVm>().FirstOrDefault(h => h.IsEditing)
                ?? throw new InvalidOperationException("no section header entered rename mode");
            Assert.Equal("New section", newHeader.Header);

            var focused = FocusManager.GetFocusedElement(window) as TextBox;
            Assert.NotNull(focused);
            Assert.Equal("New section", focused!.Text);
        }
        finally { window.Close(); vm.Dispose(); }
    });

    /// <summary>THE GUARD on the other side of the fix, part 1 of 2. The
    /// ListBoxItem template Setter exists for a real reason:
    /// ProcessingView's SuggestList is the app's ONE ListBox with plain-string
    /// items and no ItemTemplate, and WPF's auto-wrap for string Content
    /// builds a TextBlock that resolves the APPLICATION-level implicit
    /// TextBlock style (Theme.Text) instead of inheriting the container's
    /// selected Theme.AccentText — a Style Setter outranking inheritance, the
    /// first face of this project's precedence trap. Narrowing that Setter to
    /// a ContentTemplateSelector must not stop reaching this list.
    ///
    /// Drives the REAL ProcessingView with a real ShellViewModel and a
    /// genuinely open suggestion popup, and asserts the generated container
    /// resolved the very same PlainStringListItemTemplate object that
    /// Styles.xaml declares — resolved BY KEY out of the real, loaded
    /// dictionary, so this cannot pass against a hand-copied stand-in.
    ///
    /// It does NOT select the row: SuggestList_SelectionChanged treats any
    /// selection as "take this suggestion" (it sets TypedName and dismisses
    /// the popup), so a selected suggestion row is not a reachable state in
    /// production. <see cref="SelectedPlainStringListRowReadsAsAccentText"/>
    /// covers the selected-contrast half in a list where selection persists.
    ///
    /// Passes both before and after Task 8b by design — its job is to fail if
    /// a future author "fixes" a WatchList-shaped list by deleting the
    /// plain-string path outright.</summary>
    [Fact]
    public void PlainStringSuggestionRowStillGetsTheThemedTemplate() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: true);

        var shellFx = new ShellFixture();
        shellFx.WriteNamesFile("SMITH JOHN", "SMITH JANE");
        shellFx.AddInboxFile("20240115--111111.pdf");
        shellFx.Shell.Initialize();

        var view = new ProcessingView { DataContext = shellFx.Shell };
        var window = new Window
        {
            Content = view, Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            shellFx.Shell.StartProcessing();
            window.UpdateLayout();

            // Typing a prefix is what opens the popup (HasSuggestions is
            // private-set; this is the real path, not a forced flag).
            shellFx.Shell.TypedName = "SMI";
            Assert.True(shellFx.Shell.HasSuggestions, "no suggestions for the typed prefix");
            PumpRender();
            window.UpdateLayout();

            var popup = FindLogicalDescendant<Popup>(view)
                ?? throw new InvalidOperationException("no suggestion Popup under ProcessingView");
            Assert.True(popup.IsOpen);
            // popup.Child is rooted in the popup's OWN visual tree (a separate
            // top-level window), not the host window's, so window.UpdateLayout()
            // never reaches it — measure/arrange it directly, the same way
            // HighlightContrastTests.Realize materializes a loose element.
            var card = (FrameworkElement)popup.Child;
            card.Measure(new Size(400, 300));
            card.Arrange(new Rect(0, 0, 400, 300));
            card.UpdateLayout();

            var list = FindDescendant<ListBox>(card)
                ?? throw new InvalidOperationException("no ListBox inside the suggestion popup");

            // Genuinely the plain-string shape the central style serves: no
            // local ContentTemplate from an ItemTemplate, no ItemContainerStyle.
            Assert.Null(list.ItemTemplate);
            Assert.Null(list.ItemContainerStyle);

            var container = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException(
                    $"suggestion row 0 never realized a container (items={list.Items.Count})");

            // The SHIPPED template object, resolved by key from the real
            // Styles.xaml — not merely "some template".
            AssertShippedPlainStringTemplateReached(container);

            var text = FindDescendant<TextBlock>(container)
                ?? throw new InvalidOperationException("no TextBlock in the suggestion row");
            Assert.Equal((string)list.Items[0]!, text.Text);
        }
        finally { window.Close(); shellFx.Dispose(); }
    });

    /// <summary>THE GUARD, part 2 of 2: the selected-row contrast the
    /// plain-string template actually protects, in a real ListBox whose
    /// selection persists (SuggestList's does not — see part 1). Dark mode
    /// specifically, because this app's dark Theme.Accent is itself a
    /// near-white grey: an unfixed row is Theme.Text on Accent, i.e. genuinely
    /// illegible rather than merely off-brand.
    ///
    /// Uses a bare ListBox of strings inside a real off-screen window rather
    /// than the loose ListBoxItem of
    /// HighlightContrastTests.SelectedListBoxItemUsesTheAccentPalette, so the
    /// container comes from the real ItemContainerGenerator — the same path
    /// that decides whether the central style's template reaches a generated
    /// row at all. Proven load-bearing: temporarily deleting the Foreground
    /// binding from PlainStringListItemTemplate turns this red at Theme.Text
    /// (see the task report for the pasted output).</summary>
    [Fact]
    public void SelectedPlainStringListRowReadsAsAccentText() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: true);
        var palette = ThemePalette.Dark;

        var list = new ListBox { ItemsSource = new[] { "Invoices", "Statements" } };
        var window = new Window
        {
            Content = list, Width = 300, Height = 200,
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();
            list.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            var container = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("string row 0 never realized a container");
            AssertShippedPlainStringTemplateReached(container);

            var text = FindDescendant<TextBlock>(container)
                ?? throw new InvalidOperationException("no TextBlock in the string row");
            Assert.Equal("Invoices", text.Text);

            var fg = Assert.IsAssignableFrom<SolidColorBrush>(text.Foreground);
            Assert.Equal((palette.AccentText.R, palette.AccentText.G, palette.AccentText.B),
                (fg.Color.R, fg.Color.G, fg.Color.B));
        }
        finally { window.Close(); }
    });

    // -------------------------------------------------------------- plumbing

    /// <summary>Proves the SHIPPED plain-string path reached this generated
    /// container: the implicit ListBoxItem style's Setter put the real
    /// Styles.xaml selector on it, and that selector carries the real
    /// Styles.xaml template. Note the row's own ContentTemplate stays null by
    /// design — that is the whole point of the remedy, since a non-null
    /// ContentTemplate is what suppressed WatchList's DataType templates. The
    /// chosen template is therefore asserted through the selector rather than
    /// through ListBoxItem.ContentTemplate; what it actually RENDERED is
    /// asserted separately by each caller.</summary>
    private void AssertShippedPlainStringTemplateReached(ListBoxItem container)
    {
        Assert.Null(container.ContentTemplate);
        var selector = Assert.IsType<PlainStringOnlyTemplateSelector>(container.ContentTemplateSelector);
        Assert.Same(_fx.App.Resources["PlainStringListItemSelector"], selector);
        Assert.Same(_fx.App.Resources["PlainStringListItemTemplate"], selector.PlainStringTemplate);
        Assert.Same(selector.PlainStringTemplate,
            selector.SelectTemplate(container.Content, container));
    }

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

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

    /// <summary>A Popup renders into its OWN top-level visual tree, so the
    /// suggestion list is not a visual descendant of ProcessingView; the Popup
    /// element itself is reachable through the logical tree from the panel
    /// that declares it.</summary>
    private static T? FindLogicalDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) return match;
            if (FindLogicalDescendant<T>(node) is { } nested) return nested;
        }
        return null;
    }
}
