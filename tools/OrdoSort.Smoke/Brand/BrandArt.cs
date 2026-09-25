using System.Globalization;
using System.Text;

namespace OrdoSort.Smoke.Brand;

/// <summary>One filled shape of an icon.</summary>
/// <param name="Color">"#RRGGBB".</param>
/// <param name="Path">Geometry in the M/L/H/V/C/A/Z subset with absolute
/// coordinates only, which reads the same as an SVG path and as WPF path
/// markup. BrandAssetsTests holds every path to that subset.</param>
public sealed record BrandLayer(string Color, string Path);

/// <summary>An app icon, drawn twice: a master on a 48-unit grid for 20 px
/// and up, and a hand-tuned version on a 16-unit grid whose edges land on
/// whole pixels, because a scaled-down master goes soft at 16 px.</summary>
public sealed record BrandIcon(string Name, IReadOnlyList<BrandLayer> Master, IReadOnlyList<BrandLayer> Small)
{
    public const double MasterGrid = 48;
    public const double SmallGrid = 16;

    /// <summary>Which drawing to use at a given pixel size, and its grid.</summary>
    public (IReadOnlyList<BrandLayer> Layers, double Grid) For(int size) =>
        size <= SmallGrid ? (Small, SmallGrid) : (Master, MasterGrid);
}

/// <summary>The brand art, as data: the single source for both app icons,
/// the website mark and favicons, and docs/brand. Regenerate everything with
/// <c>dotnet run --project tools/OrdoSort.Smoke -- brand .</c>; see
/// docs/brand/BRAND.md.
///
/// Concept A from the 2026-09 rebrand: three index dividers with stepped
/// tabs, the front one bronze, meaning "sorted into place". Box Labels puts
/// the back tab behind an archive-box front with its handle cut-out.</summary>
public static class BrandArt
{
    // The two plates sit in the luminance band 0.14-0.26, the only band that
    // reaches 3:1 against both the light (#F3F3F3) and the dark (#202020)
    // Windows taskbar. The plate colour is also what tells the two apps apart.
    public const string Slate = "#647080";    // OrdoSort plate
    public const string Brass = "#8A6A34";    // Box Labels plate
    public const string Ink = "#2A2F36";      // handle hole
    public const string Paper = "#F4F1EA";    // front sheet, label
    public const string Mid = "#828C99";      // middle divider
    public const string Dim = "#3A424D";      // back divider
    public const string Line = "#6E7885";     // text lines on the front sheet
    public const string Bronze = "#E0B96A";   // front tab
    public const string Manila = "#E6CF9C";   // box front

    /// <summary>The frames every .ico carries, per the Windows 11 icon
    /// guidance.</summary>
    public static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    private const string Plate48 =
        "M11,3 H37 A8,8 0 0 1 45,11 V37 A8,8 0 0 1 37,45 H11 A8,8 0 0 1 3,37 V11 A8,8 0 0 1 11,3 Z";
    private const string Plate16 =
        "M3,0 H13 A3,3 0 0 1 16,3 V13 A3,3 0 0 1 13,16 H3 A3,3 0 0 1 0,13 V3 A3,3 0 0 1 3,0 Z";

    public static BrandIcon OrdoSort { get; } = new("OrdoSort",
        Master: new BrandLayer[]
        {
            new(Slate, Plate48),
            new(Dim, "M9,11 H19 V27 H9 Z"),
            new(Mid, "M19,16 H29 V27 H19 Z"),
            new(Bronze, "M29,21 H39 V27 H29 Z"),
            new(Paper, "M9,26 H39 V39 H9 Z"),
            new(Line, "M13,30 H31 V31.5 H13 Z"),
            new(Line, "M13,33.5 H25 V35 H13 Z"),
        },
        Small: new BrandLayer[]
        {
            new(Slate, Plate16),
            new(Dim, "M2,3 H6 V9 H2 Z"),
            new(Mid, "M6,5 H10 V9 H6 Z"),
            new(Bronze, "M10,7 H14 V9 H10 Z"),
            new(Paper, "M2,9 H14 V14 H2 Z"),
        });

    public static BrandIcon BoxLabels { get; } = new("Box Labels",
        Master: new BrandLayer[]
        {
            new(Brass, Plate48),
            new(Dim, "M11,13 H21 V20 H11 Z"),
            new(Manila, "M9,19 H39 V38 H9 Z"),
            new(Ink, "M21,22 H27 A2,2 0 0 1 29,24 A2,2 0 0 1 27,26 H21 A2,2 0 0 1 19,24 A2,2 0 0 1 21,22 Z"),
            new(Paper, "M14,29 H34 V35 H14 Z"),
            new(Slate, "M14,29 H17 V35 H14 Z"),
        },
        Small: new BrandLayer[]
        {
            new(Brass, Plate16),
            new(Dim, "M4,3 H7 V6 H4 Z"),
            new(Manila, "M3,6 H13 V13 H3 Z"),
            new(Ink, "M6,7 H10 V8 H6 Z"),
            new(Paper, "M5,10 H11 V12 H5 Z"),
            new(Slate, "M5,10 H6 V12 H5 Z"),
        });

    /// <summary>The layers as a standalone SVG document. Deterministic text,
    /// so a committed copy can be checked for drift.</summary>
    public static string ToSvg(IReadOnlyList<BrandLayer> layers, double grid)
    {
        var size = grid.ToString(CultureInfo.InvariantCulture);
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
            .Append(size).Append(' ').Append(size).Append("\">\n");
        foreach (var layer in layers)
            svg.Append("  <path fill=\"").Append(layer.Color).Append("\" d=\"").Append(layer.Path).Append("\"/>\n");
        svg.Append("</svg>\n");
        return svg.ToString();
    }
}
