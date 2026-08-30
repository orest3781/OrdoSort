using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Core;

/// <summary>CSV, TSV and XLSX to PDF with nothing installed — the fallback
/// for a PC without Office. It reads with the same readers the roster loader
/// uses and draws a plain table: accurate values, not the spreadsheet's own
/// look. A workbook's LATER SHEETS ARE NOT INCLUDED — XlsxTable returns the
/// first worksheet only. When a workbook actually has more than one, the
/// returned <see cref="ConversionResult"/> stays "ok" but carries a
/// <c>Message</c> saying so, rather than letting the rest disappear
/// silently — precisely what the merge's fail-whole design otherwise exists
/// to prevent. A single-sheet workbook gets no message: there is nothing to
/// warn about, and attaching one anyway would just be noise on the common
/// case. CSV and TSV never carry a message; only xlsx can lose a sheet.
///
/// It never prompts. There is no decryptor here, so a password could not be
/// used even if one were typed; a protected file reports the reason instead
/// of raising a prompt that cannot help.</summary>
public sealed class TableToPdf : IDocumentConverter
{
    internal const double PageWidthPt = 792;    // Letter landscape: a table is
    internal const double PageHeightPt = 612;   // wider than it is tall
    internal const double MarginPt = 36;
    internal const double FontSizePt = 9;
    internal const double RowHeightPt = 14;

    // internal, not private: TextToPdf's hard-wrap has to budget the SAME
    // padding this class's own column measurement adds (see Render's local
    // Measure), or a line could measure as fitting at wrap time and then
    // overflow once column width is computed from the wrapped pieces.
    internal const double CellPaddingPt = 6;

    public bool Handles(string extension) =>
        extension.ToLowerInvariant() is "csv" or "tsv" or "xlsx";

    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (!Handles(extension))
            return new("unsupported", null, $"{displayName} isn't a spreadsheet or CSV");

        List<List<string>> table;
        try
        {
            table = extension == "xlsx"
                ? XlsxTable.Read(new MemoryStream(source))
                : Csv.Parse(Csv.ReadText(source));
        }
        catch (Exception ex)
        {
            // An encrypted xlsx is an OLE compound file rather than a zip, so
            // it lands here too — and no password would help, since nothing
            // in this class can decrypt one.
            return new("error", null,
                $"couldn't read it without Excel installed: {ex.Message}", displayName);
        }

        if (table.Count == 0 || table.All(r => r.Count == 0))
            return new("error", null, "there was nothing in it to merge", displayName);

        // Only xlsx can have hidden sheets to lose; CSV/TSV are inherently
        // single-table. A fresh MemoryStream, not the one Read already
        // consumed — its position is at the end and re-wrapping the same
        // bytes is cheap, so there is no reason to rely on rewinding it.
        var note = "";
        if (extension == "xlsx")
        {
            var sheets = XlsxTable.CountSheets(new MemoryStream(source));
            if (sheets > 1)
                note = $"only the first of {sheets} worksheets — install Excel to include them all";
        }

        try
        {
            return new("ok", Render(table, PageWidthPt, PageHeightPt, MarginPt, RowHeightPt, FontSizePt), note);
        }
        catch (Exception ex)
        {
            return new("error", null, $"couldn't lay it out: {ex.Message}", displayName);
        }
    }

    /// <summary>The core (non-Windows-specific) PdfSharp build resolves NO
    /// fonts on its own — same requirement <see cref="BoxLabels"/> documents
    /// and guards against independently. Called explicitly by both this
    /// class and <see cref="TextToPdf"/> — rather than left to a static
    /// constructor — so each is guaranteed a working resolver before its OWN
    /// first <see cref="XFont"/>: TextToPdf measures for its hard-wrap before
    /// it ever calls into <see cref="Render"/>, so waiting for Render's own
    /// static constructor to run would already be too late. Idempotent and
    /// safe to call from either class in either order.</summary>
    internal static void EnsureFontResolver()
    {
        if (PdfSharp.Fonts.GlobalFontSettings.FontResolver is null)
            PdfSharp.Fonts.GlobalFontSettings.FontResolver = new SystemFontResolver();
    }

    /// <summary>Serves Segoe UI and Consolas (regular/bold) from
    /// C:\Windows\Fonts. Functionally identical to BoxLabels' own resolver —
    /// duplicated rather than shared so this file has no dependency on the
    /// label-printing feature. It resolves Consolas too, even though neither
    /// converter in this file draws with it: <see cref="EnsureFontResolver"/>
    /// only installs whichever resolver gets there first, so if THIS one
    /// wins the race, BoxLabels' Consolas barcode line must still render
    /// correctly rather than silently falling back to Segoe UI.</summary>
    private sealed class SystemFontResolver : PdfSharp.Fonts.IFontResolver
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

    /// <summary>Shared with <see cref="TextToPdf"/>: paginate with real text
    /// metrics, then draw. Internal so both converters lay out identically
    /// rather than drifting apart. The five geometry parameters are what let
    /// each converter keep its OWN page size and still share this one
    /// drawing path — this class passes its landscape constants, TextToPdf
    /// its portrait ones; a hard-coded constant here would have silently
    /// forced one converter into the other's orientation.
    /// <paramref name="repeatHeader"/> is the one behavioural difference
    /// TextToPdf needs: a text file has no heading row to repeat on every
    /// page (see <see cref="TablePages.Paginate"/>).</summary>
    internal static byte[] Render(IReadOnlyList<IReadOnlyList<string>> table,
        double pageWidth, double pageHeight, double marginPt, double rowHeightPt,
        double fontSizePt, bool repeatHeader = true)
    {
        EnsureFontResolver();
        var font = new XFont("Segoe UI", fontSizePt);
        var headerFont = new XFont("Segoe UI", fontSizePt, XFontStyleEx.Bold);

        using var scratch = new PdfDocument();
        using var scratchGfx = XGraphics.FromPdfPage(scratch.AddPage());
        double Measure(string text) => scratchGfx.MeasureString(text ?? "", font).Width + CellPaddingPt;

        var pages = TablePages.Paginate(table, pageWidth - 2 * marginPt,
            pageHeight - 2 * marginPt, rowHeightPt, Measure, repeatHeader);

        using var document = new PdfDocument();
        foreach (var layout in pages)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            var y = marginPt;
            if (repeatHeader)
            {
                DrawRow(gfx, layout, table[layout.HeaderRow], headerFont, y, marginPt, rowHeightPt);
                y += rowHeightPt;
            }
            foreach (var rowIndex in layout.Rows)
            {
                DrawRow(gfx, layout, table[rowIndex], font, y, marginPt, rowHeightPt);
                y += rowHeightPt;
            }
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static void DrawRow(XGraphics gfx, TablePage layout,
        IReadOnlyList<string> row, XFont font, double y, double marginPt, double rowHeightPt)
    {
        var x = marginPt;
        for (var i = 0; i < layout.Columns.Count; i++)
        {
            var column = layout.Columns[i];
            // Ragged rows are ordinary in a CSV: a row with fewer fields than
            // the header draws blanks, it does not fail the merge.
            var text = column < row.Count ? row[column] ?? "" : "";
            gfx.DrawString(text, font, XBrushes.Black,
                new XRect(x, y, layout.Widths[i], rowHeightPt), XStringFormats.CenterLeft);
            x += layout.Widths[i];
        }
    }
}
