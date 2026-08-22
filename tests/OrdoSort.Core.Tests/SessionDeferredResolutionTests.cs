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

    // QC-02 (2026-08-21 audit, task 3): Config.ResolveBeside("", cfgPath) is
    // Path.Combine(dir, "") — which .NET defines as `dir` — so an unguarded
    // blank Deferred silently became "the folder config.json lives in".
    // Commit.SkipFile's own "is it blank" guard (Commit.cs) can no longer
    // fire once that flattening has already happened, so Skip moved the
    // document next to config.json and reported success. SkipCurrent must
    // refuse BEFORE calling ResolveBeside, while cfg.Deferred is still
    // recognizably blank.
    [Fact]
    public void SkipCurrentWithABlankDeferredRefusesInsteadOfMisfilingBesideConfigJson()
    {
        var cfg = new Config { Inbox = _inbox, Deferred = "" };
        using var history = new History(Path.Combine(_root, "h3.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--333333.pdf");
        session.Start(new[] { src });

        // Record.Exception, not Assert.Throws: unfixed, the blank Deferred
        // is silently flattened to _configDir and the move SUCCEEDS — no
        // exception at all, just a misfiled document and a reported
        // success. Asserting the throw first would only ever report "no
        // exception was thrown" and stop there; checking location first
        // shows the actual misfile the throw's absence causes.
        var ex = Record.Exception(() => session.SkipCurrent());

        Assert.True(File.Exists(src),
            $"expected {src} to remain in the inbox after a refused skip, but it is gone — " +
            $"found beside config.json instead, at {_configDir}: " +
            $"{string.Join(", ", Directory.GetFiles(_configDir).Select(Path.GetFileName))}");
        Assert.Empty(Directory.GetFiles(_configDir));
        Assert.IsType<CommitError>(ex);
    }

    // Unlike "", "   " does NOT collapse under Path.Combine — it survives
    // as a literal three-space directory NAME, so before the fix this threw
    // for an unrelated, accidental reason: Directory.Exists trims trailing
    // whitespace off a path's last segment (so Commit.SkipFile's own guard
    // sees the config directory as "available"), but File.Move does not
    // extend that same leniency to an intermediate segment, so the actual
    // move fails with "Could not find a part of the path", caught and
    // rewrapped as a CommitError by MoveNeverOverwrite. That is a Windows
    // path-resolution quirk, not a deliberate refusal — SkipCurrent's own
    // blank-or-whitespace guard (Session.SkipCurrent) makes the refusal
    // deliberate instead of coincidental.
    //
    // That coincidence means File.Exists/Directory.GetFiles/CommitError-type
    // alone are satisfied identically whether the deliberate guard fires or
    // whether it's gone and MoveNeverOverwrite's unrelated IOException fires
    // instead — none of the three would notice SkipCurrent's
    // IsNullOrWhiteSpace guard silently narrowing to IsNullOrEmpty (proven
    // directly by staging that exact narrowing and watching this test keep
    // passing on the message-less assertions alone). The message text is the
    // one thing that differs between the two paths — the guard's literal
    // "Set-aside folder is not available: (not set)" versus
    // MoveNeverOverwrite's "Couldn't move {name} to {dir}: …" — so assert on
    // it to make this test actually discriminate which path fired.
    [Fact]
    public void SkipCurrentWithAWhitespaceOnlyDeferredRefusesTheSameWay()
    {
        var cfg = new Config { Inbox = _inbox, Deferred = "   " };
        using var history = new History(Path.Combine(_root, "h4.sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        var src = MakePdf("20240115--444444.pdf");
        session.Start(new[] { src });

        var ex = Record.Exception(() => session.SkipCurrent());

        Assert.True(File.Exists(src),
            $"expected {src} to remain in the inbox after a refused skip, but it is gone — " +
            $"found beside config.json instead, at {_configDir}: " +
            $"{string.Join(", ", Directory.GetFiles(_configDir).Select(Path.GetFileName))}");
        Assert.Empty(Directory.GetFiles(_configDir));
        var commitError = Assert.IsType<CommitError>(ex);
        Assert.Contains("Set-aside folder is not available", commitError.Message);
    }

    // Scanner.DeferredSummary's own blank-folder guard (Scanner.cs) already
    // returns (0, 0) for a literal "" — that was true before this task and
    // stays true after, since the actual knock-on defect isn't in
    // DeferredSummary itself but in the callers that resolve a blank
    // Deferred to the config directory BEFORE DeferredSummary ever sees it
    // (ShellViewModel.RefreshFoldersAsync/RefreshDeferredAsync). This test
    // pins DeferredSummary's own contract; the composition defect is pinned
    // where the resolving caller actually lives, in
    // OrdoSort.Wpf.Tests.FolderPathResolutionTests.
    [Fact]
    public void DeferredSummaryOfABlankFolderIsZero() =>
        Assert.Equal(new Scanner.DeferredInfo(0, 0), Scanner.DeferredSummary(""));
}
