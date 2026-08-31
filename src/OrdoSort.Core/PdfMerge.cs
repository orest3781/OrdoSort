using ICSharpCode.SharpZipLib.Zip;
using PdfSharp.Pdf;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace OrdoSort.Core;

/// <summary>
/// Merge PDFs into one document — plus, through an <see cref="IDocumentConverter"/>,
/// any other document type the caller supplies one for and the user has
/// switched on (see <see cref="MergeTypes"/>). Two shapes, one routine: every
/// mergeable entry inside a zip into "&lt;zipname&gt;.pdf" saved beside the
/// zip, or a handful of loose files into one file saved beside the first of
/// them. Never throws — the same discipline PageCounts.Count and
/// Unlock.UnlockPdf use for their own PdfSharp calls: every failure comes
/// back as a MergeResult, not an exception.
///
/// Converting: a source that is not already a PDF is handed to the converter
/// as bytes and comes back as bytes, never as a path — see the ZipSlip
/// paragraph below for why. A RECOGNIZED type the user has switched OFF
/// never reaches that exchange at all — filtered out of the unit before a
/// single byte of it is read, so it is not an error, just absent, in EITHER
/// shape. A type nothing recognizes at all (an .exe, an .mp4) is different
/// again: not "switched off", just unsupported, and unsupported still has
/// to reach this exchange and say so — conflating the two would drop a
/// chosen file for a reason that has nothing to do with the user's toggles.
/// A type nothing can CONVERT — recognized, switched on, but no converter
/// handles it — is where the two shapes part ways, and deliberately so: a
/// loose file was individually CHOSEN, so one nothing can convert is an
/// error naming the document, the same as a corrupt PDF would be — a merge
/// that silently dropped a chosen document is indistinguishable from a
/// complete one until someone notices. A zip's entries are found, not
/// chosen, so one of a recognized type nothing can convert is ordinary
/// clutter, counted in <see cref="MergeResult.SkippedEntries"/> exactly like
/// a zip entry that is not a mergeable type at all — the same treatment a
/// zip has always given a stray non-PDF file.
///
/// Notes: things worth saying out loud that are not failures — a zip's
/// recognized-but-nothing-converts-it entries (named, not just counted:
/// SkippedEntries alone cannot tell ordinary clutter apart from "the Word
/// documents you asked for, but nothing here can open"), and a conversion
/// that succeeded with something left behind (a workbook's later
/// worksheets, say) — travel in <see cref="MergeResult.Notes"/>.
/// <see cref="MergeResult.Message"/> keeps its one existing meaning, why the
/// unit failed, so these needed their own channel; without one they die
/// here, and no caller downstream can retrieve what this method already knew.
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
/// escape the zip's own folder. A converter keeps that rule rather than
/// bending it: <see cref="IDocumentConverter.ToPdf"/> takes the entry's
/// BYTES, never its name, so an implementation that needs a real file on
/// disk has to invent its own temp name. The only path this class ever
/// writes to is built from the ZIP FILE's own name (zipStem) plus ".pdf", or
/// from the first loose document's folder, run through
/// <see cref="Collision.FreeFile"/> — nothing an entry inside the zip
/// controls.
///
/// Fail-whole, not partial output: one bad document (skipped at the prompt,
/// corrupt, unconvertible, or anything AddPage chokes on) fails the WHOLE
/// unit — the zip, or the loose group — rather than silently omitting that
/// one document from the merge. A merged file that quietly dropped a page
/// range looks identical to a complete one until someone notices a document
/// is missing; a loud, whole-unit failure that names the offending item is
/// safer than a merge nobody can trust without re-checking page by page.
///
/// Memory: every source this class reads — a PDF as-is, or a document's
/// converted output — is buffered in memory (a zip entry's own stream is
/// forward-only, and PdfReader.Open needs random access), and the buffers
/// all stay alive until the merged document is saved — so peak memory is
/// roughly the SUM of every document's size in the unit, not just the
/// largest one. A converted document's bytes join that same buffered set
/// the moment ToPdf returns them; conversion changes nothing about this
/// class's own memory shape. Acceptable for v1, the same call Unlock.cs's
/// own doc comment makes for its buffered path;
/// <see cref="Unlock.LargeFileThresholdBytes"/> is the precedent this would
/// follow if a unit's documents ever turn out too large to buffer whole.
/// </summary>
public static class PdfMerge
{
    /// <summary><see cref="Source"/> is the zip, or the first loose PDF in
    /// merge order. <see cref="Item"/> is the file path (MergeFiles) or the
    /// entry name (MergeZip) that stopped a merge — what lets a caller mark
    /// the right row — and null on ok / no_pdfs. <see cref="PdfCount"/>
    /// counts every document in a successful merge, converted ones
    /// included, not just literal PDFs. <see cref="Notes"/> is things the
    /// caller should say out loud that are not failures: a document this PC
    /// could not convert although its type is switched on, or a conversion
    /// that succeeded with something left behind (a workbook's later
    /// worksheets). <see cref="Message"/> keeps its single meaning — why
    /// the unit failed — so these need their own channel; without one they
    /// die here, and no caller downstream can retrieve what this method
    /// already knew.</summary>
    public sealed record MergeResult(string Source, string Status, string? Output = null,
        int PdfCount = 0, int SkippedEntries = 0, string Message = "", string? Item = null,
        IReadOnlyList<string>? Notes = null);
    // Status: "ok" | "no_pdfs" | "needs_password" | "error" — never throws

    /// <summary>Merge every PDF inside <paramref name="zipPath"/> — plus,
    /// through <paramref name="converter"/>, any other type switched on in
    /// <paramref name="includeTypes"/> (null means every type is on) —
    /// natural-sorted by entry path, into "&lt;zipStem&gt;.pdf" saved beside
    /// the zip (collision-suffixed, never overwritten). Wrapped so nothing
    /// this method does — a missing/garbage zip file, an entry that fails to
    /// parse as a PDF, a save that fails partway — can ever throw out to
    /// the caller; every one of those becomes a readable MergeResult.</summary>
    public static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, IDocumentConverter? converter = null,
        ISet<string>? includeTypes = null) =>
        MergeZip(zipPath, candidates, ask, pickOutput: null, converter, includeTypes);

    /// <summary>Test seam for the save-failure cleanup gate (see
    /// MergeZipCore's own comment on <c>created</c>): <paramref name="pickOutput"/>
    /// defaults to <see cref="Collision.FreeFile"/> and stands in for it, so a
    /// test can make the "collision-free" name resolve to a path IT already
    /// controls — the deterministic equivalent of another station claiming
    /// that exact name in the gap between the real FreeFile probe and this
    /// call's own FileMode.CreateNew, without needing real thread timing to
    /// provoke it.</summary>
    internal static MergeResult MergeZip(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string>? pickOutput,
        IDocumentConverter? converter = null, ISet<string>? includeTypes = null)
    {
        try
        {
            return MergeZipCore(zipPath, candidates, ask, pickOutput ?? Collision.FreeFile, converter, includeTypes);
        }
        catch (Exception ex)
        {
            return new(zipPath, "error", Message: $"couldn't read the zip: {ex.Message}");
        }
    }

    private static MergeResult MergeZipCore(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string> pickOutput,
        IDocumentConverter? converter, ISet<string>? includeTypes)
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

            // Directory entries are skipped without counting. Everything else
            // that isn't mergeable — not a PDF, and either not a type the
            // converter offers or a type the user has switched off — counts
            // toward SkippedEntries so the caller can tell "an empty zip"
            // apart from "a zip full of things that don't take part". This is
            // the narrower IsMergeable, not IsSwitchedOff, deliberately: a
            // zip's entries are found, not individually chosen, so a
            // recognized-but-unconvertible one (nothing offers a converter
            // for it) is exactly the kind of incidental clutter
            // SkippedEntries exists to absorb — unlike MergeFilesCore, where
            // the same case is a chosen document and has to fail loudly. It
            // is still NAMED, though (in notConvertible, below): "recognized
            // but nothing here converts it" is worth saying even when it
            // costs the merge nothing.
            var mergeable = new List<ZipEntry>();
            var notConvertible = new List<string>();
            var skipped = 0;
            foreach (var entry in entries)
            {
                if (!entry.IsFile) continue;
                if (IsMergeable(entry.Name, converter, includeTypes))
                {
                    mergeable.Add(entry);
                    continue;
                }
                skipped++;
                // Named only when a converter EXISTS and specifically
                // doesn't handle this recognized, switched-on type — "we
                // tried, and this PC can't" (no Word installed, say). A null
                // converter means no conversion was ever attempted for
                // anything, which is the same quiet, pre-Task-4 shape a
                // stray non-PDF has always had (readme.txt, an unrecognized
                // extension, a switched-off type) — none of those get named.
                if (converter is not null && MergeTypes.GroupOf(ExtensionOf(entry.Name)) is not null
                    && !IsSwitchedOff(entry.Name, includeTypes))
                {
                    // Most of the time nothing here can say more than the
                    // bare name — no converter offers this type at all, or
                    // this PC lacks the app. ".ppt" is the one exception
                    // today: OfficeConverter deliberately refuses it (no
                    // safe password path exists — see its own Handles() doc
                    // comment) even when PowerPoint IS installed and the
                    // type IS switched on, which reads as a missing
                    // capability unless the reason is named alongside it.
                    var reason = SpecificRefusalReasonFor(ExtensionOf(entry.Name));
                    notConvertible.Add(reason is null ? entry.Name : $"{entry.Name}: {reason}");
                }
            }
            if (mergeable.Count == 0)
                return new(zipPath, "no_pdfs", SkippedEntries: skipped, Message: "nothing to merge inside",
                    Notes: notConvertible.Count > 0 ? notConvertible : null);

            // NaturalSort, not the zip's own entry order: "2.pdf" must merge
            // before "10.pdf" the same way this app lists any other batch of
            // files, and a zip's central directory carries no ordering
            // guarantee beyond "however the tool that built it happened to
            // write entries".
            mergeable.Sort((a, b) => NaturalSort.Instance.Compare(a.Name, b.Name));

            using var output = new PdfDocument();
            var openDocs = new List<IDisposable>();
            var notes = new List<string>(notConvertible);
            try
            {
                foreach (var entry in mergeable)
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
                    var unconverted = AsPdfBytes(ref bytes, entry.Name, entry.Name, converter, candidates, ask, out var note);
                    if (unconverted is not null) return unconverted with { Source = zipPath };
                    if (note is not null) notes.Add(note);
                    var stopped = AddPdf(bytes, entry.Name, zipName, entry.Name, candidates, ask, output, openDocs);
                    if (stopped is not null) return stopped with { Source = zipPath };
                }

                var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
                var zipStem = Path.GetFileNameWithoutExtension(zipPath);
                var target = pickOutput(Path.Combine(zipDir, zipStem + ".pdf"));
                return SaveNew(output, target, zipPath, mergeable.Count, skipped)
                    with { Notes = notes.Count > 0 ? notes : null };
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
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask,
        IDocumentConverter? converter = null, ISet<string>? includeTypes = null)
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
            return MergeFilesCore(ordered, outputPath, candidates, ask, converter, includeTypes);
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
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask,
        IDocumentConverter? converter, ISet<string>? includeTypes)
    {
        // ONLY an explicitly switched-off type is filtered out here, before
        // a single path is read — that alone is not an error, it simply
        // never enters the unit. IsSwitchedOff, not the narrower IsMergeable:
        // these paths were each individually CHOSEN by the caller, unlike a
        // zip's contents, which are found. A chosen document of an
        // unrecognized extension, or a recognized one nothing can convert,
        // still has to reach AsPdfBytes and fail loudly — pre-filtering
        // either of those here would be exactly the silent short merge this
        // class exists to refuse. Only once nothing is left at all does this
        // report the same "nothing to merge" MergeFiles itself reports for a
        // genuinely empty list.
        var mergeable = ordered.Where(p => !IsSwitchedOff(p, includeTypes)).ToList();
        if (mergeable.Count == 0) return new(ordered[0], "error", Message: "nothing to merge");

        var source = mergeable[0];
        using var output = new PdfDocument();
        var openDocs = new List<IDisposable>();
        var notes = new List<string>();
        try
        {
            foreach (var path in mergeable)
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
                var unconverted = AsPdfBytes(ref bytes, Path.GetFileName(path), path, converter, candidates, ask, out var note);
                if (unconverted is not null) return unconverted with { Source = source };
                if (note is not null) notes.Add(note);
                var stopped = AddPdf(bytes, Path.GetFileName(path), null, path, candidates, ask, output, openDocs);
                if (stopped is not null) return stopped with { Source = source };
            }

            if (outputPath is not null)
            {
                if (!AtomicPlace.TryReplace(outputPath, tmp => output.Save(tmp), out var placeError))
                    return new(source, "error", Message: $"couldn't save the merged PDF: {placeError}");
                return new(source, "ok", Output: outputPath, PdfCount: mergeable.Count,
                    Notes: notes.Count > 0 ? notes : null);
            }

            var target = Collision.FreeFile(
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, DefaultName(mergeable)));
            return SaveNew(output, target, source, mergeable.Count, 0)
                with { Notes = notes.Count > 0 ? notes : null };
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

    /// <summary>Dot-less, lowercase, and tolerant of a full path (not just a
    /// bare file name) — the one spelling every extension check in this
    /// class shares, so "what counts as a PDF" and "what group is this"
    /// can never quietly drift apart from each other.</summary>
    private static string ExtensionOf(string name) =>
        Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

    /// <summary>The one case today where a RECOGNIZED, switched-on type is
    /// refused for a specific, documented reason rather than "this PC lacks
    /// the app" or "no converter offers it" — null for every other
    /// extension. ".ppt" is OfficeConverter's own deliberate exception (see
    /// its Handles() doc comment): PowerPoint's Presentations.Open has no
    /// password parameter in any object-model generation, and legacy .ppt's
    /// OLE2 container gives OfficeConverter's protected-pptx byte check
    /// nothing to work with, so it is excluded even when PowerPoint is
    /// installed and the type is switched on. Naming the reason here,
    /// rather than falling into the generic "can't be converted" every
    /// other unhandled type gets, is what keeps that refusal from reading
    /// as a missing capability instead of a deliberate one.</summary>
    private static string? SpecificRefusalReasonFor(string extension) =>
        extension.Equals("ppt", StringComparison.OrdinalIgnoreCase)
            ? "PowerPoint 97-2003 can't be opened safely — save it as .pptx."
            : null;

    /// <summary>Whether the user has explicitly switched this file's TYPE
    /// off — true only for a type <see cref="MergeTypes"/> recognizes AND
    /// <paramref name="includeTypes"/> excludes. An extension nothing
    /// recognizes at all (an .exe, an .mp4) is NOT "switched off" — that
    /// would silently drop a chosen file this window was never going to
    /// convert either way, regardless of any toggle, which is exactly the
    /// defect this predicate exists to avoid. It is simply unsupported, and
    /// unsupported has to reach <see cref="AsPdfBytes"/> and say so rather
    /// than vanish here.</summary>
    private static bool IsSwitchedOff(string name, ISet<string>? includeTypes)
    {
        if (includeTypes is null) return false;
        var group = MergeTypes.GroupOf(ExtensionOf(name));
        return group is not null && !includeTypes.Contains(group);
    }

    /// <summary>A PDF, or something the converter offers to turn into one —
    /// and in both cases only when the user has that type switched on.
    /// <paramref name="includeTypes"/> null means every type is on. An
    /// extension <see cref="MergeTypes"/> does not recognize at all comes out
    /// false here too, the same as one nothing can convert — this predicate
    /// only ever asks "does a page come out of it", which is why
    /// <see cref="MergeFilesCore"/> uses the narrower
    /// <see cref="IsSwitchedOff"/> instead: a zip is allowed to silently
    /// absorb an unrecognized entry as clutter, but a chosen loose file is
    /// not.</summary>
    private static bool IsMergeable(string name, IDocumentConverter? converter, ISet<string>? includeTypes)
    {
        if (IsSwitchedOff(name, includeTypes)) return false;
        var extension = ExtensionOf(name);
        return extension == "pdf" || (converter is not null && converter.Handles(extension));
    }

    /// <summary>PDF bytes for a source that may not be a PDF: passed straight
    /// through when it already is one, otherwise handed to the converter with
    /// the caller's own passwords and prompt. Returns the failure to report,
    /// or null with <paramref name="bytes"/> replaced by the converted
    /// document and <paramref name="note"/> set to a non-empty advisory
    /// message the converter attached to an otherwise-successful conversion
    /// (a workbook's later worksheets, say) — null when there is nothing to
    /// say. A type nothing can convert is an ERROR, not a silent skip — a
    /// merge that quietly omitted a document looks identical to a complete
    /// one until somebody notices it is missing. (A type the user switched
    /// OFF never reaches here; it is filtered out of the unit instead. A
    /// type nothing RECOGNIZES does reach here, and gets the same honest
    /// "can't be converted" as one nothing can convert — from here on,
    /// unsupported and unconvertible are the same failure.)
    ///
    /// The converter call itself is guarded: <see cref="IDocumentConverter"/>
    /// promises never to throw, but the real implementation is Office
    /// interop over a temp file, the most throw-prone dependency in this
    /// feature, so this is defence in depth rather than distrust — the same
    /// standard this class already applies to <see cref="Zipper"/>. An
    /// unguarded throw here would unwind to the outer wrapper and blame the
    /// zip or the whole merge, with no <see cref="MergeResult.Item"/> to
    /// mark the actual culprit.</summary>
    private static MergeResult? AsPdfBytes(ref byte[] bytes, string displayName, string itemKey,
        IDocumentConverter? converter, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, out string? note)
    {
        note = null;
        var extension = ExtensionOf(displayName);
        if (extension == "pdf") return null;

        if (converter is null || !converter.Handles(extension))
            return new("", "error", Item: itemKey,
                Message: SpecificRefusalReasonFor(extension) ?? $"{displayName} can't be converted on this PC");

        ConversionResult converted;
        try
        {
            converted = converter.ToPdf(bytes, displayName, candidates, ask);
        }
        catch (Exception ex)
        {
            return new("", "error", Item: itemKey, Message: $"couldn't convert '{displayName}': {ex.Message}");
        }
        switch (converted.Status)
        {
            case "ok" when converted.Pdf is not null:
                bytes = converted.Pdf;
                if (converted.Message.Length > 0) note = converted.Message;
                return null;
            case "needs_password":
                return new("", "needs_password", Item: itemKey,
                    Message: converted.Message.Length > 0 ? converted.Message : "needs a password");
            default:
                return new("", "error", Item: itemKey,
                    Message: converted.Message.Length > 0 ? converted.Message : "couldn't convert it");
        }
    }

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
