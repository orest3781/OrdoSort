using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>A read-only or locked config file must never crash the app —
/// load failures become readable ConfigExceptions, save failures a bool.</summary>
public class ConfigHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordocfg_" + Guid.NewGuid());

    public ConfigHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void LoadWrapsIoErrorsAsConfigException()
    {
        var path = Path.Combine(_dir, "locked.json");
        File.WriteAllText(path, "{}");
        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = Assert.Throws<ConfigException>(() => Config.Load(path));
        Assert.Contains("locked.json", ex.Message);
    }

    [Fact]
    public void TrySaveReportsFailureInsteadOfThrowing()
    {
        var dest = Path.Combine(_dir, "sub", "nope", "config.json");   // missing dirs
        var ok = Config.TrySave(new Config(), dest, out var error);
        Assert.True(ok, error);   // TrySave creates missing directories
        Assert.Equal("", error);
    }

    [Fact]
    public void TrySaveSucceedsNormally()
    {
        var dest = Path.Combine(_dir, "config.json");
        Assert.True(Config.TrySave(new Config(), dest, out var error));
        Assert.Equal("", error);
        Assert.True(File.Exists(dest));
    }

    // -----------------------------------------------------------------
    // Final review, Minor (2026-08-06): Load(path, createIfMissing: false)
    // finishes Gap B's guard (fix round 2 — see
    // ShellViewModel.SaveSavedPasswordsNow's doc comment). The old shape
    // paired a caller's own File.Exists pre-check with the default,
    // create-on-missing Load overload; a file that vanished in the gap
    // between those two calls still hit Load's first-run path and silently
    // wrote a fresh all-defaults config over whatever a peer had there —
    // the exact peer-clobber class Gap B exists to prevent, one level down.
    // These tests pin the new overload's contract directly: the mechanism
    // the fix actually relies on, the same way UnlockThresholdTestCollectionMembershipTests
    // pins a collection attribute rather than trying to force a
    // microsecond-scale race.
    // -----------------------------------------------------------------

    [Fact]
    public void LoadWithCreateIfMissingFalseThrowsInsteadOfCreatingTheFile()
    {
        var path = Path.Combine(_dir, "missing.json");
        Assert.False(File.Exists(path));

        var ex = Assert.Throws<ConfigMissingException>(() => Config.Load(path, createIfMissing: false));

        Assert.Contains("missing.json", ex.Message);
        // The whole point: unlike the default overload, nothing gets
        // written here. A caller that reacts to this exception by writing
        // nothing at all (SaveSavedPasswordsNow) is protected because
        // there is genuinely nothing on disk to have raced with.
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LoadWithCreateIfMissingTrueStillCreatesTheFileTheDefaultOverloadDoes()
    {
        // The single-arg Load(path) — used at app startup and by
        // FreshConfigForSettings — must be completely unaffected by adding
        // the new overload: same create-on-first-run behavior as always.
        var path = Path.Combine(_dir, "fresh.json");
        var cfg = Config.Load(path);
        Assert.True(File.Exists(path));
        Assert.NotNull(cfg);
    }

    [Fact]
    public void ConfigMissingExceptionIsCatchableAsAPlainConfigException()
    {
        // ApplySettingsAsync's SavedPasswords overlay (final review,
        // Important 1) catches the base ConfigException type to treat
        // "missing" and "exists but corrupt" identically — both just mean
        // "nothing fresher to overlay, skip it". That only works because
        // ConfigMissingException derives from ConfigException.
        var path = Path.Combine(_dir, "still-missing.json");
        var caught = Assert.Throws<ConfigMissingException>(() => Config.Load(path, createIfMissing: false));
        Assert.IsAssignableFrom<ConfigException>(caught);
    }

    [Fact]
    public void LoadWithCreateIfMissingFalseLoadsNormallyWhenTheFileAlreadyExists()
    {
        var path = Path.Combine(_dir, "existing.json");
        Assert.True(Config.TrySave(new Config { Inbox = "X" }, path, out var error), error);

        var cfg = Config.Load(path, createIfMissing: false);

        Assert.Equal("X", cfg.Inbox);
    }
}
