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
    /// <paramref name="maxWidth"/>, each one verified to fit rather than
    /// estimated. Splits on a character boundary, not a word boundary: a
    /// log line or a path can run for thousands of characters with no
    /// whitespace at all, so a word-wrapper would just hand back one giant
    /// "word" anyway. Always takes at least one character per chunk, so a
    /// single character wider than the page still makes progress instead of
    /// looping forever — the same must-make-progress guarantee
    /// TablePages.Paginate documents for its own row loop.
    ///
    /// Binary search over the break point (review Minor 3), not a linear
    /// grow-by-one-character scan: the old version re-measured the whole
    /// prefix on every single character, which is roughly one
    /// <c>MeasureString</c> call per CHARACTER of the line — harmless for an
    /// ordinary line, but ".json" and ".log" are default-on text types, and
    /// a minified JSON export is routinely one multi-megabyte line with no
    /// newlines at all, which turned into tens of seconds to minutes of an
    /// unresponsive-looking "Merging" that <c>Cancel()</c> cannot interrupt
    /// (cancellation is checked only BETWEEN units, and this all happens
    /// inside one).
    ///
    /// Doubling first, THEN a bounded binary search — not a single search
    /// straight over [1, remaining] — deliberately: a naive search's very
    /// first probe mid-point is roughly HALF THE ENTIRE REMAINING LINE, so
    /// for a multi-million-character line wrapping into ordinary
    /// ~80-character chunks, every single chunk's search would start by
    /// measuring a several-hundred-thousand-character substring before ever
    /// narrowing toward the real answer. Measured directly: PdfSharp's own
    /// text measurement does not merely slow down on input that long, it
    /// throws (a negative-width failure downstream in the PDF drawing
    /// code). Doubling from 1 finds a "fits"/"too wide" bracket within a
    /// factor of two of the REAL chunk length first, so every subsequent
    /// measure call — doubling or the binary search that follows it — stays
    /// close to that chunk's own size, however long the line is overall.
    /// No upfront `measure(line) &lt;= maxWidth` shortcut for the common
    /// short-line case either, deliberately: that was the ORIGINAL
    /// unbounded measure call this whole fix exists to avoid, and it is
    /// unnecessary — the loop below already reaches the identical one-chunk
    /// result on its own (doubling runs out of line before it ever fails to
    /// fit, `tooWide` stays -1, the whole remainder becomes the one chunk),
    /// in a handful of cheap, small-string measure calls even for an
    /// ordinary short line.</summary>
    private static List<string> WrapLine(string line, double maxWidth, Func<string, double> measure)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < line.Length)
        {
            var remaining = line.Length - start;

            // `fits` is the largest length confirmed (by measuring it) to
            // fit so far; `tooWide` the smallest confirmed not to, or -1
            // when doubling reached the end of the line without ever
            // failing to fit.
            var fits = 1;
            var tooWide = -1;
            while (fits < remaining)
            {
                var probe = Math.Min(fits * 2, remaining);
                if (measure(line.Substring(start, probe)) <= maxWidth) { fits = probe; continue; }
                tooWide = probe;
                break;
            }

            if (tooWide < 0)
            {
                chunks.Add(line.Substring(start, remaining));
                break;
            }

            // The exact break point, inside (fits, tooWide) — both ends
            // already measured above, so every probe here stays within a
            // factor of two of the real chunk length regardless of how
            // much of the line is still left after it. Biased toward the
            // upper half (`lo = mid` on a fit) so this converges on the
            // MAXIMUM fitting length rather than merely A fitting one.
            var lo = fits;
            var hi = tooWide - 1;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (measure(line.Substring(start, mid)) <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            chunks.Add(line.Substring(start, lo));
            start += lo;
        }
        return chunks;
    }
}
