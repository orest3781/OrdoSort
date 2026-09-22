using System.Text.Json;
using System.Text.Json.Nodes;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Audit finding 2.1: two stations editing Settings concurrently.
/// The second station's OK must not silently overwrite the first station's
/// edit — it must ask, name the section(s) that changed, and (on decline)
/// leave both the peer's on-disk edit AND this station's own in-memory
/// config exactly as they were.</summary>
public class ConcurrentSettingsEditTests
{
    /// <summary>A second station saving its own Settings edit to one
    /// section of the shared config.json, behind this app's back — exactly
    /// like a real peer on the same network share.</summary>
    private static void PeerEdit(string cfgPath, string key, JsonNode value)
    {
        var node = JsonNode.Parse(File.ReadAllText(cfgPath))!.AsObject();
        node[key] = value;
        File.WriteAllText(cfgPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonNode PeerRoutes(string label) =>
        JsonNode.Parse($$"""[{"label":"{{label}}","path":"C:/peer","color":"#000000"}]""")!;

    private static JsonNode PeerAlerts(string text) => new JsonArray(text);

    private static JsonNode PeerMonitoredFolders(string label) =>
        JsonNode.Parse($$"""[{"label":"{{label}}","path":"C:/peer"}]""")!;

    private static string RoutesOnDisk(ShellFixture fx) =>
        string.Join(",", Config.Load(fx.CfgPath).Routes.Select(r => r.Label));

    [Fact]
    public void PeerEditToDestinationsWhileSettingsIsOpenIsDetectedAndDecliningKeepsThePeersRoutes()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk

        // Settings window opens: this is where the snapshot is taken.
        var fresh = fx.Shell.FreshConfigForSettings();

        // A second station saves its own Settings edit while this station's
        // dialog is still open.
        PeerEdit(fx.CfgPath, "routes", PeerRoutes("PEER"));

        // This station's own (now-stale) edit: a route added on top of what
        // it read when the window opened.
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Dialogs.ConfirmAnswer = false;   // the user declines to clobber the peer's edit
        fx.Shell.ApplySettings(mine);

        // The peer's edit must survive on disk — not silently overwritten.
        // (This is the assertion that catches today's bug: without the fix,
        // ApplySettings saves straight over the peer's edit.)
        var onDisk = RoutesOnDisk(fx);
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
        PeerEdit(fx.CfgPath, "routes", PeerRoutes("PEER"));

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Dialogs.ConfirmAnswer = true;   // the user chooses to save anyway
        fx.Shell.ApplySettings(mine);

        Assert.Single(fx.Dialogs.Confirms);
        var onDisk = RoutesOnDisk(fx);
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
        Assert.Contains("MINE", RoutesOnDisk(fx));
    }

    [Fact]
    public void MultipleChangedSectionsAreAllNamedInThePromptWithNaturalPhrasing()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        var fresh = fx.Shell.FreshConfigForSettings();
        PeerEdit(fx.CfgPath, "routes", PeerRoutes("PEER"));
        PeerEdit(fx.CfgPath, "watch_folders", PeerMonitoredFolders("PEER"));
        PeerEdit(fx.CfgPath, "alert_texts", PeerAlerts("PEER-ALERT"));

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
    public void APeerRewriteThatLeavesTheSharedSectionsUnchangedDoesNotPrompt()
    {
        // SaveConfigNow (the header-bar tile-visibility toggle, remembered
        // match/merge headers — none of them a Settings edit) rewrites
        // config.json on every call. A peer station doing that while this
        // station's Settings window happens to be open must not trip the
        // conflict prompt: destinations, monitored folders and alerts are
        // unchanged, even though the file's bytes (a different
        // tile_visibility, different formatting) are not.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk

        var fresh = fx.Shell.FreshConfigForSettings();

        var node = JsonNode.Parse(File.ReadAllText(fx.CfgPath))!.AsObject();
        node["tile_visibility"] = "hidden";
        File.WriteAllText(fx.CfgPath, node.ToJsonString());   // compact: every byte moves

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.WordSeparator = "-";   // this station's own, unrelated Settings edit

        fx.Shell.ApplySettings(mine);

        Assert.Empty(fx.Dialogs.Confirms);
        Assert.Equal("-", fx.Shell.Cfg.WordSeparator);   // the save still went through
    }

    [Fact]
    public void MissingSectionFingerprintsAsAbsentNotAsChanged()
    {
        // First-run creation must not read as a conflict: a section missing
        // at both ends fingerprints as null both times, not as "changed".
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();
        var node = JsonNode.Parse(File.ReadAllText(fx.CfgPath))!.AsObject();
        node.Remove("routes");
        File.WriteAllText(fx.CfgPath, node.ToJsonString());

        var a = fx.Shell.SnapshotSections();
        var b = fx.Shell.SnapshotSections();

        Assert.Equal(a, b);
        Assert.Null(a.Destinations);
        Assert.NotNull(a.Alerts);
    }
}
