namespace OrdoSort.Core.Tests;

/// <summary>QC-08 (2026-08-21 audit): destinations_file, monitored_folders_file,
/// alerts_file and box_labels_file each get a full re-serialization on every
/// Save (WriteDoc -&gt; WriteJson -&gt; WriteAtomic), never a read-modify-write —
/// so pointing two of them at the same file makes the second write silently
/// erase what the first one just wrote. Save/TrySave must refuse a collision
/// before any of the four writes run; Load must not (2026-08-21 audit D2:
/// a config problem that blocks startup leaves the user no in-app recovery).</summary>
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
    public void SaveThrowsNamingBothCollidingKeys()
    {
        var cfg = new Config { MonitoredFoldersFile = "destinations.json" };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.Contains("monitored_folders_file", ex.Message);
    }

    [Fact]
    public void TrySaveReportsCollisionAsFailureInsteadOfThrowing()
    {
        var cfg = new Config { MonitoredFoldersFile = "destinations.json" };
        var ok = Config.TrySave(cfg, ConfigPath, out var error);
        Assert.False(ok);
        Assert.Contains("destinations_file", error);
        Assert.Contains("monitored_folders_file", error);
    }

    /// <summary>The assertion that actually pins the data loss (brief's own
    /// words: "the good red phase"). A pre-existing destinations.json must
    /// survive a refused Save byte-for-byte — against unfixed code this
    /// fails by showing the routes actually destroyed, overwritten with the
    /// watch_folders shape from the second write.</summary>
    [Fact]
    public void PreExistingDestinationsFileIsUnchangedAfterARefusedSave()
    {
        const string original = """{"routes":[{"label":"REAL","path":"C:/y"}]}""";
        var destPath = Write("destinations.json", original);

        var cfg = new Config { MonitoredFoldersFile = "destinations.json" };
        cfg.WatchFolders.Add(new WatchFolder { Label = "W", Path = "C:/w" });

        Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));

        Assert.Equal(original, File.ReadAllText(destPath));
    }

    /// <summary>The brief's own example — resolution has to run BEFORE
    /// comparison, or a hand-rolled compare of the raw, unresolved strings
    /// ("./destinations.json" != "destinations.json") would miss this.</summary>
    [Fact]
    public void DifferentSpellingsOfTheSamePathAreCaught()
    {
        var cfg = new Config
        {
            DestinationsFile = "./destinations.json",
            MonitoredFoldersFile = "destinations.json",
        };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.Contains("monitored_folders_file", ex.Message);
    }

    /// <summary>Path.GetFullPath (inside ResolveBesideForWrite) already
    /// collapses "./" on its own, so the test above would pass even against
    /// a post-resolution raw `==`. THIS is the pair that only PathIdentity's
    /// ordinal-case-INSENSITIVE compare catches — Windows names a file the
    /// same regardless of case, but GetFullPath never changes the case you
    /// typed. Without PathIdentity.Same this collision would slip through.</summary>
    [Fact]
    public void DifferentCaseSpellingsOfTheSamePathAreCaught()
    {
        var cfg = new Config
        {
            DestinationsFile = "destinations.json",
            MonitoredFoldersFile = "DESTINATIONS.JSON",
        };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("destinations_file", ex.Message);
        Assert.Contains("monitored_folders_file", ex.Message);
    }

    /// <summary>Scope note in the brief: box_labels_file's own write is
    /// gated on !File.Exists, so it doesn't share destinations_file's
    /// guaranteed-loss shape — but four keys naming three files is never
    /// intended, so it must still be refused, not just the other three.</summary>
    [Fact]
    public void BoxLabelsFileCollidingWithAnotherKeyIsAlsoCaught()
    {
        var cfg = new Config { BoxLabelsFile = "alerts.json" };
        var ex = Assert.Throws<ConfigException>(() => Config.Save(cfg, ConfigPath));
        Assert.Contains("alerts_file", ex.Message);
        Assert.Contains("box_labels_file", ex.Message);
    }

    /// <summary>A collision is a structural property of the configured
    /// paths, not a transient I/O failure — but it is not a confinement
    /// escape either, and refusedSideFileKeys' contract (see TrySave's own
    /// doc comment) is specifically about confinement. Left empty here
    /// deliberately: a caller that suppresses repeat toasts using this list
    /// must not lump a collision in with what that list actually means.</summary>
    [Fact]
    public void TrySaveReportsNoRefusedKeysForACollision()
    {
        var cfg = new Config { MonitoredFoldersFile = "destinations.json" };
        var ok = Config.TrySave(cfg, ConfigPath, out var error, out var refusedKeys);
        Assert.False(ok);
        Assert.Contains("destinations_file", error);
        Assert.Empty(refusedKeys);
    }

    /// <summary>The controller ruling this task was amended by: Load must
    /// let the app start even with a collision baked into config.json
    /// already (a hand edit, or a config saved before this fix existed).
    /// Save/TrySave refusing is what prevents the data loss; this is only
    /// the "make it visible instead of losing it silently" half.</summary>
    [Fact]
    public void LoadDoesNotThrowOnACollisionAndSurfacesItAsAWarning()
    {
        Write("config.json", """{"inbox":"C:/in","monitored_folders_file":"destinations.json"}""");
        Write("destinations.json", """{"routes":[{"label":"REAL","path":"C:/y"}]}""");

        var cfg = Config.Load(ConfigPath);   // must not throw

        Assert.NotNull(cfg.SideFileCollisionWarning);
        Assert.Contains("destinations_file", cfg.SideFileCollisionWarning);
        Assert.Contains("monitored_folders_file", cfg.SideFileCollisionWarning);
    }

    [Fact]
    public void LoadHasNoCollisionWarningWhenAllFourKeysAreDistinct()
    {
        Write("config.json", """{"inbox":"C:/in"}""");
        var cfg = Config.Load(ConfigPath);
        Assert.Null(cfg.SideFileCollisionWarning);
    }
}
