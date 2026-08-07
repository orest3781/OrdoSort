using System.Text.Json;
using OrdoSort.Core;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-07 audit, Task 1b: <c>Config.TrySaveMain</c> (writes only
/// config.json, never the four side files) was wired into
/// <c>ShellViewModel.SaveSavedPasswordsNow</c> ONLY — see that method's doc
/// comment. <c>SaveConfigNow</c> and <c>ApplySettingsAsync</c> still call the
/// full <c>Config.TrySave</c>, which independently attempts every side file
/// on every call; a side-file key whose configured path resolves outside the
/// config's own directory (<c>Config.ResolveBesideForWrite</c>'s confinement
/// check, 2026-08 audit 4.2[A]) — reachable in the wild via the Settings
/// "Data files" Browse... buttons before this fix, or a hand-edited/legacy
/// config afterward — is refused on EVERY future write. Before this fix,
/// that turned every subsequent SaveConfigNow/ApplySettingsAsync call at
/// that station into a fresh "settings not saved" dialog, forever, with no
/// actionable next step short of hand-editing config.json.
///
/// <see cref="SideFileConfinementDoesNotBrickRepeatedOrdinarySaves"/> proves
/// the fix: the FIRST ordinary save after the config already carries an
/// unwritable side-file path is still told about it (once), but a SECOND,
/// unrelated ordinary save neither repeats the dialog nor fails to persist
/// its own (unrelated) change — the "bricked forever" symptom is gone. This
/// fails against the pre-fix ShellViewModel/Config (reverted): the old
/// unconditional <c>_dialogs.Warn</c> fires on both calls, so
/// <c>Assert.Single(fx.Dialogs.Warnings)</c> sees 2, not 1, after the second
/// save.
///
/// <see cref="AnOrdinarySettingsSaveWithNormalRelativePathsStillWritesAllThreeSideFiles"/>
/// is the trap the brief calls out by name: the tempting blind fix — swap
/// SaveConfigNow/ApplySettingsAsync to <c>Config.TrySaveMain</c> the same
/// way SaveSavedPasswordsNow was fixed — stops persisting a user's
/// destination, monitored-folder, and alert edits entirely, in the ORDINARY
/// case where nothing is even broken. This is a regression guard for that:
/// it cannot fail against the pre-2026-08-07 baseline (ordinary persistence
/// was never broken there), only against a WRONG fix — see
/// task-1b-report.md for a deliberate run against that wrong fix, proving
/// this test is not a tautology.</summary>
public class SideFileConfinementBrickingTests
{
    [Fact]
    public void SideFileConfinementDoesNotBrickRepeatedOrdinarySaves()
    {
        var outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ordo_outside_" + Guid.NewGuid())).FullName;
        try
        {
            // A destinations_file that already resolves outside the config
            // directory — exactly what a pre-fix Browse... could hand back,
            // or what a hand-edited/legacy config carries forward untouched.
            var outsideDestinations = Path.Combine(outsideDir, "destinations.json");
            using var fx = new ShellFixture(cfg => cfg.DestinationsFile = outsideDestinations);
            fx.Shell.Initialize();

            // First ordinary save at this station: a plain main-section
            // field edit (the header-bar tile-visibility dropdown) — nothing
            // to do with destinations_file at all.
            fx.Shell.TileVisibilityIndex = 1;   // "all"

            Assert.Single(fx.Dialogs.Warnings);
            Assert.Contains("destinations_file", fx.Dialogs.Warnings[0].Message);

            var afterFirst = Config.Load(fx.CfgPath, createIfMissing: false);
            Assert.Equal("all", afterFirst.TileVisibility);   // the ordinary save's own change landed

            // A second, different ordinary save — same still-broken
            // destinations_file. Without the fix this shows a second,
            // identical "settings not saved" dialog every time, forever;
            // with it, the station already knows, and the new change still
            // lands regardless.
            fx.Shell.TileVisibilityIndex = 2;   // "hidden"

            Assert.Single(fx.Dialogs.Warnings);   // still just the one — not bricked

            var afterSecond = Config.Load(fx.CfgPath, createIfMissing: false);
            Assert.Equal("hidden", afterSecond.TileVisibility);   // the second save's change ALSO landed
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AnOrdinarySettingsSaveWithNormalRelativePathsStillWritesAllThreeSideFiles()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json + the three side files now exist on disk

        var fresh = fx.Shell.FreshConfigForSettings();
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
        mine.Routes.Add(new Route { Label = "NewRoute", Path = fx.RouteDir, Color = "#123456" });
        mine.WatchFolders.Add(new WatchFolder { Label = "NewWatch", Path = fx.Deferred });
        mine.AlertTexts.Add("URGENT");

        fx.Shell.ApplySettings(mine);

        Assert.Empty(fx.Dialogs.Warnings);   // an ordinary save with nothing broken: no warning at all

        var destinations = File.ReadAllText(Path.Combine(fx.Dir, "destinations.json"));
        Assert.Contains("NewRoute", destinations);
        var monitoredFolders = File.ReadAllText(Path.Combine(fx.Dir, "monitored-folders.json"));
        Assert.Contains("NewWatch", monitoredFolders);
        var alerts = File.ReadAllText(Path.Combine(fx.Dir, "alerts.json"));
        Assert.Contains("URGENT", alerts);
    }

    /// <summary>Final review, Important 1 (2026-08-07): the once-per-session
    /// suppression this class otherwise proves correct for <c>SaveConfigNow</c>
    /// is WRONG for <c>ApplySettingsAsync</c> — whose entire job is
    /// persisting Routes/WatchFolders/AlertTexts to these same three side
    /// files. Before the fix, a station that had already been warned once
    /// this session (a background save, exactly like the first half of
    /// <see cref="SideFileConfinementDoesNotBrickRepeatedOrdinarySaves"/>
    /// above) would then open Settings, add a route, click OK — and the
    /// write would fail on the very same confinement refusal with NO dialog
    /// at all: the edit lives only in memory, is lost on restart, and no
    /// peer ever sees it. This fails against the pre-fix ShellViewModel
    /// (reverted): <c>fx.Dialogs.Warnings</c> has only the one entry from
    /// the earlier background save; the explicit Settings OK adds nothing,
    /// and <c>Assert.Equal(2, ...)</c> below sees 1.</summary>
    [Fact]
    public void ApplySettingsAlwaysWarnsEvenAfterTheSameRefusalWasAlreadyWarnedOnce()
    {
        var outsideDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ordo_outside_" + Guid.NewGuid())).FullName;
        try
        {
            var outsideDestinations = Path.Combine(outsideDir, "destinations.json");
            using var fx = new ShellFixture(cfg => cfg.DestinationsFile = outsideDestinations);
            fx.Shell.Initialize();

            // Warm the once-per-session suppression exactly the way a real
            // station would: an unrelated background save.
            fx.Shell.TileVisibilityIndex = 1;   // "all"
            Assert.Single(fx.Dialogs.Warnings);
            Assert.Contains("destinations_file", fx.Dialogs.Warnings[0].Message);

            // Now drive an explicit Settings OK: the user opens Settings and
            // adds a route. This save is refused for the exact same
            // still-broken destinations_file — it must be reported, not
            // silently eaten by the suppression above.
            var fresh = fx.Shell.FreshConfigForSettings();
            var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fresh))!;
            mine.Routes.Add(new Route { Label = "NewRoute", Path = fx.RouteDir, Color = "#123456" });

            fx.Shell.ApplySettings(mine);

            Assert.Equal(2, fx.Dialogs.Warnings.Count);   // the explicit save reported its own failure too
            Assert.Contains("destinations_file", fx.Dialogs.Warnings[1].Message);
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { /* best effort */ }
        }
    }
}
