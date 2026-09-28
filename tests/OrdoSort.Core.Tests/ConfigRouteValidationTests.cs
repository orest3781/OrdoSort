using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>DW-34: <see cref="Config.ValidateRoute"/> and
/// <see cref="Config.ProbeWritable"/> decide what a route button says when a
/// destination can't be used. Only the "blank" and "relative path" branches
/// were tested; a regression in the others would have shown a user a working
/// destination button for a folder that files nothing.</summary>
public class ConfigRouteValidationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ARouteThatPointsAtAFileIsReportedAsNotAFolder()
    {
        var file = _dir.File("Filed.pdf");

        Assert.Equal($"destination is not a folder: {file}",
            Config.ValidateRoute(new Route { Path = file }, configPath: null));
    }

    [Fact]
    public void ARouteToAMissingFolderIsReportedAsMissing()
    {
        var missing = Path.Combine(_dir.Path, "Gone");

        Assert.Equal($"destination does not exist: {missing}",
            Config.ValidateRoute(new Route { Path = missing }, configPath: null));
    }

    [Fact]
    public void AWritableFolderPassesAndTheProbeLeavesNothingBehind()
    {
        var folder = _dir.Dir("Filed");

        Assert.Equal("", Config.ValidateRoute(new Route { Path = folder }, configPath: null));
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    /// <summary>The folder can vanish (a share drops) between the existence
    /// check and the probe; the probe's own failure must come back as a
    /// readable "not writable", never an exception.</summary>
    [Fact]
    public void AProbeThatCannotWriteIsReportedAsNotWritable()
    {
        var missing = Path.Combine(_dir.Path, "Dropped");

        var problem = Config.ProbeWritable(missing);

        Assert.StartsWith("destination not writable: ", problem);
        Assert.Contains("Dropped", problem);
    }
}
