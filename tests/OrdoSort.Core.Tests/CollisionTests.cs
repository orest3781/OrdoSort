namespace OrdoSort.Core.Tests;

public class CollisionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "collisiontest_" + Guid.NewGuid());
    public CollisionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Touch(string path) => File.WriteAllText(path, "x");

    [Fact]
    public void FreeFileReturnsUnchangedWhenNothingIsThere()
    {
        var target = Path.Combine(_dir, "report.pdf");
        Assert.Equal(target, Collision.FreeFile(target));
    }

    [Fact]
    public void FreeFileAppendsCounterWhenTargetExists()
    {
        var target = Path.Combine(_dir, "report.pdf");
        Touch(target);
        Assert.Equal(Path.Combine(_dir, "report (2).pdf"), Collision.FreeFile(target));
    }

    [Fact]
    public void FreeFileAdvancesPastACounterThatIsAlsoTaken()
    {
        var target = Path.Combine(_dir, "report.pdf");
        Touch(target);
        Touch(Path.Combine(_dir, "report (2).pdf"));
        Assert.Equal(Path.Combine(_dir, "report (3).pdf"), Collision.FreeFile(target));
    }

    [Fact]
    public void FreeFilePreservesTheExtension()
    {
        var target = Path.Combine(_dir, "scan.tif");
        Touch(target);
        Assert.Equal(Path.Combine(_dir, "scan (2).tif"), Collision.FreeFile(target));
    }

    [Fact]
    public void FreeDirectoryReturnsUnchangedWhenNothingIsThere()
    {
        var target = Path.Combine(_dir, "batch");
        Assert.Equal(target, Collision.FreeDirectory(target));
    }

    [Fact]
    public void FreeDirectoryAppendsCounterWhenTargetExists()
    {
        var target = Path.Combine(_dir, "batch");
        Directory.CreateDirectory(target);
        Assert.Equal(Path.Combine(_dir, "batch (2)"), Collision.FreeDirectory(target));
    }

    [Fact]
    public void FreeDirectoryAdvancesPastACounterThatIsAlsoTaken()
    {
        var target = Path.Combine(_dir, "batch");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(_dir, "batch (2)"));
        Assert.Equal(Path.Combine(_dir, "batch (3)"), Collision.FreeDirectory(target));
    }

    /// <summary>DW-17: filing into a folder that already held "SMITH.pdf",
    /// "SMITH (2).pdf" … asked the share about each number in turn, one
    /// round trip per taken name. The counter now lists the folder once, at
    /// the first taken name, and answers every further number from that
    /// list; a name claimed after the listing is caught by the move's own
    /// last check (CommitSkipFileTests' race tests).</summary>
    [Fact]
    public void TakenNamesAreAnsweredFromOneListingOfTheFolder()
    {
        Touch(Path.Combine(_dir, "SMITH.pdf"));
        Touch(Path.Combine(_dir, "SMITH (2).pdf"));
        Directory.CreateDirectory(Path.Combine(_dir, "SMITH (3).pdf"));
        var taken = Collision.TakenIn(_dir);

        Assert.True(taken("SMITH.pdf"));
        Assert.True(taken("smith (2).pdf"));
        Assert.True(taken("SMITH (3).pdf"));   // a folder counts (DW-18)
        Touch(Path.Combine(_dir, "SMITH (4).pdf"));
        Assert.False(taken("SMITH (4).pdf"));  // from the listing, not a fresh trip
    }

    /// <summary>A free first name costs one check and lists nothing.</summary>
    [Fact]
    public void AFreeFirstNameIsNotTaken()
    {
        var taken = Collision.TakenIn(_dir);
        Assert.False(taken("JONES.pdf"));
        Touch(Path.Combine(_dir, "JONES (2).pdf"));
        Assert.True(taken("JONES (2).pdf"));   // nothing listed yet, so asked directly
    }
}
