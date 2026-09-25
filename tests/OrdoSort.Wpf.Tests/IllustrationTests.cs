using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The empty-state pictures in Theme/Illustrations.xaml, rendered
/// for real.</summary>
[Collection(HighlightContrastTests.Name)]
public class IllustrationTests
{
    private readonly HighlightContrastFixture _fx;
    public IllustrationTests(HighlightContrastFixture fx) => _fx = fx;

    /// <summary>The old padlock drew its shackle on top of the body, so the
    /// left leg showed through the lock. The body now covers it. At 100 px
    /// the picture's 100-unit canvas maps one unit to one pixel, so these
    /// points are in the XAML's own coordinates.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheUnlockShackleDisappearsIntoThePadlockBody(bool dark) => _fx.Invoke(() =>
    {
        try
        {
            ThemeManager.Apply(_fx.App, dark);
            var pixels = Render("Illustration.Unlock");
            var p = ThemeManager.Current;

            // Inside the body, where the left leg runs down behind it.
            AssertNear(p.Surface, pixels.At(34, 54));
            // Above the body the leg is visible, so the check above is not
            // passing just because nothing was drawn at x=34.
            AssertNear(p.SubtleText, pixels.At(34, 40));
        }
        finally { ThemeManager.Apply(_fx.App, dark: false); }
    });

    [Theory]
    [InlineData("Illustration.InboxClear")]
    [InlineData("Illustration.Done")]
    [InlineData("Illustration.LabelsEmpty")]
    [InlineData("Illustration.Unlock")]
    public void EachPictureCarriesTheBrandAccent(string key) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var pixels = Render(key);
        var bronze = ThemeManager.Current.AccentBronze;

        Assert.True(pixels.Count(c => Distance(c, bronze) < 8) >= 20, $"{key} has no bronze accent");
    });

    private sealed record Pixels(byte[] Bgra, int Width)
    {
        public Rgb At(int x, int y)
        {
            var i = (y * Width + x) * 4;
            return new Rgb(Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        }

        public int Count(Func<Rgb, bool> match)
        {
            var n = 0;
            for (var i = 0; i < Bgra.Length; i += 4)
                if (match(new Rgb(Bgra[i + 2], Bgra[i + 1], Bgra[i]))) n++;
            return n;
        }
    }

    private Pixels Render(string key)
    {
        var picture = new ContentControl { Template = (ControlTemplate)_fx.App.FindResource(key), Width = 100, Height = 100 };
        var card = new Border { Child = picture };
        card.SetResourceReference(Border.BackgroundProperty, "Theme.WindowBg");
        card.Measure(new Size(100, 100));
        card.Arrange(new Rect(0, 0, 100, 100));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card);
        var bgra = new byte[100 * 100 * 4];
        bitmap.CopyPixels(bgra, 100 * 4, 0);
        return new Pixels(bgra, 100);
    }

    private static int Distance(Rgb a, Rgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static void AssertNear(Rgb expected, Rgb actual) =>
        Assert.True(Distance(expected, actual) < 12, $"expected about {expected}, got {actual}");
}
