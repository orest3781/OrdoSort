namespace OrdoSort.Core;

/// <summary>
/// Put a file where it belongs without anyone ever seeing it half-written.
///
/// Write to a sibling temp file, then move that into place in one filesystem
/// operation, so a reader sees either the old file or the complete new one
/// and never a truncated or empty one. Before this, the same procedure was
/// written out three times — Config.WriteAtomic, Config.WriteAtomicNew and
/// Zipper.PlaceAtomically — each re-deriving the two facts below, and each
/// carrying a doc comment naming a sibling it had been copied from.
///
/// Two facts a caller no longer has to know:
///
/// The temp file is a SIBLING of the destination, never %TEMP%. File.Replace
/// is only atomic within one volume, and these files live on shares
/// (2026-08-04 audit 2.3: the previous File.WriteAllText truncated in place,
/// so a crash or a full disk destroyed a valid config and took every station
/// down until someone repaired it by hand).
///
/// The temp name carries a GUID, not a fixed "&lt;file&gt;.tmp". Two stations
/// saving the same file concurrently used to share one temp name, so one
/// could install the other's bytes, or find its own temp deleted out from
/// under it mid-retry (2026-08 audit finding 4a).
///
/// That GUID is also why nothing here needs the "created by me" gate that
/// Unlock.PlaceAndSwap, ZipMerge.MergeZipCore and Zipper's own created flag
/// carry: those write to a COLLISION-FREED name that a peer can legitimately
/// own, so they must only ever clean up what they themselves put on disk.
/// The temp file here is, by construction, a name no other call can hold —
/// so cleanup is unconditional against it, and the destination is never
/// touched on failure. Two different disciplines that read alike; this module
/// is only the first one.
/// </summary>
internal static class AtomicPlace
{
    /// <summary>Attempts, not an elapsed-time budget: a deadline makes
    /// "did it retry enough" depend on how loaded the machine is, which this
    /// repo already learned the hard way in its test suite (41ae2f7 tore a
    /// 2000ms budget out of the probe tests for exactly that reason). Both
    /// loops this replaced already used 50 × 10ms, so nothing changes.</summary>
    internal const int Attempts = 50;
    internal const int RetrySleepMs = 10;

    /// <summary>Test seam: fired immediately before each placement attempt
    /// with the destination path and the zero-based attempt number. Settable
    /// only by tests, inert in production — the same shape as the hooks it
    /// replaces (Config.OnRetryForTests, Config.BeforeCreateOnlyMove) and as
    /// Commit.RaceHookForTests / Unlock.RaceHookForTests.
    ///
    /// It carries the PATH rather than firing blind because the hook is
    /// process-wide and xUnit runs other classes' saves concurrently: a test
    /// must be able to ignore placements that aren't its own.
    ///
    /// It carries the ATTEMPT so one hook covers both races the two old
    /// seams covered separately — plant a peer's file on attempt 0 to prove
    /// create-only defers to it, or release a held reader on attempt 2 to
    /// prove the retry loop is what makes the write land rather than luck.
    ///
    /// Note the timing differs from the old OnRetryForTests, which fired
    /// AFTER a failed attempt: this fires before each one, so releasing on
    /// attempt 2 means attempt 2 is the one that succeeds rather than
    /// attempt 3. What the tests assert — that the write lands at all — is
    /// unchanged.</summary>
    internal static Action<string, int>? BeforeAttempt;

    /// <summary>For files where a newer replacement is always correct: the
    /// main config, the destinations/monitored-folders/alerts side files, and
    /// a zip Save-As where the user has already confirmed the overwrite. The
    /// existing file is swapped out in one operation, never deleted first and
    /// rebuilt after.
    ///
    /// Retries a destination that is briefly held open — Config.Load reads
    /// with File.ReadAllText and no FileShare.Delete, so a reader really can
    /// block the replace for a moment — giving that reader time to let go.
    ///
    /// Must NOT be used where the destination belongs to whoever created it;
    /// see <see cref="TryCreateNew"/>.</summary>
    internal static bool TryReplace(string destination, Action<string> writeTemp, out string error) =>
        Place(destination, writeTemp, replaceExisting: true, out error);

    /// <summary>For files whose ownership passes to whoever creates them —
    /// today, box-labels.json's bootstrap. NEVER falls back to File.Replace:
    /// if the destination has appeared by the time the move runs, that is a
    /// SUCCESS, not a reason to retry and overwrite. The peer that created it
    /// holds newer truth than this caller's snapshot.
    ///
    /// Why that matters (2026-08 audit finding 1): Config.Save guards its
    /// bootstrap with `if (!File.Exists(labels))`, but that guard and this
    /// write are not atomic together. Another station's BoxLabelStore.Mutate
    /// can create the file, advance a counter and release its lock entirely
    /// inside the gap. The old code re-checked File.Exists INSIDE its retry
    /// loop and switched to File.Replace the instant the destination
    /// appeared — so a station would wait out the peer's lock and then
    /// silently replace freshly written counters with a stale snapshot, and a
    /// box number already printed on a physical box got reissued.
    ///
    /// There is deliberately no retry here. A peer holding the destination is
    /// not a transient condition to wait out; it is the answer.</summary>
    internal static bool TryCreateNew(string destination, Action<string> writeTemp, out string error) =>
        Place(destination, writeTemp, replaceExisting: false, out error);

    private static bool Place(
        string destination, Action<string> writeTemp, bool replaceExisting, out string error)
    {
        var tmp = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Inside the try (2026-08 audit finding 4b): a disk-full failure
            // while writing used to strand a partial temp outside any
            // cleanup path.
            writeTemp(tmp);

            if (replaceExisting) MoveOverExisting(tmp, destination);
            else MoveOnlyIfAbsent(tmp, destination);

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            // Only ever the temp — never the destination. The GUID means no
            // other call can own this name, so there is nothing here to be
            // careful about deleting.
            RemoveQuietly(tmp);
            error = ex.Message;
            return false;
        }
    }

    private static void MoveOverExisting(string tmp, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            BeforeAttempt?.Invoke(destination, attempt);
            try
            {
                // File.Replace preserves the destination's ACLs and is the
                // strongest primitive Windows offers, but it REQUIRES the
                // destination to exist — hence the fallback for first
                // creation.
                if (File.Exists(destination))
                    File.Replace(tmp, destination, destinationBackupFileName: null);
                else
                    File.Move(tmp, destination);
                return;
            }
            // On the final attempt these guards stop matching and the
            // exception escapes to Place's catch, which cleans up the temp
            // and reports the failure.
            catch (IOException) when (attempt < Attempts - 1) { }
            catch (UnauthorizedAccessException) when (attempt < Attempts - 1) { }
            Thread.Sleep(RetrySleepMs);
        }
    }

    private static void MoveOnlyIfAbsent(string tmp, string destination)
    {
        BeforeAttempt?.Invoke(destination, 0);
        try
        {
            // File.Move has no overwrite fallback: it fails outright when the
            // destination exists, and that failure is exactly the signal this
            // treats as done.
            File.Move(tmp, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Someone else won. Their content stands; ours is discarded.
            // This is success.
            File.Delete(tmp);
        }
    }

    private static void RemoveQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
