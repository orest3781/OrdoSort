using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08 audit finding C2: Settings' Inbox/Deferred note claims a
/// relative value "resolved beside the config file", but ShellViewModel used
/// to pass <c>cfg.Inbox</c>/<c>cfg.Deferred</c> raw to Scanner.Scan,
/// FolderWatchService.SetFolders and OpenFolder, so a relative value actually
/// resolved against <see cref="Environment.CurrentDirectory"/> — true only by
/// coincidence, since ShellFixture's own config directory and the test
/// process's working directory are never the same place (asserted below,
/// per the "make sure they differ" rule: a fixture where they coincide would
/// pass whether or not the fix is wired in).</summary>
public class FolderPathResolutionTests
{
    [Fact]
    public void ARelativeInboxResolvesBesideTheConfigFileNotTheWorkingDirectory()
    {
        using var fx = new ShellFixture(cfg => cfg.Inbox = "relative-inbox");
        Assert.NotEqual(
            Path.GetFullPath(fx.Dir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar));

        // Pin the exact resolved location every call site now uses.
        var resolved = ShellViewModel.ResolvePath(fx.Cfg.Inbox, fx.CfgPath);
        Assert.Equal(Path.Combine(fx.Dir, "relative-inbox"), resolved);
        Assert.NotEqual(Path.Combine(Environment.CurrentDirectory, "relative-inbox"), resolved);

        // The folder exists ONLY beside the config file — a wrong resolution
        // (against the working directory) would find nothing there and the
        // scan would report "Inbox problem", not a ready count. The failure
        // message below deliberately names BOTH candidate folders so a
        // regression that reintroduces raw, unresolved cfg.Inbox at any
        // ShellViewModel call site is unambiguous about which one it used.
        var wrongCwdFolder = Path.Combine(Environment.CurrentDirectory, "relative-inbox");
        Directory.CreateDirectory(resolved);
        File.WriteAllText(Path.Combine(resolved, "20240115--111111.pdf"), "pdf");

        fx.Shell.Initialize();

        Assert.True(fx.Shell.CountLine == "1 file ready" && fx.Shell.StartEnabled,
            $"expected the inbox scanned beside the config file at {resolved} (\"1 file ready\", " +
            $"Start enabled), but got \"{fx.Shell.CountLine}\" (Start enabled: {fx.Shell.StartEnabled}) — " +
            $"as if it had scanned the working directory at {wrongCwdFolder} instead, where no such file exists");
    }

    [Fact]
    public void ARelativeDeferredResolvesBesideTheConfigFileNotTheWorkingDirectory()
    {
        using var fx = new ShellFixture(cfg => cfg.Deferred = "relative-deferred");

        var resolved = ShellViewModel.ResolvePath(fx.Cfg.Deferred, fx.CfgPath);
        Assert.Equal(Path.Combine(fx.Dir, "relative-deferred"), resolved);
        Assert.NotEqual(Path.Combine(Environment.CurrentDirectory, "relative-deferred"), resolved);

        Directory.CreateDirectory(resolved);
        File.WriteAllText(Path.Combine(resolved, "waiting.pdf"), "x");

        fx.Shell.Initialize();

        Assert.True(fx.Shell.HasDeferred);
        Assert.Contains("1 set-aside file waiting", fx.Shell.DeferredAlert);
    }

    [Fact]
    public void AnAbsoluteInboxOrDeferredPathIsUnaffectedByResolution()
    {
        var cfgPath = Path.Combine(Path.GetTempPath(), "ordoresabs_" + Guid.NewGuid(), "config.json");
        const string absoluteInbox = @"C:\completely\unrelated\drive-path\inbox";
        const string absoluteDeferred = @"D:\another\unrelated\path\deferred";

        Assert.Equal(absoluteInbox, ShellViewModel.ResolvePath(absoluteInbox, cfgPath));
        Assert.Equal(absoluteDeferred, ShellViewModel.ResolvePath(absoluteDeferred, cfgPath));
    }

    // QC-02 (2026-08-21 audit, task 3): ResolvePath("", cfgPath) is
    // Config.ResolveBeside's documented behaviour for a blank value —
    // Path.Combine(dir, "") returns `dir` — so RefreshFoldersAsync's
    // Scanner.DeferredSummary(ResolvePath(cfg.Deferred, cfgPath)) used to
    // hand DeferredSummary the config directory itself for a blank
    // Deferred: non-blank, exists, so DeferredSummary's own "is it blank"
    // guard never fired and it counted whatever already lives beside
    // config.json (history.sqlite, at minimum — ShellFixture's History
    // opens it synchronously in the constructor) as "set-aside files
    // waiting". The fix is in the caller, not DeferredSummary: a blank
    // Deferred must never reach ResolvePath in the first place.
    [Fact]
    public void ABlankDeferredNeverCountsTheConfigDirectorysOwnFilesAsSetAside()
    {
        using var fx = new ShellFixture(cfg => cfg.Deferred = "");
        Assert.True(File.Exists(Path.Combine(fx.Dir, "history.sqlite")),
            "fixture assumption broken: expected history.sqlite already sitting beside config.json");

        fx.Shell.Initialize();

        Assert.False(fx.Shell.HasDeferred,
            $"expected no set-aside alert for a blank Deferred folder, got \"{fx.Shell.DeferredAlert}\"");
    }
}
