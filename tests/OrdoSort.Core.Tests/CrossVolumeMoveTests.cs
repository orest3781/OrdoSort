namespace OrdoSort.Core.Tests;

/// <summary>DW-01: across drives or shares, a move is a copy then a delete.
/// Cut off mid-copy (a kill, a power cut, a full disk), it left a partial
/// PDF under the proper filed name; filed again, the real document became
/// "… (2)" beside it and nothing flagged the broken one. The copy now goes
/// to a ".partial" name first and is renamed into place only once whole.
/// Test machines have one drive, so <see cref="Commit.SameVolume"/> is
/// forced false to take the across-drives path.</summary>
[Collection(UndoFailureTests.Name)]
public sealed class CrossVolumeMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoxvol_" + Guid.NewGuid());
    private readonly string _inbox, _deferred;

    public CrossVolumeMoveTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        _deferred = Path.Combine(_root, "deferred");
        foreach (var d in new[] { _inbox, _deferred }) Directory.CreateDirectory(d);
        Commit.SameVolume = (_, _) => false;
    }

    public void Dispose()
    {
        Commit.SameVolume = Commit.OnSameVolume;
        Commit.PartialCopiedHookForTests = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Document(string name, string content)
    {
        var path = Path.Combine(_inbox, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 15, 9, 30, 0, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public void FilingToAnotherDriveLandsWholeAndLeavesNoPartialBehind()
    {
        var src = Document("20240115--111111.pdf", "the whole document");

        var outcome = Commit.SkipFile(src, _deferred);

        Assert.False(File.Exists(src));
        Assert.Equal("the whole document", File.ReadAllText(outcome.NewPath!));
        Assert.Equal(new DateTime(2024, 1, 15, 9, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(outcome.NewPath!));
        Assert.Empty(Directory.GetFiles(_deferred, "*.partial"));
    }

    [Fact]
    public void ACopyCutOffMidwayLeavesNothingUnderTheRealNameAndTheOriginalInPlace()
    {
        var src = Document("20240115--111111.pdf", "the whole document");
        Commit.PartialCopiedHookForTests = _ => throw new IOException("There is not enough space on the disk.");

        Assert.Throws<CommitError>(() => Commit.SkipFile(src, _deferred));

        Assert.Equal("the whole document", File.ReadAllText(src));
        Assert.Empty(Directory.GetFiles(_deferred));
    }

    [Fact]
    public void APartialLeftByACrashIsClearedOnceItIsOldAndAFreshOneIsLeftAlone()
    {
        var stale = Path.Combine(_deferred, $"20240101--000001.pdf.{Guid.NewGuid():N}.partial");
        var fresh = Path.Combine(_deferred, $"20240101--000002.pdf.{Guid.NewGuid():N}.partial");
        var lookalike = Path.Combine(_deferred, "notes.partial");
        foreach (var p in new[] { stale, fresh, lookalike }) File.WriteAllText(p, "half");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(lookalike, DateTime.UtcNow.AddHours(-3));

        Commit.SkipFile(Document("20240115--111111.pdf", "doc"), _deferred);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));       // may be another station's copy in progress
        Assert.True(File.Exists(lookalike));   // not a name OrdoSort makes
    }
}
