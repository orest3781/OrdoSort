namespace OrdoSort.Core.Tests;

/// <summary>Dashboard sections: the watch-folder `section` key and its ride
/// through the sweep.</summary>
public class SectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordosection_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SectionRoundTripsThroughMonitoredFoldersJson()
    {
        var path = Path.Combine(_dir, "config.json");
        var cfg = new Config();
        cfg.WatchFolders.Add(new WatchFolder { Label = "A", Path = "C:/a", Section = "Failed queues" });
        cfg.WatchFolders.Add(new WatchFolder { Label = "B", Path = "C:/b" });
        Config.Save(cfg, path);
        Assert.Contains("Failed queues",
            File.ReadAllText(Path.Combine(_dir, "monitored-folders.json")));
        var back = Config.Load(path);
        Assert.Equal("Failed queues", back.WatchFolders[0].Section);
        Assert.Equal("", back.WatchFolders[1].Section);   // omitted -> ""
    }

    [Fact]
    public void SweepCarriesTheSectionIntoTheStatus()
    {
        var folder = Path.Combine(_dir, "watch");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "doc.pdf"), "x");
        var w = new WatchFolder { Label = "W", Path = folder, Section = "Incoming" };
        var status = Assert.Single(FolderMonitor.All(new List<WatchFolder> { w }, new List<string>()));
        Assert.Equal("Incoming", status.Section);
    }
}
