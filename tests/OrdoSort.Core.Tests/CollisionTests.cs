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
}
