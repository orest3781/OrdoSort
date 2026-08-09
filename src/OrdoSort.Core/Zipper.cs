using System.IO.Compression;

namespace OrdoSort.Core;

/// <summary>
/// Build one zip from a mix of files and folders, or extract one zip back
/// out to a sibling folder. Never throws — same discipline as ZipMerge and
/// every other batch tool: every failure comes back as a result record, not
/// an exception.
///
/// Created-gate discipline (both directions): a name being free at CHECK time
/// (Collision.FreeFile/FreeDirectory, or an explicit Save-As path) does not
/// guarantee this call is the one that actually creates it — another process
/// could win the race, or a later step in THIS call could fail after the
/// file/folder was created but before the work finished. So a `created` flag
/// is set ONLY once the filesystem object this method is responsible for has
/// actually been created by THIS call, and cleanup on failure — deleting the
/// partial zip in <see cref="CreateZip"/>, deleting the partial output
/// folder in <see cref="Extract"/> — runs ONLY when that flag is set. This is
/// the exact discipline ZipMerge.MergeZipCore's own `created` gate documents
/// (2026-08 audit finding 1.2, Unlock.PlaceAndSwap's markCreated): cleanup
/// must never touch something this call did not itself bring into existence.
///
/// ZipSlip: <see cref="Extract"/> hands the whole zip to
/// <see cref="ZipFile.ExtractToDirectory(string, string)"/>, which since
/// .NET's original 4.5/Core ZipFile implementation has always refused to
/// extract an entry whose resolved path would land outside the destination
/// directory — a crafted entry name like "..\evil.txt" throws an IOException
/// rather than writing outside the folder this method just created. That
/// runtime guarantee is LOAD-BEARING here: this class does no path-safety
/// checking of its own on the way in, and depends entirely on the framework
/// call refusing the traversal before a single byte lands outside `dir`. See
/// ZipperTests' ZipSlip pin test, which exists specifically to catch a
/// regression (or a .NET version) where that guarantee stops holding.
/// </summary>
public static class Zipper
{
    public sealed record ZipResult(string Status, string? Output, string Message = "");   // "ok" | "error"
    public sealed record UnzipResult(string Zip, string Status, string? OutputFolder, string Message = "");  // "ok" | "error"

    /// <summary>Zip every file/folder in <paramref name="paths"/> into one
    /// archive. Entries that no longer exist by the time this runs are
    /// silently skipped (a stale row in the caller's list, not this call's
    /// problem) — unless NOTHING in the list exists, which is reported as
    /// "nothing to zip" rather than silently writing an empty archive nobody
    /// asked for.
    ///
    /// <paramref name="outputPath"/> null picks a default name beside the
    /// first item (see <see cref="DefaultName"/>), collision-suffixed via
    /// <see cref="Collision.FreeFile"/> so an existing file is never
    /// clobbered. A non-null <paramref name="outputPath"/> is a Save-As
    /// path: the save dialog that produced it already asked the user to
    /// confirm overwriting, so it is used verbatim — but this method still
    /// creates the file EXCLUSIVELY (delete whatever's there first, then
    /// FileMode.CreateNew via ZipArchiveMode.Create) rather than letting
    /// ZipArchive open in a mode that could append to or otherwise reuse an
    /// existing file's bytes.</summary>
    public static ZipResult CreateZip(IReadOnlyList<string> paths, string? outputPath = null)
    {
        try
        {
            return CreateZipCore(paths, outputPath);
        }
        catch (Exception ex)
        {
            return new ZipResult("error", null, $"couldn't create the zip: {ex.Message}");
        }
    }

    private static ZipResult CreateZipCore(IReadOnlyList<string> paths, string? outputPath)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
            return new ZipResult("error", null, "nothing to zip");

        string target;
        if (outputPath is not null)
        {
            // Save-As semantics: the dialog already confirmed overwrite
            // intent, so a pre-existing file at this exact path is expected
            // and fine to remove — the exclusive create right after is what
            // actually protects the created-gate below, not this delete.
            try { if (File.Exists(outputPath)) File.Delete(outputPath); }
            catch { /* let ZipFile.Open's own exception surface the real error */ }
            target = outputPath;
        }
        else
        {
            var besideDir = BesideDirectory(existing[0]);
            target = Collision.FreeFile(Path.Combine(besideDir, DefaultName(existing)));
        }

        // See class doc comment for the created-gate discipline this
        // implements: `created` flips to true only once ZipFile.Open has
        // actually made the file (FileMode.CreateNew inside it — throws
        // immediately if the name is taken, in which case `created` is
        // never set and the catch below must not delete anything).
        var created = false;
        try
        {
            using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                created = true;
                // In-archive dedupe for loose files sharing a root entry
                // name — a tiny local counter, not Collision (which only
                // ever probes the real filesystem): two files both named
                // "a.txt" dropped from different folders become "a.txt" and
                // "a (2).txt" INSIDE the archive. Folder entries carry their
                // own folder-name prefix and can't collide with a root entry
                // the same way, so they're outside this set.
                var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in existing)
                {
                    if (File.Exists(p))
                    {
                        var name = UniqueRootName(usedRootNames, Path.GetFileName(p));
                        archive.CreateEntryFromFile(p, name, CompressionLevel.Optimal);
                    }
                    else if (Directory.Exists(p))
                    {
                        // every file under the folder becomes
                        // "<folderName>/<relative path>" — forward slashes
                        // always, regardless of the OS separator, because a
                        // zip's own entry-name convention is '/' even on
                        // Windows (Path.GetRelativePath gives back whatever
                        // this OS uses, hence the explicit replace).
                        var folderName = new DirectoryInfo(p).Name;
                        foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        {
                            var rel = Path.GetRelativePath(p, file).Replace('\\', '/');
                            archive.CreateEntryFromFile(file, $"{folderName}/{rel}", CompressionLevel.Optimal);
                        }
                    }
                }
            }
            return new ZipResult("ok", target);
        }
        catch (Exception ex)
        {
            if (created) RemoveFileQuietly(target);
            return new ZipResult("error", null, $"couldn't create the zip: {ex.Message}");
        }
    }

    /// <summary>The default archive name for <paramref name="paths"/> — just
    /// the file name, not a directory (so it doubles as a Save-As dialog's
    /// suggested name). A single folder gets its own name ("photos" →
    /// "photos.zip"); anything else (one loose file, or a mix) gets the name
    /// of the folder CONTAINING the first item ("C:\Job\a.txt" →
    /// "Job.zip") — the same directory <see cref="CreateZip"/> itself
    /// places the archive beside, so the two always agree. Falls back to
    /// "Archive.zip" when that name would be empty (the first item sits at
    /// a drive root, e.g. "C:\a.txt", whose containing folder has no name of
    /// its own) or when nothing in <paramref name="paths"/> exists.</summary>
    public static string DefaultName(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) return "Archive.zip";

        if (existing.Count == 1 && Directory.Exists(existing[0]))
        {
            var folderName = new DirectoryInfo(existing[0]).Name;
            return folderName.Length == 0 ? "Archive.zip" : folderName + ".zip";
        }

        var parentName = Path.GetFileName(BesideDirectory(existing[0]));
        return parentName.Length == 0 ? "Archive.zip" : parentName + ".zip";
    }

    /// <summary>The directory <paramref name="path"/> itself sits in — for a
    /// file that's simply its parent folder; for a folder it's THAT folder's
    /// parent, i.e. "beside" the folder rather than inside it. Trailing
    /// separators are stripped first so a folder path like "C:\docs\" still
    /// resolves to "C:\", not itself.</summary>
    private static string BesideDirectory(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(Path.GetFullPath(trimmed)) ?? "";
    }

    private static string UniqueRootName(HashSet<string> used, string name)
    {
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Extract every entry in <paramref name="zipPath"/> into a
    /// fresh sibling folder named after the zip (collision-suffixed via
    /// <see cref="Collision.FreeDirectory"/>, so re-extracting the same zip
    /// never overwrites a previous run's output). See the class doc comment
    /// for why <see cref="ZipFile.ExtractToDirectory(string, string)"/>'s own
    /// traversal protection is load-bearing for this method's path safety,
    /// and for the created-gate discipline the cleanup below follows.</summary>
    public static UnzipResult Extract(string zipPath)
    {
        try
        {
            return ExtractCore(zipPath);
        }
        catch (Exception ex)
        {
            return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
        }
    }

    private static UnzipResult ExtractCore(string zipPath)
    {
        var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        var zipStem = Path.GetFileNameWithoutExtension(zipPath);
        var dir = Collision.FreeDirectory(Path.Combine(zipDir, zipStem));

        // created flips true only once Directory.CreateDirectory below has
        // actually succeeded — the same gate CreateZipCore uses, so a
        // failure that happens before the folder exists (or a FreeDirectory
        // name lost to a race, in which case CreateDirectory itself either
        // succeeds harmlessly into the existing empty folder or throws)
        // never deletes something this call didn't create.
        var created = false;
        try
        {
            Directory.CreateDirectory(dir);
            created = true;
            ZipFile.ExtractToDirectory(zipPath, dir);
            return new UnzipResult(zipPath, "ok", dir);
        }
        catch (InvalidDataException)
        {
            // ZipArchive's own "this isn't a zip" exception — readable voice
            // for what is, from the user's side, just a bad file.
            if (created) RemoveDirectoryQuietly(dir);
            return new UnzipResult(zipPath, "error", null, "not a valid zip");
        }
        catch (Exception ex)
        {
            // Covers everything else, including the IOException
            // ZipFile.ExtractToDirectory throws when an entry's resolved
            // path would land outside `dir` (the ZipSlip guard the class
            // doc comment describes) and ordinary IO failures (locked file,
            // gone share, out of disk space mid-extract).
            if (created) RemoveDirectoryQuietly(dir);
            return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
        }
    }

    private static void RemoveFileQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void RemoveDirectoryQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
