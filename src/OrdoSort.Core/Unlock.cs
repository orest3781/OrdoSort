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
///
/// <see cref="ProbeReadiness"/> is a separate, read-only entry point: given a
/// path and an ordered list of candidate passwords, it reports whether one of
/// them already opens the file, without unlocking anything. It never writes,
/// moves or deletes — see its own doc comment for why its verdicts can be
/// trusted to agree with what a real <see cref="UnlockPdf"/> call would do.
/// (Lowercase "probe" elsewhere in this file, e.g. two paragraphs up, refers
/// to the unrelated no-password encryption check inside UnlockBuffered /
/// UnlockStreaming, not to this method.)
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

    public sealed record ProbeResult(
        string Status, string Source, int? MatchedIndex = null, string Message = "")
    {
        // not_encrypted | ready | needs_password | in_use | unreadable
        /// <summary>True when a real <see cref="UnlockPdf"/> call needs no
        /// candidate at all (not_encrypted) or is expected to succeed with
        /// <see cref="MatchedIndex"/> (ready). False for needs_password,
        /// in_use and unreadable — none of those mean "go ahead."</summary>
        public bool ReadyToUnlock => Status is "not_encrypted" or "ready";
    }

    /// <summary>Read-only readiness check: does a SAVED password already open
    /// this PDF, before the user types anything or clicks Unlock? Reports one
    /// of five states — not_encrypted, ready (with the winning
    /// <see cref="ProbeResult.MatchedIndex"/> into <paramref name="candidates"/>),
    /// needs_password (encrypted, but none of <paramref name="candidates"/>
    /// opened it), in_use, unreadable — and never writes, moves or deletes
    /// anything, anywhere: after the one read below it only ever opens a
    /// MemoryStream over bytes already in memory.
    ///
    /// A verdict here is only useful if it is not contradicted by what
    /// <see cref="UnlockPdf"/> actually does with the same source and the
    /// same candidates — that agreement is asserted in
    /// OrdoSort.Core.Tests.UnlockProbeAgreementTests, not just assumed here.
    /// This method is allowed to diverge from UnlockPdf in exactly one,
    /// harmless direction — it must never claim readiness UnlockPdf wouldn't
    /// back up. It is, honestly, still a LIGHTER approximation of the real
    /// unlock: its open calls use the same mode and the same
    /// password-exception discipline as UnlockPdf (risk 3, below), but it
    /// never calls AddPage, never calls Save, and never runs VerifyReadable
    /// against a re-saved copy — it cannot, without violating "this probe
    /// writes nothing" (Task 1, step 6). A 2026-08 review flagged exactly
    /// this gap: a correct password on a document whose PAGE data is broken
    /// could read "ready" here and "error" from the real unlock, because the
    /// real unlock's own VerifyReadable step (below) does something this
    /// probe used to skip entirely — open a candidate, then discard it
    /// without ever touching a page.
    ///
    /// What closes it, and what doesn't: this method now runs VerifyReadable's
    /// own technique — <c>for (page in PageCount) touch Pages[page]</c> — on
    /// the winning candidate before declaring ready (see the loop below).
    /// That is exact parity with what the real unlock's own safety net does,
    /// not a proven fix for a reproduced bug: a six-fixture investigation
    /// (2026-08-08) tried to construct a document that opens fine but fails
    /// this touch — corrupted content-stream bytes (Flate-compressed, so
    /// genuinely broken by the corruption), corrupted page-dictionary syntax,
    /// a /Contents reference pointed at the wrong object type, a corrupted
    /// /MediaBox array, a corrupted /ProcSet array, and a GENUINELY dangling
    /// reference built via PdfSharp's own PdfInternals.RemoveObject (not
    /// hand-edited bytes) — and found that PdfReader.Open in this PdfSharp
    /// version (6.1.1) eagerly tokenizes every page object's own dictionary
    /// syntax as part of establishing PageCount, so dictionary-level
    /// corruption breaks Open itself, identically for probe and real unlock;
    /// and that touching a page never decodes or validates content-stream
    /// bytes or resolves resource/content references at all, so those three
    /// corruption kinds were invisible not just to this probe but to
    /// VerifyReadable's identical mechanism when run directly against the
    /// same bytes. No test in this file claims to reproduce the divergence,
    /// because none of the six fixtures did. What remains unclosed, and
    /// cannot be closed by a read-only method: a defect that AddPage or Save
    /// itself introduces into output that did not exist in the source — a
    /// probe that never saves anything cannot detect a flaw in a save it
    /// never performs. The UI label already only claims what was tested ("a
    /// saved password opens this," never "this will unlock cleanly"), which
    /// is the honest boundary for that residual gap.
    ///
    /// Cost of the touch: measured (2026-08-08) on 40-page/53KB and
    /// 300-page/214KB encrypted fixtures, 15 iterations, open-only vs.
    /// open-plus-touch: no measurable timing difference (40 pages ~1.9ms
    /// either way; 300 pages ~12ms either way — touch was within noise, not
    /// consistently slower), and a small, roughly constant-per-page managed
    /// memory increase (40 pages: 619KB -> 650KB, +31KB; 300 pages: 3802KB ->
    /// 3978KB, +176KB — under 5% both times). Extrapolated to risk 1's
    /// worst case (50 files against 5 saved passwords, up to 250 opens): the
    /// touch adds low single-digit percent to a cost already dominated by
    /// the opens themselves, not by what happens after each one succeeds.
    ///
    /// Cost of the open mode itself (risk 1): the source is read from disk
    /// exactly ONCE regardless of how many candidates are tried — same
    /// discipline as <see cref="UnlockBuffered"/>, whose own doc comment
    /// explains why: three separate opens over a share meant three full
    /// transfers. A 50-file drop against 5 saved passwords is therefore 50
    /// network reads, not 250. PdfDocumentOpenMode.InformationOnly was
    /// measured (2026-08-08) against Import for both the encryption check
    /// and the password check, on a 40-page / 54KB encrypted fixture, 25
    /// iterations each, single-shot managed-memory snapshots with the opened
    /// document kept alive: wrong password threw the identical
    /// PdfReaderException under both modes (~0.44ms Import vs ~0.47ms
    /// InformationOnly); the right password opened under both, retaining
    /// ~628KB (Import) vs ~627KB (InformationOnly) with no timing advantage
    /// (~2.2ms vs ~1.7ms). PdfSharp 6.1.1 marks InformationOnly
    /// <c>[Obsolete("InformationOnly is not implemented, use Import
    /// instead.")]</c> — the measurement confirms that isn't just a stale
    /// label: it behaves exactly like Import, not a cheaper path. Import is
    /// used here for that reason, and because it keeps this probe's open
    /// calls identical to UnlockPdf's, which is what makes the agreement
    /// test meaningful rather than coincidental.
    ///
    /// Exception discipline (risk 3), mirrors UnlockBuffered exactly:
    /// <see cref="PdfReaderException"/> is a wrong password for that one
    /// candidate — try the next; an <see cref="IOException"/> where
    /// <see cref="IsInUse"/> is true (only possible at the single disk read
    /// below, never from the in-memory reopens that follow) means in_use and
    /// stops; anything else — including a failure during the page touch
    /// above — means unreadable and stops. Collapsing these would report a
    /// damaged or otherwise unreadable file as merely needing a
    /// password.</summary>
    public static ProbeResult ProbeReadiness(string src, IReadOnlyList<string> candidates)
    {
        if (!File.Exists(src))
            return new("unreadable", src, Message: "File not found.");

        byte[] sourceBytes;
        try
        {
            sourceBytes = File.ReadAllBytes(src);
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            return new("in_use", src, Message:
                "It's open in another program — close it there and try again.");
        }
        catch (Exception ex)
        {
            return new("unreadable", src, Message: $"Couldn't read it: {ex.Message}");
        }

        // encryption state, checked without a password — shared helper, see
        // IsProvablyNotEncrypted's own doc comment for why opening WITH a
        // password cannot answer this question.
        using (var probeStream = new MemoryStream(sourceBytes, writable: false))
        {
            if (IsProvablyNotEncrypted(probeStream))
                return new("not_encrypted", src, Message: "This PDF isn't password-protected.");
        }
        // couldn't prove it unencrypted -> it's encrypted (or damaged in a
        // way that looks the same from here, e.g. no StartXRef); fall
        // through and let the candidate loop's own exception discipline
        // below decide which.

        for (var i = 0; i < candidates.Count; i++)
        {
            try
            {
                using var ms = new MemoryStream(sourceBytes, writable: false);
                using var doc = PdfReader.Open(ms, candidates[i], PdfDocumentOpenMode.Import);
                // VerifyReadable's own technique (Unlock.cs, below), applied
                // here to the winning candidate before declaring ready:
                // closes the gap a 2026-08 review found — this loop used to
                // open a candidate and immediately discard it, never
                // touching a single page, while the real UnlockPdf goes on
                // to copy every page, save, and run this exact touch over
                // the result. See this method's doc comment for what
                // touching a page does and does not catch, and what a
                // six-fixture investigation into the gap found.
                for (var p = 0; p < doc.PageCount; p++) { var _ = doc.Pages[p]; }
                return new("ready", src, MatchedIndex: i, Message: "A saved password opens this.");
            }
            catch (PdfReaderException)
            {
                // wrong password for this one candidate; try the next
            }
            catch (Exception ex)
            {
                return new("unreadable", src, Message: $"Couldn't read it: {ex.Message}");
            }
        }

        return new("needs_password", src,
            Message: "This PDF needs a password none of the saved ones supply.");
    }

    /// <summary>Shared by <see cref="ProbeReadiness"/> and
    /// <see cref="UnlockBuffered"/> — both need the identical no-password
    /// encryption check and, before the 2026-08 fix round, each carried its
    /// own copy. Opening WITH a password cannot answer "is this encrypted",
    /// because a correctly decrypted document reports itself unencrypted
    /// just like one that never was. Returns true only when opening without
    /// a password succeeded AND proved the document unencrypted; false means
    /// "couldn't prove that" — encrypted, or damaged in a way that looks the
    /// same from here — and the caller falls through to its own
    /// password-based path either way. <paramref name="stream"/> must be
    /// freshly positioned at 0 (a fresh MemoryStream view in both current
    /// callers); this does not rewind it back for the caller to reuse, so
    /// callers pass a stream they are about to discard, same as before this
    /// was extracted.</summary>
    private static bool IsProvablyNotEncrypted(Stream stream)
    {
        try
        {
            using var probe = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            return !probe.SecuritySettings.IsEncrypted;
        }
        catch
        {
            return false;
        }
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

        // encryption state, checked without a password — shared with
        // ProbeReadiness via IsProvablyNotEncrypted; see its doc comment for
        // why opening WITH the password cannot answer this.
        using (var probeStream = new MemoryStream(sourceBytes, writable: false))
        {
            if (IsProvablyNotEncrypted(probeStream))
                return new("not_encrypted", src, Message: "This PDF isn't password-protected.");
        }
        // couldn't prove it unencrypted -> it's encrypted; fall through

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

        return PlaceAndSwap(src, dest, suffix, (target, markCreated) =>
        {
            // Exclusive create: fails atomically if the name is taken rather
            // than truncating whatever is there. CollisionFree only proved
            // `target` was free AT CHECK TIME — on the shared folders this
            // app targets, another station can claim that exact name before
            // this runs, and File.WriteAllBytes used to silently destroy
            // whatever that station had just written (2026-08 audit finding
            // 1.2). markCreated fires the instant the file exists on disk,
            // before the write — so if the write itself then fails, that
            // still counts as "this call created it" and PlaceAndSwap cleans
            // up the partial file instead of orphaning it.
            using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            markCreated();
            fs.Write(unlockedBytes, 0, unlockedBytes.Length);
        });
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
            // destination is a share and the temp is on the local disk. The
            // two-argument overload is already create-only — it does not
            // overwrite an existing destination — so this only needs to
            // report the creation once it has actually happened.
            return PlaceAndSwap(src, dest, suffix, (target, markCreated) =>
            {
                File.Move(localTemp, target);
                markCreated();
            });
        }
        finally
        {
            RemoveQuietly(localTemp);
        }
    }

    /// <summary>Test seam: invoked with the destination path immediately
    /// before <c>place</c> attempts its exclusive create, so a test can
    /// deterministically plant a colliding file in the gap between
    /// <see cref="CollisionFree"/>'s free-at-check-time answer and the write
    /// landing — the exact race a shared folder makes possible (another
    /// station claims that name in that gap). Same "settable only by tests,
    /// inert in production" shape as <see cref="Commit.RaceHookForTests"/>,
    /// parameterized by path like <see cref="Config.BeforeCreateOnlyMove"/>
    /// so a test can filter to its own target — this hook is process-wide
    /// and xUnit runs other test classes' Unlock calls concurrently, so no
    /// [Collection] coordination is needed, only a path check.</summary>
    internal static Action<string>? RaceHookForTests;

    /// <summary>The shared tail of both paths: pick a collision-free target,
    /// let the caller put the verified content there, then do the
    /// archive-and-swap. <paramref name="place"/> runs exactly once and is
    /// the only step that differs between buffered and streamed content. It
    /// must create <paramref name="target"/>-only — never overwrite whatever
    /// is already there — and call the <c>markCreated</c> callback it is
    /// given the instant the file actually comes into existence on disk
    /// (including on a later failure, so a partial write still counts as
    /// "created"). Every RemoveQuietly(target) below is gated on that flag:
    /// CollisionFree only proves the name was free AT CHECK TIME, and on the
    /// shared folders this app targets another station can claim that exact
    /// name before place() runs. When that happens this call must not delete
    /// the file that beat it there — only content THIS call put on disk is
    /// ever removed (2026-08 audit finding 1.2).
    ///
    /// A crash or power loss between the two moves below leaves the document
    /// in one of three places: still under the temp name here, moved aside
    /// to the archive with the temp name still present, or (the success
    /// case) restored to its original name with the archive holding the
    /// locked original. That window cannot be closed — two File.Move calls
    /// cannot be made atomic — but its temp name CAN be kept out of the
    /// user's queue: Scanner.Eligible matches any name ending ".pdf" outside
    /// insert mode, and FolderMonitor.ParseFiletypes/TypeMatches match on
    /// Path.GetExtension, which for a name like "X.unlocking.pdf" is still
    /// ".pdf". A ".pdf"-suffixed temp name is therefore something both an
    /// inbox scan and a watch-folder tile would count as a document, turning
    /// an interrupted unlock into a spurious entry in the filing queue
    /// (2026-08 audit finding 1.3). ".tmp" — the same convention
    /// Config.WriteAtomicNew uses — is not matched by either, so an
    /// interrupted swap can strand the temp file on disk but can never make
    /// it reappear as something to file.</summary>
    private static UnlockResult PlaceAndSwap(
        string src, string dest, string suffix, Action<string, Action> place)
    {
        var stem = Path.GetFileNameWithoutExtension(src);
        var swapInPlace = string.IsNullOrEmpty(suffix)
            && string.Equals(Path.GetFullPath(dest),
                Path.GetFullPath(Path.GetDirectoryName(src)!), StringComparison.OrdinalIgnoreCase);
        var target = swapInPlace
            ? CollisionFree(Path.Combine(dest, stem + ".unlocking.tmp"))
            : CollisionFree(Path.Combine(dest, stem + suffix + ".pdf"));

        var createdTarget = false;
        void MarkCreated() => createdTarget = true;

        RaceHookForTests?.Invoke(target);
        try
        {
            place(target, MarkCreated);
        }
        catch (Exception ex)
        {
            if (createdTarget) RemoveQuietly(target);
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
                if (createdTarget) RemoveQuietly(target);
                return new("error", src, Message:
                    "It's open in another program — close it there and unlock it again.");
            }
            catch (Exception ex)
            {
                if (createdTarget) RemoveQuietly(target);
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
                try { File.Move(archived, src); if (createdTarget) RemoveQuietly(target); }
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
