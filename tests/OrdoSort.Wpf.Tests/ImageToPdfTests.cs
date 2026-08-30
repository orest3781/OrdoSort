using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdoSort.Wpf.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Wpf.Tests;

/// <summary>Images need no Office, so unlike the Office adapter these are
/// ordinary hermetic tests. Fixtures are encoded in-process by WPF itself.</summary>
[Collection(HighlightContrastTests.Name)]
public class ImageToPdfTests
{
    private readonly HighlightContrastFixture _fx;
    public ImageToPdfTests(HighlightContrastFixture fx) => _fx = fx;

    private static byte[] Png(int width, int height, double dpi = 96)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)200);
        var source = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] MultiPageTiff(int frames)
    {
        var encoder = new TiffBitmapEncoder();
        for (var i = 0; i < frames; i++)
        {
            var stride = 8 * 4;
            var pixels = new byte[stride * 8];
            var source = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            encoder.Frames.Add(BitmapFrame.Create(source));
        }
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (int Pages, double Width, double Height) Read(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.InformationOnly);
        return (doc.PageCount, doc.Pages[0].Width.Point, doc.Pages[0].Height.Point);
    }

    [Theory]
    [InlineData("png", true)] [InlineData("jpg", true)] [InlineData("TIFF", true)]
    [InlineData("bmp", true)] [InlineData("gif", true)]
    [InlineData("docx", false)] [InlineData("pdf", false)]
    public void HandlesTheImageTypesAndNothingElse(string extension, bool handled) =>
        _fx.Invoke(() => Assert.Equal(handled, new ImageToPdf().Handles(extension)));

    [Fact]
    public void AnImageBecomesOnePage() => _fx.Invoke(() =>
    {
        var r = new ImageToPdf().ToPdf(Png(100, 100), "shot.png", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1, Read(r.Pdf!).Pages);
    });

    [Fact]
    public void AMultiPageTiffBecomesOnePagePerFrame() => _fx.Invoke(() =>
    {
        // The reason images live in the Wpf layer at all: WPF's decoder
        // exposes every frame, and a multi-page TIFF is what a sheet-feed
        // scanner produces.
        var r = new ImageToPdf().ToPdf(MultiPageTiff(4), "scan.tif", Array.Empty<string>(), null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(4, Read(r.Pdf!).Pages);
    });

    [Fact]
    public void AScanAtItsOwnDpiComesOutAtItsTrueSize() => _fx.Invoke(() =>
    {
        // 1700 x 2200 at 200 DPI is exactly 8.5 x 11 inches — 612 x 792 pt.
        var r = new ImageToPdf().ToPdf(Png(1700, 2200, dpi: 200), "scan.png", Array.Empty<string>(), null);
        var (_, width, height) = Read(r.Pdf!);
        Assert.Equal(612, width, 1);
        Assert.Equal(792, height, 1);
    });

    [Fact]
    public void APhotoWithMeaninglessDpiIsFittedToLetterInsteadOfAnAbsurdPage() => _fx.Invoke(() =>
    {
        // 4000 x 3000 at 72 DPI would be 55 x 41 inches. Fit instead.
        var r = new ImageToPdf().ToPdf(Png(4000, 3000, dpi: 72), "photo.jpg", Array.Empty<string>(), null);
        var (_, width, height) = Read(r.Pdf!);
        Assert.True(width <= 792 + 1 && height <= 792 + 1, $"page came out {width} x {height} pt");
        Assert.True(width > height, "a landscape photo should get a landscape page");
    });

    [Fact]
    public void ACorruptImageIsAnErrorNotAThrow() => _fx.Invoke(() =>
    {
        var r = new ImageToPdf().ToPdf([0, 1, 2, 3], "broken.png", Array.Empty<string>(), null);
        Assert.Equal("error", r.Status);
    });

    [Fact]
    public void ItNeverPrompts() => _fx.Invoke(() =>
    {
        var asked = false;
        new ImageToPdf().ToPdf([0, 1, 2], "x.png", ["pw"], _ => { asked = true; return "pw"; });
        Assert.False(asked);
    });
}
