using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Final review, Important 1 (2026-08-06): ApplySettingsAsync used
/// to save <c>cfg.SavedPasswords</c> verbatim — whatever this station's
/// Settings window loaded when it OPENED (<c>FreshConfigForSettings</c>).
/// Settings itself never edits that field: SettingsViewModel.TryBuildResult
/// carries the JSON-cloned original's SavedPasswords through untouched (its
/// own comment says so); the Unlock window's "Manage saved…" dialog, and
/// its load-time sweep, are the only things that ever change it, persisting
/// straight to config.json through ShellViewModel.SaveSavedPasswordsNow.
///
/// So a station that opened Settings BEFORE a peer's Unlock window swept
/// legacy plaintext into DPAPI ciphertext, and pressed OK AFTER, would
/// silently overwrite that protection with the plaintext it loaded when it
/// opened — undoing this branch's own safety fix, with no prompt at all.
/// (Unlike the existing peer-edit conflict flow just above this code in
/// ApplySettingsAsync — SnapshotSections / ChangedSectionNames — which only
/// ever fingerprints the three side files, never config.json's main
/// section, so it can't see this change to begin with.) And a peer's next
/// Unlock-window open would re-sweep the resurrected plaintext and fire its
/// "one-time" protected notice all over again — forever.
///
/// These tests prove ApplySettingsAsync now reads SavedPasswords fresh from
/// disk immediately before writing, so neither a peer's protection nor this
/// same station's own just-added password (added through Unlock while
/// Settings happened to be open too) can be reverted by a Settings OK that
/// never touched that field.</summary>
public class ApplySettingsSavedPasswordsTests
{
    [Fact]
    public void APeersPasswordProtectionSweepSurvivesASettingsOkFromBeforeTheSweep()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json must already exist — SaveSavedPasswordsNow (Gap B) never creates it from nothing

        // This station's own plaintext saved password, persisted before
        // Settings ever opens — a hand-edited or legacy entry, exactly what
        // the load-time sweep exists to catch.
        fx.Shell.Cfg.SavedPasswords.Add(new SavedPassword { Label = "Shared", Password = "hunter2" });
        Assert.True(fx.Shell.SaveSavedPasswordsNow());

        // Settings opens: this station's snapshot still carries the
        // plaintext password.
        var loadedForSettings = fx.Shell.FreshConfigForSettings();
        Assert.Equal("hunter2", Assert.Single(loadedForSettings.SavedPasswords).Password);

        // A peer's Unlock window opens WHILE this station's Settings dialog
        // is still open, sweeps the plaintext into DPAPI ciphertext and
        // persists it — reproducing ReprotectLegacyPlaintext + the
        // constructor's load-time save directly against the shared file,
        // the way a second station would actually reach it.
        var peerFresh = Config.Load(fx.CfgPath);
        var peerEntry = Assert.Single(peerFresh.SavedPasswords);
        peerEntry.Password = PasswordVault.Protect(peerEntry.Password);
        Assert.True(Config.TrySave(peerFresh, fx.CfgPath, out var seedError), seedError);
        Assert.True(PasswordVault.IsProtected(Config.Load(fx.CfgPath).SavedPasswords.Single().Password));

        // This station's Settings dialog now presses OK — with the STALE,
        // plaintext-carrying config it loaded before the peer's sweep ran.
        // No field was edited; the JsonSerializer round trip mirrors what
        // SettingsViewModel.TryBuildResult actually does to _original.
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(loadedForSettings))!;
        fx.Shell.ApplySettings(mine);

        // The peer's protection must survive: NOT reverted to plaintext.
        // (This is the assertion that catches the bug: without the fix,
        // ApplySettingsAsync saves cfg.SavedPasswords — "hunter2" — straight
        // over the peer's protected value.)
        var onDisk = Config.Load(fx.CfgPath);
        var savedNow = Assert.Single(onDisk.SavedPasswords);
        Assert.True(PasswordVault.IsProtected(savedNow.Password));
        Assert.Equal("hunter2", PasswordVault.Reveal(savedNow.Password));

        // No peer-edit conflict prompt either: this is a structural
        // guarantee, not something the user has to notice and answer
        // correctly under time pressure the way the three-side-file
        // conflict flow (ConcurrentSettingsEditTests) works.
        Assert.Empty(fx.Dialogs.Confirms);
        Assert.Empty(fx.Dialogs.Warnings);
    }

    [Fact]
    public void ThisStationsOwnPasswordAddedThroughUnlockWhileSettingsWasOpenSurvivesToo()
    {
        // Same mechanism, same-process variant: nothing requires a second
        // station for the bug to bite. If THIS station's Unlock window adds
        // a password after Settings opened but before OK is pressed, the
        // Settings dialog's own snapshot still doesn't have it — Settings
        // and Unlock are two different windows over the same live _cfg, and
        // Settings' _original was captured at open time.
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        fx.Shell.SaveConfigNow();   // config.json must already exist — SaveSavedPasswordsNow (Gap B) never creates it from nothing

        var loadedForSettings = fx.Shell.FreshConfigForSettings();
        Assert.Empty(loadedForSettings.SavedPasswords);

        // The Unlock window's "Manage saved…" add flow, in miniature:
        // protect, append to the live _cfg, persist immediately.
        fx.Shell.Cfg.SavedPasswords.Add(
            new SavedPassword { Label = "New", Password = PasswordVault.Protect("freshly-added") });
        Assert.True(fx.Shell.SaveSavedPasswordsNow());

        // Settings OK, still carrying the empty list it opened with.
        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(loadedForSettings))!;
        mine.WordSeparator = "-";   // a real, unrelated Settings edit rides along
        fx.Shell.ApplySettings(mine);

        var onDisk = Config.Load(fx.CfgPath);
        var saved = Assert.Single(onDisk.SavedPasswords);
        Assert.Equal("New", saved.Label);
        Assert.Equal("freshly-added", PasswordVault.Reveal(saved.Password));
        Assert.Equal("-", onDisk.WordSeparator);          // the real edit still landed
        Assert.Equal("-", fx.Shell.Cfg.WordSeparator);
    }
}
