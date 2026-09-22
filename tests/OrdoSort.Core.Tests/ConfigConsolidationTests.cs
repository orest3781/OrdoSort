namespace OrdoSort.Core.Tests;

/// <summary>One settings file: destinations, monitored folders and alerts
/// live inline in config.json; only box labels keep a file of their own. A
/// config saved in the 2026-07 split layout folds its legacy side files back
/// in at load, and the next save completes the migration.</summary>
/// <summary>In the AtomicPlace seam collection because
/// BootstrapLeavesAPeerCreatedBoxLabelsFileIntact assigns
/// AtomicPlace.BeforeAttempt — see AtomicPlaceTests.Name for why every
/// setter of that single process-wide field must be serialised against the
/// others.</summary>
[Collection(AtomicPlaceTests.Name)]
public class ConfigConsolidationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordoconsolidate_").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string json)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void LegacySideFilesAreFoldedInWhenConfigHasNoInlineSections()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in","destinations_file":"destinations.json"}""");
        Write("destinations.json", """{"routes":[{"label":"SIDE","path":"C:/y"}]}""");
        Write("monitored-folders.json", """{"watch_folders":[{"label":"W","path":"C:/w"}]}""");
        Write("alerts.json", """{"alert_texts":["URGENT"]}""");

        var c = Config.Load(cfg);

        Assert.Equal("SIDE", Assert.Single(c.Routes).Label);
        Assert.Equal("W", Assert.Single(c.WatchFolders).Label);
        Assert.Equal(new[] { "URGENT" }, c.AlertTexts);
        Assert.False(c.Extras.ContainsKey("destinations_file"));
    }

    /// <summary>Once config.json holds a section — even an empty one — it
    /// is the truth, and a leftover legacy file beside it is ignored.
    /// Otherwise emptying a list in Settings would bring the stale legacy
    /// list back at the next start.</summary>
    [Fact]
    public void InlineSectionWinsOverALeftoverLegacyFile()
    {
        var cfg = Write("config.json",
            """{"inbox":"C:/in","routes":[{"label":"INLINE","path":"C:/x"}],"alert_texts":[]}""");
        Write("destinations.json", """{"routes":[{"label":"STALE","path":"C:/y"}]}""");
        Write("alerts.json", """{"alert_texts":["STALE"]}""");

        var c = Config.Load(cfg);

        Assert.Equal("INLINE", Assert.Single(c.Routes).Label);
        Assert.Empty(c.AlertTexts);
    }

    [Fact]
    public void InlineIsUsedWhenNoLegacyFileExists()
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
    public void BrokenLegacyFileNamesTheFile()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in"}""");
        Write("alerts.json", "{ not json");
        var ex = Assert.Throws<ConfigException>(() => Config.Load(cfg));
        Assert.Contains("alerts.json", ex.Message);
    }

    [Fact]
    public void BrokenLegacyFileIsNotReadOnceConfigHoldsTheSection()
    {
        var cfg = Write("config.json", """{"inbox":"C:/in","alert_texts":["A"]}""");
        Write("alerts.json", "{ not json");
        Assert.Equal(new[] { "A" }, Config.Load(cfg).AlertTexts);
    }

    [Fact]
    public void LegacyFileNullsNormalizeLikeInline()
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
    public void RelativeLegacySectionPathResolvesBesideConfig()
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
    public void SaveMigratesTheSplitLayoutIntoOneConfigAndKeepsTheOldFiles()
    {
        var cfg = Write("config.json", """
            {"inbox":"C:/in","destinations_file":"destinations.json",
             "monitored_folders_file":"monitored-folders.json","alerts_file":"alerts.json"}
            """);
        const string legacyDestinations = """{"routes":[{"label":"A","path":"C:/a"}]}""";
        Write("destinations.json", legacyDestinations);
        Write("monitored-folders.json", """{"watch_folders":[{"label":"W","path":"C:/w"}]}""");
        Write("alerts.json", """{"alert_texts":["URGENT"]}""");
        Write("box-labels.json", """{"label_clients":[{"id":"ACME","destroy_days":30,"next_number":7}]}""");

        Config.Save(Config.Load(cfg), cfg);

        var main = File.ReadAllText(cfg);
        Assert.Contains("\"A\"", main);
        Assert.Contains("\"W\"", main);
        Assert.Contains("URGENT", main);
        Assert.DoesNotContain("destinations_file", main);
        Assert.DoesNotContain("monitored_folders_file", main);
        Assert.DoesNotContain("alerts_file", main);
        Assert.DoesNotContain("\"label_clients\"", main);
        // the old files are left exactly as they were, never deleted
        Assert.Equal(legacyDestinations, File.ReadAllText(Path.Combine(_dir, "destinations.json")));

        // and with the old files gone, config.json alone loads back identically
        File.Delete(Path.Combine(_dir, "destinations.json"));
        File.Delete(Path.Combine(_dir, "monitored-folders.json"));
        File.Delete(Path.Combine(_dir, "alerts.json"));
        var back = Config.Load(cfg);
        Assert.Equal("A", Assert.Single(back.Routes).Label);
        Assert.Equal("W", Assert.Single(back.WatchFolders).Label);
        Assert.Equal(new[] { "URGENT" }, back.AlertTexts);
        Assert.Equal(7, Assert.Single(back.LabelClients).NextNumber);
    }

    [Fact]
    public void SaveKeepsSectionsInlineAndBoxLabelsInTheirOwnFile()
    {
        var cfg = Write("config.json",
            """
            {"inbox":"C:/in","routes":[{"label":"A","path":"C:/a"}],
             "watch_folders":[{"label":"W","path":"C:/w"}],
             "alert_texts":["URGENT"],
             "label_clients":[{"id":"ACME","destroy_days":30,"next_number":7}]}
            """);
        Config.Save(Config.Load(cfg), cfg);

        var main = File.ReadAllText(cfg);
        Assert.Contains("\"routes\"", main);
        Assert.Contains("\"watch_folders\"", main);
        Assert.Contains("\"alert_texts\"", main);
        Assert.DoesNotContain("\"label_clients\"", main);
        Assert.Contains("\"ACME\"", File.ReadAllText(Path.Combine(_dir, "box-labels.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "destinations.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "monitored-folders.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "alerts.json")));
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
    public void FirstRunCreatesTheConfigAndTheBoxLabelsFileOnly()
    {
        var fresh = Path.Combine(_dir, "fresh");
        Directory.CreateDirectory(fresh);
        Config.Load(Path.Combine(fresh, "config.json"));   // first-run: creates defaults
        var created = Directory.GetFiles(fresh).Select(Path.GetFileName).Order().ToArray();
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
