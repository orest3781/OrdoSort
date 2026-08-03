using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>A row view model with a real label. A row that renders its
/// <c>DataType</c> template shows the label; a row whose DataType lookup was
/// suppressed shows this type's name instead — the exact symptom Task 8b
/// measured on SettingsWindow's WatchList, printed by the assertions
/// below.</summary>
internal sealed class ComboRowProbeVm
{
    public string Label { get; init; } = "";
}

/// <summary>Selects <see cref="Template"/> for a <see cref="ComboRowProbeVm"/>
/// and null for anything else — a minimal stand-in for the kind of
/// <c>ItemTemplateSelector</c> a call site writes. Its only job here is to
/// prove that a call site's selector is CONSULTED at all.</summary>
internal sealed class ComboRowProbeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Template { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is ComboRowProbeVm ? Template : null;
}

/// <summary>Task 8b fix round 1 (2026-08-03): the SAME trap Task 8b removed
/// from the implicit <c>ListBoxItem</c> style was still live, unchanged, ~90
/// lines below it in the same file — a blanket
/// <c>&lt;Setter Property="ContentTemplate"&gt;</c> on the implicit
/// <c>ComboBoxItem</c> style. QC measured it directly: a ComboBox declaring a
/// <c>DataType</c> template in its own Resources and no ItemTemplate got
/// <c>ContentTemplate=NON-NULL</c> on its containers, so that template was dead
/// markup, exactly as WatchList's were.
///
/// This was never a shipping defect — all nine ComboBoxes in the app either set
/// ItemTemplate (SettingsWindow's process-order/naming-mode/font pickers, via
/// KvpValueTemplate/FontChoiceTemplate) or use plain strings (MainWindow's tile
/// filter, BulkRenameWindow's case picker, SettingsWindow's sound picker,
/// MatchMergeWindow's three header pickers, PrintPreviewWindow's printer list,
/// SettingsWindow's editable section picker). It was a LATENT one, and the
/// decisive reason Task 8b chose the selector remedy over a one-instance patch
/// was that it "leaves the next author no hole" — which was only true of the
/// next ListBox author. The only thing standing between the next COMBOBOX
/// author and a repeat of the Critical was a call-site comment reading
/// "verified no ComboBoxItem in this app sets compound (non-string) Content":
/// the same decaying-premise shape that hid the original bug through a full
/// audit.
///
/// MECHANISM (identical to WatchListRowTemplateTests', which has the long
/// account): <c>ContentPresenter.ChooseTemplate</c> tries ContentTemplate, then
/// ContentTemplateSelector, and only if BOTH are null its own built-in default
/// selector — which is what performs implicit <c>DataType</c>-keyed lookup and
/// the string/UIElement auto-wraps. A non-null ContentTemplate from ANY source
/// suppresses all of it. Note that this is a RESOLUTION order, not a
/// DependencyProperty precedence order, which is why
/// <see cref="ComboBoxHonoursACallSitesItemTemplateSelector"/> below fails
/// pre-fix even though the call site's selector is a LOCAL value and the
/// blanket template was only a Style Setter.
///
/// Every ComboBox test drives a REAL ComboBox with the REAL Theme/Styles.xaml
/// merged (HighlightContrastFixture) and takes its container from the REAL
/// ItemContainerGenerator with the drop-down genuinely open — never a loose
/// hand-configured ComboBoxItem, which would keep passing while the shipped
/// style stayed wrong.
///
/// The file is named for the TRAP rather than for ComboBox because it also
/// carries the deliberate decision NOT to convert the two remaining blanket
/// ContentTemplate Setters in Styles.xaml — CalendarDayButtonStyle and
/// CalendarButtonStyle — see
/// <see cref="CalendarCellsCanOnlyEverCarryStringContent"/>, which pins the
/// premise that decision rests on instead of asserting it in a comment.</summary>
[Collection(HighlightContrastTests.Name)]
public class ContentTemplateSetterTests
{
    private readonly HighlightContrastFixture _fx;
    public ContentTemplateSetterTests(HighlightContrastFixture fx) => _fx = fx;

    private const string RowLabel = "Failed queues";

    // ---------------------------------------------------- reproduction tests

    /// <summary>The ComboBox mirror of Task 8b's headline defect: row shapes
    /// declared as implicit <c>DataType</c> templates in the ComboBox's own
    /// <c>&lt;ComboBox.Resources&gt;</c>, with no ItemTemplate. Pre-fix the
    /// blanket ContentTemplate Setter won ChooseTemplate's step 1 and the
    /// DataType template never ran, so the row rendered
    /// <c>item.ToString()</c>.</summary>
    [Fact]
    public void ComboBoxWithDataTypeTemplateRendersIt() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var combo = new ComboBox();
        combo.Resources.Add(new DataTemplateKey(typeof(ComboRowProbeVm)), ProbeRowTemplate());
        combo.ItemsSource = new[] { new ComboRowProbeVm { Label = RowLabel } };

        WithOpenDropDown(combo, container =>
        {
            // No local ContentTemplate of any kind: the shape whose ONLY route
            // to its own templates is ChooseTemplate's DataType fallthrough.
            Assert.Null(combo.ItemTemplate);
            Assert.Null(combo.ItemContainerStyle);

            var text = FindDescendant<TextBlock>(container)
                ?? throw new InvalidOperationException("no TextBlock in the ComboBox row");
            Assert.Equal(RowLabel, text.Text);
        });
    });

    /// <summary>The second latent shape, the one Task 8b's report claims the
    /// selector remedy repairs for ListBox: <c>&lt;ComboBoxItem&gt;</c> with
    /// UIElement content, as opposed to a string. Pre-fix the blanket Setter's
    /// <c>{Binding}</c> stringified the TextBlock instead of hosting it.</summary>
    [Fact]
    public void ComboBoxItemWithElementContentHostsThatElement() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var authored = new TextBlock { Text = RowLabel };
        var combo = new ComboBox();
        combo.Items.Add(new ComboBoxItem { Content = authored });

        WithOpenDropDown(combo, container =>
        {
            var rendered = FindAllDescendants<TextBlock>(container);
            // Text first, so a failure PRINTS the defect: pre-fix this row's
            // one TextBlock read "System.Windows.Controls.TextBlock", the
            // blanket Setter's {Binding} having stringified the authored
            // element instead of hosting it.
            Assert.Equal(new[] { RowLabel }, rendered.Select(t => t.Text));
            Assert.Contains(authored, rendered);
        });
    });

    /// <summary>The third latent shape, and the clearest demonstration that
    /// ChooseTemplate's order is a RESOLUTION order rather than a
    /// DependencyProperty-precedence one: a call site that sets
    /// <c>ItemTemplateSelector</c> and no ItemTemplate. ItemsControl assigns
    /// that to each container's ContentTemplateSelector as a LOCAL value, which
    /// outranks any Style Setter — and pre-fix it was ignored anyway, because
    /// the Style's blanket ContentTemplate won step 1 before the local selector
    /// at step 2 was ever consulted. No ComboBox or ListBox in this app uses an
    /// ItemTemplateSelector today (zero occurrences outside Theme/), so this is
    /// a latent shape rather than a live instance — but it is the shape a
    /// future author is most likely to write next, and the one whose failure
    /// mode is most baffling.</summary>
    [Fact]
    public void ComboBoxHonoursACallSitesItemTemplateSelector() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var combo = new ComboBox
        {
            ItemTemplateSelector = new ComboRowProbeTemplateSelector { Template = ProbeRowTemplate() },
            ItemsSource = new[] { new ComboRowProbeVm { Label = RowLabel } },
        };

        WithOpenDropDown(combo, container =>
        {
            Assert.Null(combo.ItemTemplate);
            Assert.IsType<ComboRowProbeTemplateSelector>(container.ContentTemplateSelector);

            var text = FindDescendant<TextBlock>(container)
                ?? throw new InvalidOperationException("no TextBlock in the ComboBox row");
            Assert.Equal(RowLabel, text.Text);
        });
    });

    // ------------------------------------------------------------- the guards

    /// <summary>THE GUARD the reproduction tests trade against, part 1 of 3:
    /// the plain-string ComboBoxItem the Setter was written for must keep the
    /// themed template. Asserts the SHIPPED selector and template objects,
    /// resolved BY KEY out of the real loaded Styles.xaml, reached a genuinely
    /// generated container — and that the container's own ContentTemplate
    /// stays null, which is the entire point of the remedy.</summary>
    [Fact]
    public void PlainStringComboBoxRowStillGetsTheThemedTemplate() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: true);

        var combo = new ComboBox { ItemsSource = new[] { "Active only", "All", "Hidden" } };

        WithOpenDropDown(combo, container =>
        {
            Assert.Null(container.ContentTemplate);
            var selector = Assert.IsType<PlainStringOnlyTemplateSelector>(container.ContentTemplateSelector);
            Assert.Same(_fx.App.Resources["PlainStringComboItemSelector"], selector);
            Assert.Same(_fx.App.Resources["PlainStringComboItemTemplate"], selector.PlainStringTemplate);
            Assert.Same(selector.PlainStringTemplate, selector.SelectTemplate(container.Content, container));

            var text = FindDescendant<TextBlock>(container)
                ?? throw new InvalidOperationException("no TextBlock in the string row");
            Assert.Equal("Active only", text.Text);
        });
    });

    /// <summary>Part 2 of 3 — the contrast the themed template actually buys,
    /// on a REAL generated container rather than the loose ComboBoxItem
    /// <see cref="HighlightContrastTests.HighlightedComboBoxItemTextMeetsWcagAa"/>
    /// uses. Dark mode specifically: this app's dark Theme.Accent is itself a
    /// near-white grey, so an unfixed highlighted row is Theme.Text on Accent —
    /// genuinely illegible (measured 1.27:1 in Task 1), not merely off-brand.
    /// Proven load-bearing the same way its ListBox twin was: deleting the
    /// Foreground binding from PlainStringComboItemTemplate turns this red at
    /// Theme.Text (233,235,238) instead of AccentText.</summary>
    [Fact]
    public void HighlightedPlainStringComboBoxRowReadsAsAccentText() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: true);
        var palette = ThemePalette.Dark;

        var combo = new ComboBox { ItemsSource = new[] { "Active only", "All", "Hidden" } };

        WithOpenDropDown(combo, container =>
        {
            ForceHighlighted(container);
            Realize(container);

            var text = FindDescendant<TextBlock>(container)
                ?? throw new InvalidOperationException("no TextBlock in the string row");
            var fg = Assert.IsAssignableFrom<SolidColorBrush>(text.Foreground);
            Assert.Equal((palette.AccentText.R, palette.AccentText.G, palette.AccentText.B),
                (fg.Color.R, fg.Color.G, fg.Color.B));
        });
    });

    /// <summary>Part 3 of 3, and the one genuinely new risk narrowing THIS
    /// Setter carries (the ListBox one had no equivalent): the CLOSED
    /// ComboBox. Its face is a separate ContentPresenter inside the ComboBox
    /// template bound to <c>SelectionBoxItem</c>/<c>SelectionBoxItemTemplate</c>,
    /// and <c>ComboBox.UpdateSelectionBoxItem</c> copies the selected
    /// CONTAINER's ContentTemplate into that property — a DataTemplate this
    /// remedy deliberately stops setting. There is no
    /// SelectionBoxItemTemplateSelector, so the closed face necessarily changes
    /// path here, from "the themed template with a FindAncestor Foreground
    /// binding that cannot resolve outside a ComboBoxItem" to WPF's plain
    /// string auto-wrap. Both land on Theme.Text; this pins that the visible
    /// text survives and stays legible against Theme.Surface in BOTH
    /// palettes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedComboBoxStillShowsTheSelectedItemLegibly(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;

        var combo = new ComboBox { ItemsSource = new[] { "Active only", "All", "Hidden" } };
        var window = OffScreenWindow(combo);
        try
        {
            window.Show();
            window.UpdateLayout();
            combo.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            Assert.False(combo.IsDropDownOpen);
            var face = FindAllDescendants<TextBlock>(combo).FirstOrDefault(t => t.Text == "Active only")
                ?? throw new InvalidOperationException(
                    "the closed ComboBox does not display the selected item's text at all; " +
                    $"visible texts: [{string.Join(", ", FindAllDescendants<TextBlock>(combo).Select(t => $"\"{t.Text}\""))}]");

            var fg = Assert.IsAssignableFrom<SolidColorBrush>(face.Foreground);
            var ratio = ThemePalette.ContrastRatio(
                new Rgb(fg.Color.R, fg.Color.G, fg.Color.B), palette.Surface);
            Assert.True(ratio >= 4.5,
                $"closed ComboBox face ({(dark ? "dark" : "light")}): " +
                $"{fg.Color} on {palette.Surface} = {ratio:F2}");
        }
        finally { window.Close(); }
    });

    // ------------------------------------------- the Calendar styles, decided

    /// <summary>CalendarDayButtonStyle and CalendarButtonStyle still carry a
    /// blanket <c>ContentTemplate</c> Setter, of exactly the shape this round
    /// removed from ComboBoxItem, and that is DELIBERATE. The trap needs three
    /// things to fire: content that is not a string, a call site able to supply
    /// it, and a style able to reach that call site. A Calendar cell has none
    /// of them, and this test pins that rather than leaving it asserted in a
    /// comment — because "verified nothing in this app does X" is precisely the
    /// decaying premise that hid Task 8b's Critical.
    ///
    /// * Content: every cell's Content is set by WPF's OWN
    ///   <c>CalendarItem.PopulateGrids</c>/<c>SetDayButtons</c>, to a
    ///   preformatted day-number or month/year STRING. It is not databound and
    ///   there is no public API to change it — the DataContext carries the
    ///   DateTime, the Content never does. So the string branch of
    ///   ChooseTemplate is the only one these cells could ever take, and a
    ///   string-only template is behaviourally identical to a blanket one.
    /// * Reach: both styles are KEYED, and are attached only through
    ///   Calendar's own CalendarDayButtonStyle/CalendarButtonStyle Setters
    ///   (see that Style's comment for why an implicit TargetType style cannot
    ///   work here at all). Nothing else in the app can pick them up, so there
    ///   is no "next author" to leave a hole for — which was the whole argument
    ///   for converting ListBoxItem and ComboBoxItem.
    ///
    /// If a future .NET ever changed PopulateGrids to hand these cells a
    /// DateTime (or anything else non-string), this test goes red and points
    /// straight at the two Setters that would then need the same treatment.
    /// The contrast those templates protect is covered separately and
    /// pixel-accurately by
    /// <see cref="HighlightContrastTests.CalendarDayNumbersMeetWcagAa"/>.</summary>
    [Fact]
    public void CalendarCellsCanOnlyEverCarryStringContent() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        // Keyed, and NOT implicit: an implicit entry would be keyed by the
        // Type itself, and would let these Setters reach a CalendarDayButton
        // some other call site created with content of its own choosing.
        Assert.IsType<Style>(_fx.App.Resources["CalendarDayButtonStyle"]);
        Assert.IsType<Style>(_fx.App.Resources["CalendarButtonStyle"]);
        Assert.False(_fx.App.Resources.Contains(typeof(CalendarDayButton)));
        Assert.False(_fx.App.Resources.Contains(typeof(CalendarButton)));

        var calendar = new Calendar { DisplayDate = new DateTime(2024, 6, 1) };
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

            var days = FindAllDescendants<CalendarDayButton>(calendar);
            Assert.NotEmpty(days);
            Assert.All(days, d => Assert.IsType<string>(d.Content));
            // ...and the DateTime really does live on DataContext, never on
            // Content — the distinction the whole decision turns on.
            Assert.All(days, d => Assert.IsType<DateTime>(d.DataContext));

            // The month/year grids use CalendarButton instead, and are only
            // built once the Calendar leaves month mode.
            calendar.DisplayMode = CalendarMode.Year;
            PumpRender();
            window.UpdateLayout();

            var months = FindAllDescendants<CalendarButton>(calendar);
            Assert.NotEmpty(months);
            Assert.All(months, m => Assert.IsType<string>(m.Content));
        }
        finally { window.Close(); }
    });

    // -------------------------------------------------------------- plumbing

    /// <summary>The DataTemplate a call site would write in XAML, built the
    /// same way XAML builds it (parsed, DataType-keyed) rather than through a
    /// FrameworkElementFactory, so nothing about the shape under test is an
    /// artefact of how the template was constructed.</summary>
    private static DataTemplate ProbeRowTemplate()
    {
        var t = typeof(ComboRowProbeVm);
        var xaml =
            $"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            $"xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
            $"xmlns:p='clr-namespace:{t.Namespace};assembly={t.Assembly.GetName().Name}' " +
            $"DataType='{{x:Type p:{t.Name}}}'>" +
            $"<TextBlock Text='{{Binding Label}}' /></DataTemplate>";
        return (DataTemplate)XamlReader.Parse(xaml);
    }

    /// <summary>Shows the ComboBox in a real off-screen window, opens the
    /// drop-down for real (a ComboBox hosts its ItemsPresenter inside a Popup,
    /// so nothing generates containers until it opens), and hands the body the
    /// generated container for row 0.</summary>
    private static void WithOpenDropDown(ComboBox combo, Action<ComboBoxItem> body)
    {
        var window = OffScreenWindow(combo);
        try
        {
            window.Show();
            window.UpdateLayout();

            combo.IsDropDownOpen = true;
            PumpRender();
            window.UpdateLayout();

            var container = combo.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem
                ?? throw new InvalidOperationException(
                    $"ComboBox row 0 never realized a container (items={combo.Items.Count}, " +
                    $"dropDownOpen={combo.IsDropDownOpen})");
            // The drop-down is its own top-level visual tree, so the host
            // window's layout pass never reaches it — realize the container
            // directly, exactly as HighlightContrastTests realizes a loose one.
            Realize(container);

            body(container);
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    }

    private static Window OffScreenWindow(UIElement content) => new()
    {
        Content = content, Width = 300, Height = 200,
        Left = -20000, Top = 0, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    /// <summary>ApplyTemplate + Measure + Arrange is what materializes a
    /// ControlTemplate's parts and a ContentPresenter's CHOSEN template — i.e.
    /// the step every assertion here depends on. Mirrors
    /// HighlightContrastTests.Realize (private there).</summary>
    private static void Realize(FrameworkElement el)
    {
        el.ApplyTemplate();
        el.Measure(new Size(400, 200));
        el.Arrange(new Rect(0, 0, 400, 200));
        el.UpdateLayout();
    }

    /// <summary>IsHighlighted is a read-only DP normally flipped only by real
    /// mouse/keyboard interaction; Task 1 proved forcing it through the private
    /// IsHighlightedPropertyKey reproduces the identical state (cross-checked
    /// against a live dropdown there). Mirrors HighlightContrastTests'
    /// identically-named private helper.</summary>
    private static void ForceHighlighted(DependencyObject item)
    {
        var field = item.GetType().GetField("IsHighlightedPropertyKey",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{item.GetType()} has no private static IsHighlightedPropertyKey field");
        item.SetValue((DependencyPropertyKey)field.GetValue(null)!, true);
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
}
