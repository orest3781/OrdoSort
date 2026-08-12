using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Initialize, StartProcessing and ApplySettings are called by the
/// window as <c>_ = SomethingAsync()</c>. An exception thrown inside an async
/// method is captured into the returned Task rather than thrown to the caller,
/// and .NET does not tear the process down for an unobserved faulted Task — so
/// discarding that Task used to discard the failure with it: no dialog, no
/// crash.log entry, nothing at all.
///
/// The worst case was Initialize. <c>_watch.SetFolders</c> can genuinely throw
/// on this app's own stated deployment — a locked-down SMB share can list a
/// folder while denying change-notification rights, and the share can drop
/// between the Directory.Exists check and EnableRaisingEvents. When it did,
/// the first scan was skipped AND the session was left with no live folder
/// watcher, permanently, because nothing re-arms it; the dashboard just looked
/// idle. RouteCommand/SkipCommand/UndoCommand have had this net via
/// AsyncRelayCommand all along — these three had no equivalent.
///
/// These tests drive the failure through the IWorkScheduler seam, which is
/// where each of the three methods does its first offloaded work.</summary>
public class FireAndForgetGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ordo_faf_" + Guid.NewGuid());

    public FireAndForgetGuardTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>Stands in for the share that denies change-notification
    /// rights: the very first offloaded call blows up the way
    /// FileSystemWatcher's constructor would.</summary>
    private sealed class ThrowingWorkScheduler : IWorkScheduler
    {
        public Task<T> Run<T>(Func<T> work) => throw new UnauthorizedAccessException("denied");
        public Task Run(Action work) => throw new UnauthorizedAccessException("denied");
    }

    private (Config Cfg, string CfgPath) NewBareConfig()
    {
        var inbox = Path.Combine(_dir, "inbox");
        var deferred = Path.Combine(_dir, "deferred");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(deferred);
        return (new Config { Inbox = inbox, Deferred = deferred, Sort = "filename_asc" },
                Path.Combine(_dir, "config.json"));
    }

    private ShellViewModel NewShell(FakeDialogs dialogs, FolderWatchService watch)
    {
        var (cfg, cfgPath) = NewBareConfig();
        return new ShellViewModel(cfg, cfgPath, new FakeViewer(), dialogs, watch,
            uiContext: null, scheduler: new ThrowingWorkScheduler());
    }

    /// <summary>Before the fix this test failed on every assertion at once:
    /// nothing was reported and nothing was logged, because the faulted Task
    /// was simply dropped on the floor.</summary>
    [Fact]
    public void AStartupFailureIsReportedInsteadOfVanishing()
    {
        var dialogs = new FakeDialogs();
        var watch = new FolderWatchService(debounceMs: 600_000, pollMs: 600_000);
        var shell = NewShell(dialogs, watch);
        Exception? logged = null;
        shell.UnexpectedError += ex => logged = ex;
        try
        {
            shell.Initialize();

            Assert.NotNull(logged);
            Assert.IsType<UnauthorizedAccessException>(logged);
            var warning = Assert.Single(dialogs.Warnings);
            Assert.Contains("Starting up", warning.Message);
            // The document-in-flight sentence belongs to a failed file move,
            // not to a failed startup — no document was in flight here.
            Assert.DoesNotContain("Nothing was deleted", warning.Message);
        }
        finally { shell.Dispose(); watch.Dispose(); }
    }

    [Fact]
    public void ASessionStartFailureIsReportedInsteadOfVanishing()
    {
        var dialogs = new FakeDialogs();
        var watch = new FolderWatchService(debounceMs: 600_000, pollMs: 600_000);
        var shell = NewShell(dialogs, watch);
        Exception? logged = null;
        shell.UnexpectedError += ex => logged = ex;
        try
        {
            shell.StartProcessing();

            Assert.NotNull(logged);
            Assert.Contains("Starting that session", Assert.Single(dialogs.Warnings).Message);
        }
        finally { shell.Dispose(); watch.Dispose(); }
    }

    [Fact]
    public void ASettingsSaveFailureIsReportedInsteadOfVanishing()
    {
        var dialogs = new FakeDialogs();
        var watch = new FolderWatchService(debounceMs: 600_000, pollMs: 600_000);
        var shell = NewShell(dialogs, watch);
        var (fresh, _) = NewBareConfig();
        Exception? logged = null;
        shell.UnexpectedError += ex => logged = ex;
        try
        {
            shell.ApplySettings(fresh);

            Assert.NotNull(logged);
            Assert.Contains("Saving your settings", Assert.Single(dialogs.Warnings).Message);
        }
        finally { shell.Dispose(); watch.Dispose(); }
    }
}
