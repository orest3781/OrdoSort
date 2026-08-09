using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core;

/// <summary>
/// Merge every PDF inside a zip into one PDF, saved next to the zip. Never
/// throws — the same discipline PageCounts.Count and Unlock.UnlockPdf use for
/// their own PdfSharp calls: every failure comes back as a MergeResult, not
/// an exception.
///
/// ZipSlip immunity: entry names never touch the filesystem here. A zip entry
/// with a crafted name like "../../evil.pdf" is only ever used as a Content
/// SOURCE (read through <see cref="ZipArchiveEntry.Open"/> straight into
/// memory) and, separately, as TEXT in an error message — never as a
/// filesystem path passed to File/Directory/Path APIs, which is what a
/// ZipSlip exploit needs to escape the zip's own folder. The only path this
/// method ever writes to is built from the ZIP FILE's own name (zipStem)
/// plus ".pdf", run through <see cref="Collision.FreeFile"/> — nothing an
/// entry inside the zip controls. That makes path traversal structurally
/// impossible in this tool, not just guarded against.
///
/// Fail-whole-zip, not partial output: one bad entry (encrypted, corrupt, or
/// anything AddPage chokes on) fails the WHOLE zip rather than silently
/// omitting that one PDF from the merge. A merged file that quietly dropped
/// a page range looks identical to a complete one until someone notices a
/// document is missing — a loud, whole-zip failure that names the offending
/// entry is safer than a merge nobody can trust without re-checking page by
/// page.
///
/// Memory: every source PDF this method reads is buffered in memory (a zip
/// entry's own stream is forward-only, and PdfReader.Open needs random
/// access — see the per-entry comment in <c>MergeZipCore</c> below), and the
/// buffers all stay alive until the merged document is saved — so peak
/// memory is roughly the SUM of every PDF's size inside the zip, not just the
/// largest one. Acceptable for v1, the same call Unlock.cs's own doc comment
/// makes for its buffered path: <see cref="Unlock.LargeFileThresholdBytes"/>
/// is the precedent this tool would follow (stream a document straight from
/// its zip entry to a local temp file instead of a MemoryStream) if a zip's
/// PDFs ever turn out too large to buffer whole.
/// </summary>
public static class ZipMerge
{
    public sealed record MergeResult(string Zip, string Status, string? Output = null,
        int PdfCount = 0, int SkippedEntries = 0, string Message = "");
    // Status: "ok" | "no_pdfs" | "error" — never throws

    /// <summary>Merge every PDF inside <paramref name="zipPath"/>, natural-
    /// sorted by entry path, into "&lt;zipStem&gt;.pdf" saved beside the zip
    /// (collision-suffixed, never overwritten). Wrapped so nothing this
    /// method does — a missing/garbage zip file, a corrupt zip's own
    /// InvalidDataException, an entry that fails to parse as a PDF, a save
    /// that fails partway — can ever throw out to the caller; every one of
    /// those becomes a readable MergeResult instead.</summary>
    public static MergeResult MergeZip(string zipPath)
    {
        try
        {
            return MergeZipCore(zipPath);
        }
        catch (Exception ex)
        {
            return new(zipPath, "error", Message: $"couldn't read the zip: {ex.Message}");
        }
    }

    private static MergeResult MergeZipCore(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // entry.Name is empty for a directory entry (its FullName ends in
        // "/"); those are skipped without counting. Everything else that
        // isn't a .pdf counts toward SkippedEntries so the caller can tell
        // "an empty zip" apart from "a zip full of things that aren't PDFs".
        var candidates = new List<ZipArchiveEntry>();
        var skipped = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            if (entry.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                candidates.Add(entry);
            else
                skipped++;
        }

        if (candidates.Count == 0)
            return new(zipPath, "no_pdfs", SkippedEntries: skipped, Message: "no PDFs inside");

        // NaturalSort, not the zip's own entry order: "2.pdf" must merge
        // before "10.pdf" the same way this app lists any other batch of
        // files, and a zip's central directory carries no ordering guarantee
        // beyond "however the tool that built it happened to write entries".
        candidates.Sort((a, b) => NaturalSort.Instance.Compare(a.FullName, b.FullName));

        using var output = new PdfDocument();

        // Every source document opened below — and the MemoryStream backing
        // it — has to stay alive until output.Save() runs, not just through
        // its own AddPage loop: PdfSharp's Import-mode AddPage does not fully
        // materialise a page's content at call time, it keeps resolving
        // objects from the SOURCE document lazily, up to Save. That is
        // exactly why Unlock.cs's own buffered idiom keeps its "input"
        // document alive across the whole AddPage loop through to Save,
        // never disposing it first — the one difference here is that a zip
        // merge has MANY source documents in play at once (one per PDF
        // entry), so all of them are tracked and disposed together at the
        // very end instead of the usual single "using var input = ...".
        var openDocs = new List<IDisposable>();
        try
        {
            var mergedCount = 0;
            foreach (var entry in candidates)
            {
                // A zip entry's stream is forward-only (non-seekable), but
                // PdfReader.Open needs random access while it parses the
                // xref table — so each entry is buffered into a MemoryStream
                // first, the same "read once, work from memory" shape
                // Unlock.UnlockBuffered uses for its own source file.
                //
                // The whole per-entry attempt — buffering, PdfReader.Open,
                // and the AddPage loop — is wrapped in ONE try so any
                // failure at any of those three steps (a corrupt entry that
                // won't even buffer, an encrypted or malformed PDF that
                // PdfReader.Open rejects, or a page AddPage itself chokes on)
                // is reported against the SAME entry name, not just "the
                // merge failed somewhere".
                try
                {
                    var ms = new MemoryStream();
                    using (var es = entry.Open()) es.CopyTo(ms);
                    ms.Position = 0;
                    var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
                    openDocs.Add(doc);
                    openDocs.Add(ms);
                    foreach (var page in doc.Pages) output.AddPage(page);
                }
                catch (Exception ex)
                {
                    return new(zipPath, "error",
                        Message: $"couldn't read '{entry.FullName}': {ex.Message}");
                }
                mergedCount++;
            }

            var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
            var zipStem = Path.GetFileNameWithoutExtension(zipPath);
            var target = Collision.FreeFile(Path.Combine(zipDir, zipStem + ".pdf"));

            try
            {
                // Exclusive create, same as Unlock.PlaceAndSwap's own write:
                // fails atomically if the name is taken rather than
                // truncating whatever another station just wrote there.
                using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                output.Save(fs, closeStream: false);
            }
            catch (Exception ex)
            {
                RemoveQuietly(target);
                return new(zipPath, "error", Message: $"couldn't save the merged PDF: {ex.Message}");
            }

            return new(zipPath, "ok", Output: target, PdfCount: mergedCount, SkippedEntries: skipped);
        }
        finally
        {
            foreach (var d in openDocs) d.Dispose();
        }
    }

    private static void RemoveQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
