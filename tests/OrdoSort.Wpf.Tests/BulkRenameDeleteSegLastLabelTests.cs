using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-03 audit-remediation-finish, Task 5, Question (a):
/// BulkRenameWindow.xaml's fifth delete-segment CheckBox
/// (<c>Content="last"</c>, no visible label per the audit screenshot) while
/// its four numbered siblings (<c>Content="1"</c>…<c>"4"</c>) show theirs
/// fine.
///
/// This was NOT the auto-wrap "Style Setter outranks inheritance" contrast
/// trap this codebase has hit five times before (see HighlightContrastTests'
/// class docs) — that trap resolves a WRONG but still-PAINTED colour. Here
/// the resolved Foreground DP, Brush.Opacity, element Opacity, Visibility
/// and Clip were all completely normal at every level from the TextBlock up
/// to the Window (confirmed directly, not inferred), yet a rendered-pixel
/// scan of the label's own bounds found literally ZERO variation — the
/// exact same colour for "foreground" and "background", i.e. nothing
/// painted there at all. A column-by-column pixel scan of the whole
/// five-checkbox row confirmed it precisely: the fifth checkbox's own glyph
/// square painted fine, but nothing to its right did, while re-rendering
/// the SAME bound CheckBox with no surrounding width constraint painted its
/// "last" label perfectly (49 distinct rendered colours).
///
/// Root cause: the row's <c>StackPanel</c>
/// (<c>Grid.Row="2" Grid.Column="1"</c>) sits in a column with a FIXED
/// <c>Width="170"</c> — sized for the TextBoxes the other two rows in this
/// same Grid put there — but the five checkboxes' combined desired width
/// measures 182px, 12px over. No element anywhere reported a
/// <see cref="UIElement.Clip"/> (confirmed empirically, not assumed), and
/// <c>StackPanel.ActualWidth</c> itself reported the full unclamped 182px —
/// but WPF still applies an internal render-time clip to an element whose
/// arranged <c>RenderSize</c> exceeds what its own parent's <c>Arrange</c>
/// call gave it, which isn't reflected in either of those DPs. Since the
/// StackPanel packs children left-to-right, the overflow lands entirely on
/// the LAST child — exactly the CheckBox this bug report is about.
///
/// Fix: <c>Grid.ColumnSpan="3"</c> on that StackPanel. Columns 2–3 carry no
/// other content on this row (only the "Add at end:" label/TextBox use them,
/// on Row 1), so spanning into them costs nothing here and gives
/// ~170+Auto+170px of headroom — comfortably covering the 182px this row
/// needs, with margin for larger configured font sizes too.
///
/// Asserts the RESOLVED rendered contrast (a real WCAG ratio read off actual
/// pixels), not a palette-pair DP equality check — this codebase already has
/// a suite that asserted palette pairs and got false assurance because it
/// never checked what a container actually resolves, and a DP-only
/// assertion here would have passed even on the pre-fix, nothing-painted
/// build (the Foreground DP was never wrong).</summary>
[Collection(HighlightContrastTests.Name)]
public class BulkRenameDeleteSegLastLabelTests
{
    private readonly HighlightContrastFixture _fx;
    public BulkRenameDeleteSegLastLabelTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteSegLastCheckboxLabelPaintsAndMeetsWcagAa(bool dark) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark);
        var win = new BulkRenameWindow(new BulkRenameViewModel())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, ShowActivated = false,
        };
        try
        {
            win.Show();
            win.UpdateLayout();
            PumpRender();
            win.UpdateLayout();

            var cb = FindAllDescendants<CheckBox>(win)
                .SingleOrDefault(c => c.Content is string s && s == "last")
                ?? throw new InvalidOperationException("no CheckBox with Content \"last\" found");

            // FindTextElement stops at EITHER a TextBlock or an AccessText —
            // never assume the first plain TextBlock a naive walk finds is
            // the real one (see HighlightContrastTests' class docs for the
            // decoy this guards against elsewhere in this app).
            var text = FindTextElement(cb)
                ?? throw new InvalidOperationException("no TextBlock/AccessText descendant under the \"last\" CheckBox");

            var (fg, bg) = SampleRenderedMaxContrast(win, (FrameworkElement)text);
            var ratio = ThemePalette.ContrastRatio(fg, bg);

            Assert.True(ratio >= 4.5,
                $"BulkRenameWindow DeleteSegLast checkbox label ({(dark ? "dark" : "light")}): " +
                $"{fg} on {bg} = {ratio:F2}");
        }
        finally
        {
            try { win.Close(); } catch { /* best effort */ }
        }
    });

    private static void PumpRender() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

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
            throw new InvalidOperationException($"target has no on-screen bounds ({w}x{h})");

        var stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(new Int32Rect(x0, y0, w, h), pixels, stride, 0);

        var counts = new Dictionary<Rgb, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var rgb = new Rgb(pixels[i + 2], pixels[i + 1], pixels[i]);
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

    /// <summary>Stops at EITHER a TextBlock or an AccessText — see
    /// HighlightContrastTests' identical helper for why a TextBlock-only walk
    /// is a trap for auto-wrapped Content.</summary>
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
}
