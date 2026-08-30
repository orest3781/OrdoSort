using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Core;

/// <summary>Plain text (txt, log, md, json) to PDF with nothing installed.
/// Every line becomes one row of a single-column "table" so it can share
/// <see cref="TableToPdf.Render"/> — the same paginate-then-draw path a
/// spreadsheet takes, with two differences a text file needs:
///
/// <list type="bullet">
/// <item>No header row: <see cref="TablePages.Paginate"/> is called with
/// <c>repeatHeader: false</c>, so the first line of the file is ordinary
/// content rather than a heading repeated on every page.</item>
/// <item>Long lines are hard-wrapped to the page width BEFORE pagination,
/// using the same text metrics <see cref="TableToPdf.Render"/> measures
/// columns with — <c>TablePages</c> sizes a "column" to its widest cell, and
/// without this a single 5000-character line would become one 5000-point-
/// wide column instead of several lines.</item>
/// </list>
///
/// Portrait Letter, unlike TableToPdf's landscape: prose reads top to
/// bottom, where a page of table columns reads left to right. It never
/// prompts — there is nothing here that could be password-protected.</summary>
public sealed class TextToPdf : IDocumentConverter
{
    internal const double PageWidthPt = 612;    // Letter portrait: text reads
    internal const double PageHeightPt = 792;   // top-to-bottom, not sideways
    internal const double MarginPt = 36;
    internal const double FontSizePt = 10;
    internal const double RowHeightPt = 14;

    // MergeTypes' own group, not a second hard-coded list: unlike
    // TableToPdf's Excel group (which includes xls/xlsm/ods that converter
    // genuinely cannot read), every extension MergeTypes files under "text"
    // — txt, log, md, json — really is just readable text here.
    private static readonly IReadOnlyList<string> Extensions = MergeTypes.ExtensionsOf(MergeTypes.Text);

    public bool Handles(string extension) =>
        Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (!Handles(extension))
            return new("unsupported", null, $"{displayName} isn't a text file");

        if (source.Length == 0)
            return new("error", null, "there was nothing in it to merge", displayName);

        try
        {
            TableToPdf.EnsureFontResolver();
            var lines = Csv.ReadText(source).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            using var scratch = new PdfDocument();
            using var scratchGfx = XGraphics.FromPdfPage(scratch.AddPage());
            var font = new XFont("Segoe UI", FontSizePt);
            // Mirrors TableToPdf.Render's own column-measuring formula
            // exactly (raw width + its CellPaddingPt): a wrapped line must
            // still fit once Render re-measures it for column width, or the
            // hard-wrap's budget and the eventual column width disagree.
            double Measure(string text) =>
                scratchGfx.MeasureString(text ?? "", font).Width + TableToPdf.CellPaddingPt;

            var usableWidth = PageWidthPt - 2 * MarginPt;
            List<List<string>> table = lines
                .SelectMany(line => WrapLine(line, usableWidth, Measure))
                .Select(line => new List<string> { line })
                .ToList();

            return new("ok", TableToPdf.Render(table, PageWidthPt, PageHeightPt, MarginPt,
                RowHeightPt, FontSizePt, repeatHeader: false));
        }
        catch (Exception ex)
        {
            return new("error", null, $"couldn't lay it out: {ex.Message}", displayName);
        }
    }

    /// <summary>Break one line into chunks that each measure within
    /// <paramref name="maxWidth"/>, growing a chunk one character at a time
    /// so every returned piece is verified to fit rather than estimated.
    /// Splits on a character boundary, not a word boundary: a log line or a
    /// path can run for thousands of characters with no whitespace at all,
    /// so a word-wrapper would just hand back one giant "word" anyway.
    /// Always takes at least one character per chunk, so a single character
    /// wider than the page still makes progress instead of looping forever —
    /// the same must-make-progress guarantee TablePages.Paginate documents
    /// for its own row loop.</summary>
    private static List<string> WrapLine(string line, double maxWidth, Func<string, double> measure)
    {
        if (measure(line) <= maxWidth) return [line];

        var chunks = new List<string>();
        var start = 0;
        while (start < line.Length)
        {
            var count = 1;
            while (start + count < line.Length &&
                   measure(line.Substring(start, count + 1)) <= maxWidth)
                count++;
            chunks.Add(line.Substring(start, count));
            start += count;
        }
        return chunks;
    }
}
