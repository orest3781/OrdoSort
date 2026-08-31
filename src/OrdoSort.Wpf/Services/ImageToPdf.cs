using System.Windows.Media.Imaging;
using OrdoSort.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Services;

/// <summary>Scans and photos to PDF, one page per frame — the highest-value
/// converter in this feature: this app files scanned documents, and a
/// sheet-feed scanner or a phone camera is what actually lands in the inbox
/// day to day. No COM, no external process, no password path (an image is
/// never encrypted), and WPF ships every decoder this needs in-process, so
/// unlike the Office adapter this converter is ordinary and hermetic.
///
/// Lives in the Wpf layer rather than Core for exactly one reason:
/// <see cref="BitmapDecoder"/> is WPF's, and it is the only decoder available
/// here that exposes every FRAME of a multi-page TIFF — what a sheet-feed
/// scanner produces.
///
/// Called from the merge's background worker thread, not the UI thread.
/// <see cref="BitmapDecoder"/> needs no STA thread to decode — it is not
/// itself a UI element, only a WPF-namespaced codec — so that is safe; this
/// class must still never be handed a live UI object (a rendered
/// <c>Visual</c>, a screen-sourced bitmap) beyond the decoder/encoder
/// instances it creates within a single call. Neither <see cref="BitmapDecoder"/>
/// nor <see cref="PngBitmapEncoder"/> implements <see cref="IDisposable"/> —
/// there is nothing on either to dispose — so what actually gets cleaned up
/// at the end of that single call, via <c>using</c>, is the streams
/// (<see cref="MemoryStream"/>) and the PdfSharp objects
/// (<see cref="PdfDocument"/>) built around them.</summary>
public sealed class ImageToPdf : IDocumentConverter
{
    private const double MinTrustedInches = 1;
    private const double MaxTrustedInches = 30;
    private const double LetterShortPt = 612;
    private const double LetterLongPt = 792;
    private const double PointsPerInch = 72;

    // MergeTypes' own group, not a second hard-coded list — see TextToPdf
    // for the same reasoning: one source of truth for what "images" means.
    private static readonly IReadOnlyList<string> Extensions = MergeTypes.ExtensionsOf(MergeTypes.Images);

    public bool Handles(string extension) =>
        Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Never prompts (an image is never password-protected —
    /// <paramref name="candidates"/>/<paramref name="ask"/> exist only to
    /// satisfy <see cref="IDocumentConverter"/>) and never throws: every
    /// failure, including a corrupt or truncated image, comes back as
    /// <c>"error"</c> rather than an exception escaping this method.</summary>
    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        if (!Handles(extension))
            return new("unsupported", null, $"{displayName} isn't an image");

        try
        {
            // BitmapCacheOption.OnLoad reads every frame's pixel data up
            // front, so the source stream can be (and is, via `using`)
            // closed the moment this method returns, and no file handle or
            // deferred read ever outlives this call.
            using var stream = new MemoryStream(source);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return new("error", null, "there was nothing in it to merge", displayName);

            using var document = new PdfDocument();
            foreach (var frame in decoder.Frames)
            {
                var (widthPt, heightPt) = PageSizeFor(frame);
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(widthPt);
                page.Height = XUnit.FromPoint(heightPt);

                // Re-encoded to PNG in memory before it ever reaches
                // PdfSharp, so PdfSharp reads a format it certainly
                // understands regardless of the source codec (a CMYK JPEG,
                // an indexed GIF, a TIFF compression PdfSharp has no reader
                // for at all).
                using var gfx = XGraphics.FromPdfPage(page);
                var png = EncodeToPng(frame);
                // publiclyVisible: true -- XImage.FromStream calls
                // MemoryStream.GetBuffer() internally, which throws
                // "MemoryStream's internal buffer cannot be accessed" on a
                // stream built from the plain new MemoryStream(byte[])
                // constructor (proven by experiment: that is exactly the
                // exception this converter surfaced before this fix).
                using var pngStream = new MemoryStream(png, 0, png.Length, writable: false, publiclyVisible: true);
                using var image = XImage.FromStream(pngStream);
                gfx.DrawImage(image, 0, 0, widthPt, heightPt);
            }

            using var output = new MemoryStream();
            document.Save(output, closeStream: false);
            return new("ok", output.ToArray());
        }
        catch (Exception ex)
        {
            return new("error", null, $"couldn't read it: {ex.Message}", displayName);
        }
    }

    /// <summary>A frame's physical size is <c>PixelWidth / DpiX</c> by
    /// <c>PixelHeight / DpiY</c> inches. That is trusted as the page's true
    /// size only when EVERY side lands between 1 and 30 inches — exactly the
    /// scan case: a 1700x2200 frame at 200 DPI is a dead-on 8.5x11in Letter
    /// page (612x792pt). Outside that band the DPI tag is not just unusual,
    /// it is meaningless — a phone photo commonly reports 72 DPI, which for
    /// an ordinary 4000x3000 photo would claim a 55.6x41.7-inch page. Outside
    /// the trusted band the frame is instead fitted inside a single Letter
    /// envelope (612x792pt), preserving its aspect ratio and choosing a
    /// landscape envelope (792x612pt) when the frame is wider than it is
    /// tall — so a wide scan or photo still gets a page shaped like it,
    /// rather than being squeezed into a portrait sheet. Either way the
    /// returned size becomes the PDF page itself (see <see cref="ToPdf"/>'s
    /// <c>DrawImage</c> call), so the image always fills its page exactly:
    /// there is never letterboxing or a blank margin from this sizing
    /// decision.</summary>
    private static (double WidthPt, double HeightPt) PageSizeFor(BitmapFrame frame)
    {
        var widthInches = frame.PixelWidth / frame.DpiX;
        var heightInches = frame.PixelHeight / frame.DpiY;
        if (widthInches is >= MinTrustedInches and <= MaxTrustedInches &&
            heightInches is >= MinTrustedInches and <= MaxTrustedInches)
        {
            return (widthInches * PointsPerInch, heightInches * PointsPerInch);
        }

        var landscape = frame.PixelWidth > frame.PixelHeight;
        var envelopeWidth = landscape ? LetterLongPt : LetterShortPt;
        var envelopeHeight = landscape ? LetterShortPt : LetterLongPt;
        var scale = Math.Min(envelopeWidth / frame.PixelWidth, envelopeHeight / frame.PixelHeight);
        return (frame.PixelWidth * scale, frame.PixelHeight * scale);
    }

    private static byte[] EncodeToPng(BitmapFrame frame)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(frame);
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
