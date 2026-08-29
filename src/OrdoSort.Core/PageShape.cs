using PdfSharp.Pdf.IO;

namespace OrdoSort.Core;

/// <summary>
/// The shape of a PDF's first page, as a width-to-height ratio. The window
/// uses it to size the viewer pane to the document rather than leaving the
/// user to drag the splitter for every batch of landscape scans.
///
/// Never throws, for the same reason <see cref="PageCounts"/> doesn't: this
/// runs on the inbox, whose files can be locked, encrypted, half-copied over
/// a share, or not PDFs at all, and none of those is worth an error dialog
/// when the only consequence is that a window keeps the size it already had.
/// Every failure is a null aspect, which callers read as "leave it alone".
/// </summary>
public static class PageShape
{
    /// <summary>Page 1's width divided by its height, or null when the file
    /// cannot be read. Only page 1: a document whose pages disagree has no
    /// single shape, and the pane is being sized for what is on screen when
    /// the session starts.</summary>
    public static double? AspectOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            // Import, not the [Obsolete] InformationOnly — Unlock.cs's doc
            // comment records why that is the right open mode for this
            // library, and PageCounts follows it for the same reason.
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            if (doc.PageCount == 0) return null;

            var page = doc.Pages[0];
            // No /Rotate arithmetic here on purpose. PdfSharp's Width and
            // Height are ALREADY the rotated, on-screen dimensions, which is
            // exactly what the pane has to match — measured 2026-08-27 on a
            // 612x792 MediaBox written with each /Rotate value and read back:
            //
            //   Rotate=0    W=612 H=792 Portrait     Rotate=180  W=612 H=792 Portrait
            //   Rotate=90   W=792 H=612 Landscape    Rotate=270  W=792 H=612 Landscape
            //   Rotate=-90  W=792 H=612 Landscape    Rotate=450  W=792 H=612 Landscape
            //
            // (the MediaBox stays [0 0 612 792] throughout, and the
            // uncanonical -90 and 450 are handled by the library, not by us).
            // Turning the page a second time here reported a quarter-turned
            // portrait as portrait — the bug this comment exists to prevent
            // someone re-introducing. AQuarterTurnedPortraitPageReadsAsLandscape
            // is the test that caught it.
            var width = page.Width.Point;
            var height = page.Height.Point;
            if (width <= 0 || height <= 0) return null;
            return width / height;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
