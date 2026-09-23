using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>A relative route destination ("Filed\Smith") must land beside
/// config.json — the rule inbox, deferred, names_file and history_db already
/// follow — not against the process's current directory, which is wherever
/// the app happened to be launched from.
///
/// Like <see cref="SessionDeferredResolutionTests"/>, config.json lives in a
/// directory that is NOT the working directory (asserted in the
/// constructor): a fixture where the two coincide would pass either way.
/// CommitCurrent reaches Commit's shared move helper, hence the
/// collection.</summary>
[Collection(UndoFailureTests.Name)]
public class SessionRouteResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordosessroute_" + Guid.NewGuid());
    private readonly string _configDir, _inbox, _cfgPath;

    public SessionRouteResolutionTests()
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
    public void CommitCurrentFilesIntoARelativeRouteResolvedBesideTheConfigFile()
    {
        // Created ONLY beside config.json, so a wrong resolution has nowhere
        // to land and the commit would fail as "not available".
        var expectedDir = Path.Combine(_configDir, "Filed");
        Directory.CreateDirectory(expectedDir);

        var cfg = new Config { Inbox = _inbox, Deferred = Path.Combine(_root, "deferred") };
        using var history = new History(Path.Combine(_root, "h.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--111111.pdf");
        session.Start(new[] { src });
        var route = new Route { Label = "Filed", Path = "Filed" };

        var outcome = session.CommitCurrent("SMITH", route);

        Assert.False(File.Exists(src));
        Assert.Equal(expectedDir, Path.GetDirectoryName(outcome.NewPath));
        Assert.Single(Directory.GetFiles(expectedDir));
        // The audit row says where the document really went, so a reader of
        // the shared history is not left guessing which directory "Filed"
        // meant on the station that filed it.
        Assert.Equal(expectedDir, history.Rows().Single()["route_path"]);
        // The route itself is untouched: a shared config.json must never be
        // rewritten by resolving one station's copy.
        Assert.Equal("Filed", route.Path);
    }

    [Fact]
    public void CommitCurrentUsesAnAbsoluteRouteExactlyAsTyped()
    {
        var absoluteDir = Path.Combine(_root, "absolute-dest");
        Directory.CreateDirectory(absoluteDir);

        var cfg = new Config { Inbox = _inbox, Deferred = Path.Combine(_root, "deferred") };
        using var history = new History(Path.Combine(_root, "h2.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--222222.pdf");
        session.Start(new[] { src });

        var outcome = session.CommitCurrent("SMITH", new Route { Label = "Abs", Path = absoluteDir });

        Assert.Equal(absoluteDir, Path.GetDirectoryName(outcome.NewPath));
        Assert.Equal(absoluteDir, history.Rows().Single()["route_path"]);
    }

    [Fact]
    public void CommitCurrentWithABlankRouteStillRefusesInsteadOfFilingBesideConfigJson()
    {
        // Path.Combine(dir, "") == dir: resolving a blank route would turn
        // "not set" into "the folder config.json lives in".
        var cfg = new Config { Inbox = _inbox, Deferred = Path.Combine(_root, "deferred") };
        using var history = new History(Path.Combine(_root, "h3.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--333333.pdf");
        session.Start(new[] { src });

        var ex = Record.Exception(() => session.CommitCurrent("SMITH", new Route { Label = "R", Path = "" }));

        Assert.IsType<CommitError>(ex);
        Assert.True(File.Exists(src));
        Assert.Empty(Directory.GetFiles(_configDir));
    }

    [Fact]
    public void ValidateRouteChecksARelativeRouteBesideTheConfigFile()
    {
        Directory.CreateDirectory(Path.Combine(_configDir, "Filed"));

        Assert.Equal("", Config.ValidateRoute(new Route { Path = "Filed" }, _cfgPath));
    }

    [Fact]
    public void ValidateRouteStillReportsABlankRouteAsUnset() =>
        Assert.Equal("no destination path configured",
            Config.ValidateRoute(new Route { Path = "  " }, _cfgPath));
}
