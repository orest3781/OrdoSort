using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>A no-op IDialogService, so a real SettingsWindow can be built
/// off-screen (HighlightContrastTests declares its own `file`-scoped copy of
/// this, which is invisible from here — hence the duplicate rather than a
/// shared helper).</summary>
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

/// <summary>Task 8 (audit remediation, 2026-08-02), Step 3: Styles.xaml's
/// shared bronze focus ring (<c>BronzeFocusVisual</c>) was wired only to
/// Button and ToggleButton — CheckBox, RadioButton, ComboBox, ListBoxItem and
/// TabItem fell back to the stock OS dashed rectangle, the one place the
/// theme's focus language broke. Fixed by adding a
/// <c>FocusVisualStyle="{StaticResource BronzeFocusVisual}"</c> Setter to
/// each of those five implicit styles, plus SettingsWindow.xaml's own keyed
/// "ThemeCard" RadioButton style — an <c>x:Key</c>'d Style with no
/// <c>BasedOn</c> bypasses implicit-by-type lookup entirely, so the Setter
/// could never have reached those three theme-picker cards from Styles.xaml
/// (covered by <see cref="SettingsThemeCardRadioButtonShowsTheBronzeFocusRing"/>)
/// — and, for CheckBox/RadioButton only, a matching Style TRIGGER, because a
/// plain Setter there is outranked by the stock template's own
/// HasContent trigger and was measured to change nothing at all. That last
/// one is the reason this file renders pixels instead of reading the
/// property: the Setter looked right in the diff and did nothing.
///
/// Every case below proves the ring by RENDERING REAL PIXELS, not by reading
/// back the FocusVisualStyle property — a resolved-property assertion cannot
/// see a differently-templated control swallowing the visual, and this
/// project has already shipped one decorative test that passed against a
/// provably unfixed build. Each assertion is a DIFFERENTIAL: the same band
/// around the same control is scanned twice, once with the control unfocused
/// and once focused, and the unfocused pass must contain NO AccentBronze.
/// That matters because AccentBronze is not unique to the focus ring —
/// TextBox/PasswordBox paint their own focused border with it and both
/// TabItem templates paint a selected-tab underline with it — so a
/// focused-only scan could pass on decoration that has nothing to do with
/// the ring.
///
/// THREE non-obvious mechanics, all established empirically here rather than
/// assumed (an earlier draft of this file got the first two wrong and was
/// red; the third turned out to be a real production defect, not a test bug):
///
/// 1. THE RENDER ROOT MUST BE THE WINDOW. WPF draws a focus visual as a
///    <c>FocusVisualAdorner</c> in the AdornerLayer supplied by the Window
///    template's own AdornerDecorator, which is a SIBLING of
///    <c>Window.Content</c>, not a descendant of it. Rendering the content
///    element misses the ring entirely — measured: rendering the content
///    Border found 0 bronze pixels for the very same focused Button whose
///    Window render found 364.
///
/// 2. FOCUS VISUALS ARE OFF BY DEFAULT ON THIS MACHINE, AND A SYNTHETIC Tab
///    DOES NOT TURN THEM ON. KeyboardNavigation only builds the adorner when
///    <c>SystemParameters.KeyboardCues || KeyboardNavigation.AlwaysShowFocusVisual</c>
///    — Windows' "hide focus rectangles until the keyboard is used" default
///    leaves KeyboardCues false (measured: False here), so a bare
///    programmatic .Focus() paints nothing at all. Pushing a real
///    <see cref="Key.Tab"/> KeyEventArgs through
///    <see cref="InputManager"/> — the technique that works for
///    <see cref="UnlockEnterKeyTests"/> — DOES move focus but leaves
///    AlwaysShowFocusVisual false (measured: focus moved to the next Button,
///    adorner count still 0). WPF sets that flag from the RAW Win32 keyboard
///    input report, a stage the InputManager staging pipeline is entered
///    below, so nothing short of a real HWND message pump reaches it. The
///    honest options were therefore (a) set the very flag WPF itself sets,
///    or (b) downgrade to a property assertion. This file does (a): the
///    internal static is flipped for the duration of the assertion and
///    restored afterwards, so everything downstream — adorner creation,
///    FocusVisualStyle resolution, template instantiation, rasterisation —
///    is the real WPF code path, painting real pixels.
///
/// 3. A FocusVisualStyle STYLE SETTER IS NOT ALWAYS ENOUGH. CheckBox and
///    RadioButton keep the stock template, and the stock template assigns
///    FocusVisualStyle from a template trigger on HasContent=True — which
///    outranks a Style Setter in DP precedence. Measured:
///    GetValueSource returned TemplateTrigger and the rendered ring was the
///    OS dashed rectangle, while ComboBox/ListBoxItem/TabItem (whose styles
///    do supply a template) returned Style and rendered bronze. The
///    FocusVisualStyle identity assertion below is what names this when it
///    happens; a matching Style trigger in Styles.xaml is what fixes it.
///
/// Shares HighlightContrastFixture's single STA thread/Application (see that
/// fixture's own class doc). NOTE: every control must be CONSTRUCTED inside
/// <c>_fx.Invoke</c> — merely calling `new CheckBox()` touches InputManager
/// and throws "The calling thread must be STA" on the xunit worker thread,
/// which is why the cases below pass a <c>Func&lt;FrameworkElement&gt;</c>
/// factory rather than an instance.</summary>
[Collection(HighlightContrastTests.Name)]
public class FocusRingCoverageTests
{
    private readonly HighlightContrastFixture _fx;
    public FocusRingCoverageTests(HighlightContrastFixture fx) => _fx = fx;

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

    /// <summary>The flag WPF's own KeyboardNavigation consults before it will
    /// build a FocusVisualAdorner at all (see the class doc, point 2). It is
    /// <c>internal static</c>, so reflection is the only way in; a missing
    /// member fails loudly here rather than silently skipping the pixel
    /// proof.</summary>
    private static readonly PropertyInfo AlwaysShowFocusVisual =
        typeof(KeyboardNavigation).GetProperty(
            "AlwaysShowFocusVisual",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException(
            "KeyboardNavigation.AlwaysShowFocusVisual no longer exists on this WPF build — " +
            "the focus-ring render proof cannot enable focus visuals without it.");

    private sealed class FocusVisualsEnabled : IDisposable
    {
        private readonly bool _previous = (bool)AlwaysShowFocusVisual.GetValue(null)!;
        public FocusVisualsEnabled() => AlwaysShowFocusVisual.SetValue(null, true);
        public void Dispose() => AlwaysShowFocusVisual.SetValue(null, _previous);
    }

    /// <summary>Counts pixels within <paramref name="tolerance"/> per channel
    /// of <paramref name="expected"/> in the band around — and deliberately
    /// NOT inside — <paramref name="target"/>'s bounds, rendered from
    /// <paramref name="window"/>.
    ///
    /// The root is the Window (class doc, point 1). The interior is excluded
    /// 2px in from the control's own edge: BronzeFocusVisual's Border is
    /// Margin="-2" with a 2px stroke, so the ring occupies exactly [-2, 0]
    /// relative to the bounds and nothing the control itself paints inside
    /// can be mistaken for it.</summary>
    private static int CountBandPixels(
        Window window, FrameworkElement target, Rgb expected,
        int tolerance = 8, double outset = 6, double inset = 2)
    {
        var w = (int)Math.Ceiling(window.ActualWidth);
        var h = (int)Math.Ceiling(window.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);

        var tl = target.TranslatePoint(new Point(0, 0), window);
        var x0 = Math.Max(0, (int)Math.Floor(tl.X - outset));
        var y0 = Math.Max(0, (int)Math.Floor(tl.Y - outset));
        var x1 = Math.Min(w, (int)Math.Ceiling(tl.X + target.ActualWidth + outset));
        var y1 = Math.Min(h, (int)Math.Ceiling(tl.Y + target.ActualHeight + outset));
        var bw = x1 - x0;
        var bh = y1 - y0;
        if (bw <= 0 || bh <= 0)
            throw new InvalidOperationException(
                $"{target.GetType().Name} has no on-screen bounds to scan ({bw}x{bh})");

        var stride = bw * 4;
        var pixels = new byte[stride * bh];
        bmp.CopyPixels(new Int32Rect(x0, y0, bw, bh), pixels, stride, 0);

        var ix0 = tl.X + inset;
        var iy0 = tl.Y + inset;
        var ix1 = tl.X + target.ActualWidth - inset;
        var iy1 = tl.Y + target.ActualHeight - inset;

        var hits = 0;
        for (var row = 0; row < bh; row++)
        {
            var y = y0 + row;
            for (var col = 0; col < bw; col++)
            {
                var x = x0 + col;
                if (x >= ix0 && x < ix1 && y >= iy0 && y < iy1) continue;  // control's own interior
                var i = row * stride + col * 4;
                if (pixels[i + 3] == 0) continue;                          // nothing rendered here
                if (Math.Abs(pixels[i + 2] - expected.R) <= tolerance
                    && Math.Abs(pixels[i + 1] - expected.G) <= tolerance
                    && Math.Abs(pixels[i + 0] - expected.B) <= tolerance)
                    hits++;
            }
        }
        return hits;
    }

    private static Window HostWindow(FrameworkElement content) => new()
    {
        // Generous padding: the ring sits 2px OUTSIDE the target's bounds, so
        // nothing may be flush against the window edge or it renders off the
        // bitmap.
        Content = new Border { Padding = new Thickness(30), Child = content },
        SizeToContent = SizeToContent.WidthAndHeight,
        Left = -20000, Top = 0, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    /// <summary>The shared proof. <paramref name="build"/> constructs the
    /// content ON the STA thread; <paramref name="pick"/> then selects the
    /// element to focus once the window is up (ListBoxItem containers only
    /// exist after layout).</summary>
    private void AssertBronzeFocusRing(
        bool dark, Func<FrameworkElement> build,
        Func<FrameworkElement, FrameworkElement>? pick = null) => _fx.Invoke(() =>
    {
        var p = dark ? ThemePalette.Dark : ThemePalette.Light;
        ThemeManager.Apply(_fx.App, dark);

        var content = build();
        var window = HostWindow(content);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpRender();
            window.UpdateLayout();

            var target = pick is null ? content : pick(content);
            var label = target.GetType().Name + (dark ? " (dark)" : " (light)");

            var before = CountBandPixels(window, target, p.AccentBronze);
            Assert.True(before == 0,
                $"{label}: {before} AccentBronze ({p.AccentBronze}) pixels already in the band " +
                "around this control BEFORE it was focused — the differential is meaningless, " +
                "pick a control/state that doesn't paint bronze decoration of its own");

            // Structural companion to the pixel proof, and the assertion that
            // actually names the defect when it regresses: FocusVisualStyle
            // must RESOLVE to the shared BronzeFocusVisual instance. A plain
            // Style Setter is not enough to guarantee that — the stock
            // CheckBox/RadioButton templates pin this property from a
            // TEMPLATE TRIGGER, which outranks a Style Setter, so the Setter
            // added for those two was measured to be a complete no-op until a
            // matching Style TRIGGER was added alongside it (see Styles.xaml).
            Assert.True(
                ReferenceEquals(target.FocusVisualStyle, _fx.App.Resources["BronzeFocusVisual"]),
                $"{label}: FocusVisualStyle did not resolve to BronzeFocusVisual (value source: " +
                $"{DependencyPropertyHelper.GetValueSource(target, FrameworkElement.FocusVisualStyleProperty).BaseValueSource}) " +
                "— something at higher DP precedence than this style is overriding it");

            using (new FocusVisualsEnabled())
            {
                Assert.True(target.Focus(), $"{label}: never accepted keyboard focus");
                Assert.True(target.IsKeyboardFocused, $"{label}: IsKeyboardFocused is false after Focus()");
                PumpRender();
                window.UpdateLayout();
                PumpRender();

                var layer = AdornerLayer.GetAdornerLayer(target);
                var adorners = layer?.GetAdorners(target);
                Assert.True(adorners is { Length: > 0 },
                    $"{label}: WPF added no focus-visual adorner at all — the ring can't be about " +
                    "colour yet (check the AlwaysShowFocusVisual plumbing in this file)");

                var after = CountBandPixels(window, target, p.AccentBronze);
                Assert.True(after > 0,
                    $"{label}: focus visual is painted (adorner present) but NO AccentBronze " +
                    $"({p.AccentBronze}) pixel appears in the ring band — this control is still " +
                    "falling back to the OS focus rectangle");
            }
        }
        finally
        {
            window.Close();
        }
    });

    public static IEnumerable<object[]> Palettes()
    {
        yield return new object[] { false };
        yield return new object[] { true };
    }

    [Theory, MemberData(nameof(Palettes))]
    public void CheckBoxShowsTheBronzeFocusRing(bool dark) =>
        AssertBronzeFocusRing(dark, () => new CheckBox { Content = "Option" });

    [Theory, MemberData(nameof(Palettes))]
    public void RadioButtonShowsTheBronzeFocusRing(bool dark) =>
        AssertBronzeFocusRing(dark, () => new RadioButton { Content = "Option" });

    [Theory, MemberData(nameof(Palettes))]
    public void ComboBoxShowsTheBronzeFocusRing(bool dark) =>
        AssertBronzeFocusRing(dark, () => new ComboBox
        {
            ItemsSource = new[] { "One", "Two" },
            SelectedIndex = 0,
        });

    [Theory, MemberData(nameof(Palettes))]
    public void ListBoxItemShowsTheBronzeFocusRing(bool dark) =>
        AssertBronzeFocusRing(dark,
            () => new ListBox { ItemsSource = new[] { "Row one", "Row two" } },
            lb => ((ListBox)lb).ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem
                  ?? throw new InvalidOperationException("ListBoxItem row 1 never realized a container"));

    /// <summary>Focuses the SECOND, UNSELECTED tab on purpose: both TabItem
    /// templates paint the SELECTED tab a 2px AccentBronze underline, which
    /// sits right on the control's edge and would make the before/after
    /// differential unable to tell a ring from that underline (the "before"
    /// assertion above fails outright if this is ever changed back to
    /// tab 0).</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void TabItemShowsTheBronzeFocusRing(bool dark) =>
        AssertBronzeFocusRing(dark, () =>
        {
            var tabControl = new TabControl();
            tabControl.Items.Add(new TabItem { Header = "First", Content = new TextBlock { Text = "one" } });
            tabControl.Items.Add(new TabItem { Header = "Second", Content = new TextBlock { Text = "two" } });
            tabControl.SelectedIndex = 0;
            return tabControl;
        }, tc => (TabItem)((TabControl)tc).Items[1]!);

    /// <summary>The case a Styles.xaml-only fix provably cannot reach:
    /// SettingsWindow.xaml's theme-picker cards use <c>x:Key="ThemeCard"</c>
    /// with NO <c>BasedOn</c>, and an explicitly-keyed Style replaces
    /// implicit-by-type lookup wholesale — the implicit RadioButton style's
    /// new FocusVisualStyle Setter never applies to them. Proven against the
    /// REAL style object resolved by key out of a real SettingsWindow's
    /// resources (not a copy), so deleting that window's own duplicate Setter
    /// fails this test.</summary>
    [Theory, MemberData(nameof(Palettes))]
    public void SettingsThemeCardRadioButtonShowsTheBronzeFocusRing(bool dark)
    {
        Style? themeCard = null;
        _fx.Invoke(() =>
        {
            var cfgPath = Path.Combine(Path.GetTempPath(), "ordo_test_a11y_" + Guid.NewGuid(), "config.json");
            var vm = new SettingsViewModel(new Config(), new NoDialogs(),
                () => dark ? ThemePalette.Dark : ThemePalette.Light, cfgPath,
                uiContext: SynchronizationContext.Current);
            var settings = new SettingsWindow(vm);
            themeCard = (Style)settings.Resources["ThemeCard"];
        });

        AssertBronzeFocusRing(dark, () => new RadioButton
        {
            Style = themeCard,
            Content = new Border { Width = 60, Height = 40, Background = Brushes.Transparent },
            Tag = "Always dark",
        });
    }
}
