using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The blind spot beside <see cref="WindowOverflowTests"/>.
///
/// That suite asks one question — does any text ESCAPE the window — and it
/// answers it well. It cannot see a field that clips its own value, because
/// nothing escapes anything: the text is simply not all visible. Settings'
/// destination Folder box is a star column sharing its row with Browse…, Open
/// and a SharedSizeGroup label, and at a wider configured font it squeezed to
/// about 110px and showed roughly the first third of a real path. Every route
/// then looked identical, because they share a prefix (2026-08-22 UI audit,
/// UI-25, found by running the app rather than by any test).
///
/// A TextBox scrolls, so nothing is lost — but "click in and arrow across" is
/// not reading, and the single most important fact on that tab is where files
/// actually go. The rule this pins is therefore not "it must fit", which no
/// layout can promise at every font: it is that a field which cannot show its
/// whole value must make that value recoverable without editing it — a
/// ToolTip, the same answer every grid cell in this app already gives.</summary>
[Collection(HighlightContrastTests.Name)]
public class FieldClippingTests
{
    private readonly HighlightContrastFixture _fx;
    public FieldClippingTests(HighlightContrastFixture fx) => _fx = fx;

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

    /// <summary>How wide the text in this box would like to be. Measured
    /// against the box's own typeface rather than guessed from a character
    /// count, which is the whole point — the defect only appears at a font
    /// wider than the default.</summary>
    private static double NaturalTextWidth(TextBox box)
    {
        var text = new FormattedText(
            box.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch),
            box.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(box).PixelsPerDip);
        return text.Width;
    }

    public static TheoryData<string, double> Fonts() => new()
    {
        { "Segoe UI Variable Text, Segoe UI", 14.0 },
        { "Consolas", 14.0 },   // the family that exposed UI-25
        { "Consolas", 18.0 },
    };

    [Theory, MemberData(nameof(Fonts))]
    public void APathFieldThatCannotShowItsValueOffersItInATooltip(string family, double size) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var oldFamily = _fx.App.Resources["AppFontFamily"];
        var oldSize = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontFamily"] = new FontFamily(family);
        _fx.App.Resources["AppFontSize"] = size;

        // A real destination path, long enough that its routes share a prefix —
        // which is what makes a clipped one indistinguishable from its siblings.
        const string LongPath = @"S:\OrdoSort\demo-full\routes\01-invoices";
        var cfg = new Config
        {
            Inbox = @"C:\inbox", Deferred = @"C:\deferred",
            Routes = { new Route { Label = "Invoices", Path = LongPath, Color = "#2e7d32" } },
        };
        var vm = new SettingsViewModel(cfg, new FakeDialogs(),
            directoryExists: _ => true, fileExists: _ => true,
            scheduler: new InlineWorkScheduler());
        var w = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 760,   // the window's own MinWidth — the honest worst case
        };
        try
        {
            w.Show();
            var tabs = Descendants(w).OfType<TabControl>().First();
            tabs.SelectedItem = tabs.Items.Cast<TabItem>()
                .First(t => (t.Header as string)?.Contains("Destinations") == true);
            w.UpdateLayout();
            OverflowProbe.PumpRender();
            w.UpdateLayout();

            var box = Descendants(w).OfType<TextBox>()
                .FirstOrDefault(b => b.Text == LongPath)
                ?? throw new InvalidOperationException(
                    "the destination Folder box was not found — this suite's premise changed");

            var natural = NaturalTextWidth(box);
            var visible = box.ActualWidth - box.Padding.Left - box.Padding.Right
                          - box.BorderThickness.Left - box.BorderThickness.Right;
            if (natural <= visible) return;   // it all fits: nothing to recover

            Assert.True(box.ToolTip is not null,
                $"at {family} {size} the Folder box shows {visible:F0}px of a path that wants " +
                $"{natural:F0}px — about {visible / natural:P0} of it — and offers no tooltip, " +
                "so the destination is unreadable without clicking in and scrolling. Every route " +
                "shares this path's prefix, so a clipped one is indistinguishable from its siblings.");

            // And the tooltip has to carry the WHOLE value, not a second copy of
            // the same truncation.
            Assert.Equal(LongPath, box.ToolTip?.ToString());
        }
        finally
        {
            w.Close();
            _fx.App.Resources["AppFontFamily"] = oldFamily;
            _fx.App.Resources["AppFontSize"] = oldSize;
        }
    });
}
