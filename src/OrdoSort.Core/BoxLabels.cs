using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Core;

/// <summary>Code 39 barcode encoding — the symbology every handheld scanner
/// reads out of the box: uppercase letters, digits, no checksum required,
/// self-checking per character.</summary>
public static class Code39
{
    // 9 elements per character (bar,space,bar,space,bar,space,bar,space,bar),
    // '1' = wide, '0' = narrow — the standard AIM table for A-Z, 0-9 and '*'.
    private static readonly Dictionary<char, string> Patterns = new()
    {
        ['0'] = "000110100", ['1'] = "100100001", ['2'] = "001100001",
        ['3'] = "101100000", ['4'] = "000110001", ['5'] = "100110000",
        ['6'] = "001110000", ['7'] = "000100101", ['8'] = "100100100",
        ['9'] = "001100100",
        ['A'] = "100001001", ['B'] = "001001001", ['C'] = "101001000",
        ['D'] = "000011001", ['E'] = "100011000", ['F'] = "001011000",
        ['G'] = "000001101", ['H'] = "100001100", ['I'] = "001001100",
        ['J'] = "000011100", ['K'] = "100000011", ['L'] = "001000011",
        ['M'] = "101000010", ['N'] = "000010011", ['O'] = "100010010",
        ['P'] = "001010010", ['Q'] = "000000111", ['R'] = "100000110",
        ['S'] = "001000110", ['T'] = "000010110", ['U'] = "110000001",
        ['V'] = "011000001", ['W'] = "111000000", ['X'] = "010010001",
        ['Y'] = "110010000", ['Z'] = "011010000",
        ['*'] = "010010100",
    };

    /// <summary>One printed element: a bar or a space, narrow or wide.</summary>
    public readonly record struct Element(bool Bar, bool Wide);

    /// <summary>Encode text (A-Z, 0-9) as elements, including the start/stop
    /// '*' characters and the narrow inter-character gaps. Throws on any
    /// character outside the supported set.</summary>
    public static List<Element> Encode(string text)
    {
        var elements = new List<Element>();
        var full = "*" + text + "*";
        for (var c = 0; c < full.Length; c++)
        {
            if (c > 0) elements.Add(new Element(Bar: false, Wide: false));   // gap
            if (full[c] == '*' && c != 0 && c != full.Length - 1)
                throw new ArgumentException("'*' is reserved for start/stop.");
            if (!Patterns.TryGetValue(full[c], out var pattern))
                throw new ArgumentException($"Code 39 can't encode '{full[c]}' — use A-Z and 0-9.");
            for (var i = 0; i < 9; i++)
                elements.Add(new Element(Bar: i % 2 == 0, Wide: pattern[i] == '1'));
        }
        return elements;
    }
}

/// <summary>Box labels: ten 4"×2" labels per US-letter sheet, gutters between
/// every pair for hand-cutting, each carrying the created date, a large
/// client+number code, its Code 39 barcode, and the destruction date.</summary>
public static class BoxLabels
{
    static BoxLabels()
    {
        // the core PdfSharp build resolves NO fonts on its own — point it at
        // the Windows font folder once, before the first XFont is created
        if (PdfSharp.Fonts.GlobalFontSettings.FontResolver is null)
            PdfSharp.Fonts.GlobalFontSettings.FontResolver = new LabelFontResolver();
    }

    /// <summary>Serves Segoe UI and Consolas (regular/bold) from
    /// C:\Windows\Fonts — the only families the labels use. Consolas carries
    /// the code line: monospaced with a slashed zero, so 0/O and 1/I can't be
    /// confused when someone transcribes a label by hand.</summary>
    private sealed class LabelFontResolver : PdfSharp.Fonts.IFontResolver
    {
        public PdfSharp.Fonts.FontResolverInfo ResolveTypeface(
            string familyName, bool bold, bool italic) =>
            familyName.Equals("Consolas", StringComparison.OrdinalIgnoreCase)
                ? new(bold ? "consola#b" : "consola#r")
                : new(bold ? "segoe#b" : "segoe#r");

        public byte[] GetFont(string faceName) => File.ReadAllBytes(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            faceName switch
            {
                "consola#b" => "consolab.ttf",
                "consola#r" => "consola.ttf",
                "segoe#b" => "segoeuib.ttf",
                _ => "segoeui.ttf",
            }));
    }

    public sealed record Item(string Code, DateTime Created, DateTime Destroy);

    public const int PerSheet = 10;
    public const long MaxNumber = 99_999_999;   // the code carries 8 digits

    // date_style: "bars" (default) prints the created/destroy dates in white
    // text on a black bar (readable across a storage room); "plain" drops
    // both bars and prints the same dates in black, for printers/stock that
    // can't lay down solid black fields cleanly.
    public const string DateStyleBars = "bars";
    public const string DateStylePlain = "plain";

    /// <summary>Unknown or missing style → "bars" (today's only style, and
    /// the safe default if a config file predates this setting).</summary>
    public static string NormalizeDateStyle(string? style) =>
        style == DateStylePlain ? DateStylePlain : DateStyleBars;

    /// <summary>"ABCD" + 42 → "ABCD00000042".</summary>
    public static string Compose(string clientId, long number) =>
        clientId + number.ToString("D8");

    /// <summary>Display-only digit grouping for the printed code line:
    /// "ABCD00000042" → "ABCD 0000 0042". The barcode always carries the
    /// continuous code — this is purely for human reading/transcription.</summary>
    public static string DisplayCode(string code)
    {
        if (code.Length <= 8) return code;
        for (var i = code.Length - 8; i < code.Length; i++)
            if (code[i] is < '0' or > '9') return code;
        return $"{code[..^8]} {code[^8..^4]} {code[^4..]}";
    }

    /// <summary>Problem with a client id, or "" when it's usable (2-8
    /// characters, capital letters and digits only — Code 39's alphabet).</summary>
    public static string ValidateClientId(string id)
    {
        if (id.Length is < 2 or > 8)
            return "Client id needs 2 to 8 characters (like ABCD).";
        foreach (var c in id)
            if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
                return "Client id can only use capital letters and digits.";
        return "";
    }

    /// <summary>The consecutive labels for one print run.</summary>
    public static List<Item> Batch(string clientId, long start, int count,
        DateTime created, int destroyDays)
    {
        if (count < 1) throw new ArgumentException("Need at least one label.");
        if (start < 1 || start + count - 1 > MaxNumber)
            throw new ArgumentException($"Label numbers must stay within 1–{MaxNumber}.");
        var destroy = created.Date.AddDays(destroyDays);
        var items = new List<Item>(count);
        for (var i = 0; i < count; i++)
            items.Add(new Item(Compose(clientId, start + i), created.Date, destroy));
        return items;
    }

    // ------------------------------------------------- shared sheet layout
    // Sheet geometry: 2 columns × 5 rows of 4×2" labels with a cutting gutter
    // between every pair — these are cut by hand and slipped into box
    // pouches, so no two labels ever share an edge. All lengths in POINTS
    // (1/72"); every renderer (PDF export, in-app preview, in-app printing)
    // replays the same drawing, so what you see is exactly what prints.
    public const double PageWidthPt = 612, PageHeightPt = 792;      // US letter
    public const double LabelWidthPt = 288, LabelHeightPt = 144;    // 4 × 2 in
    private const double MarginLeft = 0.15625, MarginTop = 0.25;    // inches
    private const double PitchX = 4.1875, PitchY = 2.125;           // 0.1875 / 0.125 gutters
    private const double BarH = 22;   // both date bars, same height

    // The block is 10.5in tall (5 labels + 4 gutters) on an 11in page, so the
    // 0.5in of slack is split evenly: 0.25in top AND bottom. That symmetry is
    // load-bearing, not tidiness. A printer cannot reach the outermost
    // 0.16-0.25in of a sheet and the bottom is the worst edge, being where the
    // rollers release the trailing sheet. This layout once spent 0.4in at the
    // top and left 0.1in at the bottom, and every sheet came out with the last
    // row's DESTROY bar clipped. BoxLabelsTests guards both margins.

    /// <summary>The one instruction that prevents a whole misprinted sheet.
    /// Shown in the print-preview window, NOT drawn on the paper — its old
    /// home was the oversized top margin that was costing the bottom row its
    /// clearance. Nothing may be drawn above the first row.</summary>
    public const string SheetNote =
        "Print at 100% scale (no “fit to page”)  ·  10 labels, 4 × 2 in  ·  cut along the gray guides";

    /// <summary>A black filled rectangle (date bars and barcode bars).</summary>
    public sealed record BarRect(double X, double Y, double W, double H);

    /// <summary>Bold text centered in its box. Mono = Consolas (the code
    /// line); otherwise Segoe UI. White text sits on a black bar.</summary>
    public sealed record TextRun(string Text, double X, double Y, double W, double H,
        double Size, bool Mono, bool White);

    /// <summary>Everything one label draws, at origin (0,0), in points.</summary>
    public sealed record LabelDrawing(
        IReadOnlyList<BarRect> Bars, IReadOnlyList<TextRun> Texts, BarRect CutGuide);

    /// <summary>Top-left corner of a sheet slot (0-9), in points.</summary>
    public static (double X, double Y) SlotOrigin(int slot) =>
        (72 * (MarginLeft + slot % 2 * PitchX), 72 * (MarginTop + slot / 2 * PitchY));

    /// <summary>26pt when it fits, scaled down for long client ids so the
    /// code line never crowds the label edges. Pure math — Consolas is
    /// monospaced (advance ≈ 0.6 em), so every renderer agrees.</summary>
    public static double CodeFontSize(string display) =>
        Math.Min(26.0, (LabelWidthPt - 20) / (display.Length * 0.6));

    /// <summary>Lay out one label: matching black date bars top and bottom
    /// (readable across a storage room), the grouped code line, and the
    /// Code 39 barcode with clear air above it so an angled scan sweep can't
    /// catch the digit strokes. <paramref name="dateStyle"/> governs ONLY the
    /// two date bars/texts (<see cref="DateStyleBars"/>/<see cref="DateStylePlain"/>)
    /// — the barcode bars and code line are unaffected either way.</summary>
    public static LabelDrawing ComposeDrawing(Item item, string dateStyle = DateStyleBars)
    {
        const double w = LabelWidthPt, h = LabelHeightPt;
        var plainDates = NormalizeDateStyle(dateStyle) == DateStylePlain;
        var bars = new List<BarRect>();
        if (!plainDates)
        {
            bars.Add(new(0, 0, w, BarH));            // CREATED bar
            bars.Add(new(0, h - BarH, w, BarH));      // DESTROY bar
        }
        var display = DisplayCode(item.Code);
        var texts = new List<TextRun>
        {
            new($"CREATED {item.Created:yyyy-MM-dd}", 0, 0, w, BarH, 12,
                Mono: false, White: !plainDates),
            new(display, 0, BarH + 2, w, 34, CodeFontSize(display), Mono: true, White: false),
            new($"DESTROY AFTER {item.Destroy:yyyy-MM-dd}", 0, h - BarH, w, BarH, 12,
                Mono: false, White: !plainDates),
        };

        // 3:1 wide:narrow ratio; the bar field spans the label minus quiet
        // zones (≥10 narrow units each side keeps hand scanners happy). The
        // 0.18" side insets keep long client ids above ~12 mil bars.
        var elements = Code39.Encode(item.Code);
        var units = elements.Sum(e => e.Wide ? 3 : 1) + 20;
        var printable = 72 * (4.0 - 0.36);
        var narrow = printable / units;
        var cursor = (w - printable) / 2 + narrow * 10;
        foreach (var e in elements)
        {
            var barW = narrow * (e.Wide ? 3 : 1);
            if (e.Bar) bars.Add(new BarRect(cursor, 66, barW, 42));
            cursor += barW;
        }

        return new LabelDrawing(bars, texts, new BarRect(0, 0, w, h));
    }

    // ------------------------------------------------------------ PDF export
    /// <summary>Write the print-ready PDF (US letter, print at 100% scale).
    /// <paramref name="dateStyle"/> is forwarded to <see cref="ComposeDrawing"/>
    /// unchanged — same "bars"/"plain" choice as the preview and in-app
    /// print paths, so the PDF always matches what the window showed.</summary>
    public static void RenderPdf(string path, IReadOnlyList<Item> items,
        string dateStyle = DateStyleBars)
    {
        if (items.Count == 0) throw new ArgumentException("Nothing to print.");
        using var doc = new PdfDocument();
        var cutLine = new XPen(XColor.FromArgb(210, 210, 210), 0.4);

        for (var i = 0; i < items.Count; i++)
        {
            if (i % PerSheet == 0) AddPage(doc);
            var page = doc.Pages[doc.PageCount - 1];
            using var gfx = XGraphics.FromPdfPage(page);
            var (x, y) = SlotOrigin(i % PerSheet);
            var d = ComposeDrawing(items[i], dateStyle);
            foreach (var b in d.Bars)
                gfx.DrawRectangle(XBrushes.Black, x + b.X, y + b.Y, b.W, b.H);
            foreach (var t in d.Texts)
                gfx.DrawString(t.Text,
                    new XFont(t.Mono ? "Consolas" : "Segoe UI", t.Size, XFontStyleEx.Bold),
                    t.White ? XBrushes.White : XBrushes.Black,
                    new XRect(x + t.X, y + t.Y, t.W, t.H), XStringFormats.Center);
            gfx.DrawRectangle(cutLine, x + d.CutGuide.X, y + d.CutGuide.Y,
                d.CutGuide.W, d.CutGuide.H);
        }
        doc.Save(path);
    }

    private static void AddPage(PdfDocument doc)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(PageWidthPt);
        page.Height = XUnit.FromPoint(PageHeightPt);
        // nothing is drawn outside the labels: the margins are the bottom
        // row's clearance now, and SheetNote is shown in the preview window
    }
}
