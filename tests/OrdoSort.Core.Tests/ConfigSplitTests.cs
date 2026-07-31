namespace OrdoSort.Core.Tests;

/// <summary>Split config: side files win, inline is the legacy fallback,
/// and a broken side file fails naming that file.</summary>
public class ConfigSplitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordosplit_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string json)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void SideFileWinsOverInline()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","routes":[{"label":"INLINE","path":"C:/x"}]}""");
        Write("destinations.json",
            """{"routes":[{"label":"SIDE","path":"C:/y"}],"custom_top":"kept"}""");
        var c = Config.Load(cfg);
        var r = Assert.Single(c.Routes);
        Assert.Equal("SIDE", r.Label);
        Assert.True(c.DestinationsFileExtras.ContainsKey("custom_top"));
    }

    [Fact]
    public void InlineIsUsedWhenSideFileMissing()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","alert_texts":["URGENT"],"watch_folders":[{"label":"W","path":"C:/w"}]}""");
        var c = Config.Load(cfg);
        Assert.Equal(new[] { "URGENT" }, c.AlertTexts);
        Assert.Equal("W", Assert.Single(c.WatchFolders).Label);
    }

    [Fact]
    public void MissingEverythingIsEmpty()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        var c = Config.Load(cfg);
        Assert.Empty(c.Routes);
        Assert.Empty(c.WatchFolders);
        Assert.Empty(c.AlertTexts);
        Assert.Empty(c.LabelClients);
    }

    [Fact]
    public void BrokenSideFileNamesTheFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("alerts.json", "{ not json");
        var ex = Assert.Throws<ConfigException>(() => Config.Load(cfg));
        Assert.Contains("alerts.json", ex.Message);
    }

    [Fact]
    public void SideFileNullsNormalizeLikeInline()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("destinations.json",
            """{"routes":[null,{"label":null,"path":"C:/a"}]}""");
        var c = Config.Load(cfg);
        var r = Assert.Single(c.Routes);          // null entry dropped
        Assert.Equal("", r.Label);                // null field defaulted
        Assert.NotNull(r.Extras);
    }

    [Fact]
    public void RelativeSectionPathResolvesBesideConfig()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "shared")).FullName;
        File.WriteAllText(Path.Combine(sub, "team-dests.json"),
            """{"routes":[{"label":"TEAM","path":"C:/t"}]}""");
        var cfg = Write("config.json",
            """{"inbox":"C:/in","destinations_file":"shared/team-dests.json"}""");
        var c = Config.Load(cfg);
        Assert.Equal("TEAM", Assert.Single(c.Routes).Label);
        Assert.Equal(Path.Combine(_dir, "x.json"),
            Config.ResolveBeside(cfg, "x.json"));
    }
}
