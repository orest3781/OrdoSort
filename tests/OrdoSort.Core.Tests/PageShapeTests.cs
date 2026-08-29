using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Core.Tests;

/// <summary>PageShape.AspectOf, the measurement behind "fit the viewer pane
/// to the document when a session starts". Fixture builders follow
/// PageCountsTests' MakePlain/MakeEncrypted idiom.</summary>
public class PageShapeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pageshapetest_" + Guid.NewGuid());
    public PageShapeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>US Letter by default (612 x 792 pt), the shape almost every
    /// scanned document in the inbox arrives as.</summary>
    private string MakePage(string name, double widthPt = 612, double heightPt = 792, int rotate = 0)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(widthPt);
        page.Height = XUnit.FromPoint(heightPt);
        page.Rotate = rotate;
        doc.Save(path);
        return path;
    }

    [Fact]
    public void APortraitPageIsTallerThanItIsWide()
    {
        var aspect = PageShape.AspectOf(MakePage("portrait.pdf"));
        Assert.NotNull(aspect);
        Assert.Equal(612d / 792d, aspect!.Value, 4);
    }

    [Fact]
    public void ALandscapePageIsWiderThanItIsTall()
    {
        var aspect = PageShape.AspectOf(MakePage("landscape.pdf", 792, 612));
        Assert.NotNull(aspect);
        Assert.Equal(792d / 612d, aspect!.Value, 4);
    }

    /// <summary>The case the naive version gets wrong: the MediaBox says
    /// portrait, /Rotate says the viewer must turn it a quarter, and what the
    /// pane has to match is what the viewer draws — landscape.</summary>
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    [InlineData(-90)]
    [InlineData(450)]
    public void AQuarterTurnedPortraitPageReadsAsLandscape(int rotate)
    {
        var aspect = PageShape.AspectOf(MakePage($"turned{rotate}.pdf", 612, 792, rotate));
        Assert.NotNull(aspect);
        Assert.Equal(792d / 612d, aspect!.Value, 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(360)]
    public void AHalfTurnLeavesTheShapeAlone(int rotate)
    {
        var aspect = PageShape.AspectOf(MakePage($"half{rotate}.pdf", 612, 792, rotate));
        Assert.NotNull(aspect);
        Assert.Equal(612d / 792d, aspect!.Value, 4);
    }

    [Fact]
    public void AnEncryptedPdfMeasuresToNothingRatherThanThrowing()
    {
        var path = Path.Combine(_dir, "locked.pdf");
        using (var doc = new PdfDocument())
        {
            doc.AddPage();
            doc.SecuritySettings.UserPassword = "secret";
            doc.SecuritySettings.OwnerPassword = "owner-secret";
            doc.Save(path);
        }
        Assert.Null(PageShape.AspectOf(path));
    }

    [Fact]
    public void GarbageBytesMeasureToNothingRatherThanThrowing()
    {
        var path = Path.Combine(_dir, "notreally.pdf");
        File.WriteAllText(path, "this is not a PDF");
        Assert.Null(PageShape.AspectOf(path));
    }

    [Fact]
    public void AMissingFileMeasuresToNothingRatherThanThrowing() =>
        Assert.Null(PageShape.AspectOf(Path.Combine(_dir, "never-written.pdf")));
}
