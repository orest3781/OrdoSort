using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

public class HistoryBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bkptest_" + Guid.NewGuid());
    private readonly string _db, _backups;

    public HistoryBackupTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "history.sqlite");
        _backups = Path.Combine(_dir, "backups");
        // A real audit database with one row: the backup goes through
        // SQLite, so the source has to be one.
        using var seed = new History(_db);
        LogOneRow(seed, "first");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void LogOneRow(History history, string name) =>
        history.LogCommit(@"C:\in\a.pdf", "a.pdf", name + ".pdf", name, "replace", "",
            "Main", @"C:\out", tagged: false, collisionSuffix: "");

    private static int CountRows(string dbPath)
    {
        using var h = new History(dbPath);
        return h.Count();
    }

    [Fact]
    public void CopiesOncePerDay()
    {
        var day = new DateTime(2026, 7, 20);
        var first = HistoryBackup.BackupDaily(_db, _backups, day);
        Assert.NotNull(first);
        Assert.True(File.Exists(Path.Combine(_backups, "history-20260720.sqlite")));
        Assert.Equal(1, CountRows(first!));

        // same day again: does not re-copy (even if the DB changed since)
        using (var h = new History(_db)) LogOneRow(h, "second");
        HistoryBackup.BackupDaily(_db, _backups, day);
        Assert.Equal(1, CountRows(first!));   // still the morning's copy
        Assert.Single(Directory.GetFiles(_backups));
    }

    [Fact]
    public void BackupWhileAnotherStationHasTheDbOpenIsACompleteCopy()
    {
        // Other workstations keep the shared DB open all day; the backup
        // must neither need them closed nor miss their committed rows.
        using var otherStation = new History(_db);
        LogOneRow(otherStation, "second");

        var dest = HistoryBackup.BackupDaily(_db, _backups, new DateTime(2026, 7, 20));

        Assert.NotNull(dest);
        Assert.Equal(2, CountRows(dest!));
    }

    [Fact]
    public void FailedBackupLeavesNothingThatCountsAsTodaysBackup()
    {
        // A copy that did not produce a complete, readable database must not
        // land at today's name: "already backed up today" is judged by that
        // file existing, so a bad file there would never be retried.
        var broken = Path.Combine(_dir, "broken.sqlite");
        File.WriteAllText(broken, "not a sqlite database");
        var day = new DateTime(2026, 7, 20);

        Assert.Null(HistoryBackup.BackupDaily(broken, _backups, day));
        Assert.Empty(Directory.GetFiles(_backups));   // no partial left behind

        // The next attempt that day (once the DB is readable) does the backup.
        var retried = HistoryBackup.BackupDaily(_db, _backups, day);
        Assert.NotNull(retried);
        Assert.Equal(1, CountRows(retried!));
    }

    [Fact]
    public void KeepsNewestNPrunesOlder()
    {
        for (var d = 1; d <= 20; d++)
            HistoryBackup.BackupDaily(_db, _backups, new DateTime(2026, 7, d), keep: 14);
        var kept = Directory.GetFiles(_backups).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(14, kept.Count);
        Assert.Equal("history-20260707.sqlite", kept.First());   // oldest kept is day 7
        Assert.Equal("history-20260720.sqlite", kept.Last());
    }

    [Fact]
    public void MissingDbIsNoOp() =>
        Assert.Null(HistoryBackup.BackupDaily(@"Z:\nope\history.sqlite", _backups, DateTime.Now));

    [Fact]
    public void NeverThrows()
    {
        // an unwritable backup dir must not blow up
        var ex = Record.Exception(() =>
            HistoryBackup.BackupDaily(_db, "\0invalid\0path", DateTime.Now));
        Assert.Null(ex);
    }

    /// <summary>Final review, Minor 2 (2026-08-07): the prune loop's
    /// File.Delete only caught IOException. A read-only stale backup throws
    /// UnauthorizedAccessException instead, which used to escape the inner
    /// try/catch and fall into BackupDaily's own outer catch(Exception) —
    /// which returns null. Today's copy (`dest`) has already landed on disk
    /// by the time the prune loop runs, so that null return misreports a
    /// successful backup as a failure and lights a false "backup failed"
    /// banner. This fails against the pre-fix HistoryBackup (reverted): the
    /// read-only stale file's UnauthorizedAccessException propagates out,
    /// BackupDaily returns null, and <c>Assert.NotNull(result)</c>
    /// fails.</summary>
    [Fact]
    public void PruneFailureDoesNotMisreportTodaysCopyAsFailed()
    {
        Directory.CreateDirectory(_backups);
        var staleReadOnly = Path.Combine(_backups, "history-20260701.sqlite");
        var staleWritable = Path.Combine(_backups, "history-20260702.sqlite");
        File.WriteAllText(staleReadOnly, "old");
        File.WriteAllText(staleWritable, "old");
        File.SetAttributes(staleReadOnly, FileAttributes.ReadOnly);

        try
        {
            // keep: 1 — only today's own copy survives the prune, so both
            // stale files are prune targets and the read-only one is hit.
            var result = HistoryBackup.BackupDaily(_db, _backups, new DateTime(2026, 7, 20), keep: 1);

            Assert.NotNull(result);
            Assert.True(File.Exists(result!));
            Assert.True(File.Exists(Path.Combine(_backups, "history-20260720.sqlite")));
        }
        finally
        {
            // clear the attribute so the class Dispose()'s plain
            // Directory.Delete(recursive: true) can actually remove it
            File.SetAttributes(staleReadOnly, FileAttributes.Normal);
        }
    }
}
