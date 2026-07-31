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

    [Fact]
    public void SaveWritesSideFilesAndStripsInlineSections()
    {
        var cfg = Write("config.json",
            """
            {"inbox":"C:/in","routes":[{"label":"A","path":"C:/a"}],
             "watch_folders":[{"label":"W","path":"C:/w"}],
             "alert_texts":["URGENT"],
             "label_clients":[{"id":"ACME","destroy_days":30,"next_number":7}]}
            """);
        var c = Config.Load(cfg);          // inline fallback path
        Config.Save(c, cfg);               // migration completes here

        var main = File.ReadAllText(cfg);
        Assert.DoesNotContain("\"routes\"", main);
        Assert.DoesNotContain("\"watch_folders\"", main);
        Assert.DoesNotContain("\"alert_texts\"", main);
        Assert.DoesNotContain("\"label_clients\"", main);
        Assert.Contains("\"destinations_file\"", main);

        Assert.Contains("\"A\"", File.ReadAllText(Path.Combine(_dir, "destinations.json")));
        Assert.Contains("\"W\"", File.ReadAllText(Path.Combine(_dir, "monitored-folders.json")));
        Assert.Contains("URGENT", File.ReadAllText(Path.Combine(_dir, "alerts.json")));
        Assert.Contains("\"ACME\"", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));

        // and the split files load back identically
        var back = Config.Load(cfg);
        Assert.Equal("A", Assert.Single(back.Routes).Label);
        Assert.Equal(7, Assert.Single(back.LabelClients).NextNumber);
    }

    [Fact]
    public void SaveNeverOverwritesExistingBoxLabels()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("box-labels.json",
            """{"label_clients":[{"id":"REAL","destroy_days":30,"next_number":99}]}""");
        var c = Config.Load(cfg);
        c.LabelClients = new() { new LabelClient { Id = "STALE", NextNumber = 1 } };
        Config.Save(c, cfg);
        Assert.Contains("\"REAL\"", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));
        Assert.DoesNotContain("STALE", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));
    }

    [Fact]
    public void SideFileExtrasSurviveSaveRoundTrip()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("alerts.json", """{"alert_texts":["A"],"admin_note":"keep me"}""");
        var c = Config.Load(cfg);
        Config.Save(c, cfg);
        Assert.Contains("keep me", File.ReadAllText(Path.Combine(_dir, "alerts.json")));
    }

    [Fact]
    public void FirstRunCreatesAllFiveFiles()
    {
        var cfg = Path.Combine(_dir, "fresh", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        Config.Load(cfg);   // first-run: creates defaults
        foreach (var f in new[] { "config.json", "destinations.json",
                 "monitored-folders.json", "alerts.json", "box-labels.json" })
            Assert.True(File.Exists(Path.Combine(_dir, "fresh", f)), f);
    }

    [Fact]
    public void TrySaveNamesTheFailingSideFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        var dests = Path.Combine(_dir, "destinations.json");
        File.WriteAllText(dests, "{}");
        using var hold = new FileStream(dests, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var c = new Config();
        var ok = Config.TrySave(c, cfg, out var error);
        Assert.False(ok);
        Assert.Contains("destinations.json", error);
    }

    [Fact]
    public void TrySaveCreatesTheConfigDirectory()
    {
        var cfgPath = Path.Combine(_dir, "brand-new", "config.json");
        var ok = Config.TrySave(new Config(), cfgPath, out var error);
        Assert.True(ok, error);
        Assert.True(File.Exists(cfgPath));
        Assert.True(File.Exists(Path.Combine(_dir, "brand-new", "destinations.json")));
    }
}
