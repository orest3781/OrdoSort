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
        var dir = Directory.CreateTempSubdirectory("fr_labels").FullName;
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
}
