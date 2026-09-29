using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Tests;

/// <summary>DW-05: LabelPreview.cs is the WPF half of box-label printing: it
/// replays <see cref="BoxLabels.ComposeDrawing"/> onto the preview card and
/// onto the sheets that spool to the printer. Nothing checked what it
/// actually draws, and a wrong box number, date or slot position is only
/// found after a sheet of physical labels is printed, cut and stuck on
/// boxes. These tests read back the drawing WPF recorded: the text runs, the
/// black bars, and where each label lands on the page.</summary>
[Collection(HighlightContrastTests.Name)]
public class LabelPrintingTests : UiTest
{
    private readonly HighlightContrastFixture _fx;
    public LabelPrintingTests(HighlightContrastFixture fx) : base(fx) => _fx = fx;

    private static readonly DateTime Created = new(2026, 7, 25);

    private static List<BoxLabels.Item> Items(int count) =>
        BoxLabels.Batch("ABCD", 1, count, Created, 30);

    private static IEnumerable<Drawing> Leaves(Drawing drawing) =>
        drawing is DrawingGroup group ? group.Children.SelectMany(Leaves) : new[] { drawing };

    /// <summary>The text lines drawn, in drawing order. WPF shapes one
    /// FormattedText into several glyph runs (it splits at the hyphens and
    /// digits of a date), so consecutive runs on one baseline are one line.</summary>
    private static List<(string Text, Brush Brush)> TextLines(Drawing drawing)
    {
        var lines = new List<(string Text, Brush Brush)>();
        double? lastBaseline = null;
        foreach (var run in Leaves(drawing).OfType<GlyphRunDrawing>())
        {
            var baseline = run.GlyphRun.BaselineOrigin.Y;
            var text = new string(run.GlyphRun.Characters.ToArray());
            if (lines.Count > 0 && lastBaseline == baseline)
                lines[^1] = (lines[^1].Text + text, lines[^1].Brush);
            else
                lines.Add((text, run.ForegroundBrush));
            lastBaseline = baseline;
        }
        return lines;
    }

    private static Drawing DrawOneLabel(BoxLabels.Item item, string dateStyle)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            LabelWpfRender.DrawLabel(dc, BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: dateStyle)), pixelsPerDip: 1.0);
        return visual.Drawing;
    }

    /// <summary>What one sheet element drew, after the layout pass that
    /// calls its OnRender.</summary>
    private static Drawing DrawSheet(PageContent content)
    {
        var page = content.GetPageRoot(forceReload: false)!;
        var sheet = Assert.IsAssignableFrom<FrameworkElement>(Assert.Single(page.Children));
        sheet.Measure(new Size(page.Width, page.Height));
        sheet.Arrange(new Rect(0, 0, page.Width, page.Height));
        return VisualTreeHelper.GetDrawing(sheet)
            ?? throw new InvalidOperationException("the label sheet drew nothing");
    }

    [Theory]
    [InlineData(BoxLabels.DateStyleBars)]
    [InlineData(BoxLabels.DateStylePlain)]
    public void ALabelPrintsItsDatesAndCodeInReadingOrder(string dateStyle) => _fx.Invoke(() =>
    {
        var item = Items(1)[0];

        var drawing = DrawOneLabel(item, dateStyle);

        var lines = TextLines(drawing);
        Assert.Equal(
            new[] { "CREATED 2026-07-25", BoxLabels.DisplayCode(item.Code), "DESTROY AFTER 2026-08-24" },
            lines.Select(l => l.Text).ToArray());
        // Bars style prints the dates white on black bars; plain prints them
        // black on white. The wrong one leaves an unreadable date.
        var dateBrush = dateStyle == BoxLabels.DateStyleBars ? Brushes.White : Brushes.Black;
        Assert.Same(dateBrush, lines[0].Brush);
        Assert.Same(dateBrush, lines[2].Brush);
        Assert.Same(Brushes.Black, lines[1].Brush);
    });

    [Fact]
    public void EveryBarInThePlanIsDrawnBlack() => _fx.Invoke(() =>
    {
        var item = Items(1)[0];
        var plan = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: BoxLabels.DateStyleBars));

        var blackRectangles = Leaves(DrawOneLabel(item, BoxLabels.DateStyleBars))
            .OfType<GeometryDrawing>()
            .Count(g => ReferenceEquals(g.Brush, Brushes.Black));

        // the two date bars plus the Code 39 bars; a dropped bar is an
        // unscannable barcode
        Assert.Equal(plan.Bars.Count, blackRectangles);
    });

    [Fact]
    public void TwentyThreeLabelsPrintOnThreeTrueSizeLetterPages() => _fx.Invoke(() =>
    {
        var items = Items(23);

        var doc = LabelPrinting.BuildDocument(items);

        Assert.Equal(3, doc.Pages.Count);
        var destroyLinesPerPage = new List<int>();
        foreach (var content in doc.Pages)
        {
            var page = content.GetPageRoot(forceReload: false)!;
            // 8.5 x 11 in at 96 DIPs per inch: WPF printing maps DIPs to
            // inches 1:1, so this is what makes the labels print at 100%.
            Assert.Equal(816, page.Width);
            Assert.Equal(1056, page.Height);
            destroyLinesPerPage.Add(TextLines(DrawSheet(content)).Count(l => l.Text.StartsWith("DESTROY AFTER")));
        }
        Assert.Equal(new[] { 10, 10, 3 }, destroyLinesPerPage);

        // the last sheet carries the last three numbers, in order
        var lastSheetCodes = TextLines(DrawSheet(doc.Pages[2]))
            .Select(l => l.Text)
            .Where(t => !t.StartsWith("CREATED") && !t.StartsWith("DESTROY"))
            .ToArray();
        Assert.Equal(items.Skip(20).Select(i => BoxLabels.DisplayCode(i.Code)).ToArray(), lastSheetCodes);
    });

    [Fact]
    public void AFullSheetFillsTheSlotsFromTheFirstToTheLast() => _fx.Invoke(() =>
    {
        var doc = LabelPrinting.BuildDocument(Items(10));

        var bounds = DrawSheet(doc.Pages[0]).Bounds;

        // Points to DIPs. The first label's corner sits at slot 0's origin and
        // the last label's far corner at slot 9's origin plus one label; the
        // cut guide's pen adds a fraction of a point, hence the tolerance.
        const double toDips = 96.0 / 72.0;
        var (firstX, firstY) = BoxLabels.SlotOrigin(0);
        var (lastX, lastY) = BoxLabels.SlotOrigin(BoxLabels.PerSheet - 1);
        Assert.Equal(firstX * toDips, bounds.Left, 0.5);
        Assert.Equal(firstY * toDips, bounds.Top, 0.5);
        Assert.Equal((lastX + BoxLabels.LabelWidthPt) * toDips, bounds.Right, 0.5);
        Assert.Equal((lastY + BoxLabels.LabelHeightPt) * toDips, bounds.Bottom, 0.5);
    });

    /// <summary>Final review, 2026-09-28: the layout tests compare boxes, and
    /// a glyph's ink can leave its box. A "Q" tail in Big's 72 pt client id
    /// dipped about 4 pt into the barcode. This reads the real ink of every
    /// glyph WPF drew and checks none of it touches a barcode bar, for the
    /// large layouts, with client ids full of the one descending capital.</summary>
    [Theory]
    [InlineData("QRS00000042", "big", false)]
    [InlineData("QRS00000042", "big", true)]
    [InlineData("QQQQQQQQ00000042", "big", false)]
    [InlineData("QRS00000042", "huge", false)]
    [InlineData("QQQQQQQQ99999999", "huge", true)]
    public void NoGlyphInkTouchesTheBarcode(string code, string layout, bool zeros) => _fx.Invoke(() =>
    {
        var item = new BoxLabels.Item(code, Created, Created.AddDays(30));
        var plan = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(layout, zeros));
        var barcodeTop = plan.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).Min(b => b.Y);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            LabelWpfRender.DrawLabel(dc, plan, pixelsPerDip: 1.0);

        foreach (var run in Leaves(visual.Drawing).OfType<GlyphRunDrawing>())
        {
            var ink = run.GlyphRun.ComputeInkBoundingBox();
            ink.Offset(run.GlyphRun.BaselineOrigin.X, run.GlyphRun.BaselineOrigin.Y);
            if (ink.Top >= barcodeTop) continue;   // the DESTROY line, below the barcode
            Assert.True(ink.Bottom <= barcodeTop,
                $"\"{new string(run.GlyphRun.Characters.ToArray())}\" ink reaches {ink.Bottom:F1} pt; the barcode starts at {barcodeTop} pt");
        }
    });
}
