using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdoSort.Wpf.Theme;
using WpfPath = System.Windows.Shapes.Path;

namespace OrdoSort.Smoke.Brand;

/// <summary>Rasterizes <see cref="BrandArt"/> with WPF, the same engine that
/// draws the app. Every method must run on an STA thread.</summary>
public static class BrandRender
{
    /// <summary>The Windows 11 taskbar colours an icon has to stand out on.</summary>
    public static readonly Color LightTaskbar = Color.FromRgb(0xF3, 0xF3, 0xF3);
    public static readonly Color DarkTaskbar = Color.FromRgb(0x20, 0x20, 0x20);

    /// <summary>The icon at an exact pixel size, on a transparent background.</summary>
    public static BitmapSource Icon(BrandIcon icon, int size) => Rasterize(IconVisual(icon, size), size, size);

    /// <summary>A mark and a word side by side, on a transparent background:
    /// the README banner and the brand sheet.</summary>
    /// <param name="dark">True for text readable on a dark page.</param>
    public static BitmapSource Lockup(BrandIcon icon, string word, bool dark)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(IconVisual(icon, 112));
        row.Children.Add(new TextBlock
        {
            Text = word,
            FontFamily = AppFonts.CreateDefault(),
            FontWeight = FontWeights.Bold,
            FontSize = 84,
            Foreground = Solid(dark ? "#ECEAE5" : "#1C1F24"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28, 0, 8, 6),
        });
        return RasterizeToContent(new Border { Padding = new Thickness(8), Child = row });
    }

    /// <summary>The 1200×630 social card the website links as og:image.</summary>
    public static BitmapSource SocialCard()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(56, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = "OrdoSort",
            FontFamily = AppFonts.CreateDefault(),
            FontWeight = FontWeights.Bold,
            FontSize = 112,
            Foreground = Solid("#ECEAE5"),
        });
        text.Children.Add(new Border
        {
            Height = 4, Width = 96, Background = Solid("#D2AE6B"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 14, 0, 22)
        });
        text.Children.Add(new TextBlock
        {
            Text = "Every document, where it belongs.",
            FontFamily = AppFonts.CreateDefault(),
            FontSize = 46,
            Foreground = Solid("#A9ADB3"),
        });
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(96, 0, 0, 0)
        };
        row.Children.Add(IconVisual(BrandArt.OrdoSort, 256));
        row.Children.Add(text);
        return Rasterize(new Border { Background = Solid("#1A1C1F"), Child = row }, 1200, 630);
    }

    /// <summary>Every icon size, actual pixels and 4× zoomed, on both taskbar
    /// colours: the sheet to eyeball before shipping a change to the art.</summary>
    public static BitmapSource IconReview()
    {
        var sheet = new StackPanel();
        foreach (var taskbar in new[] { LightTaskbar, DarkTaskbar })
            foreach (var icon in new[] { BrandArt.OrdoSort, BrandArt.BoxLabels })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(taskbar) };
                foreach (var size in BrandArt.IconSizes.Where(s => s <= 64))
                    row.Children.Add(Pixels(Icon(icon, size), size, 1));
                foreach (var size in new[] { 16, 20, 24, 32 })
                    row.Children.Add(Pixels(Icon(icon, size), size, 4));
                sheet.Children.Add(row);
            }
        return RasterizeToContent(sheet);
    }

    /// <summary>The share of an icon's drawn pixels (alpha at least half)
    /// that reach 3:1 contrast against <paramref name="background"/>. Windows
    /// asks for at least half.</summary>
    public static double TaskbarCoverage(BitmapSource icon, Color background)
    {
        var stride = icon.PixelWidth * 4;
        var pixels = new byte[stride * icon.PixelHeight];
        new FormatConvertedBitmap(icon, PixelFormats.Pbgra32, null, 0).CopyPixels(pixels, stride, 0);
        var bg = new Rgb(background.R, background.G, background.B);
        int drawn = 0, strong = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha < 128) continue;
            drawn++;
            // Premultiplied BGRA, composited over the background.
            byte Over(int channel, byte under) => (byte)Math.Round(pixels[i + channel] + under * (255 - alpha) / 255.0);
            var seen = new Rgb(Over(2, bg.R), Over(1, bg.G), Over(0, bg.B));
            if (ThemePalette.ContrastRatio(seen, bg) >= 3) strong++;
        }
        return drawn == 0 ? 0 : (double)strong / drawn;
    }

    /// <summary>PNG bytes for a rendered image.</summary>
    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static FrameworkElement IconVisual(BrandIcon icon, int size)
    {
        var (layers, grid) = icon.For(size);
        var canvas = new Canvas { Width = grid, Height = grid };
        foreach (var layer in layers)
            canvas.Children.Add(new WpfPath { Data = Geometry.Parse(layer.Path), Fill = Solid(layer.Color) });
        // The small drawing sits on whole pixels at exactly 16 px; aliased
        // edges keep it crisp instead of smearing every edge half a pixel.
        if (size == (int)BrandIcon.SmallGrid) RenderOptions.SetEdgeMode(canvas, EdgeMode.Aliased);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    private static Image Pixels(BitmapSource bitmap, int size, int zoom)
    {
        var image = new Image { Source = bitmap, Width = size * zoom, Height = size * zoom, Margin = new Thickness(8) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return image;
    }

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static BitmapSource RasterizeToContent(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Rasterize(element, (int)Math.Ceiling(element.DesiredSize.Width), (int)Math.Ceiling(element.DesiredSize.Height));
    }

    private static BitmapSource Rasterize(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }
}
