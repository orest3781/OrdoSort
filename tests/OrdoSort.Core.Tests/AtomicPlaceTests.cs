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
public class AtomicPlaceTests : IDisposable
{
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
}
