using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Config, Scanner, Commit, and Session — the filing-loop logic.
///
/// Final-review M (2026-08-05, extended 2026-08-05): SessionUndoRoundTrip
/// below drives Commit.UndoAction, the same method UndoFailureTests.
/// RaceAtTheFinalMomentIsReportedAsTheSameActionableCommitError sets the
/// static Commit.RaceHookForTests seam inside — and the same method
/// AuditFailureTests.UndoStillWorksAfterAnUnrecordedCommit also drives. See
/// UndoFailureTests's class doc for the full reasoning; this class joins
/// <see cref="UndoFailureTests.Name"/>, the collection now shared by all
/// three, so xUnit never schedules any two of them concurrently — the same
/// fix already used for OrdoSort.Wpf.App._crashDir
/// (OrdoSort.Wpf.Tests.CultureInvariantDatesTests).</summary>
[Collection(UndoFailureTests.Name)]
public class PipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pltest_" + Guid.NewGuid());
    private readonly string _inbox, _dest, _deferred, _cfgPath;

    public PipelineTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        _dest = Path.Combine(_root, "dest");
        _deferred = Path.Combine(_root, "deferred");
        _cfgPath = Path.Combine(_root, "config.json");   // Deferred above is already absolute, so this base is never actually consulted
        foreach (var d in new[] { _inbox, _dest, _deferred }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        Commit.SurvivingSourceHookForTests = null;
        SqliteClear();
        for (var a = 0; ; a++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (IOException) when (a < 10) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(50); }
        }
    }
    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private string MakePdf(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    private Route Dest => new() { Label = "R", Path = _dest };

    // ---- Config ----
    [Fact]
    public void ConfigDefaultsAndRoundTrip()
    {
        var path = Path.Combine(_root, "c.json");
        var cfg = Config.Load(path);   // first run writes defaults
        Assert.Equal("size_desc", cfg.Sort);         // biggest-first default
        Assert.True(cfg.UppercaseNames);
        Assert.Equal("insert", cfg.NamingMode);
        Assert.True(File.Exists(path));

        cfg.Inbox = "C:/ünïcode";
        cfg.Routes.Add(new Route { Label = "Café", Path = "C:/x", Color = "#abc" });
        Config.Save(cfg, path);
        var back = Config.Load(path);
        Assert.Equal("C:/ünïcode", back.Inbox);
        Assert.Equal("Café", back.Routes[0].Label);
        Assert.Equal("#abc", back.Routes[0].Color);
    }

    [Fact]
    public void ConfigUnknownKeysSurvive()
    {
        var path = Path.Combine(_root, "c.json");
        File.WriteAllText(path, """{"inbox":"C:/in","my_note":"hands off"}""");
        var cfg = Config.Load(path);
        Config.Save(cfg, path);
        Assert.Contains("my_note", File.ReadAllText(path));
    }

    [Fact]
    public void ConfigBadSortIsReadable()
    {
        var path = Path.Combine(_root, "c.json");
        File.WriteAllText(path, """{"sort":"banana"}""");
        Assert.Throws<ConfigException>(() => Config.Load(path));
    }

    // ---- Scanner ----
    [Fact]
    public void ScanSeparatesMatchingAndIgnored()
    {
        MakePdf(_inbox, "20240115--1234567890.pdf");
        MakePdf(_inbox, "20240116--222.pdf");
        MakePdf(_inbox, "scan_001.pdf");
        File.WriteAllText(Path.Combine(_inbox, "notes.txt"), "x");
        var r = Scanner.Scan(_inbox, "filename_asc");
        Assert.Equal(2, r.Count);
        Assert.Equal(2, r.IgnoredCount);
    }

    [Fact]
    public void ReplaceModeScanTakesEveryPdf()
    {
        MakePdf(_inbox, "20240115--1234567890.pdf");
        MakePdf(_inbox, "scan_001.pdf");                 // no marker — still in
        File.WriteAllText(Path.Combine(_inbox, "notes.txt"), "x");
        var r = Scanner.Scan(_inbox, "filename_asc", Naming.ModeReplace);
        Assert.Equal(2, r.Count);
        Assert.Equal(1, r.IgnoredCount);                 // only the .txt
    }

    [Theory]
    [InlineData("20240115--1234.pdf", "insert", true)]
    [InlineData("scan_001.pdf", "insert", false)]        // marker required
    [InlineData("scan_001.pdf", "replace", true)]        // any pdf
    [InlineData("scan_001.PDF", "replace", true)]        // case-insensitive
    [InlineData("notes.txt", "replace", false)]           // pdfs only
    public void EligibilityFollowsTheMode(string name, string mode, bool eligible) =>
        Assert.Equal(eligible, Scanner.Eligible(name, mode));

    [Fact]
    public void ScanDefaultIsBiggestFirst()
    {
        File.WriteAllBytes(Path.Combine(_inbox, "20240101--1.pdf"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_inbox, "20240102--2.pdf"), new byte[300]);
        var r = Scanner.Scan(_inbox);   // no sort arg -> size_desc
        Assert.Equal("20240102--2.pdf", Path.GetFileName(r.Matching[0]));
    }

    [Fact]
    public void ScanMissingFolderIsMessageNotCrash() =>
        Assert.Contains("does not exist", Scanner.Scan(@"Z:\nope\gone").Error);

    [Fact]
    public void DeferredSummaryCountsAndAgesTheOldest()
    {
        Assert.Equal(new Scanner.DeferredInfo(0, null), Scanner.DeferredSummary(_inbox));

        var old = MakePdf(_inbox, "20240101--old.pdf");
        MakePdf(_inbox, "20240102--new.pdf");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-4));
        var info = Scanner.DeferredSummary(_inbox, DateTime.Now);
        Assert.Equal(2, info.Count);
        Assert.Equal(4, info.OldestAgeDays);
    }

    [Fact]
    public void DeferredSummaryMissingFolderIsZeroNotCrash() =>
        Assert.Equal(0, Scanner.DeferredSummary(@"Z:\nope\gone").Count);

    [Fact]
    public void SafeMtimeOfAFileGoneByReadTimeIsNullNotTheSentinelTicks()
    {
        // QC-13: FileInfo.LastWriteTimeUtc does not throw for a file that's
        // gone -- it silently returns 1601-01-01 UTC -- so files.Min(SafeMtime)
        // used to latch that sentinel and report a ~155,000-day-old folder
        // over one vanished file. Directory.GetFiles's result is captured
        // BEFORE the delete, exactly how DeferredSummary/Scan capture it, so
        // this reproduces "gone by the time it's read" deterministically
        // instead of racing a real deletion against the scan (not
        // reproducible on this machine per the plan's filesystem-condition
        // note).
        var path = MakePdf(_inbox, "20240101--1.pdf");
        var listed = Directory.GetFiles(_inbox);
        File.Delete(path);
        Assert.Null(Scanner.SafeMtime(listed[0]));
    }

    [Fact]
    public void OldestAgeDaysSkipsUnknownMtimesInsteadOfLettingOnePoisonTheFolder()
    {
        var now = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Local);
        var readable = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc).Ticks;   // 4 days old
        Assert.Equal(4, Scanner.OldestAgeDays(new long?[] { readable, null }, now));
    }

    [Fact]
    public void OldestAgeDaysIsNullWhenEveryFileVanished() =>
        Assert.Null(Scanner.OldestAgeDays(new long?[] { null, null }, DateTime.Now));

    // ---- Commit ----
    [Fact]
    public void CommitMovesAndRenames()
    {
        var src = MakePdf(_inbox, "20240115--1234567890.pdf");
        var outcome = Commit.CommitFile(src, "SMITH JOHN", Dest, "insert");
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(_dest, "20240115-SMITH JOHN-1234567890.pdf")));
    }

    [Fact]
    public void CommitCollisionCountsAndNeverOverwrites()
    {
        File.WriteAllText(Path.Combine(_dest, "SMITH JOHN.pdf"), "existing");
        var src = MakePdf(_inbox, "20240115--1234567890.pdf");
        var outcome = Commit.CommitFile(src, "SMITH JOHN", Dest, "replace");
        Assert.Equal("SMITH JOHN (2).pdf", Path.GetFileName(outcome.NewPath!));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_dest, "SMITH JOHN.pdf")));
    }

    [Fact]
    public void CommitColonNameFailsReadablyFileStays()
    {
        var src = MakePdf(_inbox, "20240115--1234567890.pdf");
        var ex = Assert.Throws<CommitError>(() =>
            Commit.CommitFile(src, "SMITH:JOHN", Dest, "replace"));
        Assert.Contains("can't contain", ex.Message);
        Assert.True(File.Exists(src));   // untouched — no ADS data loss
        Assert.Empty(Directory.GetFiles(_dest));
    }

    [Fact]
    public void CommitMissingDestIsReadableAndFileStays()
    {
        var src = MakePdf(_inbox, "20240115--1234567890.pdf");
        var route = new Route { Label = "R", Path = Path.Combine(_root, "gone") };
        Assert.Throws<CommitError>(() => Commit.CommitFile(src, "X", route, "insert"));
        Assert.True(File.Exists(src));
    }

    // ---- 2026-08 audit QC-03: File.Move can report success and still leave
    // the source behind (MoveFileExW + MOVEFILE_COPY_ALLOWED, cross-volume,
    // undeletable original). That real Win32 branch needs a read-only source
    // on a second volume and can't be forced on one machine — these pin the
    // seam instead (Commit.SurvivingSourceHookForTests, wired into
    // MoveNeverOverwrite) rather than the syscall itself. ----

    [Fact]
    public void CommitMoveThatSurvivesAtTheSourceRefusesLoudlyAndFileStays()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var originalBytes = File.ReadAllBytes(src);
        // The real Win32 branch never deletes src in the first place; this
        // recreates it right after the real (same-volume) move actually
        // deletes it, so MoveNeverOverwrite's post-move check sees exactly
        // what the cross-volume case would leave behind.
        Commit.SurvivingSourceHookForTests = () => File.WriteAllBytes(src, originalBytes);

        var ex = Assert.Throws<CommitError>(() => Commit.CommitFile(src, "SMITH", Dest, "insert"));

        Assert.Contains("20240115--111111.pdf", ex.Message);
        Assert.Contains("could not be removed", ex.Message);
        Assert.True(File.Exists(src));
        Assert.Equal(originalBytes, File.ReadAllBytes(src));
        // The copy that genuinely reached the destination is left alone —
        // deleting it would risk destroying the only good copy.
        Assert.True(File.Exists(Path.Combine(_dest, "20240115-SMITH-111111.pdf")));
    }

    [Fact]
    public void SkipMoveThatSurvivesAtTheSourceRefusesLoudlyAndFileStays()
    {
        var src = MakePdf(_inbox, "20240115--222222.pdf");
        var originalBytes = File.ReadAllBytes(src);
        Commit.SurvivingSourceHookForTests = () => File.WriteAllBytes(src, originalBytes);

        var ex = Assert.Throws<CommitError>(() => Commit.SkipFile(src, _deferred));

        Assert.Contains("20240115--222222.pdf", ex.Message);
        Assert.Contains("could not be removed", ex.Message);
        Assert.True(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(_deferred, "20240115--222222.pdf")));
    }

    // ---- Session (with real history) ----
    [Fact]
    public void SessionCommitLogsAndAdvances()
    {
        using var h = new History(Path.Combine(_root, "h.sqlite"));
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred, NamingMode = "insert" };
        var s = new Session(cfg, h, _cfgPath);
        var a = MakePdf(_inbox, "20240101--1.pdf");
        var b = MakePdf(_inbox, "20240102--2.pdf");
        s.Start(new[] { a, b });
        s.CommitCurrent("SMITH JOHN", Dest);
        Assert.Equal(1, s.Pos);
        Assert.Equal(1, s.Filed);
        Assert.Equal(b, s.Current);
        var row = h.Rows(1)[0];
        Assert.Equal("SMITH JOHN", row["name_entered"]);
        Assert.Equal("20240101-SMITH JOHN-1.pdf", row["new_name"]);
    }

    [Fact]
    public void SessionUndoRoundTrip()
    {
        using var h = new History(Path.Combine(_root, "h.sqlite"));
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred };
        var s = new Session(cfg, h, _cfgPath);
        var a = MakePdf(_inbox, "20240101--1.pdf");
        s.Start(new[] { a });
        s.CommitCurrent("SMITH JOHN", Dest);
        Assert.False(File.Exists(a));

        var (_, original) = s.UndoLast();
        Assert.True(File.Exists(a));         // file is back
        Assert.Equal(0, s.Pos);              // current again
        Assert.Equal(0, s.Filed);
        Assert.Equal(1L, Convert.ToInt64(h.Rows(1)[0]["reverted"]));
    }

    [Fact]
    public void SessionSkipAndExtend()
    {
        using var h = new History(Path.Combine(_root, "h.sqlite"));
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred };
        var s = new Session(cfg, h, _cfgPath);
        var a = MakePdf(_inbox, "20240101--1.pdf");
        s.Start(new[] { a });
        Assert.Equal(1, s.Total);
        var b = MakePdf(_inbox, "20240102--2.pdf");
        Assert.Equal(1, s.Extend(new[] { a, b }));   // only b is new
        Assert.Equal(2, s.Total);
        s.SkipCurrent();
        Assert.Equal(1, s.Skipped);
        Assert.True(File.Exists(Path.Combine(_deferred, "20240101--1.pdf")));
    }

    [Fact]
    public void SessionExtendTreatsACaseOnlyDifferentPathAsAlreadyKnown()
    {
        // QC-12: Extend used to dedupe with a raw case-sensitive
        // HashSet<string> -- a second, stricter answer to the question
        // CONTEXT.md says PathIdentity alone decides.
        using var h = new History(Path.Combine(_root, "h.sqlite"));
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred };
        var s = new Session(cfg, h, _cfgPath);
        var a = MakePdf(_inbox, "20240101--1.pdf");
        s.Start(new[] { a });
        Assert.Equal(1, s.Total);

        Assert.Equal(0, s.Extend(new[] { a.ToUpperInvariant() }));
        Assert.Equal(1, s.Total);
    }

    [Fact]
    public void SessionCommitThatSurvivesAtTheSourceAdvancesNothingAndLogsNothing()
    {
        using var h = new History(Path.Combine(_root, "h.sqlite"));
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred, NamingMode = "insert" };
        var s = new Session(cfg, h, _cfgPath);
        var a = MakePdf(_inbox, "20240101--1.pdf");
        var originalBytes = File.ReadAllBytes(a);
        s.Start(new[] { a });
        Commit.SurvivingSourceHookForTests = () => File.WriteAllBytes(a, originalBytes);

        var ex = Assert.Throws<CommitError>(() => s.CommitCurrent("SMITH JOHN", Dest));

        Assert.Contains("20240101--1.pdf", ex.Message);
        Assert.Equal(0, s.Pos);        // never advanced — Current is still `a`
        Assert.Equal(0, s.Filed);
        Assert.Equal(a, s.Current);
        Assert.False(s.CanUndo);       // no undo entry was pushed
        Assert.Empty(h.Rows());        // no history row for a move that half-failed
    }

    [Fact]
    public void SessionSkipThatSurvivesAtTheSourceAdvancesNothingAndLogsNothing()
    {
        using var h = new History(Path.Combine(_root, "h.sqlite"));
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred };
        var s = new Session(cfg, h, _cfgPath);
        var a = MakePdf(_inbox, "20240101--1.pdf");
        var originalBytes = File.ReadAllBytes(a);
        s.Start(new[] { a });
        Commit.SurvivingSourceHookForTests = () => File.WriteAllBytes(a, originalBytes);

        var ex = Assert.Throws<CommitError>(() => s.SkipCurrent());

        Assert.Contains("20240101--1.pdf", ex.Message);
        Assert.Equal(0, s.Pos);
        Assert.Equal(0, s.Skipped);
        Assert.Equal(a, s.Current);
        Assert.False(s.CanUndo);
        Assert.Empty(h.Rows());
    }
}
