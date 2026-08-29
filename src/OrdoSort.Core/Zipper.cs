using System.IO.Compression;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SzlZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using ZipFile = System.IO.Compression.ZipFile;

namespace OrdoSort.Core;

/// <summary>
/// Build one zip from a mix of files and folders, or extract one zip back
/// out to a sibling folder. Never throws — same discipline as PdfMerge and
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
/// folder in
/// <see cref="Extract(string, IReadOnlyList{string}, Func{PasswordRequest, string})"/>
/// — runs ONLY when that flag is set. This is
/// the exact discipline PdfMerge.MergeZipCore's own `created` gate documents
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
/// Reading goes through SharpZipLib (2026-08-28): it decrypts ZipCrypto and
/// WinZip-AES archives, which System.IO.Compression cannot, so Extract and
/// Probe take the candidate passwords the caller knows and an `ask`
/// callback for the ones it doesn't (see <see cref="Passwords.Resolve"/>).
/// Creation stays on System.IO.Compression: output is never encrypted, and
/// ZipFile.Open's atomic CreateNew above is proven.
///
/// Two things the old reader did for free are this class's own now, and
/// both are correctness rules rather than niceties:
///
/// ZipSlip: ZipFile.ExtractToDirectory refused any entry resolving outside
/// the destination. SharpZipLib hands entry names back exactly as the
/// archive stored them — measured: "..\evil.txt", "/rooted.txt" and
/// "C:\drive.txt" all arrive verbatim from an archive another tool wrote —
/// so <see cref="GuardedTarget"/> resolves every entry's full path itself
/// and refuses one that does not sit under the output folder before a byte
/// is written. ZipperTests' ZipSlip theory pins all four forms.
///
/// Verification: ZipCrypto's header check is one byte, so 1 wrong password
/// in 256 passes it and yields garbage — silently, on a stored entry
/// (measured 2026-08-28: "wrong147" read 39 bytes with the CRC wrong). So a
/// password counts only if the entry decrypts AND its CRC matches; AES
/// entries store no CRC (AE-2 writes zero) and are authenticated by
/// SharpZipLib itself at end of stream instead, which is why every read
/// runs to the END of the entry. The probe verifies against the smallest
/// NON-EMPTY encrypted entry (see <see cref="SmallestEncryptedEntry"/> — a
/// 0-byte entry's CRC trivially matches under any password, which defeats
/// the check above just as completely as the 1-byte header collision does),
/// which bounds its cost; Extract verifies every entry it writes. One
/// password per archive: the one that opens the smallest non-empty
/// encrypted entry is set for all of them, and an entry that rejects it
/// fails the zip naming that entry.
/// </summary>
public static class Zipper
{
    public sealed record ZipResult(string Status, string? Output, string Message = "");   // "ok" | "error"
    public sealed record UnzipResult(string Zip, string Status, string? OutputFolder, string Message = "");  // "ok" | "needs_password" | "error"

    /// <summary>The read-only readiness verdict for one archive — the zip
    /// side of Unlock.ProbeReadiness. not_encrypted | ready (with the index
    /// into the candidates that opened it) | needs_password | unreadable.</summary>
    public sealed record ZipProbeResult(string Zip, string Status, int? MatchedIndex = null, string Message = "");

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
    /// confirm overwriting, so a pre-existing file there is replaced — but
    /// NEVER by deleting it up front. The archive is built to a GUID-named
    /// temp sibling first (FileMode.CreateNew via ZipArchiveMode.Create, so
    /// the created-gate below still gets an unambiguous signal) and only
    /// moved onto <paramref name="outputPath"/> — via <see cref="File.Replace"/>
    /// when something is there, <see cref="File.Move"/> when nothing is —
    /// once the zip is fully and successfully written. A delete-then-create
    /// leaves a window, on the SMB shares this app targets, where two
    /// coworkers who both Zip -> Save-As to the same filename at nearly the
    /// same instant can have the second one delete the first one's
    /// just-written archive with nothing yet in its place to recover from
    /// (2026-08 audit finding); temp-then-replace closes that window and
    /// also means a zip build that fails after this call already created
    /// the temp file leaves whatever was previously at
    /// <paramref name="outputPath"/> untouched, not deleted. Same shape as
    /// <see cref="Config.WriteAtomic"/>.</summary>
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

        // The two branches below look similar and are governed by DIFFERENT
        // rules — see CONTEXT.md's "atomic placement" for why they must not
        // be merged.
        //
        // Save-As writes to a temp file whose name carries a GUID, so no peer
        // can own it: AtomicPlace picks it, hands it to the build, moves it
        // onto the real path, and deletes it if anything fails. Nothing here
        // needs a created-gate, because there is no contested name to be
        // careful about.
        //
        // The default branch writes DIRECTLY to a collision-freed name, which
        // a peer legitimately can own — Collision.FreeFile only proves the
        // name was free at check time. That branch keeps its created-gate.
        if (outputPath is not null)
        {
            // finalPath is where the bytes land once the build succeeds. See
            // the class doc comment on CreateZip for why Save-As no longer
            // deletes the pre-existing file up front: if the build or the
            // placement fails, outputPath is left exactly as the user last
            // saw it, because nothing above ever touched it.
            if (!AtomicPlace.TryReplace(outputPath, tmp => BuildArchive(tmp, existing), out var placeError))
                return new ZipResult("error", null, $"couldn't create the zip: {placeError}");
            return new ZipResult("ok", outputPath);
        }

        var besideDir = BesideDirectory(existing[0]);
        var target = Collision.FreeFile(Path.Combine(besideDir, DefaultName(existing)));

        // See class doc comment for the created-gate discipline this
        // implements: `created` flips to true only once ZipFile.Open has
        // actually made the file (FileMode.CreateNew inside it — throws
        // immediately if the name is taken, in which case `created` is
        // never set and the catch below must not delete anything).
        var created = false;
        try
        {
            BuildArchive(target, existing, onCreated: () => created = true);
            return new ZipResult("ok", target);
        }
        catch (Exception ex)
        {
            if (created) RemoveFileQuietly(target);
            return new ZipResult("error", null, $"couldn't create the zip: {ex.Message}");
        }
    }

    /// <summary>Writes the archive for <paramref name="existing"/> at
    /// <paramref name="path"/>. <paramref name="onCreated"/> fires the instant
    /// ZipFile.Open has actually made the file — only the collision-freed
    /// branch needs it, to gate its own cleanup; the Save-As branch's temp is
    /// GUID-private and AtomicPlace owns its lifetime.</summary>
    private static void BuildArchive(
        string path, IReadOnlyList<string> existing, Action? onCreated = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        onCreated?.Invoke();

        // In-archive dedupe for anything that becomes a ROOT-LEVEL name in
        // the archive — a loose file's own entry name, OR a folder's own name
        // (the prefix every file under it inherits). A tiny local counter, not
        // Collision (which only ever probes the real filesystem): two files
        // both named "a.txt" dropped from different folders become "a.txt" and
        // "a (2).txt"; two DIFFERENT top-level folders both named "docs"
        // become "docs/..." and "docs (2)/...".
        //
        // The folder half of this isn't just cosmetic (2026-08 review
        // finding): ZipArchive.CreateEntry happily writes two entries sharing
        // the exact same FullName, but ZipFile.ExtractToDirectory throws
        // IOException the second time it tries to write that same relative
        // path — so without this, zipping two same-named folders with
        // overlapping contents (e.g. "ProjectA\docs" and "ProjectB\docs", both
        // containing "readme.txt") would return Status "ok" here for an
        // archive this app's own Extract could never fully unpack. One shared
        // set covering both kinds means a root name is claimed the instant
        // either a loose file or a folder uses it, regardless of order.
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
                // "<folderName>/<relative path>" — forward slashes always,
                // regardless of the OS separator, because a zip's own
                // entry-name convention is '/' even on Windows
                // (Path.GetRelativePath gives back whatever this OS uses,
                // hence the explicit replace).
                var folderName = UniqueRootName(usedRootNames, new DirectoryInfo(p).Name);
                foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(p, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, $"{folderName}/{rel}", CompressionLevel.Optimal);
                }
            }
        }
    }

    // PlaceAtomically lived here — a byte-for-byte copy of
    // Config.WriteAtomic's retry loop, as its own doc comment admitted
    // ("same shape, same reasoning"). Both are now AtomicPlace.TryReplace,
    // which also picks the temp name the Save-As branch used to build by
    // hand, with the same GUID reasoning restated in a third place.

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
    /// never overwrites a previous run's output). A locked archive is opened
    /// with the first of <paramref name="candidates"/> that verifies, else
    /// with what <paramref name="ask"/> supplies; a skipped prompt (or no
    /// prompt at all) is "needs_password" and nothing is written. See the
    /// class doc comment for the path guard, the verification rule, and the
    /// created-gate discipline the cleanup below follows.</summary>
    public static UnzipResult Extract(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask) =>
        Extract(zipPath, candidates, ask, pickOutputDir: null);

    /// <summary>Test seam for the created-gate cleanup (see ExtractCore's own
    /// comment on `created`): <paramref name="pickOutputDir"/> defaults to
    /// <see cref="Collision.FreeDirectory"/> and stands in for it, so a test
    /// can make the "collision-free" name resolve to a path IT already
    /// controls — the deterministic equivalent of another process (or a user
    /// in Explorer) claiming that exact folder in the gap between the real
    /// FreeDirectory probe and this call's own Directory.Exists check,
    /// without needing real thread timing to provoke it. Same shape as
    /// PdfMerge.MergeZip's internal pickOutput seam.</summary>
    internal static UnzipResult Extract(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string>? pickOutputDir)
    {
        try
        {
            return ExtractCore(zipPath, candidates, ask, pickOutputDir ?? Collision.FreeDirectory);
        }
        catch (Exception ex)
        {
            return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
        }
    }

    private static UnzipResult ExtractCore(string zipPath, IReadOnlyList<string> candidates,
        Func<PasswordRequest, string?>? ask, Func<string, string> pickOutputDir)
    {
        SzlZipFile zip;
        try
        {
            zip = new SzlZipFile(zipPath);
        }
        catch (ZipException)
        {
            // "Cannot find central directory" — readable voice for what is,
            // from the user's side, just a bad file.
            return new UnzipResult(zipPath, "error", null, "not a valid zip");
        }

        using (zip)
        {
            var entries = zip.Cast<ZipEntry>().ToList();

            // Passwords are settled BEFORE the output folder exists: a skipped
            // prompt must leave nothing behind, and there is nothing to clean up
            // if nothing was created.
            var archive = UnlockArchive(zip, entries, candidates, ask, Path.GetFileName(zipPath));
            if (archive.Status == "needs_password")
                return new UnzipResult(zipPath, "needs_password", null, "needs a password");
            if (archive.Status == "unreadable")
                return new UnzipResult(zipPath, "error", null, "couldn't extract: an encrypted entry couldn't be read");

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
                foreach (var entry in entries) WriteEntry(zip, entry, dir);
                return new UnzipResult(zipPath, "ok", dir);
            }
            catch (Exception ex)
            {
                // Covers the path guard's refusal, a wrong-password entry
                // failing verification, and ordinary IO failures (locked file,
                // gone share, out of disk space mid-extract).
                if (created) RemoveDirectoryQuietly(dir);
                return new UnzipResult(zipPath, "error", null, $"couldn't extract: {ex.Message}");
            }
        }
    }

    /// <summary>Read-only readiness check: does one of <paramref name="candidates"/>
    /// already open this archive? Never writes, moves or deletes anything —
    /// ZipperTests.ProbeWritesNothing holds it to that. The verdicts mirror
    /// Unlock.ProbeReadiness's, minus in_use (an archive is opened once,
    /// read-shared, and a locked one surfaces as unreadable).</summary>
    public static ZipProbeResult Probe(string zipPath, IReadOnlyList<string> candidates)
    {
        try
        {
            using var zip = new SzlZipFile(zipPath);
            var entries = zip.Cast<ZipEntry>().ToList();
            var archive = UnlockArchive(zip, entries, candidates, ask: null, Path.GetFileName(zipPath));
            return archive.Status switch
            {
                "not_encrypted" => new ZipProbeResult(zipPath, "not_encrypted", Message: "This zip isn't password-protected."),
                "opened" => new ZipProbeResult(zipPath, "ready", archive.MatchedIndex, "A saved password opens this."),
                "needs_password" => new ZipProbeResult(zipPath, "needs_password",
                    Message: "This zip needs a password none of the saved ones supply."),
                _ => new ZipProbeResult(zipPath, "unreadable", Message: "An encrypted entry couldn't be read."),
            };
        }
        catch (ZipException)
        {
            return new ZipProbeResult(zipPath, "unreadable", Message: "not a valid zip");
        }
        catch (Exception ex)
        {
            return new ZipProbeResult(zipPath, "unreadable", Message: $"Couldn't read it: {ex.Message}");
        }
    }

    /// <summary>Settles the archive's password when any entry is encrypted:
    /// "not_encrypted" when none is (nothing to do); otherwise
    /// <see cref="Passwords.Resolve"/> over the smallest NON-EMPTY encrypted
    /// entry (see <see cref="SmallestEncryptedEntry"/>), and on "opened" the
    /// password is left set on <paramref name="zip"/> for every later read.
    /// Internal so PdfMerge opens archives exactly this way.</summary>
    internal static PasswordResolution UnlockArchive(SzlZipFile zip, IReadOnlyList<ZipEntry> entries,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask, string zipName)
    {
        var probeEntry = SmallestEncryptedEntry(entries);
        if (probeEntry is null) return new PasswordResolution("not_encrypted");

        var resolution = Passwords.Resolve(candidates, ask, zipName, inside: null,
            password => Decrypts(zip, probeEntry, password));
        if (resolution.Status == "opened") zip.Password = resolution.Password;
        return resolution;
    }

    /// <summary>The whole decrypted entry, verified (see <see cref="CopyVerified"/>).
    /// Throws InvalidDataException when it does not verify. Internal for
    /// PdfMerge, which buffers PDF entries the same way.</summary>
    internal static byte[] ReadEntry(SzlZipFile zip, ZipEntry entry)
    {
        using var output = new MemoryStream();
        using (var input = zip.GetInputStream(entry))
        {
            if (!CopyVerified(input, entry, output))
                throw new InvalidDataException($"'{entry.Name}' didn't decrypt cleanly — wrong password or a damaged entry");
        }
        return output.ToArray();
    }

    /// <summary>The entry <see cref="Decrypts"/> tests a candidate password
    /// against. Prefers the smallest NON-EMPTY encrypted entry: a 0-byte
    /// entry decrypts to 0 bytes under ANY password, so its CRC (0) always
    /// matches the archive's recorded CRC (also 0) regardless of whether the
    /// password is right — picking one as the probe would silently collapse
    /// verification back to ZipCrypto's bare 1-byte header check, exactly
    /// what the class doc comment's verification rule exists to rule out
    /// (2026-08-28 review finding). Falls back to the first empty entry only
    /// when every encrypted entry in the archive is empty — there is nothing
    /// else to test against.</summary>
    private static ZipEntry? SmallestEncryptedEntry(IReadOnlyList<ZipEntry> entries)
    {
        ZipEntry? smallestNonEmpty = null;
        ZipEntry? firstEmpty = null;
        foreach (var entry in entries)
        {
            if (!entry.IsCrypted || !entry.IsFile) continue;
            if (entry.Size == 0) { firstEmpty ??= entry; continue; }
            if (smallestNonEmpty is null || entry.Size < smallestNonEmpty.Size) smallestNonEmpty = entry;
        }
        return smallestNonEmpty ?? firstEmpty;
    }

    /// <summary>One attempt with one password against one entry, read to the
    /// END of the stream so ZipCrypto's CRC can be compared and AES's
    /// authentication code gets checked. SharpZipLib's own exceptions —
    /// "Invalid password" from the header check, an inflater choking on
    /// garbage, the AES code failing — are all the same answer: wrong
    /// password. Anything else (an IO failure) is unreadable.</summary>
    private static PasswordTry Decrypts(SzlZipFile zip, ZipEntry entry, string password)
    {
        zip.Password = password;
        try
        {
            using var stream = zip.GetInputStream(entry);
            return CopyVerified(stream, entry, Stream.Null) ? PasswordTry.Opened : PasswordTry.WrongPassword;
        }
        catch (SharpZipBaseException)
        {
            return PasswordTry.WrongPassword;
        }
        catch (Exception)
        {
            return PasswordTry.Unreadable;
        }
    }

    /// <summary>Copies an entry's decrypted bytes to <paramref name="destination"/>,
    /// computing the CRC on the way. False when an encrypted, non-AES entry's
    /// CRC does not match what the archive recorded — the only thing that
    /// catches a wrong password the 1-byte header check let through. Plain
    /// entries are not second-guessed (the old reader never checked them
    /// either), and AES entries store no CRC at all (measured: entry.Crc is
    /// 0) — SharpZipLib authenticates those itself at end of stream, which is
    /// why this always reads to the end.</summary>
    private static bool CopyVerified(Stream source, ZipEntry entry, Stream destination)
    {
        var crc = new Crc32();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc.Update(new ArraySegment<byte>(buffer, 0, read));
            destination.Write(buffer, 0, read);
        }
        if (!entry.IsCrypted || entry.AESKeySize > 0) return true;
        return (uint)crc.Value == (uint)entry.Crc;
    }

    private static void WriteEntry(SzlZipFile zip, ZipEntry entry, string dir)
    {
        var target = GuardedTarget(dir, entry.Name);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // CreateNew, so a duplicate entry path fails loudly instead of the
        // second one silently overwriting the first — the behaviour
        // ZipFile.ExtractToDirectory had, and which CreateZip's in-archive
        // dedupe exists to avoid producing.
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        try
        {
            using var input = zip.GetInputStream(entry);
            if (!CopyVerified(input, entry, output))
                throw new InvalidDataException($"'{entry.Name}' didn't decrypt cleanly — wrong password or a damaged entry");
        }
        catch (SharpZipBaseException ex)
        {
            // One password per archive: an entry the archive's password does
            // not open ("Invalid password" from its header check, or the
            // inflater/AES check failing further in) fails the zip NAMING
            // the entry — SharpZipLib's own message never says which one.
            throw new InvalidDataException(
                $"'{entry.Name}' didn't decrypt cleanly — wrong password or a damaged entry ({ex.Message})");
        }
    }

    /// <summary>The ZipSlip guard. Resolves where <paramref name="entryName"/>
    /// would land and refuses anything not strictly under <paramref name="dir"/>:
    /// ".." segments resolve above it, a rooted name ("/evil.txt") resolves to
    /// the drive root, and a drive-qualified one ("C:\evil.txt") makes
    /// Path.Combine discard the folder altogether — all three arrive verbatim
    /// from SharpZipLib. Checked before a byte is written; the caller's
    /// created-gate cleanup removes whatever this call created up to then.</summary>
    private static string GuardedTarget(string dir, string entryName)
    {
        var relative = entryName.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"refused '{entryName}' — it would land outside the output folder");
        return full;
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
