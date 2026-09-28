using OrdoSort.TestSupport;

namespace OrdoSort.Core.Tests;

/// <summary>"It's open in another program" told people nothing they could act
/// on: the file was often held by Explorer's preview, not anything they had
/// opened. Windows knows which program holds a file, and says so.</summary>
public sealed class FileHoldersTests
{
    [Fact]
    public void AFileHeldOpenNamesTheProgramHoldingIt()
    {
        using var dir = new TempDir();
        var path = dir.File("held.pdf");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var holders = FileHolders.Of(path);

            var me = Assert.Single(holders, h => h.ProcessId == Environment.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(me.Name));
        }
    }

    [Fact]
    public void AFileNobodyHoldsHasNoHolders()
    {
        using var dir = new TempDir();
        var path = dir.File("free.pdf");

        Assert.Empty(FileHolders.Of(path));
    }

    [Fact]
    public void AMissingFileHasNoHoldersAndDoesNotThrow()
    {
        using var dir = new TempDir();

        Assert.Empty(FileHolders.Of(Path.Combine(dir.Path, "missing.pdf")));
    }
}
