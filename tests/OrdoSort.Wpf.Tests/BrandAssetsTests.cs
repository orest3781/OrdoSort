using System.Windows.Media.Imaging;
using OrdoSort.Smoke.Brand;

namespace OrdoSort.Wpf.Tests;

/// <summary>The files generated from BrandArt and committed to the repo are
/// the current ones. A change to the art that was not followed by running
/// the generator fails here. See docs/brand/BRAND.md for the command.</summary>
[Collection(HighlightContrastTests.Name)]
public class BrandAssetsTests
{
    private readonly HighlightContrastFixture _fx;
    public BrandAssetsTests(HighlightContrastFixture fx) => _fx = fx;

    public static TheoryData<string> AppIcons => new()
    {
        "src/OrdoSort.Wpf/app.ico",
        "src/BoxLabels.App/app.ico",
    };

    [Theory, MemberData(nameof(AppIcons))]
    public void EachAppIconCarriesEveryWindowsSize(string relative) => _fx.Invoke(() =>
    {
        using var stream = File.OpenRead(Path.Combine(FindRepoRoot(), relative));
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        Assert.Equal(BrandArt.IconSizes, decoder.Frames.Select(f => f.PixelWidth));
    });

    /// <summary>Line endings are ignored: git may check a text file out with
    /// CRLF on Windows, and that is not drift.</summary>
    [Fact]
    public void GeneratedTextFilesMatchTheArt()
    {
        var root = FindRepoRoot();
        foreach (var (relative, content) in BrandCommand.TextOutputs())
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"{relative} is missing; run the brand generator");
            Assert.True(BrandCommand.SameText(content, File.ReadAllText(path)),
                $"{relative} is out of date; run: dotnet run --project tools/OrdoSort.Smoke -- brand .");
        }
    }

    [Fact]
    public void TheWebsiteHeaderMarkMatchesTheArt()
    {
        var index = File.ReadAllText(Path.Combine(FindRepoRoot(), BrandCommand.IndexHtml));

        Assert.True(BrandCommand.SameText(BrandCommand.WithInlineMark(index), index),
            "the header mark in ordosort.com/index.html is out of date; run the brand generator");
    }

    [Fact]
    public void BoxLabelsLoadsItsOwnIcon() => _fx.Invoke(() =>
    {
        var icon = BitmapFrame.Create(BoxLabelsApp.App.AppIconUri);

        Assert.True(icon.PixelWidth > 0);
    });

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("couldn't find OrdoSort.sln walking up from " + AppContext.BaseDirectory);
    }
}
