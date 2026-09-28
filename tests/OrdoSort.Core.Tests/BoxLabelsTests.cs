using OrdoSort.Core;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core.Tests;

public class BoxLabelsTests
{
    [Fact]
    public void ComposePadsTheNumberToEightDigits()
    {
        Assert.Equal("ABCD00000001", BoxLabels.Compose("ABCD", 1));
        Assert.Equal("ABCD00000042", BoxLabels.Compose("ABCD", 42));
        Assert.Equal("XY99999999", BoxLabels.Compose("XY", 99_999_999));
    }

    [Theory]
    [InlineData("ABCD", "")]
    [InlineData("AB", "")]
    [InlineData("CLIENT01", "")]
    [InlineData("A", "2 to 8")]
    [InlineData("TOOLONGID", "2 to 8")]
    [InlineData("", "2 to 8")]
    [InlineData("abcd", "capital")]
    [InlineData("AB CD", "capital")]
    [InlineData("AB-1", "capital")]
    public void ClientIdValidationCatchesTheBadOnes(string id, string expectedFragment)
    {
        var problem = BoxLabels.ValidateClientId(id);
        if (expectedFragment.Length == 0) Assert.Equal("", problem);
        else Assert.Contains(expectedFragment, problem);
    }

    [Theory]
    [InlineData("ABCD00000042", "ABCD 0000 0042")]
    [InlineData("CLIENT0100000001", "CLIENT01 0000 0001")]
    [InlineData("AB99999999", "AB 9999 9999")]
    [InlineData("SHORT", "SHORT")]                    // nothing to group
    [InlineData("ABCDEFGHIJKL", "ABCDEFGHIJKL")]      // tail isn't digits
    public void DisplayCodeGroupsTheDigitsForHumansOnly(string code, string display) =>
        Assert.Equal(display, BoxLabels.DisplayCode(code));

    [Fact]
    public void BatchNumbersRunConsecutivelyWithTheRetentionDate()
    {
        var created = new DateTime(2026, 1, 1, 14, 30, 0);   // time of day dropped
        var items = BoxLabels.Batch("ABCD", 7, 3, created, 30);

        Assert.Equal(new[] { "ABCD00000007", "ABCD00000008", "ABCD00000009" },
            items.Select(i => i.Code));
        Assert.All(items, i => Assert.Equal(new DateTime(2026, 1, 1), i.Created));
        Assert.All(items, i => Assert.Equal(new DateTime(2026, 1, 31), i.Destroy));
    }

    [Fact]
    public void BatchRefusesToRunPastTheEightDigitCeiling()
    {
        Assert.Throws<ArgumentException>(() =>
            BoxLabels.Batch("ABCD", 99_999_995, 10, DateTime.Now, 30));
        Assert.Throws<ArgumentException>(() => BoxLabels.Batch("ABCD", 0, 1, DateTime.Now, 30));
        Assert.Throws<ArgumentException>(() => BoxLabels.Batch("ABCD", 1, 0, DateTime.Now, 30));
    }

    [Fact]
    public void Code39EncodesWithStartStopAndGaps()
    {
        // "AB" → *AB* = 4 characters × 9 elements + 3 inter-character gaps
        var elements = Code39.Encode("AB");
        Assert.Equal(4 * 9 + 3, elements.Count);
        Assert.True(elements[0].Bar);                       // always starts on a bar
        Assert.Equal(4 * 5, elements.Count(e => e.Bar));    // 5 bars per character
        Assert.Equal(4 * 3, elements.Count(e => e.Wide));   // exactly 3 wide per character
        Assert.False(elements[9].Bar);                      // the gap is a space
        Assert.False(elements[9].Wide);                     // ...a narrow one
    }

    [Fact]
    public void Code39StartAndStopAreTheAsteriskPattern()
    {
        // '*' = 010010100: wide at elements 1, 4, 6
        var elements = Code39.Encode("7");
        var star = elements.Take(9).Select(e => e.Wide).ToArray();
        Assert.Equal(new[] { false, true, false, false, true, false, true, false, false }, star);
        var stop = elements.Skip(elements.Count - 9).Select(e => e.Wide).ToArray();
        Assert.Equal(star, stop);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("AB*CD")]
    [InlineData("AB CD")]
    [InlineData("AB-1")]
    public void Code39RejectsCharactersOutsideItsAlphabet(string text) =>
        Assert.ThrowsAny<ArgumentException>(() => Code39.Encode(text));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(25, 3)]
    public void PdfHoldsTenLabelsPerSheet(int labels, int expectedPages)
    {
        var dir = Directory.CreateTempSubdirectory("ordoboxlabels_").FullName;
        var path = Path.Combine(dir, "labels.pdf");
        try
        {
            var items = BoxLabels.Batch("ABCD", 1, labels, new DateTime(2026, 7, 25), 30);
            BoxLabels.RenderPdf(path, items);

            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(expectedPages, pdf.PageCount);
            // US letter, in points
            Assert.Equal(612, pdf.Pages[0].Width.Point, 1);
            Assert.Equal(792, pdf.Pages[0].Height.Point, 1);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void EveryLabelClearsThePrintersUnprintableEdge()
    {
        // A printer cannot reach the outermost ~0.16-0.25in of a sheet, and
        // the bottom is the worst edge because that is where the rollers
        // release the trailing edge. The bottom row used to end 0.100in from
        // the page edge, so every sheet lost a sliver of its DESTROY bar.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (var slot = 0; slot < BoxLabels.PerSheet; slot++)
        {
            var (x, y) = BoxLabels.SlotOrigin(slot);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + BoxLabels.LabelWidthPt);
            maxY = Math.Max(maxY, y + BoxLabels.LabelHeightPt);
        }
        var bottom = BoxLabels.PageHeightPt - maxY;
        var right = BoxLabels.PageWidthPt - maxX;

        const double safeV = 18;     // 0.25in — the axis that clipped
        const double safeH = 10.8;   // 0.15in — the sides have always printed

        Assert.True(minY >= safeV, $"top margin is {minY / 72:N3}in");
        Assert.True(bottom >= safeV, $"bottom margin is {bottom / 72:N3}in — this is the edge that clipped");
        Assert.True(minX >= safeH, $"left margin is {minX / 72:N3}in");
        Assert.True(right >= safeH, $"right margin is {right / 72:N3}in");

        // Balanced top and bottom: neither edge is the one sacrificed, and a
        // sheet reversed in the tray prints identically.
        Assert.Equal(minY, bottom, 1);
    }

    [Fact]
    public void TheTopMarginIsTooSmallToHoldTheSheetNote()
    {
        // The note lives in the print-preview window now, not on the paper:
        // its old home was a 0.4in top margin, and that space is spent on
        // keeping the bottom row clear of the unprintable edge. This is the
        // guard against quietly restoring it — anyone who draws the note up
        // there again has to grow the margin back and take the clipping with
        // it, and this test fails first.
        var (_, firstRowTop) = BoxLabels.SlotOrigin(0);
        Assert.True(firstRowTop < 22,
            $"the top margin is back up to {firstRowTop / 72:N3}in — that is room for the "
            + "note again, which costs the bottom row its clearance");
    }

    [Fact]
    public void EmptyBatchDoesNotRender() =>
        Assert.Throws<ArgumentException>(() =>
            BoxLabels.RenderPdf(Path.Combine(Path.GetTempPath(), "x.pdf"),
                new List<BoxLabels.Item>()));

    [Fact]
    public void BarsDateStyleIsTheDefaultAndPrintsWhiteOnBlackDateBars()
    {
        var item = new BoxLabels.Item("ABCD00000042",
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
        var defaulted = BoxLabels.ComposeDrawing(item);
        var explicitBars = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: BoxLabels.DateStyleBars));

        // default parameter behaves exactly like an explicit "bars" request
        Assert.Equal(explicitBars.Bars.Count, defaulted.Bars.Count);
        Assert.Equal(explicitBars.Texts, defaulted.Texts);

        var created = defaulted.Texts.Single(t => t.Text.StartsWith("CREATED"));
        var destroy = defaulted.Texts.Single(t => t.Text.StartsWith("DESTROY"));
        Assert.True(created.White);
        Assert.True(destroy.White);

        // the two full-width date bars (top + bottom) are present
        Assert.Equal(2, defaulted.Bars.Count(b => b.W == BoxLabels.LabelWidthPt));
    }

    [Fact]
    public void PlainDateStyleSuppressesOnlyTheDateBarsAndBlackensOnlyTheDateTexts()
    {
        var item = new BoxLabels.Item("ABCD00000042",
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
        var bars = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: BoxLabels.DateStyleBars));
        var plain = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(DateStyle: BoxLabels.DateStylePlain));

        // exactly the two full-width date bars vanish — the barcode bars
        // (never full label width) are the same rects, same count, in bars
        // mode as in plain mode
        Assert.Equal(2, bars.Bars.Count(b => b.W == BoxLabels.LabelWidthPt));
        Assert.Equal(0, plain.Bars.Count(b => b.W == BoxLabels.LabelWidthPt));
        Assert.Equal(bars.Bars.Count - 2, plain.Bars.Count);
        Assert.Equal(bars.Bars.Where(b => b.W != BoxLabels.LabelWidthPt),
            plain.Bars.Where(b => b.W != BoxLabels.LabelWidthPt));

        // same three texts either way — only the CREATED/DESTROY White flags flip
        Assert.Equal(bars.Texts.Count, plain.Texts.Count);
        var barsCreated = bars.Texts.Single(t => t.Text.StartsWith("CREATED"));
        var barsDestroy = bars.Texts.Single(t => t.Text.StartsWith("DESTROY"));
        var plainCreated = plain.Texts.Single(t => t.Text.StartsWith("CREATED"));
        var plainDestroy = plain.Texts.Single(t => t.Text.StartsWith("DESTROY"));
        Assert.True(barsCreated.White);
        Assert.True(barsDestroy.White);
        Assert.False(plainCreated.White);
        Assert.False(plainDestroy.White);
        Assert.Equal(barsCreated with { White = false }, plainCreated);
        Assert.Equal(barsDestroy with { White = false }, plainDestroy);

        // the code line (mono text) is untouched by date_style
        var barsCode = bars.Texts.Single(t => t.Mono);
        var plainCode = plain.Texts.Single(t => t.Mono);
        Assert.Equal(barsCode, plainCode);
        Assert.False(barsCode.White);

        // cut guide (layout box) identical either way
        Assert.Equal(bars.CutGuide, plain.CutGuide);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("neon")]
    public void UnknownOrMissingDateStyleNormalizesToBars(string? style) =>
        Assert.Equal(BoxLabels.DateStyleBars, BoxLabels.NormalizeDateStyle(style));

    // ---- label styles (spec 2026-09-28-box-label-style-design) ----------

    private static readonly BoxLabels.Item Plain42 =
        new("ABCD00000042", new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

    /// <summary>The regression pin: the default style draws exactly the
    /// label every existing install prints today.</summary>
    [Fact]
    public void TheDefaultStyleDrawsTodaysLabel()
    {
        var d = BoxLabels.ComposeDrawing(Plain42);

        var code = d.Texts.Single(t => t.Mono);
        Assert.Equal("ABCD 0000 0042", code.Text);
        Assert.Equal((0.0, 24.0, 288.0, 34.0), (code.X, code.Y, code.W, code.H));
        Assert.Equal(BoxLabels.CodeFontSize("ABCD 0000 0042"), code.Size);
        var barcode = d.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).ToList();
        Assert.All(barcode, b => Assert.Equal((66.0, 42.0), (b.Y, b.H)));
        Assert.Equal("CREATED 2026-01-01", d.Texts[0].Text);
    }

    [Theory]
    [InlineData("ABCD00000042", "standard", true, "ABCD 0000 0042")]
    [InlineData("ABCD00000042", "standard", false, "ABCD 42")]
    [InlineData("ABCD00000042", "big", false, "ABCD 42")]
    [InlineData("ABCD00004200", "huge", false, "4200")]
    [InlineData("ABCD00004200", "huge", true, "0000 4200")]
    [InlineData("NOTACODE", "huge", false, "NOTACODE")]   // no 8-digit tail: shown as-is
    public void NumberTextFollowsLayoutAndZeros(string code, string layout, bool zeros, string expected) =>
        Assert.Equal(expected, BoxLabels.NumberText(code, layout, zeros));

    public static TheoryData<string, string, bool, string> EveryStyle()
    {
        var data = new TheoryData<string, string, bool, string>();
        foreach (var code in new[] { "ABCD00000042", "ABCDEFGH99999999", "NGC00004200" })
            foreach (var layout in new[] { BoxLabels.LayoutStandard, BoxLabels.LayoutBig, BoxLabels.LayoutHuge })
                foreach (var zeros in new[] { true, false })
                    foreach (var dates in new[] { BoxLabels.DateStyleBars, BoxLabels.DateStylePlain })
                        data.Add(code, layout, zeros, dates);
        return data;
    }

    [Theory, MemberData(nameof(EveryStyle))]
    public void EveryStyleKeepsTheLabelSafeToPrintAndScan(string code, string layout, bool zeros, string dates)
    {
        var item = new BoxLabels.Item(code, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
        var d = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(layout, zeros, dates));
        var standard = BoxLabels.ComposeDrawing(item);
        var barcode = d.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).ToList();
        var number = d.Texts.Single(t => t.Mono);

        // the barcode: same bars across the label as today, never under 0.5 in
        Assert.Equal(standard.Bars.Where(b => b.W < BoxLabels.LabelWidthPt).Select(b => (b.X, b.W)),
            barcode.Select(b => (b.X, b.W)));
        Assert.All(barcode, b => Assert.True(b.H >= BoxLabels.MinBarcodeHeight));
        // inside the label
        Assert.All(d.Bars, b => Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.W <= 288 && b.Y + b.H <= 144));
        Assert.All(d.Texts, t => Assert.True(t.Y >= 0 && t.Y + t.H <= 144));
        // the number fits the width and clears the barcode and the date lines
        Assert.True(number.Size * 0.6 * number.Text.Length <= BoxLabels.LabelWidthPt - 20 + 0.001);
        Assert.All(barcode, b => Assert.True(number.Y + number.H <= b.Y));
        Assert.True(number.Y >= 22 && number.Y + number.H <= 144 - 22);
    }

    [Fact]
    public void HugeMovesTheClientIdIntoTheCreatedLine()
    {
        var item = new BoxLabels.Item("NGC00004200", new DateTime(2026, 9, 28), new DateTime(2033, 9, 26));
        var d = BoxLabels.ComposeDrawing(item, new BoxLabels.LabelStyle(BoxLabels.LayoutHuge, LeadingZeros: false));

        Assert.Equal("NGC  ·  CREATED 2026-09-28", d.Texts[0].Text);
        Assert.Equal("4200", d.Texts.Single(t => t.Mono).Text);
        Assert.Equal(72, d.Texts.Single(t => t.Mono).Size);   // capped by height, not width
    }

    [Fact]
    public void BigWithoutZerosPrintsANumberTwiceTodaysSize()
    {
        var d = BoxLabels.ComposeDrawing(Plain42, new BoxLabels.LabelStyle(BoxLabels.LayoutBig, LeadingZeros: false));

        Assert.True(d.Texts.Single(t => t.Mono).Size >= 2 * BoxLabels.CodeFontSize("ABCD 0000 0042") - 0.001);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("neon")]
    public void UnknownOrMissingLayoutIsStandard(string? layout) =>
        Assert.Equal(BoxLabels.LayoutStandard, BoxLabels.NormalizeLayout(layout));
}
