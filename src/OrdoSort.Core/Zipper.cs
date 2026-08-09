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
/// folder in <see cref="Extract(string)"/> — runs ONLY when that flag is set. This is
/// the exact discipline ZipMerge.MergeZipCore's own `created` gate documents
/// (2026-08 audit finding 1.2, Unlock.PlaceAndSwap's markCreated): cleanup
/// must never touch something this call did not itself bring into existence.
///
/// The two directions get there differently, though, and it matters: for
/// CreateZip's file target, ZipFile.Open's own FileMode.CreateNew is
/// ATOMIC — it throws immediately if the name is taken, so `created` can be
/// set right after that call succeeds with no gap at all. Directory.CreateDirectory
/// has no such primitive — it is idempotent and does NOT throw just because
/// the target already exists (empty or not), so Extract's own `created` flag
/// is set from an explicit Directory.Exists check taken immediately before
/// the create call. That closes the race window down to those two lines
/// instead of leaving it open for the whole extraction, but — unlike the
/// file case — it is a narrowed window, not a hard guarantee (2026-08 review
/// finding: an earlier version of this method set `created = true`
/// unconditionally after CreateDirectory returned, which meant a directory
/// that already existed — created by another process, or a user in Explorer —
/// could get deleted, contents and all, the moment extraction into it failed).
///
/// ZipSlip: <see cref="Extract(string)"/> hands the whole zip to
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
                // In-archive dedupe for anything that becomes a ROOT-LEVEL
                // name in the archive — a loose file's own entry name, OR a
                // folder's own name (the prefix every file under it
                // inherits). A tiny local counter, not Collision (which only
                // ever probes the real filesystem): two files both named
                // "a.txt" dropped from different folders become "a.txt" and
                // "a (2).txt"; two DIFFERENT top-level folders both named
                // "docs" become "docs/..." and "docs (2)/...".
                //
                // The folder half of this isn't just cosmetic (2026-08
                // review finding): ZipArchive.CreateEntry happily writes two
                // entries sharing the exact same FullName, but
                // ZipFile.ExtractToDirectory throws IOException the second
                // time it tries to write that same relative path — so
                // without this, zipping two same-named folders with
                // overlapping contents (e.g. "ProjectA\docs" and
                // "ProjectB\docs", both containing "readme.txt") would
                // return Status "ok" here for an archive this app's own
                // Extract could never fully unpack. One shared set covering
                // both kinds means a root name is claimed the instant either
                // a loose file or a folder uses it, regardless of order.
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
                        var folderName = UniqueRootName(usedRootNames, new DirectoryInfo(p).Name);
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
    public static UnzipResult Extract(string zipPath) => Extract(zipPath, pickOutputDir: null);

    /// <summary>Test seam for the created-gate cleanup (see ExtractCore's own
    /// comment on `created`): <paramref name="pickOutputDir"/> defaults to
    /// <see cref="Collision.FreeDirectory"/> and stands in for it, so a test
    /// can make the "collision-free" name resolve to a path IT already
    /// controls — the deterministic equivalent of another process (or a user
    /// in Explorer) claiming that exact folder in the gap between the real
    /// FreeDirectory probe and this call's own Directory.Exists check,
    /// without needing real thread timing to provoke it. Same shape as
    /// ZipMerge.MergeZip's internal pickOutput seam.</summary>
    internal static UnzipResult Extract(string zipPath, Func<string, string>? pickOutputDir)
    {
        try
        {
            return ExtractCore(zipPath, pickOutputDir ?? Collision.FreeDirectory);
        }
        catch (Exception ex)
        {
            return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
        }
    }

    private static UnzipResult ExtractCore(string zipPath, Func<string, string> pickOutputDir)
    {
        var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        var zipStem = Path.GetFileNameWithoutExtension(zipPath);
        var dir = pickOutputDir(Path.Combine(zipDir, zipStem));

        // Directory.CreateDirectory is idempotent — unlike ZipFile.Open's
        // FileMode.CreateNew for the CreateZip file path, it does NOT throw
        // just because `dir` already exists (empty or not), so "did THIS
        // call create it" can't be inferred from CreateDirectory succeeding
        // the way `created = true` right after ZipFile.Open works above.
        // Instead `created` is decided by an explicit existence check taken
        // immediately before the create call — Collision.FreeDirectory (or
        // whatever pickOutputDir returns) only proves the name was free AT
        // CHECK TIME, and another process/user can still claim it in the gap
        // before this line runs. See the class doc comment for why this is a
        // narrowed race window, not the atomic guarantee the file path gets.
        var created = false;
        try
        {
            created = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
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
