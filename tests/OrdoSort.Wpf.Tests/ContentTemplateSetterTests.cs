using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;

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
/// For the DROP-DOWN rows it was latent, not live — all ELEVEN ComboBoxes in
/// the app (round 1 said "nine" while enumerating eleven; corrected in fix
/// round 2) either set ItemTemplate (three: SettingsWindow's
/// process-order/naming-mode pickers via KvpValueTemplate, its font picker via
/// FontChoiceTemplate) or carry plain-string content (eight: MainWindow's tile
/// filter, BulkRenameWindow's case picker, SettingsWindow's sound picker,
/// MatchMergeWindow's three header pickers, PrintPreviewWindow's printer list,
/// SettingsWindow's editable section picker). Zero DataType-templated
/// ComboBoxes, zero ItemTemplateSelector anywhere in src/ outside Theme/.
///
/// For the CLOSED FACE it was live and had been all along, which round 1 got
/// backwards — see
/// <see cref="ClosedComboBoxStillShowsTheSelectedItemLegibly"/>: removing the
/// blanket Setter silently repaired a real dark-mode WCAG failure (1.44:1 ->
/// 12.23:1) on the six <c>&lt;ComboBoxItem&gt;</c>-children instances.
///
/// The decisive reason Task 8b chose the selector remedy over a one-instance
/// patch was that it "leaves the next author no hole" — which was only true of
/// the next ListBox author. The only thing standing between the next COMBOBOX
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

    /// <summary>Part 3 of 3 — the CLOSED ComboBox, i.e. the face the user looks
    /// at when nothing is dropped down. It is a separate ContentPresenter inside
    /// the ComboBox's OWN template, bound to
    /// <c>SelectionBoxItem</c>/<c>SelectionBoxItemTemplate</c>, and there is no
    /// SelectionBoxItemTemplateSelector — so every question about the closed
    /// face is a question about which DataTemplate
    /// <c>ComboBox.UpdateSelectionBoxItem</c> puts in that property, and about
    /// whether that template's bindings can still resolve OUT THERE, outside
    /// any ComboBoxItem.
    ///
    /// It runs over all THREE shapes the app actually ships (see
    /// <see cref="SelectionBoxItemTemplateComesFromTheSelectedItemNeverFromItsContainer"/>
    /// for the mechanism that decides which template each gets) and BOTH
    /// palettes, because the two facts this covers were each invisible from one
    /// angle:
    ///
    /// * The <c>&lt;ComboBoxItem&gt;</c>-children shape (MainWindow's tile
    ///   filter, BulkRenameWindow's case picker, the four Settings sound
    ///   pickers) was a LIVE dark-mode WCAG failure until fix round 1 removed
    ///   the blanket ContentTemplate Setter: the item IS a ContentControl, so
    ///   its ContentTemplate — the Setter's, whose Foreground binding is a
    ///   FindAncestor on ComboBoxItem that cannot resolve on the selection
    ///   box — became the SelectionBoxItemTemplate, and the closed face fell
    ///   back to the DP default, BLACK. Measured on screen: (0,0,0) on dark
    ///   Theme.Surface (38,41,45) = 1.44:1; after, (233,235,238) = 12.23:1.
    /// * The <c>ItemTemplate</c> shape (the two KvpValueTemplate pickers and
    ///   the FontChoiceTemplate font picker) had the IDENTICAL failure from the
    ///   identical cause, was untouched by that round, and was still live at
    ///   `8bdab38` — 1.44:1 on screen in dark mode, three times over.
    ///
    /// The version of this test that shipped in fix round 1 drove only
    /// <c>ItemsSource = string[]</c>, the ONE shape whose
    /// SelectionBoxItemTemplate is null on both sides of every change here. It
    /// passed before and after because the path it was named for was never
    /// entered. Hence the shape parameter — and hence
    /// <see cref="ClosedFaceProbe"/> asserting the SelectionBoxItemTemplate it
    /// expects, so a future edit that quietly stops exercising a path fails
    /// instead of going quiet again.
    ///
    /// Both assertions are load-bearing, and in opposite palettes: the ratio
    /// catches dark mode (1.44:1), and the exact-colour comparison catches
    /// light mode, where the very same broken binding produces BLACK on white —
    /// 21.00:1, a perfectly legible wrong answer. Checking only contrast is why
    /// a full theme audit walked past this three times.</summary>
    [Theory]
    [MemberData(nameof(ClosedFaceCases))]
    public void ClosedComboBoxStillShowsTheSelectedItemLegibly(string shape, string schemeKey) => _fx.Invoke(() =>
    {
        var scheme = ThemePalette.FindScheme(schemeKey)!;
        var palette = scheme.Palette;
        ThemeManager.Apply(_fx.App, scheme);

        var (combo, expectedText, expectsSelectionBoxTemplate) = ClosedFaceProbe(shape);
        var window = OffScreenWindow(combo);
        try
        {
            window.Show();
            window.UpdateLayout();
            combo.SelectedIndex = 0;
            PumpRender();
            window.UpdateLayout();

            Assert.False(combo.IsDropDownOpen);

            var face = FindAllDescendants<TextBlock>(combo).FirstOrDefault(t => t.Text == expectedText)
                ?? throw new InvalidOperationException(
                    $"the closed ComboBox [{shape}] does not display the selected item's text at all; " +
                    $"visible texts: [{string.Join(", ", FindAllDescendants<TextBlock>(combo).Select(t => $"\"{t.Text}\""))}]");

            var fg = Assert.IsAssignableFrom<SolidColorBrush>(face.Foreground);
            var rgb = new Rgb(fg.Color.R, fg.Color.G, fg.Color.B);
            var ratio = ThemePalette.ContrastRatio(rgb, palette.Surface);

            Assert.True(ratio >= 4.5,
                $"closed ComboBox face [{shape}, {schemeKey}]: " +
                $"{rgb} on Theme.Surface {palette.Surface} = {ratio:F2}:1");
            // ...and it must be Theme.Text, not merely something legible. A
            // failed FindAncestor Foreground binding yields the DP default —
            // black — which passes the ratio check on the light palette at
            // 21.00:1 while being exactly as broken as the dark one.
            Assert.Equal(palette.Text, rgb);

            // LAST, deliberately: the path this shape is here to exercise,
            // pinned rather than assumed, so a future edit that stops driving
            // it fails instead of going quiet the way the round-1 version of
            // this test did. It runs after the measurements so that a
            // regression in what the USER sees is what gets printed first.
            if (expectsSelectionBoxTemplate)
                Assert.Same(combo.ItemTemplate, combo.SelectionBoxItemTemplate);
            else
                Assert.Null(combo.SelectionBoxItemTemplate);
        }
        finally { window.Close(); }
    });

    public static TheoryData<string, string> ClosedFaceCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var shape in new[] { "itemssource-strings", "comboboxitem-children", "itemtemplate-kvp", "itemtemplate-font" })
        foreach (var s in ThemePalette.Schemes)
            data.Add(shape, s.Key);
        return data;
    }

    /// <summary>The four closed-face shapes, each built the way a real call
    /// site builds it and — for the two ItemTemplate ones — through the actual
    /// SHIPPED template and the actual production choice arrays, resolved by
    /// key out of the loaded Styles.xaml. Returns the text the closed face must
    /// show and whether SelectionBoxItemTemplate is expected to be the
    /// ItemTemplate (as opposed to null).</summary>
    private (ComboBox Combo, string Text, bool HasSelectionBoxTemplate) ClosedFaceProbe(string shape)
    {
        switch (shape)
        {
            // MatchMergeWindow's three header pickers, PrintPreviewWindow's
            // printer list, SettingsWindow's editable section picker.
            case "itemssource-strings":
                return (new ComboBox { ItemsSource = new[] { "Active only", "All", "Hidden" } },
                        "Active only", false);

            // MainWindow's tile filter, BulkRenameWindow's case picker, and the
            // four Settings sound pickers. The item is a ContentControl, so
            // UpdateSelectionBoxItem takes ITS ContentTemplate — which is
            // exactly what the blanket Setter used to supply.
            case "comboboxitem-children":
            {
                var combo = new ComboBox();
                foreach (var s in new[] { "Active only", "All", "Hidden" })
                    combo.Items.Add(new ComboBoxItem { Content = s });
                return (combo, "Active only", false);
            }

            // SettingsWindow's "Process order" (Filing) and naming-mode
            // (Destinations) pickers.
            case "itemtemplate-kvp":
                return (new ComboBox
                        {
                            ItemTemplate = (DataTemplate)_fx.App.Resources["KvpValueTemplate"],
                            ItemsSource = SettingsViewModel.SortChoices,
                        },
                        SettingsViewModel.SortChoices[0].Value, true);

            // SettingsWindow's "App font" picker (Appearance).
            case "itemtemplate-font":
                return (new ComboBox
                        {
                            ItemTemplate = (DataTemplate)_fx.App.Resources["FontChoiceTemplate"],
                            ItemsSource = SettingsViewModel.FontChoices,
                        },
                        SettingsViewModel.FontChoices[0].Value, true);

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown closed-face shape");
        }
    }

    /// <summary>The mechanism the two comments above rest on, stated so it can
    /// be falsified instead of believed. Fix round 1 wrote — in
    /// <c>Styles.xaml</c>, in PlainStringOnlyTemplateSelector's doc comment and
    /// in its report — that <c>ComboBox.UpdateSelectionBoxItem</c> "copies the
    /// SELECTED CONTAINER's ContentTemplate" into SelectionBoxItemTemplate.
    /// That is wrong, and the wrongness had consequences: it is why the closed
    /// face was pinned with the one item shape (<c>ItemsSource = string[]</c>)
    /// whose containers get a template but whose selection box never does, so
    /// the test could not fail.
    ///
    /// What it actually does (PresentationFramework 8.0.27,
    /// ComboBox.UpdateSelectionBoxItem): it reads <c>InternalSelectedItem</c> —
    /// the ITEM — and if that item is itself a <see cref="ContentControl"/> it
    /// unwraps to <c>item.Content</c> and takes <c>item.ContentTemplate</c>;
    /// otherwise it takes the ComboBox's own <c>ItemTemplate</c>. The generated
    /// container is never consulted at all. The two probes below say exactly
    /// that: give the CONTAINERS a template via ItemContainerStyle and the
    /// selection box ignores it; give the ITEM one and the selection box takes
    /// it.
    ///
    /// This matters beyond bookkeeping. "Container" and "item" coincide for
    /// <c>&lt;ComboBoxItem&gt;</c> children — which is why the wrong sentence
    /// still described that case correctly — and diverge for every other shape,
    /// including the <c>ItemTemplate</c> shape whose live dark-mode failure the
    /// wrong sentence steered three review rounds away from.</summary>
    [Fact]
    public void SelectionBoxItemTemplateComesFromTheSelectedItemNeverFromItsContainer() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);

        var containersTemplate = ProbeRowTemplate();
        var itemsOwnTemplate = ProbeRowTemplate();
        Assert.NotSame(containersTemplate, itemsOwnTemplate);

        // An explicit ItemContainerStyle is the only way to put a
        // ContentTemplate on the CONTAINERS without also putting one anywhere
        // else — the shape the wrong sentence predicts would drive the closed
        // face.
        Style ContainerStyle() => new(typeof(ComboBoxItem))
        {
            Setters = { new Setter(ComboBoxItem.ContentTemplateProperty, containersTemplate) },
        };

        // (a) a plain, non-ContentControl item: the container has a template,
        //     the selection box has none.
        var plain = new ComboBox { ItemContainerStyle = ContainerStyle() };
        plain.Items.Add("Active only");
        plain.SelectedIndex = 0;
        WithOpenDropDown(plain, container =>
        {
            Assert.Same(containersTemplate, container.ContentTemplate);
            Assert.Null(plain.ItemTemplate);
            Assert.Null(plain.SelectionBoxItemTemplate);
        });

        // (b) an item that IS a ContentControl and carries its own template:
        //     the selection box takes the ITEM's, still not the container's,
        //     and unwraps the item to its Content.
        var wrapping = new ComboBox { ItemContainerStyle = ContainerStyle() };
        wrapping.Items.Add(new Label { Content = RowLabel, ContentTemplate = itemsOwnTemplate });
        wrapping.SelectedIndex = 0;
        WithOpenDropDown(wrapping, container =>
        {
            Assert.Same(containersTemplate, container.ContentTemplate);
            Assert.Same(itemsOwnTemplate, wrapping.SelectionBoxItemTemplate);
            Assert.NotSame(container.ContentTemplate, wrapping.SelectionBoxItemTemplate);
            Assert.Equal(RowLabel, wrapping.SelectionBoxItem);
        });
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
    /// * Content: every cell's Content is set by WPF's OWN CalendarItem, and
    ///   the API it sets it through is STATICALLY TYPED to string —
    ///   <c>internal void SetContentInternal(string)</c>, the only
    ///   Content-setting member CalendarDayButton and CalendarButton declare.
    ///   That is a stronger fact than "it currently happens to pass a string",
    ///   which is all the round-1 wording claimed, and it is asserted by
    ///   reflection below rather than believed. (The round-1 comment also named
    ///   the wrong callers: <c>PopulateGrids</c> only CREATES the cells and
    ///   never touches Content; the setters are
    ///   <c>SetMonthModeCalendarDayButtons</c>, <c>SetMonthButtons</c> and
    ///   <c>SetYearButtons</c>, and there is no <c>SetDayButtons</c> at all.)
    ///   The DateTime rides on DataContext, never on Content. So the string
    ///   branch of ChooseTemplate is the only one these cells could ever take,
    ///   and a string-only template is behaviourally identical to a blanket
    ///   one. Content is of course a public settable property on any
    ///   ContentControl — the claim is about what can REACH these cells, which
    ///   is the "Reach" bullet below.
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

        // The API WPF sets these cells' Content through is typed to string, so
        // no future CalendarItem could hand one a DateTime without this going
        // red first. Stated as a type, not as an observation about today's
        // values — the distinction the keep-the-Setter decision turns on.
        foreach (var cell in new[] { typeof(CalendarDayButton), typeof(CalendarButton) })
        {
            var setter = cell.GetMethod("SetContentInternal",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(setter is not null, $"{cell.Name}.SetContentInternal(string) no longer exists");
            Assert.Equal(typeof(void), setter!.ReturnType);
            Assert.Equal(typeof(string), Assert.Single(setter.GetParameters()).ParameterType);
        }

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
