using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdoSort.Smoke.Brand;

namespace OrdoSort.Wpf.Tests;

/// <summary>The brand art (tools/OrdoSort.Smoke/Brand) holds up where the
/// Windows 11 icon guidance says icons fail: on a dark or a light taskbar,
/// and at 16 px. Rendering needs WPF, so these join
/// <see cref="HighlightContrastFixture"/>'s STA thread.</summary>
[Collection(HighlightContrastTests.Name)]
public class BrandArtTests
{
    private readonly HighlightContrastFixture _fx;
    public BrandArtTests(HighlightContrastFixture fx) => _fx = fx;

    public static TheoryData<string> Icons => new() { "OrdoSort", "Box Labels" };

    private static BrandIcon Find(string name) => name == "OrdoSort" ? BrandArt.OrdoSort : BrandArt.BoxLabels;

    /// <summary>The same path string feeds WPF (the app icons) and SVG (the
    /// website). Absolute M/L/H/V/C/A/Z commands with plain numbers mean the
    /// same thing in both; relative commands and the S/Q/T shorthands are
    /// where the two parsers have been known to disagree.</summary>
    [Theory, MemberData(nameof(Icons))]
    public void EveryPathUsesOnlyTheSubsetWpfAndSvgReadAlike(string name)
    {
        var icon = Find(name);
        foreach (var layer in icon.Master.Concat(icon.Small))
        {
            Assert.Matches(new Regex(@"^M[MLHVCAZ0-9., \-]*$"), layer.Path);
            Assert.Matches(new Regex(@"^#[0-9A-F]{6}$"), layer.Color);
        }
    }

    [Theory, MemberData(nameof(Icons))]
    public void EverySizeStandsOutOnBothTaskbars(string name) => _fx.Invoke(() =>
    {
        var weak = new List<string>();
        foreach (var size in BrandArt.IconSizes)
        {
            var bitmap = BrandRender.Icon(Find(name), size);
            foreach (var (taskbar, colour) in new[] { ("light", BrandRender.LightTaskbar), ("dark", BrandRender.DarkTaskbar) })
            {
                var coverage = BrandRender.TaskbarCoverage(bitmap, colour);
                if (coverage < 0.5) weak.Add($"{size}px on {taskbar}: {coverage:P1}");
            }
        }
        Assert.True(weak.Count == 0, $"{name} has too little at 3:1: " + string.Join("; ", weak));
    });

    /// <summary>Both apps can sit on one taskbar. At 16 px they have to be
    /// told apart by more than a detail.</summary>
    [Fact]
    public void TheTwoAppsLookDifferentAt16Px() => _fx.Invoke(() =>
    {
        var ordo = Pixels(BrandRender.Icon(BrandArt.OrdoSort, 16));
        var box = Pixels(BrandRender.Icon(BrandArt.BoxLabels, 16));
        var different = 0;
        for (var i = 0; i < ordo.Length; i += 4)
        {
            var distance = Math.Abs(ordo[i] - box[i]) + Math.Abs(ordo[i + 1] - box[i + 1]) + Math.Abs(ordo[i + 2] - box[i + 2]);
            if (distance > 60) different++;
        }
        Assert.True(different >= 16 * 16 / 2, $"only {different} of 256 pixels differ");
    });

    [Fact]
    public void AnIcoWrittenFromTheArtReadsBackWithEverySize() => _fx.Invoke(() =>
    {
        using var stream = new MemoryStream();
        IcoWriter.Write(stream, BrandCommand.IcoFrames(BrandArt.OrdoSort));
        stream.Position = 0;

        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        Assert.Equal(BrandArt.IconSizes, decoder.Frames.Select(f => f.PixelWidth));
        Assert.Equal(BrandArt.IconSizes, decoder.Frames.Select(f => f.PixelHeight));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void AnIcoFrameMustBe1To256Pixels(int size) =>
        Assert.Throws<ArgumentException>(() => IcoWriter.Write(new MemoryStream(), new[] { (size, new byte[] { 1 }) }));

    [Fact]
    public void AnIcoNeedsAFrame() =>
        Assert.Throws<ArgumentException>(() => IcoWriter.Write(new MemoryStream(), Array.Empty<(int, byte[])>()));

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }
}
