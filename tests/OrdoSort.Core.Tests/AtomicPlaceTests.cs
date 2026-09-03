using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>The placement module the three temp-then-move call sites now
/// share. Real files here, unlike PathIdentityTests: placement IS filesystem
/// work — File.Replace's atomicity and File.Move's refusal-to-overwrite are
/// the things under test, and faking them would test the fake.
///
/// The two properties worth protecting, both of which cost real money before
/// they were fixed: a reader never sees a half-written destination, and a
/// failed write never destroys what was already there.</summary>
[Collection(AtomicPlaceTests.Name)]
public class AtomicPlaceTests : IDisposable
{
    /// <summary>Every class that ASSIGNS AtomicPlace.BeforeAttempt shares this
    /// collection, because the seam is a single process-wide field and xUnit
    /// runs classes in parallel: two setters clobber each other and the loser
    /// silently observes nothing.
    ///
    /// A path guard is not enough. It stops a hook from acting on a write it
    /// doesn't own — which is why the field carries the destination — but it
    /// cannot stop one class's assignment replacing another's. That is a
    /// distinction the seams this replaced never had to make: each of them
    /// had exactly one setter class, and Config.OnRetryForTests' own doc
    /// comment said so. Collapsing them into one shared seam is what created
    /// the hazard, and it showed up immediately — one class's reader was
    /// never released, the other counted 48 attempts instead of 50.</summary>
    public const string Name = "AtomicPlace seam collection";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_place_" + Guid.NewGuid());

    public AtomicPlaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AtomicPlace.BeforeAttempt = null;                        // process-wide seam — never leak it to another test
        AtomicPlace.Sleep = (_, delayMs) => Thread.Sleep(delayMs);   // ditto — restore the real sleep
        AtomicPlace.BeforeSweep = null;                          // ditto — never leak a sweep hook either
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Dest(string name) => Path.Combine(_dir, name);

    private static Action<string> Writes(string content) => tmp => File.WriteAllText(tmp, content);

    private string[] StrayTempFiles() =>
        Directory.GetFiles(_dir, "*.tmp").Select(Path.GetFileName).ToArray()!;

    /// <summary>A plausible crash-orphaned temp name for <paramref
    /// name="destinationFileName"/> — the exact shape Place itself stamps
    /// out, built the same way AtomicPlace.IsOwnTempFileName expects.</summary>
    private static string OrphanNameFor(string destinationFileName) =>
        $"{destinationFileName}.{Guid.NewGuid():N}.tmp";

    // ------------------------------------------------------------- replace

    [Fact]
    public void ReplaceCreatesTheDestinationWhenNothingIsThere()
    {
        var dest = Dest("config.json");

        Assert.True(AtomicPlace.TryReplace(dest, Writes("new"), out var error));

        Assert.Equal("", error);
        Assert.Equal("new", File.ReadAllText(dest));
        Assert.Empty(StrayTempFiles());
    }

    [Fact]
    public void ReplaceSwapsOutWhatWasAlreadyThere()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");

        Assert.True(AtomicPlace.TryReplace(dest, Writes("new"), out _));

        Assert.Equal("new", File.ReadAllText(dest));
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>The guarantee the whole module exists for: a writer that dies
    /// partway leaves the previous file exactly as it was. The old
    /// File.WriteAllText truncated in place, so this is what "bricked every
    /// station until someone fixed it by hand" looked like.</summary>
    [Fact]
    public void AFailedWriteLeavesTheExistingFileUntouched()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");

        var ok = AtomicPlace.TryReplace(dest, _ => throw new IOException("disk full"), out var error);

        Assert.False(ok);
        Assert.Contains("disk full", error);
        Assert.Equal("old", File.ReadAllText(dest));   // untouched
        Assert.Empty(StrayTempFiles());                // and nothing stranded
    }

    [Fact]
    public void AFailedWriteLeavesNoDestinationBehindWhenThereWasNoneToStartWith()
    {
        var dest = Dest("config.json");

        Assert.False(AtomicPlace.TryReplace(dest, _ => throw new IOException("nope"), out _));

        Assert.False(File.Exists(dest));
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>Proves the retry loop is what makes the write land, not luck:
    /// the reader is released from inside the seam on a specific attempt, so
    /// an implementation that tried once would fail this deterministically.</summary>
    [Fact]
    public void ReplaceRidesOutADestinationHeldOpenBriefly()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");

        // No FileShare.Delete: File.Replace genuinely cannot proceed while
        // this handle is open, which is exactly Config.Load's read shape.
        var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var attemptsSeen = new List<int>();
        AtomicPlace.BeforeAttempt = (path, attempt) =>
        {
            if (path != dest) return;   // process-wide seam; ignore other tests' writes
            attemptsSeen.Add(attempt);
            if (attempt == 2) holder.Dispose();
        };

        Assert.True(AtomicPlace.TryReplace(dest, Writes("new"), out _));

        Assert.Equal("new", File.ReadAllText(dest));
        Assert.True(attemptsSeen.Count > 1, "it should have taken more than one attempt to land");
    }

    [Fact]
    public void ReplaceGivesUpAfterTheBudgetAndSaysSoWithoutLosingTheOldFile()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");

        using var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var attempts = 0;
        AtomicPlace.BeforeAttempt = (path, _) => { if (path == dest) attempts++; };

        Assert.False(AtomicPlace.TryReplace(dest, Writes("new"), out var error));

        Assert.NotEqual("", error);
        Assert.Equal(AtomicPlace.Attempts, attempts);
        Assert.Equal("old", File.ReadAllText(dest));   // the point: still intact
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>The whole point of moving writeTemp inside the retry loop:
    /// a transient failure while WRITING must get the same second chance a
    /// transient failure during the move always has. Before this, nothing
    /// exercised it — Place called writeTemp exactly once, so a write that
    /// failed on its first try had no second one to succeed on.</summary>
    [Fact]
    public void ReplaceRidesOutATransientWriteFailure()
    {
        var dest = Dest("config.json");
        var calls = 0;

        var ok = AtomicPlace.TryReplace(dest, tmp =>
        {
            calls++;
            if (calls <= 2) throw new IOException("network stall");
            File.WriteAllText(tmp, "new");
        }, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("new", File.ReadAllText(dest));
        Assert.Equal(3, calls);   // failed twice, landed on the third
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>A retried write must not inherit a partial file. This
    /// delegate appends instead of truncating — the shape a stream opened
    /// the wrong way could take — so it would land "partialpartial" if a
    /// failed attempt's bytes were still sitting at tmp when the next
    /// attempt began instead of Place clearing them first.</summary>
    [Fact]
    public void ARetriedWriteNeverBuildsOnAPreviousAttemptsPartialBytes()
    {
        var dest = Dest("config.json");
        var calls = 0;

        Assert.True(AtomicPlace.TryReplace(dest, tmp =>
        {
            calls++;
            File.AppendAllText(tmp, "partial");
            if (calls == 1) throw new IOException("network stall");
        }, out _));

        Assert.Equal("partial", File.ReadAllText(dest));   // not "partialpartial"
    }

    /// <summary>Mirrors ReplaceGivesUpAfterTheBudgetAndSaysSoWithoutLosingTheOldFile
    /// above, but for the write instead of the move — proof that the write
    /// now shares the SAME budget rather than getting a single, unretried
    /// shot the way it used to.</summary>
    [Fact]
    public void AWriteThatKeepsFailingSpendsTheFullRetryBudgetBeforeGivingUp()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");
        var attempts = 0;
        AtomicPlace.BeforeAttempt = (path, _) => { if (path == dest) attempts++; };

        var ok = AtomicPlace.TryReplace(dest, _ => throw new IOException("network stall"), out var error);

        Assert.False(ok);
        Assert.NotEqual("", error);
        Assert.Equal(AtomicPlace.Attempts, attempts);
        Assert.Equal("old", File.ReadAllText(dest));   // the point: still intact
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>The regression this guards against: Place used to re-run
    /// writeTemp on every attempt, even once the write had already landed
    /// and only the MOVE needed retrying. Harmless for Config's
    /// File.WriteAllText, but Zipper's writeTemp rebuilds the whole archive
    /// and PdfMerge's re-saves the document — expensive at best, and for
    /// PdfMerge a second Save on the same instance was never proven safe to
    /// call at all. Once the write has succeeded, a move-only retry must
    /// reuse it.</summary>
    [Fact]
    public void AMovePhaseRetryDoesNotRerunTheWrite()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");

        var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var attemptsSeen = new List<int>();
        AtomicPlace.BeforeAttempt = (path, attempt) =>
        {
            if (path != dest) return;
            attemptsSeen.Add(attempt);
            if (attempt == 2) holder.Dispose();
        };
        var writeCalls = 0;

        Assert.True(AtomicPlace.TryReplace(dest, tmp =>
        {
            writeCalls++;
            File.WriteAllText(tmp, "new");
        }, out _));

        Assert.Equal("new", File.ReadAllText(dest));
        Assert.True(attemptsSeen.Count > 1, "it should have taken more than one attempt to land");
        Assert.Equal(1, writeCalls);   // the move retried; the write did not
    }

    /// <summary>The compound case the File.Exists(tmp) fallback exists to
    /// handle, and the exact shape that breaks if wroteTemp is only ever
    /// set true and never reset: a write succeeds, the move then fails and
    /// (as far as Place can tell) consumes tmp, forcing a genuine rewrite —
    /// and THAT rewrite fails partway, leaving known, truncated bytes
    /// behind. AMovePhaseRetryDoesNotRerunTheWrite locks dest, not tmp, so
    /// it never drives this branch; this test forces it directly by
    /// deleting the captured tmp path from inside BeforeAttempt, standing
    /// in for a move that failed and took tmp with it. Revert-proof against
    /// the missing reset: without it, the next attempt sees a stale
    /// wroteTemp==true together with the truncated leftovers now sitting at
    /// tmp, skips the rewrite, and moves the truncated bytes onto the
    /// destination — the exact "bricked every station" failure this module
    /// exists to prevent.</summary>
    [Fact]
    public void ARewriteTriggeredByAConsumedTempNeverLandsATruncatedFile()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");

        var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? tmp = null;
        AtomicPlace.BeforeAttempt = (path, attempt) =>
        {
            if (path != dest) return;
            if (attempt == 1 && tmp is not null) File.Delete(tmp);   // stand in for a move that consumed it
            if (attempt == 2) holder.Dispose();                       // let the eventual good write land
        };
        var calls = 0;

        Assert.True(AtomicPlace.TryReplace(dest, t =>
        {
            tmp = t;
            calls++;
            if (calls == 2)
            {
                File.WriteAllText(t, "TRUNCATED");
                throw new IOException("network stall mid-write");
            }
            File.WriteAllText(t, "good");
        }, out _));

        Assert.Equal("good", File.ReadAllText(dest));   // never the truncated attempt
        Assert.Equal(3, calls);   // succeeded, forced to rewrite, failed partway, rewrote again
    }

    // ---------------------------------------------------------- create-new

    [Fact]
    public void CreateNewWritesWhenNothingIsThere()
    {
        var dest = Dest("box-labels.json");

        Assert.True(AtomicPlace.TryCreateNew(dest, Writes("mine"), out _));

        Assert.Equal("mine", File.ReadAllText(dest));
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>2026-08 audit finding 1, as a test. A peer creating the file
    /// inside the gap between a caller's File.Exists guard and this write is
    /// SUCCESS — their counters stand. The old code waited out the peer's
    /// lock and then replaced freshly written counters with a stale snapshot,
    /// so a box number already printed on a physical box got reissued.</summary>
    [Fact]
    public void CreateNewDefersToAPeerThatGotThereFirstAndCallsItSuccess()
    {
        var dest = Dest("box-labels.json");
        AtomicPlace.BeforeAttempt = (path, _) =>
        {
            if (path == dest) File.WriteAllText(dest, "the peer's counters");
        };

        Assert.True(AtomicPlace.TryCreateNew(dest, Writes("my stale snapshot"), out var error));

        Assert.Equal("", error);
        Assert.Equal("the peer's counters", File.ReadAllText(dest));   // never overwritten
        Assert.Empty(StrayTempFiles());
    }

    /// <summary>A peer holding the destination is the answer, not a transient
    /// condition to wait out — so this must not spend the retry budget
    /// discovering that. (Named for that one case, not for create-only as a
    /// whole: CreateNewRidesOutATransientMoveFailureThatIsNotAPeerWinning,
    /// below, is the same method retrying for a different reason.)</summary>
    [Fact]
    public void CreateNewSpendsNoRetryBudgetWhenAPeerWins()
    {
        var dest = Dest("box-labels.json");
        File.WriteAllText(dest, "already here");
        var attempts = 0;
        AtomicPlace.BeforeAttempt = (path, _) => { if (path == dest) attempts++; };

        Assert.True(AtomicPlace.TryCreateNew(dest, Writes("mine"), out _));

        Assert.Equal(1, attempts);
        Assert.Equal("already here", File.ReadAllText(dest));
    }

    /// <summary>TryCreateNew's doc comment promises a transient failure that
    /// ISN'T a peer winning gets the same retry TryReplace gets. Nothing
    /// pinned that before this test: every create-only test passed even if
    /// this path reverted to single-shot. Blocks the rename itself — there
    /// is no destination yet for a peer to hold — by holding tmp open
    /// without FileShare.Delete, the mechanism
    /// ReplaceRidesOutADestinationHeldOpenBriefly uses on the destination.</summary>
    [Fact]
    public void CreateNewRidesOutATransientMoveFailureThatIsNotAPeerWinning()
    {
        var dest = Dest("box-labels.json");
        FileStream? holder = null;
        var attemptsSeen = new List<int>();
        AtomicPlace.BeforeAttempt = (path, attempt) =>
        {
            if (path != dest) return;
            attemptsSeen.Add(attempt);
            if (attempt == 2) holder?.Dispose();
        };

        Assert.True(AtomicPlace.TryCreateNew(dest, tmp =>
        {
            File.WriteAllText(tmp, "mine");
            holder = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read);
        }, out var error));

        Assert.Equal("", error);
        Assert.Equal("mine", File.ReadAllText(dest));
        Assert.True(attemptsSeen.Count > 1, "it should have taken more than one attempt to land");
    }

    // ------------------------------------------------------- the temp file

    [Fact]
    public void TheTempIsASiblingOfTheDestinationSoTheMoveStaysWithinOneVolume()
    {
        var dest = Dest("config.json");
        string? seen = null;

        Assert.True(AtomicPlace.TryReplace(dest, tmp => { seen = tmp; File.WriteAllText(tmp, "x"); }, out _));

        Assert.Equal(Path.GetDirectoryName(dest), Path.GetDirectoryName(seen));
        Assert.EndsWith(".tmp", seen);
    }

    /// <summary>2026-08 audit finding 4a: a fixed "&lt;file&gt;.tmp" meant two
    /// stations saving the same file shared one temp name, so one could
    /// install the other's bytes or find its own temp deleted mid-retry.</summary>
    [Fact]
    public void TwoPlacementsOfTheSameDestinationNeverShareATempName()
    {
        var dest = Dest("config.json");
        var names = new List<string>();
        Action<string> record = tmp => { names.Add(tmp); File.WriteAllText(tmp, "x"); };

        Assert.True(AtomicPlace.TryReplace(dest, record, out _));
        Assert.True(AtomicPlace.TryReplace(dest, record, out _));

        Assert.Equal(2, names.Distinct().Count());
    }

    // ------------------------------------------------------- retry delay

    /// <summary>Escalation, not the old flat 10ms. Pinned so a
    /// "simplification" that flattens the ramp back out fails loudly instead
    /// of quietly becoming a worse budget nobody notices.</summary>
    [Fact]
    public void TheRetryDelayEscalatesWithTheAttemptIndex()
    {
        Assert.Equal(10, AtomicPlace.DelayMs(0));
        Assert.Equal(20, AtomicPlace.DelayMs(1));
        Assert.True(AtomicPlace.DelayMs(10) > AtomicPlace.DelayMs(1),
            "later attempts must wait longer than early ones");
    }

    /// <summary>The escalation caps rather than growing without bound, and
    /// the full budget lands in the 2-3 second range ShellViewModel's
    /// synchronous save can afford — see AtomicPlace.Attempts.</summary>
    [Fact]
    public void TheFullRetryBudgetTotalsTwoToThreeSeconds()
    {
        var totalMs = Enumerable.Range(0, AtomicPlace.Attempts - 1).Sum(AtomicPlace.DelayMs);

        Assert.InRange(totalMs, 2000, 3000);
    }

    /// <summary>DelayMs is pinned as a pure function
    /// (TheRetryDelayEscalatesWithTheAttemptIndex) and its sum is pinned
    /// (TheFullRetryBudgetTotalsTwoToThreeSeconds), but nothing observed the
    /// loop actually calling it before this: mutate the production
    /// Sleep(destination, DelayMs(attempt)) to Thread.Sleep(10) and the
    /// whole suite still passed. No wall-clock assertion — this records
    /// what the loop asks the clock for, not how long it actually took.
    ///
    /// Filtered by destination like every BeforeAttempt test here, and for
    /// the identical reason: Sleep is process-wide, and xUnit runs other
    /// test classes' placements concurrently. An earlier version of this
    /// test recorded through an unfiltered Action&lt;int&gt; and picked up
    /// sleeps from whichever other class's retry happened to land in the
    /// same window — passed alone, failed under parallel load.</summary>
    [Fact]
    public void TheLoopSleepsForExactlyWhatDelayMsComputesEachAttempt()
    {
        var dest = Dest("config.json");
        File.WriteAllText(dest, "old");
        using var holder = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
        var slept = new List<int>();
        AtomicPlace.Sleep = (path, ms) => { if (path == dest) slept.Add(ms); };

        Assert.False(AtomicPlace.TryReplace(dest, Writes("new"), out _));

        var expected = Enumerable.Range(0, AtomicPlace.Attempts - 1).Select(AtomicPlace.DelayMs);
        Assert.Equal(expected, slept);
    }

    // -------------------------------------------------- stale-temp sweep

    /// <summary>Pins AtomicPlace.IsOwnTempFileName's own doc comment case by
    /// case, including the two claims that matter most: Unlock's
    /// "&lt;stem&gt;.unlocking.tmp" can never match, for any stem — not just
    /// the obvious length mismatch, but even a stem manufactured so the
    /// total length coincidentally lines up with this destination's pattern
    /// length, because the 32 characters immediately before ".tmp" would
    /// still end in the literal "unlocking" and 'g' is not a hex digit — and
    /// a sibling destination's own temp in the same directory never matches
    /// either.</summary>
    [Theory]
    [InlineData("config.json.0123456789abcdef0123456789abcdef.tmp", "config.json", true)]
    [InlineData("config.json.0123456789ABCDEF0123456789ABCDEF.tmp", "config.json", true)]
    [InlineData("config.json", "config.json", false)]                          // the destination itself
    [InlineData("config.json.tmp", "config.json", false)]                      // .tmp but no GUID at all
    [InlineData("destinations.json.0123456789abcdef0123456789abcdef.tmp", "config.json", false)]  // a sibling destination's own temp
    [InlineData("config.unlocking.tmp", "config.json", false)]                 // Unlock's real shape for a "config.json" source
    [InlineData("config.json.unlocking.tmp", "config.json", false)]            // shaped to look closer; still not ours
    [InlineData("config.json.01234567890123456789abcunlocking.tmp", "config.json", false)]  // 32 chars, right length, tail is literally "unlocking"
    [InlineData("config.json.0123456789abcdef0123456789abcde.tmp", "config.json", false)]   // 31 hex chars, one short
    [InlineData("config.json.0123456789abcdef0123456789abcdefa.tmp", "config.json", false)] // 33 hex chars, one too many
    [InlineData("config.json.0123456789abcdef0123456789abcdeg.tmp", "config.json", false)]  // 32 chars, but 'g' is not hex
    public void SweepPatternMatchesOnlyThisDestinationsOwnGuidTempName(
        string fileName, string destinationFileName, bool expected)
    {
        Assert.Equal(expected, AtomicPlace.IsOwnTempFileName(fileName, destinationFileName));
    }

    /// <summary>The fact the whole feature exists to establish: nothing
    /// before this ever swept a crash-orphaned temp, so on a share used for
    /// months they simply accumulated beside the real file. This is the
    /// ordinary case — genuinely old, genuinely stranded — where deleting it
    /// is correct.</summary>
    [Fact]
    public void SweepDeletesAnOrphanOlderThanTheStaleThreshold()
    {
        var dest = Dest("config.json");
        var orphan = Dest(OrphanNameFor("config.json"));
        File.WriteAllText(orphan, "stranded by a crash");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - AtomicPlace.StaleTempAge - TimeSpan.FromMinutes(1));

        Assert.True(AtomicPlace.TryReplace(dest, Writes("new"), out _));

        Assert.False(File.Exists(orphan));
        Assert.Equal("new", File.ReadAllText(dest));   // the destination itself is what actually matters here
    }

    /// <summary>The single most important fact in this file: another station
    /// can be mid-write RIGHT NOW, and its temp is byte-for-byte
    /// indistinguishable from an orphan except by age. A temp under the
    /// stale threshold must survive even though its name matches the
    /// pattern exactly — this is what stops a sweep from ever breaking a
    /// peer's in-flight save.</summary>
    [Fact]
    public void SweepLeavesARecentOrphanAloneBecauseItMightBeAPeerMidWrite()
    {
        var dest = Dest("config.json");
        var recent = Dest(OrphanNameFor("config.json"));
        File.WriteAllText(recent, "maybe a peer, mid-write");   // fresh LastWriteTimeUtc: right now

        Assert.True(AtomicPlace.TryReplace(dest, Writes("new"), out _));

        Assert.True(File.Exists(recent), "a temp under the stale threshold must never be swept");
        Assert.Equal("new", File.ReadAllText(dest));
    }

    /// <summary>Two more files that sit right beside a genuinely stale orphan
    /// and are just as old, but must survive anyway: a .tmp that never had
    /// the GUID shape at all, and another destination's own temp in the very
    /// same directory — config.json and destinations.json really do live
    /// side by side (see Config.Save). Both prove the PATTERN match, not
    /// just the age check, is what gates every delete.</summary>
    [Fact]
    public void SweepLeavesANonMatchingTmpAndASiblingDestinationsTempAlone()
    {
        var dest = Dest("config.json");
        var staleUtc = DateTime.UtcNow - AtomicPlace.StaleTempAge - TimeSpan.FromMinutes(1);

        var notGuidShaped = Dest("config.json.tmp");
        File.WriteAllText(notGuidShaped, "not ours to touch");
        File.SetLastWriteTimeUtc(notGuidShaped, staleUtc);

        var siblingsTemp = Dest(OrphanNameFor("destinations.json"));
        File.WriteAllText(siblingsTemp, "destinations.json's own orphan");
        File.SetLastWriteTimeUtc(siblingsTemp, staleUtc);

        Assert.True(AtomicPlace.TryReplace(dest, Writes("new"), out _));

        Assert.True(File.Exists(notGuidShaped));
        Assert.True(File.Exists(siblingsTemp));
    }

    /// <summary>Config saves happen often — tool windows persist their own
    /// state — and the sweep is a directory enumeration: a network round
    /// trip on a share. Paying that more than once per destination per
    /// process would be waste for no benefit, since orphans only ever appear
    /// on a crash: whatever the first sweep found is everything there was to
    /// find.</summary>
    [Fact]
    public void SweepRunsOnlyOnceForTheSameDestinationEvenAcrossMultipleSaves()
    {
        var dest = Dest("config.json");
        var staleUtc = DateTime.UtcNow - AtomicPlace.StaleTempAge - TimeSpan.FromMinutes(1);

        var firstOrphan = Dest(OrphanNameFor("config.json"));
        File.WriteAllText(firstOrphan, "old #1");
        File.SetLastWriteTimeUtc(firstOrphan, staleUtc);

        Assert.True(AtomicPlace.TryReplace(dest, Writes("first"), out _));
        Assert.False(File.Exists(firstOrphan));   // swept on this, the first save for this destination

        var secondOrphan = Dest(OrphanNameFor("config.json"));
        File.WriteAllText(secondOrphan, "old #2");
        File.SetLastWriteTimeUtc(secondOrphan, staleUtc);

        Assert.True(AtomicPlace.TryReplace(dest, Writes("second"), out _));
        Assert.True(File.Exists(secondOrphan),
            "the sweep already ran once for this destination in this process; it must not run a second time");
    }

    /// <summary>Best-effort and silent, like RemoveQuietly: a sweep that
    /// fails — the share drops mid-enumeration, antivirus holds a file open —
    /// must never turn a successful save into a reported failure.</summary>
    [Fact]
    public void ASweepThatThrowsDoesNotFailTheSave()
    {
        var dest = Dest("config.json");
        AtomicPlace.BeforeSweep = path =>
        {
            if (path == dest) throw new InvalidOperationException("share dropped mid-enumeration");
        };

        var ok = AtomicPlace.TryReplace(dest, Writes("new"), out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("new", File.ReadAllText(dest));
    }
}

/// <summary>Declares the collection <see cref="AtomicPlaceTests.Name"/> names.
/// No ICollectionFixture: each member builds and tears down its own temp root
/// and nulls the seam in Dispose — nothing needs to be built once and shared.
/// Same reasoning as UnlockThresholdCollection.</summary>
[CollectionDefinition(AtomicPlaceTests.Name)]
public class AtomicPlaceSeamCollection
{
}

/// <summary>Pins the mechanism, not the timing. Like
/// UnlockThresholdTestCollectionMembershipTests: a race on a single field
/// assignment can't be forced on demand, so a timing-based regression test
/// would either always pass or be flaky. What CAN be asserted is the thing
/// the fix actually relies on — that every class assigning
/// AtomicPlace.BeforeAttempt declares the same [Collection] name.
///
/// Add a class to this list when it starts assigning the seam. A grep for
/// <c>AtomicPlace.BeforeAttempt =</c> across tests/ confirms the list below
/// is the complete set.</summary>
public class AtomicPlaceSeamMembershipTests
{
    private static string? CollectionNameOf(Type t) =>
        t.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Xunit.CollectionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    [Fact]
    public void EverySeamSetterSharesOneCollection()
    {
        var setters = new[]
        {
            typeof(AtomicPlaceTests),
            typeof(AtomicWriteTests),
            typeof(ConfigSplitTests),
            typeof(ZipperTests),
        };

        var names = setters.Select(CollectionNameOf).ToArray();

        Assert.All(names, n => Assert.Equal(AtomicPlaceTests.Name, n));
    }
}
