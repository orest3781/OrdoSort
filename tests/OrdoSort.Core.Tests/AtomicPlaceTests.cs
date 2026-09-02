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
        AtomicPlace.BeforeAttempt = null;   // process-wide seam — never leak it to another test
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Dest(string name) => Path.Combine(_dir, name);

    private static Action<string> Writes(string content) => tmp => File.WriteAllText(tmp, content);

    private string[] StrayTempFiles() =>
        Directory.GetFiles(_dir, "*.tmp").Select(Path.GetFileName).ToArray()!;

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
    /// discovering that.</summary>
    [Fact]
    public void CreateNewNeverRetries()
    {
        var dest = Dest("box-labels.json");
        File.WriteAllText(dest, "already here");
        var attempts = 0;
        AtomicPlace.BeforeAttempt = (path, _) => { if (path == dest) attempts++; };

        Assert.True(AtomicPlace.TryCreateNew(dest, Writes("mine"), out _));

        Assert.Equal(1, attempts);
        Assert.Equal("already here", File.ReadAllText(dest));
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
