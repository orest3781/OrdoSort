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
/// Unlock.PlaceAndSwap, PdfMerge.MergeZipCore and Zipper's own created flag
/// carry: those write to a COLLISION-FREED name that a peer can legitimately
/// own, so they must only ever clean up what they themselves put on disk.
/// The temp file here is, by construction, a name no other call can hold —
/// so cleanup is unconditional against it, and the destination is never
/// touched on failure. Two different disciplines that read alike; this module
/// is only the first one.
/// </summary>
internal static class AtomicPlace
{
    /// <summary>Governs how many times the WHOLE placement — the write and
    /// the move alike, see <see cref="Place"/> — is attempted, and therefore,
    /// together with <see cref="DelayMs"/>, the worst-case time retrying can
    /// cost. That worst case matters because Place runs synchronously on the
    /// UI thread: these are ShellViewModel.SaveConfigNow's saves, and the
    /// TrySave/TrySaveMain calls around it. Two to three seconds of a
    /// stalled window beats a spurious "settings not saved" warning; thirty
    /// seconds would not. Raise this, or <see cref="MaxRetrySleepMs"/>, only
    /// after moving those saves off the UI thread — NOT part of this change
    /// — or the extra budget lands as a longer freeze, not a safer save.
    ///
    /// Attempts, not an elapsed-time budget: a deadline makes "did it retry
    /// enough" depend on how loaded the machine is, which this repo already
    /// learned the hard way in its test suite (41ae2f7 tore a 2000ms budget
    /// out of the probe tests for exactly that reason). Both loops this
    /// replaced already used 50 attempts, so the count carries over — only
    /// what it covers and the delay between attempts have changed; see
    /// <see cref="DelayMs"/> and <see cref="Place"/>.</summary>
    internal const int Attempts = 50;

    /// <summary>The delay before the first retry, and the step the ramp in
    /// <see cref="DelayMs"/> climbs by. Matches the flat delay this replaced,
    /// so the common case — a local antivirus or indexer holding the
    /// destination for a few milliseconds — clears exactly as fast as it
    /// always did.</summary>
    internal const int InitialRetrySleepMs = 10;

    /// <summary>Caps the ramp <see cref="DelayMs"/> computes, so a run of
    /// failures spends the budget spread across many attempts rather than a
    /// handful of huge sleeps, and so the total across <see cref="Attempts"/>
    /// lands in the 2-3 second range that constant's doc comment describes
    /// instead of growing unbounded.</summary>
    internal const int MaxRetrySleepMs = 60;

    /// <summary>Delay before retrying the given zero-based attempt: ramps
    /// from <see cref="InitialRetrySleepMs"/> up by that same step each
    /// attempt, capped at <see cref="MaxRetrySleepMs"/>. Early attempts stay
    /// fast because the common transient really is a millisecond-scale local
    /// lock — a flat fast retry serves that well, so this starts exactly
    /// where the old flat delay did. Later attempts back off, because a
    /// dropped network session takes seconds, not milliseconds, to recover
    /// from, and there is no point spending the whole budget re-knocking
    /// every 10ms.
    ///
    /// A pure function of the attempt index alone — never of elapsed time —
    /// for the same reason <see cref="Attempts"/> is a count and not a
    /// deadline: "how long did this wait" must not depend on how loaded the
    /// machine running the test is.</summary>
    internal static int DelayMs(int attempt) =>
        Math.Min(InitialRetrySleepMs * (attempt + 1), MaxRetrySleepMs);

    /// <summary>Test seam: fired immediately before each placement attempt —
    /// the write and the move together, see <see cref="Place"/> — with the
    /// destination path and the zero-based attempt number. Settable only by
    /// tests, inert in production — the same shape as the hooks it replaces
    /// (Config.OnRetryForTests, Config.BeforeCreateOnlyMove) and as
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
    /// The same retry now also covers writing the temp file itself: a
    /// dropped network session can just as easily interrupt that as it can
    /// the move.
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
    /// A peer holding the destination costs none of Place's retry budget —
    /// MoveOnlyIfAbsent (below) turns it into success internally, so Place's
    /// loop never even sees an exception for it to retry. That is
    /// deliberate: a peer holding the destination is not a transient
    /// condition to wait out; it is the answer. A failure here for any OTHER
    /// reason — the write, or the move itself failing in some way that
    /// isn't the destination already existing — gets the same retry
    /// TryReplace gets; there is no peer-race distinction left to make by
    /// that point.</summary>
    internal static bool TryCreateNew(string destination, Action<string> writeTemp, out string error) =>
        Place(destination, writeTemp, replaceExisting: false, out error);

    private static bool Place(
        string destination, Action<string> writeTemp, bool replaceExisting, out string error)
    {
        var tmp = $"{destination}.{Guid.NewGuid():N}.tmp";
        for (var attempt = 0; ; attempt++)
        {
            BeforeAttempt?.Invoke(destination, attempt);
            try
            {
                // Never build on a previous attempt's wreckage. The GUID
                // means only THIS call could own tmp, so removing it first
                // is always safe — and it means a writeTemp that appends, or
                // otherwise doesn't truncate on its own, still starts every
                // attempt from nothing instead of resurrecting a partial
                // file an earlier transient failure left behind.
                RemoveQuietly(tmp);

                // Inside the try (2026-08 audit finding 4b): a disk-full
                // failure while writing used to strand a partial temp
                // outside any cleanup path. Retried on the same terms as the
                // move below — a dropped network session can interrupt
                // either one, not just the move.
                writeTemp(tmp);

                if (replaceExisting) MoveOverExisting(tmp, destination);
                else MoveOnlyIfAbsent(tmp, destination);

                error = "";
                return true;
            }
            // On the final attempt these guards stop matching and the
            // exception falls to the catch below, which cleans up the temp
            // and reports the failure.
            catch (IOException) when (attempt < Attempts - 1) { }
            catch (UnauthorizedAccessException) when (attempt < Attempts - 1) { }
            catch (Exception ex)
            {
                // Only ever the temp — never the destination. The GUID means
                // no other call can own this name, so there is nothing here
                // to be careful about deleting.
                RemoveQuietly(tmp);
                error = ex.Message;
                return false;
            }
            Thread.Sleep(DelayMs(attempt));
        }
    }

    private static void MoveOverExisting(string tmp, string destination)
    {
        // File.Replace preserves the destination's ACLs and is the
        // strongest primitive Windows offers, but it REQUIRES the
        // destination to exist — hence the fallback for first creation. A
        // single attempt: Place's loop above is what retries it, on the
        // same terms as a failed write.
        if (File.Exists(destination))
            File.Replace(tmp, destination, destinationBackupFileName: null);
        else
            File.Move(tmp, destination);
    }

    private static void MoveOnlyIfAbsent(string tmp, string destination)
    {
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
            // This is success — and it costs none of Place's retry budget,
            // because the exception never escapes this catch.
            File.Delete(tmp);
        }
    }

    private static void RemoveQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
