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
    public static int Run(string[] args) => SmokeUi.RunSta(() => Drive(args), "BRAND OK", "BRAND FAIL:");

    /// <summary>Repo-relative path → exact file content, for every generated
    /// text file.</summary>
    public static IReadOnlyDictionary<string, string> TextOutputs() => new Dictionary<string, string>
    {
        ["docs/brand/mark-ordosort.svg"] = BrandArt.ToSvg(BrandArt.OrdoSort.Master, BrandIcon.MasterGrid),
        ["docs/brand/mark-boxlabels.svg"] = BrandArt.ToSvg(BrandArt.BoxLabels.Master, BrandIcon.MasterGrid),
    };

    /// <summary>Equal apart from line endings, which git may rewrite to CRLF
    /// on checkout.</summary>
    public static bool SameText(string expected, string actual) =>
        expected.Replace("\r\n", "\n") == actual.Replace("\r\n", "\n");

    /// <summary>The .ico frames for an icon, in <see cref="BrandArt.IconSizes"/> order.</summary>
    public static List<(int Size, byte[] Png)> IcoFrames(BrandIcon icon) =>
        BrandArt.IconSizes.Select(size => (size, BrandRender.Png(BrandRender.Icon(icon, size)))).ToList();

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

        foreach (var (relative, content) in TextOutputs())
        {
            var path = Path.Combine(root, relative);
            if (check)
            {
                if (!File.Exists(path) || !SameText(content, File.ReadAllText(path)))
                    failures.Add($"{relative} is out of date; run: dotnet run --project tools/OrdoSort.Smoke -- brand .");
                continue;
            }
            Write(path, System.Text.Encoding.UTF8.GetBytes(content));
        }
        if (check) return failures;

        // The bundled font is reached by pack URI, which needs an Application.
        SmokeUi.Boot();
        WriteIco(Path.Combine(root, "src", "OrdoSort.Wpf", "app.ico"), BrandArt.OrdoSort);
        WriteIco(Path.Combine(root, "src", "BoxLabels.App", "app.ico"), BrandArt.BoxLabels);
        var docs = Path.Combine(root, "docs", "brand");
        WritePng(Path.Combine(docs, "lockup-light.png"), BrandRender.Lockup(BrandArt.OrdoSort, "OrdoSort", dark: false));
        WritePng(Path.Combine(docs, "lockup-dark.png"), BrandRender.Lockup(BrandArt.OrdoSort, "OrdoSort", dark: true));
        WritePng(Path.Combine(docs, "icon-review.png"), BrandRender.IconReview());
        return failures;
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
