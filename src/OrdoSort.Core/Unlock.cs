using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace OrdoSort.Core;

/// <summary>
/// Unlock (decrypt) password-protected PDFs. Never throws.
///
/// Two modes, chosen by <c>suffix</c>:
/// - suffix set (e.g. "_unlocked"): a decrypted COPY is written alongside the
///   original; the original is untouched. No longer reachable from the app —
///   kept for callers that want a copy without disturbing the source.
/// - suffix EMPTY (default, and what the app does): the unlocked file takes the
///   original's name and place, and the locked original is MOVED to a dated
///   locked_archive folder beside it. It is never overwritten: overwriting had
///   to delete the original, and deleting is refused for a read-only file.
///   Nothing moves until the decrypted bytes have been verified readable, so a
///   decryption that produced garbage can never displace the encrypted file.
///
/// The source is read ONCE and the probe, decrypt and verify all run against
/// those bytes. Three separate opens meant three full transfers of every
/// document over a network share, which is where the time went.
///
/// Decryption uses PdfSharp Import mode (open with the user password, copy the
/// pages into a fresh unencrypted document) — Modify mode would demand the
/// OWNER password, which the person filing a document doesn't have.
/// </summary>
public static class Unlock
{
    public sealed record UnlockResult(
        string Status, string Source, string? NewPath = null,
        string Message = "", bool InPlace = false, string? ArchivedTo = null)
    {
        // ok | not_encrypted | wrong_password | error
        public bool Ok => Status == "ok";
    }

    /// <summary>Where a replaced original is kept, beside the file itself and
    /// dated so a day's worth lands together. Invariant: this becomes a real
    /// folder name on disk, and two stations with different Windows locales
    /// must land on the SAME name for the same day.</summary>
    public static string ArchiveFolderFor(string src, DateTime? now = null) =>
        Path.Combine(Path.GetDirectoryName(src)!,
            "locked_archive_" + (now ?? DateTime.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    /// <summary>At or over this size a file is not buffered: File.ReadAllBytes
    /// has a hard 2GB wall, and the buffered path transiently holds roughly
    /// three times the file size (source, PdfSharp's working set, the growing
    /// output stream). 32MB keeps every ordinary scan on the fast path while a
    /// giant one streams from disk and still completes. Settable only by tests,
    /// which use it to drive small files down the streaming path.</summary>
    public static long LargeFileThresholdBytes { get; internal set; } = 32L * 1024 * 1024;

    public static UnlockResult UnlockPdf(string src, string password,
        string? destDir = null, string suffix = "")
    {
        if (!File.Exists(src))
            return new("error", src, Message: "File not found.");

        var dest = destDir ?? Path.GetDirectoryName(src)!;
        if (!Directory.Exists(dest))
            return new("error", src, Message: $"The output folder isn't available: {dest}");

        long length;
        try { length = new FileInfo(src).Length; }
        catch (Exception ex)
        {
            return new("error", src, Message: $"Couldn't read it: {ex.Message}");
        }

        return length >= LargeFileThresholdBytes
            ? UnlockStreaming(src, password, dest, suffix)
            : UnlockBuffered(src, password, dest, suffix);
    }

    /// <summary>The fast path: ONE read of the source, and the probe, decrypt
    /// and verify all run against those bytes. Three separate opens meant three
    /// full transfers of every document over a network share, which was where
    /// the time went. The cost is holding the file in memory; the caller bounds
    /// how many run at once, so that stays a few megabytes.</summary>
    private static UnlockResult UnlockBuffered(
        string src, string password, string dest, string suffix)
    {
        byte[] sourceBytes;
        try
        {
            sourceBytes = File.ReadAllBytes(src);
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            return new("error", src, Message:
                "It's open in another program — close it there and unlock it again.");
        }
        catch (Exception ex)
        {
            return new("error", src, Message: $"Couldn't read it: {ex.Message}");
        }

        // encryption state, checked without a password. Still a real probe —
        // opening WITH the password cannot answer this, because a correctly
        // decrypted document reports itself unencrypted just like one that
        // never was.
        try
        {
            using var probeStream = new MemoryStream(sourceBytes, writable: false);
            using var probe = PdfReader.Open(probeStream, PdfDocumentOpenMode.Import);
            if (!probe.SecuritySettings.IsEncrypted)
                return new("not_encrypted", src, Message: "This PDF isn't password-protected.");
        }
        catch
        {
            // couldn't open without a password -> it's encrypted; fall through
        }

        byte[] unlockedBytes;
        try
        {
            using var inStream = new MemoryStream(sourceBytes, writable: false);
            using var input = PdfReader.Open(inStream, password, PdfDocumentOpenMode.Import);
            using var output = new PdfDocument();
            foreach (var page in input.Pages) output.AddPage(page);
            using var outStream = new MemoryStream();
            output.Save(outStream, closeStream: false);
            unlockedBytes = outStream.ToArray();
        }
        catch (PdfReaderException)
        {
            return new("wrong_password", src, Message: "That password didn't work.");
        }
        catch (Exception ex)
        {
            return new("error", src, Message: $"Couldn't unlock it: {ex.Message}");
        }

        // verified before a single byte is written anywhere
        using (var verifyStream = new MemoryStream(unlockedBytes, writable: false))
        {
            var problem = VerifyReadable(verifyStream);
            if (problem.Length > 0)
                return new("error", src,
                    Message: $"This PDF looks damaged — it couldn't be unlocked cleanly ({problem}).");
        }

        return PlaceAndSwap(src, dest, suffix,
            target => File.WriteAllBytes(target, unlockedBytes));
    }

    /// <summary>The big-file path: nothing is buffered whole. The source is
    /// read from disk by PdfSharp directly, the decrypted document is saved to
    /// a LOCAL temp file (never the share), verified there, and only then moved
    /// into place — one write to the destination, no read-back over the
    /// network. Slower than the buffered path (the source is opened twice) but
    /// it has no 2GB wall and its peak memory is PdfSharp's working set alone.
    /// PdfSharp still materialises the document's objects, so memory remains
    /// proportional to content — that part no path can remove.</summary>
    private static UnlockResult UnlockStreaming(
        string src, string password, string dest, string suffix)
    {
        try
        {
            using var probeStream = File.OpenRead(src);
            using var probe = PdfReader.Open(probeStream, PdfDocumentOpenMode.Import);
            if (!probe.SecuritySettings.IsEncrypted)
                return new("not_encrypted", src, Message: "This PDF isn't password-protected.");
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            return new("error", src, Message:
                "It's open in another program — close it there and unlock it again.");
        }
        catch
        {
            // couldn't open without a password -> it's encrypted; fall through
        }

        var localTemp = Path.Combine(Path.GetTempPath(),
            "ordosort_unlock_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            try
            {
                using var inStream = File.OpenRead(src);
                using var input = PdfReader.Open(inStream, password, PdfDocumentOpenMode.Import);
                using var output = new PdfDocument();
                foreach (var page in input.Pages) output.AddPage(page);
                output.Save(localTemp);
            }
            catch (PdfReaderException)
            {
                return new("wrong_password", src, Message: "That password didn't work.");
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                return new("error", src, Message:
                    "It's open in another program — close it there and unlock it again.");
            }
            catch (Exception ex)
            {
                return new("error", src, Message: $"Couldn't unlock it: {ex.Message}");
            }

            // verified before anything at the destination is touched
            try
            {
                using var verifyStream = File.OpenRead(localTemp);
                var problem = VerifyReadable(verifyStream);
                if (problem.Length > 0)
                    return new("error", src,
                        Message: $"This PDF looks damaged — it couldn't be unlocked cleanly ({problem}).");
            }
            catch (Exception ex)
            {
                return new("error", src,
                    Message: $"This PDF looks damaged — it couldn't be unlocked cleanly ({ex.Message}).");
            }

            // File.Move copies across volumes, so this works when the
            // destination is a share and the temp is on the local disk
            return PlaceAndSwap(src, dest, suffix,
                target => File.Move(localTemp, target));
        }
        finally
        {
            RemoveQuietly(localTemp);
        }
    }

    /// <summary>The shared tail of both paths: pick a collision-free target,
    /// let the caller put the verified content there, then do the
    /// archive-and-swap. <paramref name="place"/> runs exactly once and is the
    /// only step that differs between buffered and streamed content.</summary>
    private static UnlockResult PlaceAndSwap(
        string src, string dest, string suffix, Action<string> place)
    {
        var stem = Path.GetFileNameWithoutExtension(src);
        var swapInPlace = string.IsNullOrEmpty(suffix)
            && string.Equals(Path.GetFullPath(dest),
                Path.GetFullPath(Path.GetDirectoryName(src)!), StringComparison.OrdinalIgnoreCase);
        var target = swapInPlace
            ? CollisionFree(Path.Combine(dest, stem + ".unlocking.pdf"))
            : CollisionFree(Path.Combine(dest, stem + suffix + ".pdf"));

        try
        {
            place(target);
        }
        catch (Exception ex)
        {
            RemoveQuietly(target);
            return new("error", src, Message: $"Couldn't save the unlocked copy: {ex.Message}");
        }

        if (swapInPlace)
        {
            // The original is MOVED aside, never overwritten. Overwriting had to
            // delete it, and deleting is refused for a read-only file — moving
            // only rewrites a directory entry, so it succeeds where the old way
            // failed. The archive is beside the file, so it is always the same
            // volume and the move stays cheap and atomic.
            var archiveDir = ArchiveFolderFor(src);
            string archived;
            try
            {
                Directory.CreateDirectory(archiveDir);
                archived = CollisionFree(Path.Combine(archiveDir, Path.GetFileName(src)));
                File.Move(src, archived);
            }
            catch (IOException ex) when (IsInUse(ex))
            {
                RemoveQuietly(target);
                return new("error", src, Message:
                    "It's open in another program — close it there and unlock it again.");
            }
            catch (Exception ex)
            {
                RemoveQuietly(target);
                return new("error", src,
                    Message: $"Couldn't move the locked original aside: {ex.Message}");
            }

            try
            {
                File.Move(target, src);   // the name is free now: the original moved
            }
            catch (Exception ex)
            {
                // The replacement never landed. Put the original back so the
                // folder looks untouched; if even that fails, say where both
                // halves are rather than leaving someone to search for them.
                try { File.Move(archived, src); RemoveQuietly(target); }
                catch
                {
                    return new("error", src, Message:
                        $"The unlocked copy couldn't take the original's place ({ex.Message}). " +
                        $"The locked original is at {archived} and the unlocked copy at {target}.");
                }
                return new("error", src,
                    Message: $"Couldn't put the unlocked copy in place: {ex.Message}");
            }
            return new("ok", src, NewPath: src, InPlace: true, ArchivedTo: archived);
        }
        return new("ok", src, NewPath: target);
    }

    private static string CollisionFree(string target)
    {
        if (!File.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void RemoveQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Windows reports a file held by another process as a sharing
    /// violation (32) or a lock violation (33). Worth separating, because every
    /// other cause of a refused move needs a different answer from the person
    /// reading the message — and the old text blamed an open program for all
    /// of them, including files that were merely read-only.</summary>
    private static bool IsInUse(IOException ex) =>
        (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>Reopen the saved copy and force every page to load. "" if it's
    /// a clean, open PDF, else a short problem — catches a decryption that
    /// produced garbage. Import mode (not InformationOnly, which PdfSharp
    /// documents as unimplemented) is what actually parses the page objects,
    /// so touching each page below is a real check rather than a no-op.</summary>
    private static string VerifyReadable(Stream stream)
    {
        try
        {
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            if (doc.SecuritySettings.IsEncrypted) return "the copy is still password-protected";
            if (doc.PageCount == 0) return "the copy has no readable pages";
            for (var i = 0; i < doc.PageCount; i++) _ = doc.Pages[i];
            return "";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
