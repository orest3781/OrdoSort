using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-07 QC sweep R1: <see cref="HistoryBackup.BackupDaily"/>
/// returns null on failure, and both ShellViewModel call sites — the
/// constructor and the Settings history-db swap — used to discard that
/// return. A daily backup that fails every day (permissions, a full disk, a
/// disconnected share) failed silently forever, on the one database that is
/// the only link between a filed document and its original identity. These
/// prove the fix actually surfaces a genuine failure, and does NOT
/// false-positive on the ordinary "no db file yet" case BackupDaily's null
/// return also covers (first run, before any history db exists).</summary>
public class HistoryBackupWarningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordo_bkpwarn_" + Guid.NewGuid());

    public HistoryBackupWarningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (IOException) when (attempt < 10)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }

    private static (Config Cfg, string CfgPath, string Inbox, string Deferred) NewBareConfig(string dir)
    {
        var inbox = Path.Combine(dir, "inbox");
        var deferred = Path.Combine(dir, "deferred");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(deferred);
        var cfg = new Config { Inbox = inbox, Deferred = deferred, Sort = "filename_asc" };
        return (cfg, Path.Combine(dir, "config.json"), inbox, deferred);
    }

    [Fact]
    public void FreshInstallWithNoHistoryDbYetShowsNoWarning()
    {
        // The ordinary first-run case: BackupDaily ALSO returns null here
        // (nothing to back up yet) — that must not read as a failure.
        var (cfg, cfgPath, _, _) = NewBareConfig(_dir);
        var watch = new FolderWatchService(debounceMs: 600_000, pollMs: 600_000);

        var shell = new ShellViewModel(cfg, cfgPath, new FakeViewer(), new FakeDialogs(), watch,
            uiContext: null, scheduler: new InlineWorkScheduler());
        try
        {
            Assert.False(shell.HasHistoryBackupWarning);
            Assert.Equal("", shell.HistoryBackupWarning);
        }
        finally { shell.Dispose(); watch.Dispose(); }
    }

    [Fact]
    public void GenuineStartupBackupFailureSurfacesAsAPersistentWarning()
    {
        var (cfg, cfgPath, _, _) = NewBareConfig(_dir);
        var dbPath = Path.Combine(_dir, "history.sqlite");

        // A real history db already exists (as on every launch after the
        // first) — schema created, file closed, so it is genuinely "at
        // rest" the way HistoryBackup.cs's own doc comment requires.
        using (var seed = new History(dbPath)) { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Block the backups directory with a same-named FILE, so
        // BackupDaily's Directory.CreateDirectory throws and it returns
        // null — a genuine failure, not "nothing to back up".
        File.WriteAllText(Path.Combine(_dir, "backups"), "blocks the backups directory");

        var watch = new FolderWatchService(debounceMs: 600_000, pollMs: 600_000);
        var dialogs = new FakeDialogs();
        var shell = new ShellViewModel(cfg, cfgPath, new FakeViewer(), dialogs, watch,
            uiContext: null, scheduler: new InlineWorkScheduler());
        try
        {
            Assert.True(shell.HasHistoryBackupWarning);
            Assert.Contains("backup", shell.HistoryBackupWarning, StringComparison.OrdinalIgnoreCase);
            // Not a modal — startup must not have blocked on it, and nothing
            // asked FakeDialogs to show one.
            Assert.Empty(dialogs.Warnings);
        }
        finally { shell.Dispose(); watch.Dispose(); }
    }

    [Fact]
    public void GenuineSwapBackupFailureSurfacesAsAPersistentWarning()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        Assert.False(fx.Shell.HasHistoryBackupWarning);   // clean before the swap

        var newDbPath = Path.Combine(fx.Dir, "other-history.sqlite");
        using (var seed = new History(newDbPath)) { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(fx.Dir, "backups"), "blocks the backups directory");

        var mine = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Shell.Cfg))!;
        mine.HistoryDb = "other-history.sqlite";
        fx.Shell.ApplySettings(mine);   // InlineWorkScheduler: completes synchronously

        Assert.True(fx.Shell.HasHistoryBackupWarning);
        Assert.Contains("backup", fx.Shell.HistoryBackupWarning, StringComparison.OrdinalIgnoreCase);
        // The db swap itself still succeeded — a failed BACKUP is not a
        // failed OPEN, and must not roll back the (working) new database.
        Assert.Equal("other-history.sqlite", fx.Shell.Cfg.HistoryDb);
        Assert.Empty(fx.Dialogs.Warnings);
    }
}
