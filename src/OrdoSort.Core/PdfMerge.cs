using ICSharpCode.SharpZipLib.Zip;
using PdfSharp.Pdf;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace OrdoSort.Core;

/// <summary>
/// Merge PDFs into one document. Two shapes, one routine: every PDF inside
/// a zip into "&lt;zipname&gt;.pdf" saved beside the zip, or a handful of
/// loose PDFs into one file saved beside the first of them. Never throws —
/// the same discipline PageCounts.Count and Unlock.UnlockPdf use for their
/// own PdfSharp calls: every failure comes back as a MergeResult, not an
/// exception.
///
/// Passwords (2026-08-28): a locked archive, a locked loose PDF and a locked
/// PDF inside an archive all take the caller's candidate list and its
/// prompt through the same contract (<see cref="Passwords.Resolve"/>), and
/// report "needs_password" — naming the item in <see cref="MergeResult.Item"/>,
/// nothing written — when the prompt is skipped. The output is always a
/// plain, unencrypted document: Import mode copies pages into a fresh one,
/// exactly as Unlock does.
///
/// ZipSlip immunity: entry names never touch the filesystem here. A zip entry
/// with a crafted name like "../../evil.pdf" is only ever used as a content
/// SOURCE (read through <see cref="Zipper.ReadEntry"/> straight into memory)
/// and, separately, as TEXT in a message — never as a filesystem path passed
/// to File/Directory/Path APIs, which is what a ZipSlip exploit needs to
/// escape the zip's own folder. The only path this class ever writes to is
/// built from the ZIP FILE's own name (zipStem) plus ".pdf", or from the
/// first loose PDF's folder, run through <see cref="Collision.FreeFile"/> —
/// nothing an entry inside the zip controls.
///
/// Fail-whole, not partial output: one bad document (skipped at the prompt,
/// corrupt, or anything AddPage chokes on) fails the WHOLE unit — the zip,
/// or the loose group — rather than silently omitting that one PDF from the
/// merge. A merged file that quietly dropped a page range looks identical
/// to a complete one until someone notices a document is missing; a loud,
/// whole-unit failure that names the offending item is safer than a merge
/// nobody can trust without re-checking page by page.
///
/// Memory: every source PDF this class reads is buffered in memory (a zip
/// entry's own stream is forward-only, and PdfReader.Open needs random
/// access), and the buffers all stay alive until the merged document is
/// saved — so peak memory is roughly the SUM of every PDF's size in the
/// unit, not just the largest one. Acceptable for v1, the same call
/// Unlock.cs's own doc comment makes for its buffered path;
/// <see cref="Unlock.LargeFileThresholdBytes"/> is the precedent this would
/// follow if a unit's PDFs ever turn out too large to buffer whole.
/// </summary>
public static class PdfMerge
{
    /// <summary><see cref="Source"/> is the zip, or the first loose PDF in
    /// merge order. <see cref="Item"/> is the file path (MergeFiles) or the
    /// entry name (MergeZip) that stopped a merge — what lets a caller mark
    /// the right row — and null on ok / no_pdfs.</summary>
    public sealed record MergeResult(string Source, string Status, string? Output = null,
        int PdfCount = 0, int SkippedEntries = 0, string Message = "", string? Item = null);
    // Status: "ok" | "no_pdfs" | "needs_password" | "error" — never throws

    /// <summary>Merge every PDF inside <paramref name="zipPath"/>, natural-
    /// sorted by entry path, into "&lt;zipStem&gt;.pdf" saved beside the zip
    /// (collision-suffixed, never overwritten). Wrapped so nothing this
    /// method does — a missing/garbage zip file, an entry that fails to
    /// parse as a PDF, a save that fails partway — can ever throw out to
    /// the caller; every one of those becomes a readable MergeResult.</summary>
    public static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask) =>
        MergeZip(zipPath, candidates, ask, pickOutput: null);

    /// <summary>Test seam for the save-failure cleanup gate (see
    /// MergeZipCore's own comment on <c>created</c>): <paramref name="pickOutput"/>
    /// defaults to <see cref="Collision.FreeFile"/> and stands in for it, so a
    /// test can make the "collision-free" name resolve to a path IT already
    /// controls — the deterministic equivalent of another station claiming
    /// that exact name in the gap between the real FreeFile probe and this
    /// call's own FileMode.CreateNew, without needing real thread timing to
    /// provoke it.</summary>
    internal static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string>? pickOutput)
    {
        try
        {
            return MergeZipCore(zipPath, candidates, ask, pickOutput ?? Collision.FreeFile);
        }
        catch (Exception ex)
        {
            return new(zipPath, "error", Message: $"couldn't read the zip: {ex.Message}");
        }
    }

    private static MergeResult MergeZipCore(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string> pickOutput)
    {
        var zipName = Path.GetFileName(zipPath);
        SzlZipFile zip;
        try
        {
            zip = new SzlZipFile(zipPath);
        }
        catch (ZipException ex)
        {
            return new(zipPath, "error", Message: $"couldn't read the zip: {ex.Message}");
        }

        using (zip)
        {
            var entries = zip.Cast<ZipEntry>().ToList();

            // The archive's own password first, exactly as Zipper.Extract
            // settles it — before anything is read, so a skipped prompt costs
            // nothing and writes nothing.
            var archive = Zipper.UnlockArchive(zip, entries, candidates, ask, zipName);
            if (archive.Status == "needs_password")
                return new(zipPath, "needs_password", Message: "needs a password", Item: zipName);
            if (archive.Status == "unreadable")
                return new(zipPath, "error", Message: "couldn't read the zip: an encrypted entry couldn't be read");

            // Directory entries are skipped without counting. Everything
            // else that isn't a .pdf counts toward SkippedEntries so the
            // caller can tell "an empty zip" apart from "a zip full of things
            // that aren't PDFs".
            var pdfEntries = new List<ZipEntry>();
            var skipped = 0;
            foreach (var entry in entries)
            {
                if (!entry.IsFile) continue;
                if (entry.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) pdfEntries.Add(entry);
                else skipped++;
            }
            if (pdfEntries.Count == 0)
                return new(zipPath, "no_pdfs", SkippedEntries: skipped, Message: "no PDFs inside");

            // NaturalSort, not the zip's own entry order: "2.pdf" must merge
            // before "10.pdf" the same way this app lists any other batch of
            // files, and a zip's central directory carries no ordering
            // guarantee beyond "however the tool that built it happened to
            // write entries".
            pdfEntries.Sort((a, b) => NaturalSort.Instance.Compare(a.Name, b.Name));

            using var output = new PdfDocument();
            var openDocs = new List<IDisposable>();
            try
            {
                foreach (var entry in pdfEntries)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = Zipper.ReadEntry(zip, entry);
                    }
                    catch (Exception ex)
                    {
                        return new(zipPath, "error", Message: $"couldn't read '{entry.Name}': {ex.Message}", Item: entry.Name);
                    }
                    var stopped = AddPdf(bytes, entry.Name, zipName, entry.Name, candidates, ask, output, openDocs);
                    if (stopped is not null) return stopped with { Source = zipPath };
                }

                var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
                var zipStem = Path.GetFileNameWithoutExtension(zipPath);
                var target = pickOutput(Path.Combine(zipDir, zipStem + ".pdf"));
                return SaveNew(output, target, zipPath, pdfEntries.Count, skipped);
            }
            finally
            {
                foreach (var d in openDocs) d.Dispose();
            }
        }
    }

    /// <summary>Merge <paramref name="pdfPaths"/> — natural-sorted by file
    /// name, ties by full path — into one document. With
    /// <paramref name="outputPath"/> null the result is named by
    /// <see cref="DefaultName"/> and placed beside the first document in that
    /// order, collision-suffixed; a non-null path is a Save-As answer and is
    /// replaced through <see cref="AtomicPlace.TryReplace"/>, the way
    /// Zipper.CreateZip places a Save-As archive — built to a GUID-named temp
    /// sibling, moved into place only once complete, so a merge that fails
    /// part-way leaves whatever was at that name untouched.</summary>
    public static MergeResult MergeFiles(IReadOnlyList<string> pdfPaths, string? outputPath,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        // Ordering the list is INSIDE the try, not before it: it is the
        // caller's list and it touches every element (Path.GetFileName, the
        // comparer), so a null or otherwise unusable entry has to come back
        // as an error result like every other failure here — the class
        // promises "never throws", and a statement outside the try is a hole
        // in that promise.
        List<string>? ordered = null;
        try
        {
            ordered = InMergeOrder(pdfPaths);
            if (ordered.Count == 0) return new("", "error", Message: "nothing to merge");
            return MergeFilesCore(ordered, outputPath, candidates, ask);
        }
        catch (Exception ex)
        {
            // Source names the first document in merge order when there IS
            // one; ordering itself is what failed otherwise, so there is no
            // first document to name.
            return new(ordered is { Count: > 0 } ? ordered[0] : "", "error",
                Message: $"couldn't merge: {ex.Message}");
        }
    }

    private static MergeResult MergeFilesCore(IReadOnlyList<string> ordered, string? outputPath,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var source = ordered[0];
        using var output = new PdfDocument();
        var openDocs = new List<IDisposable>();
        try
        {
            foreach (var path in ordered)
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (IOException ex) when (Unlock.IsInUse(ex))
                {
                    return new(source, "error", Item: path,
                        Message: "It's open in another program — close it there and merge again.");
                }
                catch (Exception ex)
                {
                    return new(source, "error", Item: path, Message: $"couldn't read it: {ex.Message}");
                }
                var stopped = AddPdf(bytes, Path.GetFileName(path), null, path, candidates, ask, output, openDocs);
                if (stopped is not null) return stopped with { Source = source };
            }

            if (outputPath is not null)
            {
                if (!AtomicPlace.TryReplace(outputPath, tmp => output.Save(tmp), out var placeError))
                    return new(source, "error", Message: $"couldn't save the merged PDF: {placeError}");
                return new(source, "ok", Output: outputPath, PdfCount: ordered.Count);
            }

            var target = Collision.FreeFile(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, DefaultName(ordered)));
            return SaveNew(output, target, source, ordered.Count, 0);
        }
        finally
        {
            foreach (var d in openDocs) d.Dispose();
        }
    }

    /// <summary>The default name for a loose merge — just the file name, so
    /// it doubles as the Save-As dialog's suggested name: the folder
    /// CONTAINING the first document in merge order ("C:\Jobs\Job 4471\cover.pdf"
    /// → "Job 4471.pdf"), the same rule <see cref="Zipper.DefaultName"/>
    /// applies to a zip so the two windows guess alike. "Merged.pdf" when
    /// that folder has no name (a drive root) or there is nothing to merge.
    /// Wrapped for the same reason <see cref="MergeFiles"/> is: this runs
    /// BEFORE any merge — it is what fills in the Save-As dialog's suggested
    /// name — so a list it cannot read has to fall back to the default name
    /// rather than take the dialog down with it.</summary>
    public static string DefaultName(IReadOnlyList<string> pdfPaths)
    {
        try
        {
            var ordered = InMergeOrder(pdfPaths);
            if (ordered.Count == 0) return "Merged.pdf";
            var parentName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(ordered[0])) ?? "");
            return parentName.Length == 0 ? "Merged.pdf" : parentName + ".pdf";
        }
        catch
        {
            return "Merged.pdf";
        }
    }

    /// <summary>Natural sort by file name — "2.pdf" before "10.pdf", the way
    /// every list in this app sorts — with two same-named files in different
    /// folders falling back to full-path order so the result is deterministic.</summary>
    private static List<string> InMergeOrder(IReadOnlyList<string> pdfPaths) =>
        pdfPaths
            .OrderBy(p => Path.GetFileName(p), NaturalSort.Instance)
            .ThenBy(p => p, NaturalSort.Instance)
            .ToList();

    /// <summary>The one routine both merges share: open <paramref name="bytes"/>
    /// with the passwords the caller knows (and the prompt, if it comes to
    /// that), then add every page to <paramref name="output"/>. Returns null
    /// when the pages went in; otherwise the failure to report, with
    /// <see cref="MergeResult.Source"/> left blank for the caller to fill and
    /// <see cref="MergeResult.Item"/> set to <paramref name="itemKey"/> — the
    /// full path of a loose file, the entry name inside a zip. Every source
    /// document opened here — and the MemoryStream backing it — has to stay
    /// alive until output.Save() runs, not just through its own AddPage
    /// loop: PdfSharp's Import-mode AddPage does not fully materialise a
    /// page's content at call time, it keeps resolving objects from the
    /// SOURCE document lazily, up to Save. That is why both go into
    /// <paramref name="openDocs"/> and are disposed together at the end.</summary>
    private static MergeResult? AddPdf(byte[] bytes, string displayName, string? inside, string itemKey,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask,
        PdfDocument output, List<IDisposable> openDocs)
    {
        var opened = PdfPasswords.Open(bytes, candidates, ask, displayName, inside);
        switch (opened.Status)
        {
            case "needs_password":
                return new("", "needs_password", Item: itemKey,
                    Message: inside is null ? "needs a password" : $"'{displayName}' inside needs a password");
            case "unreadable":
                return new("", "error", Item: itemKey,
                    Message: inside is null
                        ? $"couldn't read it: {opened.Message}"
                        : $"couldn't read '{displayName}': {opened.Message}");
        }

        openDocs.Add(opened.Document!);
        openDocs.Add(opened.Stream!);
        try
        {
            foreach (var page in opened.Document!.Pages) output.AddPage(page);
        }
        catch (Exception ex)
        {
            return new("", "error", Item: itemKey,
                Message: inside is null
                    ? $"couldn't read it: {ex.Message}"
                    : $"couldn't read '{displayName}': {ex.Message}");
        }
        return null;
    }

    /// <summary>Exclusive-create save behind the created-by-me gate.
    /// <c>created</c> is set ONLY once FileMode.CreateNew has actually
    /// succeeded — mirroring Unlock.PlaceAndSwap's own markCreated gate
    /// (2026-08 audit finding 1.2). Collision.FreeFile only proves the name
    /// was free AT CHECK TIME: another process can create that exact file in
    /// the gap before this line runs, in which case the FileStream ctor
    /// itself throws and `created` is never set — so the catch below must
    /// NOT call RemoveQuietly in that case, or it deletes a file this call
    /// never wrote a single byte of. RemoveQuietly only ever runs against a
    /// target THIS call is certain it created.</summary>
    private static MergeResult SaveNew(PdfDocument output, string target, string source, int pdfCount, int skipped)
    {
        var created = false;
        try
        {
            using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            created = true;
            output.Save(fs, closeStream: false);
        }
        catch (Exception ex)
        {
            if (created) RemoveQuietly(target);
            return new(source, "error", Message: $"couldn't save the merged PDF: {ex.Message}");
        }
        return new(source, "ok", Output: target, PdfCount: pdfCount, SkippedEntries: skipped);
    }

    private static void RemoveQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
