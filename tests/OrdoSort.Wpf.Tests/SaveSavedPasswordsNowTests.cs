using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Fix round 1, Important 2: <c>ShellViewModel.SaveSavedPasswordsNow</c>
/// is the save path the Unlock tool's load-time sweep (and its three
/// deliberate-action persist paths) now use instead of the general
/// <c>SaveConfigNow</c>. Opening the Unlock window can trigger a save
/// PASSIVELY (2026-08 audit 4.3[A] follow-up) — before that, reaching any
/// save at all here required a deliberate password edit. <c>SaveConfigNow</c>
/// only refreshes the four SIDE files before writing
/// (<c>RefreshSharedSectionsFromDisk</c>); every other main-section field —
/// Theme, TileVisibility, MergeHeaders, LabelClients, Sounds, and the rest —
/// still comes from this station's own (possibly stale) in-memory copy. That
/// gap is pre-existing and shared with other tools' deliberate saves
/// (untouched here — see <c>SaveSavedPasswordsNow</c>'s doc comment), but a
/// save reachable by just opening a window needs its own, safer contract:
/// these tests prove it re-reads everything else fresh from disk and only
/// ever overwrites <c>SavedPasswords</c> with this station's copy.</summary>
public class SaveSavedPasswordsNowTests
{
    [Fact]
    public void APeersConcurrentEditToTheMainSectionSurvivesAndThisStationsSavedPasswordsStillLands()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json now exists on disk, Theme defaults to "auto"

        // A peer station edits Theme directly on the shared config.json —
        // part of the MAIN section, not one of the four side files
        // RefreshSharedSectionsFromDisk already covers — while this
        // station's own in-memory copy sits unaware, still "auto".
        var peerCopy = Config.Load(fx.CfgPath);
        peerCopy.Theme = "dark";
        Assert.True(Config.TrySave(peerCopy, fx.CfgPath, out var seedError), seedError);
        Assert.Equal("auto", fx.Shell.Cfg.Theme);   // this station's copy is confirmed stale

        // This station's Unlock-tool save path: adds a saved password (the
        // one field SaveSavedPasswordsNow is entitled to change) and
        // persists through the new, narrower method.
        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "X", Password = PasswordVault.Protect("secret") });
        var ok = fx.Shell.SaveSavedPasswordsNow();

        Assert.True(ok);
        var onDisk = Config.Load(fx.CfgPath);
        Assert.Equal("dark", onDisk.Theme);                 // the peer's edit survived
        var saved = Assert.Single(onDisk.SavedPasswords);   // this station's change still landed
        Assert.Equal("X", saved.Label);
        Assert.Equal("secret", PasswordVault.Reveal(saved.Password));
    }

    [Fact]
    public void APeersConcurrentEditToMergeHeadersAndTileVisibilityAlsoSurvives()
    {
        // Theme is one field; this proves the fix isn't special-cased to
        // Theme alone — every main-section field a peer could plausibly
        // touch through a DIFFERENT tool (here, Match & merge's remembered
        // headers, and the header-bar tile-visibility toggle) while this
        // station merely opens Unlock must come through unharmed.
        //
        // LabelClients (box-labels.json) is deliberately NOT exercised
        // here: Config.TrySave only ever bootstrap-writes that file once
        // (BoxLabelStore is its sole writer thereafter — see TrySave's own
        // comment), so it was never reachable through the whole-_cfg
        // overwrite this fix addresses, on either the old or new save path.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        var peerCopy = Config.Load(fx.CfgPath);
        peerCopy.MergeHeaders = new() { ["Name"] = "FullName", ["Id"] = "EmployeeId" };
        peerCopy.TileVisibility = "all";
        Assert.True(Config.TrySave(peerCopy, fx.CfgPath, out var seedError), seedError);

        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "Y", Password = PasswordVault.Protect("hunter2") });
        Assert.True(fx.Shell.SaveSavedPasswordsNow());

        var onDisk = Config.Load(fx.CfgPath);
        Assert.Equal("FullName", onDisk.MergeHeaders["Name"]);
        Assert.Equal("all", onDisk.TileVisibility);
        Assert.Single(onDisk.SavedPasswords);
    }

    [Fact]
    public void ADifferentTypedSaveWhileUnlockIsOpenDoesNotLoseThatEditEither()
    {
        // The reverse direction: SaveSavedPasswordsNow itself must not be
        // the one doing the clobbering. If a peer's edit landed BEFORE this
        // station's own unrelated field (say WordSeparator, set earlier in
        // this same session) was ever written to disk at all, that
        // in-memory-only field is expected to be dropped by this narrow
        // save — it was never persisted, so there is nothing to "preserve"
        // for it; only genuinely on-disk peer state is protected. This
        // guards against a future change accidentally treating the fresh
        // read as authoritative for SavedPasswords too.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "First", Password = PasswordVault.Protect("one") });
        Assert.True(fx.Shell.SaveSavedPasswordsNow());

        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "Second", Password = PasswordVault.Protect("two") });
        Assert.True(fx.Shell.SaveSavedPasswordsNow());

        var onDisk = Config.Load(fx.CfgPath);
        Assert.Equal(2, onDisk.SavedPasswords.Count);
        Assert.Contains(onDisk.SavedPasswords, p => p.Label == "First");
        Assert.Contains(onDisk.SavedPasswords, p => p.Label == "Second");
    }

    [Fact]
    public void ABrokenOnDiskConfigFallsBackToTheWholeCfgSaveRatherThanLosingTheSweep()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();

        // A peer's mid-write, or a bad hand-edit, leaves config.json
        // unparseable right when this station's sweep needs to persist.
        File.WriteAllText(fx.CfgPath, "{ not valid json at all");

        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "X", Password = PasswordVault.Protect("secret") });
        var ok = fx.Shell.SaveSavedPasswordsNow();

        Assert.True(ok);
        var onDisk = Config.Load(fx.CfgPath);
        Assert.Single(onDisk.SavedPasswords);
    }

    [Fact]
    public void AnAbsentConfigAtSaveTimeWritesNothingAndDefaultsNoPeerField()
    {
        // Fix round 2, Gap B: Config.Load's first-run path treats ANY
        // missing file as "create and save a fresh all-defaults Config" —
        // and it does that save itself, silently, before this method would
        // even get to overlay SavedPasswords onto the result. A station
        // that already holds a loaded Config in memory (proven by the fact
        // this method is even reachable) hitting a momentarily-missing
        // file is a share hiccup or a peer's in-flight write, never a
        // genuine first run — so this must write NOTHING, not even a
        // fresh-defaults file, rather than silently wipe every peer's
        // Theme/TileVisibility/MergeHeaders/Sounds/etc.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json exists; a peer-visible baseline

        var peerCopy = Config.Load(fx.CfgPath);
        peerCopy.Theme = "dark";
        Assert.True(Config.TrySave(peerCopy, fx.CfgPath, out var seedError), seedError);

        // The transient-missing window itself: an external delete, a share
        // hiccup, or a peer caught mid-rename on its own atomic write.
        File.Delete(fx.CfgPath);
        Assert.False(File.Exists(fx.CfgPath));

        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "X", Password = PasswordVault.Protect("secret") });
        var ok = fx.Shell.SaveSavedPasswordsNow();

        Assert.False(ok);
        // The strongest possible proof of "nothing was written": the file
        // doesn't exist at all afterward, so no field — peer-visible or
        // otherwise — could have been defaulted onto disk.
        Assert.False(File.Exists(fx.CfgPath));

        var warn = Assert.Single(fx.Dialogs.Warnings);
        Assert.Contains("missing", warn.Message, StringComparison.OrdinalIgnoreCase);
    }
}
