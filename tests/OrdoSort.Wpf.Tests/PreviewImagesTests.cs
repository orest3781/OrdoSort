using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>HEIC photos made into something Edge can show. WIC picks the
/// decoder by the file's content, so a JPEG saved under a .heic name walks
/// the real conversion path on any PC, HEIF extension or not.</summary>
public class PreviewImagesTests
{
    private static void WriteJpeg(string path, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [Theory]
    [InlineData("IMG_1.HEIC", true)]
    [InlineData("IMG_1.heif", true)]
    [InlineData("IMG_1.jpg", false)]
    [InlineData("dance.gif", false)]
    public void OnlyHeicIsConverted(string name, bool expected) =>
        Assert.Equal(expected, new PreviewImages().NeedsConversion(name));

    [Fact]
    public void AReadablePhotoBecomesAJpeg()
    {
        using var tmp = new TempDir();
        var photo = Path.Combine(tmp.Path, "IMG_1.heic");
        WriteJpeg(photo, 40, 30);

        var preview = new PreviewImages(Path.Combine(tmp.Path, "preview")).Prepare(photo);

        Assert.EndsWith(".jpg", preview);
        var decoded = BitmapDecoder.Create(new Uri(preview), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(40, decoded.PixelWidth);
    }

    [Fact]
    public void AHugePhotoIsShrunkForThePane()
    {
        using var tmp = new TempDir();
        var photo = Path.Combine(tmp.Path, "big.heic");
        WriteJpeg(photo, 4800, 1200);

        var preview = new PreviewImages(Path.Combine(tmp.Path, "preview")).Prepare(photo);

        var decoded = BitmapDecoder.Create(new Uri(preview), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(2400, decoded.PixelWidth);
    }

    [Fact]
    public void APhotoWindowsCannotReadGetsAPageSayingWhy()
    {
        using var tmp = new TempDir();
        var photo = tmp.File("IMG_2.heic", "not really a photo");

        var preview = new PreviewImages(Path.Combine(tmp.Path, "preview")).Prepare(photo);

        Assert.EndsWith(".html", preview);
        var page = File.ReadAllText(preview);
        Assert.Contains("No preview for IMG_2.heic", page);
        Assert.Contains("HEIF Image Extensions", page);
    }

    [Fact]
    public void OnlyTheLastFewPreviewsAreKept()
    {
        using var tmp = new TempDir();
        var folder = Path.Combine(tmp.Path, "preview");
        var previews = new PreviewImages(folder);
        var photo = tmp.File("x.heic", "junk");

        for (var i = 0; i < 10; i++) previews.Prepare(photo);

        Assert.True(Directory.GetFiles(folder).Length <= 6);
    }
}
