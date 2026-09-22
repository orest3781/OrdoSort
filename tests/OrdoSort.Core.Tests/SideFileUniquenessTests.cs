namespace OrdoSort.Core.Tests;

/// <summary>QC-08 (2026-08-21 audit): every Save fully re-serializes
/// config.json, never a read-modify-write — so a box_labels_file pointed at
/// config.json itself would have the settings write silently erase the label
/// counters (and BoxLabelStore's write erase the settings). Save/TrySave must
/// refuse that before any write runs; Load must not (2026-08-21 audit D2: a
/// config problem that blocks startup leaves the user no in-app recovery).
/// With destinations, monitored folders and alerts back inside config.json,
/// box-labels.json is the only side file left to collide.</summary>
public class SideFileUniquenessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordouniq_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private string Write(string name, string json)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void SaveThrowsWhenBoxLabelsPointAtTheConfigItself()
    {
        var cfg = new Config { BoxLabelsFile = "config.json" };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("box_labels_file", ex.Message);
    }

    [Fact]
    public void TrySaveReportsCollisionAsFailureInsteadOfThrowing()
    {
        var cfg = new Config { BoxLabelsFile = "config.json" };
        var ok = Config.TrySave(cfg, ConfigPath, out var error);
        Assert.False(ok);
        Assert.Contains("box_labels_file", error);
    }

    /// <summary>The assertion that actually pins the data loss: a
    /// pre-existing config.json must survive a refused Save
    /// byte-for-byte.</summary>
    [Fact]
    public void PreExistingConfigIsUnchangedAfterARefusedSave()
    {
        const string original = """{"inbox":"C:/in","box_labels_file":"config.json"}""";
        Write("config.json", original);

        var cfg = new Config { BoxLabelsFile = "config.json" };
        cfg.Routes.Add(new Route { Label = "NEW", Path = "C:/n" });

        Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));

        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    /// <summary>Resolution has to run BEFORE comparison, or a compare of the
    /// raw strings ("./config.json" != "config.json") would miss this.</summary>
    [Fact]
    public void DifferentSpellingsOfTheSamePathAreCaught()
    {
        var cfg = new Config { BoxLabelsFile = "./config.json" };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("box_labels_file", ex.Message);
    }

    /// <summary>Path.GetFullPath never changes the case you typed, but
    /// Windows names a file the same regardless of case. Only
    /// PathIdentity's case-insensitive compare catches this pair.</summary>
    [Fact]
    public void DifferentCaseSpellingsOfTheSamePathAreCaught()
    {
        var cfg = new Config { BoxLabelsFile = "CONFIG.JSON" };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("box_labels_file", ex.Message);
    }

    /// <summary>A collision is a structural property of the configured
    /// path, not a transient I/O failure — but it is not a confinement
    /// escape either, and refusedSideFileKeys' contract (see TrySave's own
    /// doc comment) is specifically about confinement. Left empty here
    /// deliberately: a caller that suppresses repeat toasts using this list
    /// must not lump a collision in with what that list actually means.</summary>
    [Fact]
    public void TrySaveReportsNoRefusedKeysForACollision()
    {
        var cfg = new Config { BoxLabelsFile = "config.json" };
        var ok = Config.TrySave(cfg, ConfigPath, out var error, out var refusedKeys);
        Assert.False(ok);
        Assert.Contains("box_labels_file", error);
        Assert.Empty(refusedKeys);
    }

    /// <summary>Load must let the app start even with a collision baked into
    /// config.json already (a hand edit). Save/TrySave refusing is what
    /// prevents the data loss; this is only the "make it visible instead of
    /// losing it silently" half.</summary>
    [Fact]
    public void LoadDoesNotThrowOnACollisionAndSurfacesItAsAWarning()
    {
        Write("config.json", """{"inbox":"C:/in","box_labels_file":"config.json"}""");

        var cfg = Config.Load(ConfigPath);   // must not throw

        Assert.NotNull(cfg.SideFileCollisionWarning);
        Assert.Contains("box_labels_file", cfg.SideFileCollisionWarning);
    }

    [Fact]
    public void LoadHasNoCollisionWarningForTheDefaultBoxLabelsFile()
    {
        Write("config.json", """{"inbox":"C:/in"}""");
        var cfg = Config.Load(ConfigPath);
        Assert.Null(cfg.SideFileCollisionWarning);
    }
}
