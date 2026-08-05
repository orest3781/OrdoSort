using System.Text.Json;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>Audit finding 2.1: two stations editing Settings concurrently.
/// The second station's OK must not silently overwrite the first station's
/// edit — it must ask, name the section(s) that changed, and (on decline)
/// leave both the peer's on-disk edit AND this station's own in-memory
/// config exactly as they were.</summary>
public class ConcurrentSettingsEditTests
{
    private static string PeerDestinations(string label) =>
        $$"""{"routes":[{"label":"{{label}}","path":"C:/peer","color":"#000000"}]}""";

    private static string PeerAlerts(string text) =>
        $$"""{"alert_texts":["{{text}}"]}""";

    [Fact]
    public void PeerEditToDestinationsWhileSettingsIsOpenIsDetectedAndDecliningKeepsThePeersRoutes()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json + destinations.json now exist on disk

        // Settings window opens: this is where the snapshot is taken.
        var fresh = fx.Shell.FreshConfigForSettings();

        // A second station saves its own Settings edit while this station's
        // dialog is still open — behind this app's back, exactly like a real
        // peer on the same network share.
        File.WriteAllText(Path.Combine(fx.Dir, "destinations.json"), PeerDestinations("PEER"));

        // This station's own (now-stale) edit: a route added on top of what
        // it read when the window opened.
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Dialogs.ConfirmAnswer = false;   // the user declines to clobber the peer's edit
        fx.Shell.ApplySettings(mine);

        // The peer's edit must survive on disk — not silently overwritten.
        // (This is the assertion that catches today's bug: without the fix,
        // ApplySettings saves straight over the peer's file.)
        var onDisk = File.ReadAllText(Path.Combine(fx.Dir, "destinations.json"));
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
        File.WriteAllText(Path.Combine(fx.Dir, "destinations.json"), PeerDestinations("PEER"));

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "MINE", Path = fx.RouteDir, Color = "#123456" });

        fx.Dialogs.ConfirmAnswer = true;   // the user chooses to save anyway
        fx.Shell.ApplySettings(mine);

        Assert.Single(fx.Dialogs.Confirms);
        var onDisk = File.ReadAllText(Path.Combine(fx.Dir, "destinations.json"));
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
        var onDisk = File.ReadAllText(Path.Combine(fx.Dir, "destinations.json"));
        Assert.Contains("MINE", onDisk);
    }

    [Fact]
    public void MultipleChangedSectionsAreAllNamedInThePrompt()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        var fresh = fx.Shell.FreshConfigForSettings();
        File.WriteAllText(Path.Combine(fx.Dir, "destinations.json"), PeerDestinations("PEER"));
        File.WriteAllText(Path.Combine(fx.Dir, "alerts.json"), PeerAlerts("PEER-ALERT"));

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        fx.Dialogs.ConfirmAnswer = false;
        fx.Shell.ApplySettings(mine);

        var ask = Assert.Single(fx.Dialogs.Confirms);
        Assert.Contains("destinations", ask.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alerts", ask.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSectionFileFingerprintsAsAbsentNotAsChanged()
    {
        // First-run creation (or a section repointed at a not-yet-created
        // file) must not read as a conflict: a file missing at both ends
        // fingerprints as null both times, not as "changed".
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();
        File.Delete(Path.Combine(fx.Dir, "destinations.json"));

        var a = fx.Shell.SnapshotSections();
        var b = fx.Shell.SnapshotSections();

        Assert.Equal(a, b);
        Assert.Null(a.Destinations);
    }
}
