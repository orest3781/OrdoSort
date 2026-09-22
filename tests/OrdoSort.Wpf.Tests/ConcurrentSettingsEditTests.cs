using System.Text.Json;
using System.Text.Json.Nodes;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Audit finding 2.1: two stations editing Settings concurrently.
/// The second station's OK must not silently overwrite the first station's
/// edit — it must ask, name the section(s) that changed, and (on decline)
/// leave both the peer's on-disk edit AND this station's own in-memory
/// config exactly as they were. The shared sections all live in config.json,
/// so a peer's edit is simulated by rewriting one key of that file.</summary>
public class ConcurrentSettingsEditTests
{
    private static JsonNode PeerRoutes(string label) =>
        new JsonArray(new JsonObject { ["label"] = label, ["path"] = "C:/peer", ["color"] = "#000000" });

    private static JsonNode PeerWatchFolders(string label) =>
        new JsonArray(new JsonObject { ["label"] = label, ["path"] = "C:/peer" });

    [Fact]
    public void PeerEditToDestinationsWhileSettingsIsOpenIsDetectedAndDecliningKeepsThePeersRoutes()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk

        // Settings window opens: this is where the snapshot is taken.
        var fresh = fx.Shell.FreshConfigForSettings();

        // A second station saves its own Settings edit while this station's
        // dialog is still open — behind this app's back, exactly like a real
        // peer on the same network share.
        fx.EditConfigOnDisk("routes", PeerRoutes("PEER"));

        // This station's own (now-stale) edit: a route added on top of what
        // it read when the window opened.
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Dialogs.ConfirmAnswer = false;   // the user declines to clobber the peer's edit
        fx.Shell.ApplySettings(mine);

        // The peer's edit must survive on disk — not silently overwritten.
        var onDisk = Config.Load(fx.CfgPath).Routes.Select(r => r.Label).ToList();
        Assert.Contains("PEER", onDisk);
        Assert.DoesNotContain("MINE", onDisk);

        // The user was asked, and told which section changed.
        var ask = Assert.Single(fx.Dialogs.Confirms);
        Assert.Contains("destinations", ask.Message, StringComparison.OrdinalIgnoreCase);

        // This station's in-memory config was left alone too, so reopening
        // Settings shows current (peer-included) state, not a half-applied mix.
        Assert.DoesNotContain(fx.Shell.Cfg.Routes, r => r.Label == "MINE");
    }

    [Fact]
    public void ConfirmingTheConflictSavesTheUsersEditsOverThePeers()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        var fresh = fx.Shell.FreshConfigForSettings();
        fx.EditConfigOnDisk("routes", PeerRoutes("PEER"));

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Dialogs.ConfirmAnswer = true;   // the user chooses to save anyway
        fx.Shell.ApplySettings(mine);

        Assert.Single(fx.Dialogs.Confirms);
        var onDisk = Config.Load(fx.CfgPath).Routes.Select(r => r.Label).ToList();
        Assert.Contains("MINE", onDisk);
        Assert.DoesNotContain("PEER", onDisk);
        Assert.Contains(fx.Shell.Cfg.Routes, r => r.Label == "MINE");
    }

    [Fact]
    public void NoPeerEditMeansSettingsSaveGoesThroughWithoutPrompting()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        var fresh = fx.Shell.FreshConfigForSettings();
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Shell.ApplySettings(mine);

        Assert.Empty(fx.Dialogs.Confirms);
        Assert.Contains(Config.Load(fx.CfgPath).Routes, r => r.Label == "MINE");
    }

    [Fact]
    public void MultipleChangedSectionsAreAllNamedInThePromptWithNaturalPhrasing()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        var fresh = fx.Shell.FreshConfigForSettings();
        fx.EditConfigOnDisk("routes", PeerRoutes("PEER"));
        fx.EditConfigOnDisk("watch_folders", PeerWatchFolders("PEER"));
        fx.EditConfigOnDisk("alert_texts", new JsonArray("PEER-ALERT"));

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        fx.Dialogs.ConfirmAnswer = false;
        fx.Shell.ApplySettings(mine);

        // All three named, joined the way a sentence needs — not
        // "destinations and monitored folders and alerts".
        var ask = Assert.Single(fx.Dialogs.Confirms);
        Assert.Contains("destinations, monitored folders, and alerts", ask.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARewriteOfConfigThatLeavesTheSectionsUnchangedDoesNotPromptForConflict()
    {
        // SaveConfigNow (the header-bar tile-visibility toggle, remembered
        // match/merge headers, saved Unlock passwords — none of them a
        // Settings edit) rewrites config.json on every call via
        // Config.TrySave. A peer station doing any of that while this
        // station's Settings window happens to be open must not trip the
        // conflict prompt: the three shared sections are compared, not the
        // file's bytes, so an unrelated field changing is not a conflict.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk

        var fresh = fx.Shell.FreshConfigForSettings();

        // Simulate a peer's unrelated SaveConfigNow: config.json is rewritten
        // with a different tile-visibility choice, sections untouched.
        fx.EditConfigOnDisk("tile_visibility", "all");

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.WordSeparator = "-";   // this station's own, unrelated Settings edit

        fx.Shell.ApplySettings(mine);

        Assert.Empty(fx.Dialogs.Confirms);
        Assert.Equal("-", fx.Shell.Cfg.WordSeparator);   // the save still went through
    }

    [Fact]
    public void ASectionConfigDoesNotHoldFingerprintsAsAbsentNotAsChanged()
    {
        // A config.json still in the old split layout (sections in side
        // files, none inline) must not read as a conflict: a section absent
        // at both ends fingerprints as null both times, not as "changed".
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();
        var root = JsonNode.Parse(File.ReadAllText(fx.CfgPath))!.AsObject();
        root.Remove("routes");
        File.WriteAllText(fx.CfgPath, root.ToJsonString());

        var a = fx.Shell.SnapshotSections();
        var b = fx.Shell.SnapshotSections();

        Assert.Equal(a, b);
        Assert.Null(a.Destinations);
    }
}
