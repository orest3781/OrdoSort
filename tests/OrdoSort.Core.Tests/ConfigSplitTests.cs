namespace OrdoSort.Core.Tests;

/// <summary>One config file plus box-labels.json: destinations, monitored
/// folders and alerts live in config.json; box labels (and their running box
/// numbers) live in their own file, which wins over any inline copy.</summary>
/// <summary>In the AtomicPlace seam collection because
/// BootstrapLeavesAPeerCreatedBoxLabelsFileIntact assigns
/// AtomicPlace.BeforeAttempt — see AtomicPlaceTests.Name for why every
/// setter of that single process-wide field must be serialised against the
/// others.</summary>
[Collection(AtomicPlaceTests.Name)]
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
    public void SectionsAreReadFromConfigJson()
    {
        var cfg = Write("config.json",
            """
            {"inbox":"C:/in","routes":[{"label":"R","path":"C:/r"}],
             "alert_texts":["URGENT"],"watch_folders":[{"label":"W","path":"C:/w"}]}
            """);
        var c = Config.Load(cfg);
        Assert.Equal("R", Assert.Single(c.Routes).Label);
        Assert.Equal(new[] { "URGENT" }, c.AlertTexts);
        Assert.Equal("W", Assert.Single(c.WatchFolders).Label);
    }

    [Fact]
    public void OldSideFilesAreIgnored()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","routes":[{"label":"INLINE","path":"C:/x"}],"destinations_file":"destinations.json"}""");
        Write("destinations.json", """{"routes":[{"label":"SIDE","path":"C:/y"}]}""");
        Write("monitored-folders.json", """{"watch_folders":[{"label":"SIDE","path":"C:/w"}]}""");
        Write("alerts.json", "{ not json — never read, so never an error");
        var c = Config.Load(cfg);
        Assert.Equal("INLINE", Assert.Single(c.Routes).Label);
        Assert.Empty(c.WatchFolders);
        Assert.Empty(c.AlertTexts);
    }

    [Fact]
    public void RetiredSideFileKeysAreDroppedOnSave()
    {
        var cfg = Write("config.json",
            """
            {"inbox":"C:/in","destinations_file":"d.json","monitored_folders_file":"m.json",
             "alerts_file":"a.json","admin_note":"keep me"}
            """);
        Config.Save(Config.Load(cfg), cfg);
        var main = File.ReadAllText(cfg);
        Assert.DoesNotContain("destinations_file", main);
        Assert.DoesNotContain("monitored_folders_file", main);
        Assert.DoesNotContain("alerts_file", main);
        Assert.Contains("keep me", main);   // other unknown keys still round-trip
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
    public void BoxLabelsFileWinsOverInline()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","label_clients":[{"id":"INLINE"}]}""");
        Write("box-labels.json", """{"label_clients":[{"id":"SIDE"}],"custom_top":"kept"}""");
        var c = Config.Load(cfg);
        Assert.Equal("SIDE", Assert.Single(c.LabelClients).Id);
        Assert.True(c.BoxLabelsFileExtras.ContainsKey("custom_top"));
    }

    [Fact]
    public void BrokenBoxLabelsFileNamesTheFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("box-labels.json", "{ not json");
        var ex = Assert.Throws<ConfigException>(() => Config.Load(cfg));
        Assert.Contains("box-labels.json", ex.Message);
    }

    [Fact]
    public void BoxLabelsFileNullsNormalizeLikeInline()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("box-labels.json", """{"label_clients":[null,{"id":null}]}""");
        var c = Config.Load(cfg);
        var client = Assert.Single(c.LabelClients);   // null entry dropped
        Assert.Equal("", client.Id);                  // null field defaulted
        Assert.NotNull(client.Extras);
    }

    [Fact]
    public void RelativeBoxLabelsPathResolvesBesideConfig()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "shared")).FullName;
        File.WriteAllText(Path.Combine(sub, "team-labels.json"),
            """{"label_clients":[{"id":"TEAM"}]}""");
        var cfg = Write("config.json",
            """{"inbox":"C:/in","box_labels_file":"shared/team-labels.json"}""");
        var c = Config.Load(cfg);
        Assert.Equal("TEAM", Assert.Single(c.LabelClients).Id);
        Assert.Equal(Path.Combine(_dir, "x.json"),
            Config.ResolveBeside(cfg, "x.json"));
    }

    [Fact]
    public void SaveKeepsSectionsInConfigJsonAndMovesBoxLabelsOut()
    {
        var cfg = Write("config.json",
            """
            {"inbox":"C:/in","routes":[{"label":"A","path":"C:/a"}],
             "watch_folders":[{"label":"W","path":"C:/w"}],
             "alert_texts":["URGENT"],
             "label_clients":[{"id":"ACME","destroy_days":30,"next_number":7}]}
            """);
        var c = Config.Load(cfg);          // inline box labels: legacy fallback
        Config.Save(c, cfg);

        var main = File.ReadAllText(cfg);
        Assert.Contains("\"A\"", main);
        Assert.Contains("\"W\"", main);
        Assert.Contains("URGENT", main);
        Assert.DoesNotContain("\"label_clients\"", main);
        Assert.Contains("\"ACME\"", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));
        foreach (var gone in new[] { "destinations.json", "monitored-folders.json", "alerts.json" })
            Assert.False(File.Exists(Path.Combine(_dir, gone)), gone);

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

    /// <summary>The bootstrap race finding 1 describes: box-labels.json does
    /// not exist when Config.Save's `if (!File.Exists(labels))` guard runs
    /// (Station A, first save on a fresh shared folder), but a peer
    /// (Station B's BoxLabelStore.Mutate, or another station's own bootstrap)
    /// creates the file with real counters in the gap between that guard and
    /// the write landing. The old WriteAtomic re-checked File.Exists inside
    /// its own retry loop and switched to File.Replace the instant it saw the
    /// peer's file — waiting out the peer's lock and then clobbering its
    /// counters. AtomicPlace.BeforeAttempt simulates the peer landing in
    /// that exact gap, deterministically, instead of racing real threads.
    /// The peer's content must survive; A's stale in-memory snapshot must
    /// not land.</summary>
    [Fact]
    public void BootstrapLeavesAPeerCreatedBoxLabelsFileIntact()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        var labelsPath = Path.Combine(_dir, "box-labels.json");
        Assert.False(File.Exists(labelsPath));   // the guard must see "missing"

        var c = Config.Load(cfg);
        c.LabelClients = new() { new LabelClient { Id = "STALE", NextNumber = 1 } };

        const string peerContent =
            """{"label_clients":[{"id":"REAL","destroy_days":30,"next_number":99}]}""";
        // Filtered by path: xUnit runs other test classes' Config.Save calls
        // concurrently, and this hook is process-wide, so an unrelated
        // bootstrap for some OTHER test's box-labels.json must no-op here
        // rather than racing to write ours. The path guard is why this class
        // can share the seam; the [Collection] on it is why two classes can't
        // clobber each other's ASSIGNMENT — a different problem, see
        // AtomicPlaceTests.Name.
        //
        // Attempt 0 because a peer winning the race is resolved on the
        // first attempt, without spending any retry budget: a peer holding
        // the destination is the answer, not something to wait out. (A
        // genuinely transient failure here, unlike this one, does retry —
        // see AtomicPlace.TryCreateNew's doc comment.)
        AtomicPlace.BeforeAttempt = (destination, attempt) =>
        {
            if (destination == labelsPath && attempt == 0)
                File.WriteAllText(labelsPath, peerContent);
        };
        try
        {
            Config.Save(c, cfg);   // must not throw, must not clobber the peer's write
        }
        finally
        {
            AtomicPlace.BeforeAttempt = null;
        }

        var finalContent = File.ReadAllText(labelsPath);
        Assert.Contains("\"REAL\"", finalContent);
        Assert.DoesNotContain("STALE", finalContent);
    }

    [Fact]
    public void ReadSharedSectionsReadsOnlyConfigJsonEvenWhileBoxLabelsIsLocked()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","routes":[{"label":"R","path":"C:/r"}],"alert_texts":["URGENT"]}""");
        var labels = Write("box-labels.json", """{"label_clients":[]}""");
        // a peer printing labels: BoxLabelStore holds the file exclusively
        using var hold = new FileStream(labels, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var s = Config.ReadSharedSections(cfg)!;

        Assert.Equal("R", Assert.Single(s.Routes).Label);
        Assert.Equal(new[] { "URGENT" }, s.AlertTexts);
        Assert.NotNull(s.RoutesJson);
        Assert.Null(s.WatchFoldersJson);   // absent key: no section text
    }

    [Fact]
    public void ReadSharedSectionsIsNullWhenConfigJsonIsMissing()
    {
        Assert.Null(Config.ReadSharedSections(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void ReadSharedSectionsReportsADuplicateKeyAsAConfigException()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in","theme":"auto","theme":"dark"}""");
        var ex = Assert.Throws<ConfigException>(() => Config.ReadSharedSections(cfg));
        Assert.Contains("written twice", ex.Message);
    }

    [Fact]
    public void FirstRunCreatesConfigAndBoxLabelsOnly()
    {
        var cfg = Path.Combine(_dir, "fresh", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        Config.Load(cfg);   // first-run: creates defaults
        var created = Directory.GetFiles(Path.Combine(_dir, "fresh"))
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "box-labels.json", "config.json" }, created);
    }

    [Fact]
    public void TrySaveNamesTheFailingFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        using var hold = new FileStream(cfg, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var ok = Config.TrySave(new Config(), cfg, out var error);
        Assert.False(ok);
        Assert.Contains("config.json", error);
    }

    [Fact]
    public void TrySaveCreatesTheConfigDirectory()
    {
        var cfgPath = Path.Combine(_dir, "brand-new", "config.json");
        var ok = Config.TrySave(new Config(), cfgPath, out var error);
        Assert.True(ok, error);
        Assert.True(File.Exists(cfgPath));
        Assert.True(File.Exists(Path.Combine(_dir, "brand-new", "box-labels.json")));
    }
}
