using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>The segment chips in Bulk rename's segment bar, rendered for
/// real. A dropped chip goes flat and struck through in SubtleText, and its
/// label must still read at 4.5:1 on what it actually paints on, in both
/// themes. Read off rendered pixels, not brush values: this window once had
/// a checkbox label that resolved a perfectly good colour and painted
/// nothing at all (the old Delete segments "last" box, 2026-08-03).</summary>
[Collection(HighlightContrastTests.Name)]
public class BulkRenameSegmentChipTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ordo_chips_" + Guid.NewGuid().ToString("N"))).FullName;

    public BulkRenameSegmentChipTests(HighlightContrastFixture fx) => _fx = fx;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Theory, MemberData(nameof(SchemeTheoryData.SchemeKeys), MemberType = typeof(SchemeTheoryData))]
    public void KeptAndDroppedChipLabelsMeetWcagAa(string schemeKey) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, ThemePalette.FindScheme(schemeKey)!);
        var file = Path.Combine(_dir, "EVANS_BRIAN_1998.pdf");
        File.WriteAllText(file, "x");
        var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler());
        vm.AddFilesAsync(new[] { file }).GetAwaiter().GetResult();
        vm.SelectedSources = new[] { file };   // only ticked files change (2026-09-26)
        vm.SetSegmentKept(2, kept: false);

        var win = new BulkRenameWindow(vm)
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

            var chips = FindAllDescendants<ToggleButton>(win).Where(t => t.DataContext is SegmentChip).ToList();
            Assert.Equal(3, chips.Count);
            foreach (var chip in chips)
            {
                var model = (SegmentChip)chip.DataContext;
                Assert.Equal(model.IsKept, chip.IsChecked);
                var label = FindAllDescendants<TextBlock>(chip).Single(t => t.Text == model.Text);
                var (fg, bg) = SampleRenderedMaxContrast(win, label);
                var ratio = ThemePalette.ContrastRatio(fg, bg);
                Assert.True(ratio >= 4.5,
                    $"{(model.IsKept ? "kept" : "dropped")} chip \"{model.Text}\" ({schemeKey}): {fg} on {bg} = {ratio:F2}");
            }
        }
        finally
        {
            try { win.Close(); } catch { /* best effort */ }
            ThemeManager.Apply(_fx.App, dark: false);
        }
    });

    /// <summary>The most common colour in the label's bounds is its
    /// background; the colour contrasting most with it is its ink.</summary>
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

    /// <summary>Rule 12 in Bulk rename: typing jumps to a file instead of
    /// starting an edit on its New name; the existing F2 path still edits.</summary>
    [Fact]
    public void TypingJumpsInsteadOfStartingAnEdit() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var files = new[] { "alpha_one.pdf", "bravo_two.pdf" }.Select(n =>
        {
            var path = Path.Combine(_dir, n);
            File.WriteAllText(path, "x");
            return path;
        }).ToList();
        var vm = new BulkRenameViewModel(scheduler: new InlineWorkScheduler());
        vm.AddFilesAsync(files).GetAwaiter().GetResult();
        var win = new BulkRenameWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false,
        };
        try
        {
            win.Show();
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var grid = FindAllDescendants<DataGrid>(win).Single();
            grid.SelectedIndex = 0;

            Assert.True(ExplorerColumns.For(grid)!.TypeAhead("b"));

            Assert.Equal(1, grid.SelectedIndex);
            Assert.False(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase tb
                && FindAllDescendants<System.Windows.Controls.Primitives.TextBoxBase>(grid).Contains(tb));
        }
        finally { win.Close(); }
    });
}
