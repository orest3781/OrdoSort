using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>A control a screen reader cannot name is a control it cannot
/// describe. The 2026-08-22 UI audit counted <c>AutomationProperties.Name</c>
/// declarations per window and found 38 in Settings against **zero** in
/// Filename list, List reformatter and Page counts, with every DataGrid in the
/// app anonymous (UI-04). Confirmed live through UI Automation against the
/// running app: both Edit controls in Filename list reported an empty Name, the
/// grid reported an empty Name, and nothing anywhere reported a LabeledBy.
///
/// <para><b>Why this asks the automation peer instead of counting attributes.</b>
/// A declaration count is the wrong measure in both directions. A button whose
/// Content is "Close" needs no attribute — WPF derives the name from the
/// content, and a screen reader says "Close" — so counting attributes
/// under-credits it. And an attribute set to <c>""</c> counts as present while
/// naming nothing. <c>AutomationPeer.GetName()</c> is what assistive tech
/// actually receives, so it is what this asserts.</para>
///
/// Scope is the controls a keyboard user can land on and that carry or collect
/// a value. Layout elements are not named, deliberately: naming a Grid adds
/// noise to the screen-reader tree rather than information.</summary>
[Collection(HighlightContrastTests.Name)]
public class AccessibleNameTests
{
    private readonly HighlightContrastFixture _fx;
    public AccessibleNameTests(HighlightContrastFixture fx) => _fx = fx;

    public static TheoryData<string> Windows()
    {
        var data = new TheoryData<string>();
        foreach (var name in WindowOverflowTests.Registry().Keys) data.Add(name);
        return data;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>Controls that must be able to say what they are. Value-carrying
    /// or value-collecting, and reachable.
    ///
    /// <c>Button</c> is excluded as a TYPE because WPF names one from its
    /// Content automatically and almost every button here is a word — the ones
    /// that are not (an icon glyph, an arrow) are caught anyway, because their
    /// derived name comes out empty and the assertion below is about the
    /// RESULT, not the declaration.</summary>
    /// <summary>Windows whose list rows are known to announce a type name,
    /// found when the ListBoxItem rule below was added alongside the label
    /// maker's fix (2026-09-23) and not yet fixed. Named here so every OTHER
    /// window is held to the rule now; delete an entry when its window gets
    /// the same AutomationProperties.Name fix, and never add one.</summary>
    private static readonly HashSet<string> KnownUnnamedListRows = new()
    {
        "SettingsWindow",      // RouteEditVm, WatchSectionVm, WatchEditVm rows
        "ManageSavedWindow",   // SavedPassword rows
        "UnlockWindow",        // UnlockFileRow rows
    };

    private static bool NeedsAName(DependencyObject d) => d switch
    {
        // A control that some OTHER control's template put there carries its
        // templated parent's identity, not its own: a ComboBox's editable
        // TextBox, a DatePicker's date box, DocumentViewer's find box. Naming
        // each of those individually would make a screen reader announce
        // internal parts the user never thinks of as separate things — and for
        // the WPF built-ins it is not this app's markup to fix. The outer
        // control is the thing that must be named, and it is checked on its
        // own.
        FrameworkElement { TemplatedParent: not null } => false,
        // WPF's own DocumentViewer find toolbar (Ctrl+F in Print preview). Its
        // parts are built by the framework, not by this app's markup, and the
        // box arrives with no TemplatedParent to catch it by above. Naming it
        // would mean retemplating DocumentViewer wholesale to reach one hidden
        // control — a large change to this app's rendering to paper over a gap
        // in a framework control. Excluded by name, deliberately and narrowly,
        // rather than silently widening the rule.
        FrameworkElement { Name: "FindTextBox" } => false,
        // Grid internals likewise: the grid is named, its cells are content.
        DataGridCell or DataGridColumnHeader or DataGridRow => false,
        // A list's rows are what a screen reader walks with the arrow keys.
        // A row bound to a view model with no ToString and no
        // AutomationProperties.Name announces its CLR type name — see
        // LooksLikeATypeName.
        ListBoxItem => true,
        TextBox or PasswordBox or ComboBox or DataGrid or ListBox or CheckBox or RadioButton => true,
        Button b => IsIconOnly(b),
        _ => false,
    };

    /// <summary>A button whose whole label is a glyph or an arrow — "✕", "↑",
    /// "↓", a Segoe icon codepoint. WPF derives nothing useful from those, so
    /// they are exactly the buttons that need saying out loud.</summary>
    private static bool IsIconOnly(Button b) =>
        b.Content is string s && (s.Length == 0 || s.All(c => !char.IsLetterOrDigit(c)));

    /// <summary>Enough to FIND the offender in the XAML without a second run:
    /// the binding path is what actually identifies an unnamed, empty box,
    /// where a type name and a blank value identify nothing.</summary>
    private static string Describe(DependencyObject d)
    {
        var type = d.GetType().Name;
        var bits = new List<string>();
        if (d is FrameworkElement { Name.Length: > 0 } named) bits.Add("x:Name=" + named.Name);

        foreach (var (prop, label) in new (DependencyProperty, string)[]
                 {
                     (TextBox.TextProperty, "Text"),
                     (Selector.SelectedValueProperty, "SelectedValue"),
                     (Selector.SelectedIndexProperty, "SelectedIndex"),
                     (ItemsControl.ItemsSourceProperty, "ItemsSource"),
                     (ToggleButton.IsCheckedProperty, "IsChecked"),
                 })
        {
            if (d is not FrameworkElement fe) break;
            if (BindingOperations.GetBinding(fe, prop) is { } b && b.Path?.Path is { Length: > 0 } path)
                bits.Add($"{label}={{Binding {path}}}");
        }

        switch (d)
        {
            case ContentControl { Content: string c } when c.Length > 0: bits.Add($"content=\"{c}\""); break;
            case TextBox { Text.Length: > 0 } t: bits.Add($"text=\"{Trim(t.Text)}\""); break;
        }
        return bits.Count == 0 ? type : $"{type} ({string.Join(", ", bits)})";
    }

    private static string Trim(string s) => s.Length <= 30 ? s : s[..30] + "…";

    /// <summary>"OrdoSort.Wpf.ViewModels.LabelClientVm" is not empty, so an
    /// emptiness check passes it — but it is what WPF falls back to for a
    /// list row whose item has no ToString and no AutomationProperties.Name,
    /// and a screen reader reads it out verbatim. A dotted run of three or
    /// more PascalCase identifiers is a namespace-qualified type, not
    /// anything this app shows a person.</summary>
    private static bool LooksLikeATypeName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Z]\w*(\.[A-Z]\w*){2,}$");

    [Theory, MemberData(nameof(Windows))]
    public void EveryValueCarryingControlCanSayWhatItIs(string windowName) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var probe = WindowOverflowTests.Registry()[windowName];
        var (window, cleanup) = probe.Build();
        try
        {
            AssertEveryControlCanSayWhatItIs(windowName, window);
        }
        finally
        {
            window.Close();
            cleanup?.Invoke();
        }
    });

    /// <summary>The label maker is not in the shared window registry (its
    /// layout has its own suite, LabelMakerOverflowTests), so it is walked
    /// here on its own — with a client in the list, because an empty list has
    /// no rows to name. The rows used to announce themselves as
    /// "OrdoSort.Wpf.ViewModels.LabelClientVm".</summary>
    [Fact]
    public void EveryControlInTheLabelMakerCanSayWhatItIs() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        var vm = new OrdoSort.Wpf.ViewModels.LabelMakerViewModel(null, boxLabelsPath, new FakeDialogs(),
            "Box labels");
        vm.Clients.Add(new OrdoSort.Wpf.ViewModels.LabelClientVm { Id = "MEDR", NextNumberText = "42" });
        vm.Clients.Add(new OrdoSort.Wpf.ViewModels.LabelClientVm { Id = "ACME", NextNumberText = "7" });
        vm.Selected = vm.Clients[0];
        var window = new OrdoSort.Wpf.Windows.LabelMakerWindow(vm, "Box labels", "Print preview");
        try
        {
            AssertEveryControlCanSayWhatItIs("LabelMakerWindow", window);
        }
        finally
        {
            window.Close();
            try { File.Delete(boxLabelsPath); } catch { /* best effort */ }
        }
    });

    private static void AssertEveryControlCanSayWhatItIs(string windowName, Window window)
    {
        window.Left = -20000; window.Top = 0; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Show();
        window.UpdateLayout();
        OverflowProbe.PumpRender();
        window.UpdateLayout();

        var content = (FrameworkElement)window.Content;
        var checkedCount = 0;
        var nameless = new List<string>();

        var tabs = Descendants(content).OfType<TabControl>().FirstOrDefault();
        var passes = tabs is null
            ? new[] { (object?)null }
            : tabs.Items.Cast<object>().ToArray();

        foreach (var tab in passes)
        {
            if (tabs is not null && tab is not null)
            {
                tabs.SelectedItem = tab;
                window.UpdateLayout();
                OverflowProbe.PumpRender();
                window.UpdateLayout();
            }

            foreach (var d in Descendants(content))
            {
                if (d is not UIElement el || !NeedsAName(d)) continue;
                if (d is ListBoxItem && KnownUnnamedListRows.Contains(windowName)) continue;
                if (el is FrameworkElement { IsVisible: false }) continue;
                checkedCount++;

                var peer = UIElementAutomationPeer.CreatePeerForElement(el);
                var name = peer?.GetName() ?? "";
                if (!string.IsNullOrWhiteSpace(name) && !LooksLikeATypeName(name)) continue;
                var where = tab is TabItem ti ? $"[tab {ti.Header}] " : "";
                var heard = name.Length > 0 ? $" — announced as \"{name}\"" : "";
                nameless.Add(where + Describe(d) + heard);
            }
        }

        // Distinct: a tabbed window walks shared chrome once per tab, and
        // the same offender reported five times is noise, not evidence.
        nameless = nameless.Distinct().ToList();

        // Guard the TRAVERSAL, not the count of value controls. About has
        // two text buttons and nothing else — legitimately zero — so a
        // blanket "must have found some" floor fails a window that is
        // simply small, which is a floor that gets deleted rather than
        // fixed. What actually needs proving is that the walk happened.
        Assert.True(Descendants(content).Count() > 5,
            $"{windowName}: the visual-tree walk found almost nothing, so a pass here " +
            "would prove nothing about the window");
        Assert.True(nameless.Count == 0,
            $"{windowName}: {nameless.Count} control(s) report no usable accessible name, so a " +
            $"screen reader announces the control type and nothing else (examined {checkedCount}):\n  " +
            string.Join("\n  ", nameless));
    }
}
