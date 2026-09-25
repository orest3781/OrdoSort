using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrdoSort.Smoke.Brand;

/// <summary><c>brand &lt;repoRoot&gt; [--check]</c>: writes every file made
/// from <see cref="BrandArt"/>, or with --check only reports text outputs
/// that no longer match it.
///
/// Only text outputs are compared. PNG bytes depend on the machine's
/// rendering stack, so a byte comparison would fail for reasons that have
/// nothing to do with the art; BrandAssetsTests checks the committed icons by
/// their frame sizes instead.</summary>
public static class BrandCommand
{
    /// <summary>The website page whose header mark this command owns.</summary>
    public const string IndexHtml = "ordosort.com/index.html";

    /// <summary>The header mark in <see cref="IndexHtml"/> sits between these
    /// two comments; everything between them is replaced on each run.</summary>
    public const string MarkBegin = "<!-- BRAND-MARK:BEGIN (generated: dotnet run --project tools/OrdoSort.Smoke -- brand .) -->";
    public const string MarkEnd = "<!-- BRAND-MARK:END -->";

    public static int Run(string[] args) => SmokeUi.RunSta(() => Drive(args), "BRAND OK", "BRAND FAIL:");

    /// <summary>Repo-relative path → exact file content, for every generated
    /// text file.</summary>
    public static IReadOnlyDictionary<string, string> TextOutputs() => new Dictionary<string, string>
    {
        ["docs/brand/mark-ordosort.svg"] = BrandArt.ToSvg(BrandArt.OrdoSort.Master, BrandIcon.MasterGrid),
        ["docs/brand/mark-boxlabels.svg"] = BrandArt.ToSvg(BrandArt.BoxLabels.Master, BrandIcon.MasterGrid),
        ["ordosort.com/assets/favicon.svg"] = BrandArt.ToSvg(BrandArt.OrdoSort.Master, BrandIcon.MasterGrid),
    };

    /// <summary>Equal apart from line endings, which git may rewrite to CRLF
    /// on checkout.</summary>
    public static bool SameText(string expected, string actual) =>
        expected.Replace("\r\n", "\n") == actual.Replace("\r\n", "\n");

    /// <summary>The .ico frames for an icon, in <see cref="BrandArt.IconSizes"/> order.</summary>
    public static List<(int Size, byte[] Png)> IcoFrames(BrandIcon icon) =>
        BrandArt.IconSizes.Select(size => (size, BrandRender.Png(BrandRender.Icon(icon, size)))).ToList();

    /// <summary><paramref name="html"/> with the header mark between
    /// <see cref="MarkBegin"/> and <see cref="MarkEnd"/> regenerated.</summary>
    /// <exception cref="InvalidOperationException">The markers are missing.</exception>
    public static string WithInlineMark(string html)
    {
        var begin = html.IndexOf(MarkBegin, StringComparison.Ordinal);
        var end = html.IndexOf(MarkEnd, StringComparison.Ordinal);
        if (begin < 0 || end < begin)
            throw new InvalidOperationException($"{IndexHtml} has no {MarkBegin} ... {MarkEnd} block");
        var newline = html.Contains("\r\n") ? "\r\n" : "\n";
        var mark = new StringBuilder();
        mark.Append(newline).Append("      <svg class=\"mark\" viewBox=\"0 0 48 48\" width=\"28\" height=\"28\" aria-hidden=\"true\">");
        foreach (var layer in BrandArt.OrdoSort.Master)
            mark.Append(newline).Append("        <path fill=\"").Append(layer.Color).Append("\" d=\"").Append(layer.Path).Append("\"/>");
        mark.Append(newline).Append("      </svg>").Append(newline).Append("      ");
        var start = begin + MarkBegin.Length;
        return html[..start] + mark + html[end..];
    }

    private static List<string> Drive(string[] args)
    {
        var failures = new List<string>();
        if (args.Length < 2)
        {
            failures.Add("usage: brand <repoRoot> [--check]");
            return failures;
        }
        var root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "OrdoSort.sln")))
        {
            failures.Add($"{root} is not the OrdoSort repo root (no OrdoSort.sln)");
            return failures;
        }
        var check = args.Contains("--check");
        var outdated = "is out of date; run: dotnet run --project tools/OrdoSort.Smoke -- brand .";

        foreach (var (relative, content) in TextOutputs())
        {
            var path = Path.Combine(root, relative);
            if (check)
            {
                if (!File.Exists(path) || !SameText(content, File.ReadAllText(path)))
                    failures.Add($"{relative} {outdated}");
                continue;
            }
            Write(path, Encoding.UTF8.GetBytes(content));
        }

        var indexPath = Path.Combine(root, IndexHtml);
        var index = File.ReadAllText(indexPath);
        if (check)
        {
            if (!SameText(WithInlineMark(index), index)) failures.Add($"{IndexHtml}'s header mark {outdated}");
            return failures;
        }
        Write(indexPath, new UTF8Encoding(false).GetBytes(WithInlineMark(index)));

        // The bundled font is reached by pack URI, which needs an Application.
        SmokeUi.Boot();
        WriteIco(Path.Combine(root, "src", "OrdoSort.Wpf", "app.ico"), BrandArt.OrdoSort);
        WriteIco(Path.Combine(root, "src", "BoxLabels.App", "app.ico"), BrandArt.BoxLabels);

        var docs = Path.Combine(root, "docs", "brand");
        WritePng(Path.Combine(docs, "lockup-light.png"), BrandRender.Lockup(BrandArt.OrdoSort, "OrdoSort", dark: false));
        WritePng(Path.Combine(docs, "lockup-dark.png"), BrandRender.Lockup(BrandArt.OrdoSort, "OrdoSort", dark: true));
        WritePng(Path.Combine(docs, "icon-review.png"), BrandRender.IconReview());

        var assets = Path.Combine(root, "ordosort.com", "assets");
        WritePng(Path.Combine(assets, "favicon-32.png"), BrandRender.Icon(BrandArt.OrdoSort, 32));
        WritePng(Path.Combine(assets, "icon-192.png"), BrandRender.Icon(BrandArt.OrdoSort, 192));
        WritePng(Path.Combine(assets, "icon-512.png"), BrandRender.Icon(BrandArt.OrdoSort, 512));
        WritePng(Path.Combine(assets, "apple-touch-icon.png"), TouchIcon());
        WritePng(Path.Combine(assets, "og.png"), BrandRender.SocialCard());
        return failures;
    }

    /// <summary>iOS rounds the corners of a touch icon itself and paints any
    /// transparency black, so this one is full-bleed on the plate colour.</summary>
    private static BitmapSource TouchIcon()
    {
        const int size = 180;
        var card = new Border
        {
            Width = size, Height = size,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BrandArt.Slate)),
            Child = new Image { Source = BrandRender.Icon(BrandArt.OrdoSort, size) },
        };
        card.Measure(new System.Windows.Size(size, size));
        card.Arrange(new System.Windows.Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card);
        return bitmap;
    }

    private static void WriteIco(string path, BrandIcon icon)
    {
        using var stream = new MemoryStream();
        IcoWriter.Write(stream, IcoFrames(icon));
        Write(path, stream.ToArray());
    }

    private static void WritePng(string path, BitmapSource image) => Write(path, BrandRender.Png(image));

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        Console.WriteLine("wrote " + path);
    }
}
