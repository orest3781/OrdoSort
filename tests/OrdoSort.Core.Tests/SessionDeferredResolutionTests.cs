namespace OrdoSort.Core.Tests;

/// <summary>2026-08 audit finding C2: Settings tells users a relative Inbox/
/// Deferred value is "resolved beside the config file", but <c>cfg.Deferred</c>
/// used to reach <see cref="Commit.SkipFile"/> raw (via <see cref="Session"/>.
/// <see cref="Session.SkipCurrent"/>) and so actually resolved against
/// <see cref="Environment.CurrentDirectory"/>. This is the single most
/// consequential of the call sites that promise broke: it doesn't just read
/// or watch the folder, it MOVES the document there.
///
/// Every test here deliberately puts config.json in a directory that is NOT
/// the test process's working directory (asserted in the constructor) — a
/// fixture where the two coincide would pass whether or not the fix is
/// actually wired in, proving nothing.</summary>
public class SessionDeferredResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordosessdef_" + Guid.NewGuid());
    private readonly string _configDir, _inbox, _cfgPath;

    public SessionDeferredResolutionTests()
    {
        _configDir = Path.Combine(_root, "station-share");   // where config.json lives
        _inbox = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_inbox);
        _cfgPath = Path.Combine(_configDir, "config.json");

        Assert.NotEqual(
            Path.GetFullPath(_configDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var a = 0; ; a++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (IOException) when (a < 10) { Thread.Sleep(50); }
        }
    }

    private string MakePdf(string name)
    {
        var p = Path.Combine(_inbox, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    [Fact]
    public void SkipCurrentSetsAsideIntoADeferredFolderResolvedBesideTheConfigFile()
    {
        // The relative value beside config.json — created ONLY there, never
        // beside the working directory, so a wrong resolution has nowhere to
        // (silently) land.
        var expectedDir = Path.Combine(_configDir, "set-aside");
        Directory.CreateDirectory(expectedDir);
        var wrongDir = Path.Combine(Environment.CurrentDirectory, "set-aside");

        var cfg = new Config { Inbox = _inbox, Deferred = "set-aside" };
        using var history = new History(Path.Combine(_root, "h.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--111111.pdf");
        session.Start(new[] { src });

        session.SkipCurrent();

        Assert.False(File.Exists(src));
        var landed = Directory.GetFiles(expectedDir);
        Assert.True(landed.Length == 1,
            $"expected the set-aside file beside the config file at {expectedDir}, found {landed.Length} there");
        Assert.False(Directory.Exists(wrongDir) && Directory.GetFiles(wrongDir).Length > 0,
            $"the file was set aside against the working directory at {wrongDir} " +
            $"instead of beside the config file at {expectedDir}");
    }

    [Fact]
    public void SkipCurrentNeverRewritesTheStoredDeferredValue()
    {
        var expectedDir = Path.Combine(_configDir, "set-aside");
        Directory.CreateDirectory(expectedDir);

        var cfg = new Config { Inbox = _inbox, Deferred = "set-aside" };
        using var history = new History(Path.Combine(_root, "h2.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--222222.pdf");
        session.Start(new[] { src });

        session.SkipCurrent();

        // Config.TrySaveMain's own doc comment: never silently rewrite a
        // relative value to absolute — a shared config.json under other
        // stations must never be touched by resolving THIS station's copy.
        Assert.Equal("set-aside", cfg.Deferred);
    }
}
